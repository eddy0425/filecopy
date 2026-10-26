package main

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
)

type deviceCtxKey struct{}

// deviceOf returns the EquipmentId of a client API request ("" for the control panel).
func deviceOf(r *http.Request) string {
	id, _ := r.Context().Value(deviceCtxKey{}).(string)
	return id
}

func (s *Server) tokenOK(r *http.Request) bool {
	h := r.Header.Get("Authorization")
	return len(h) > 7 && strings.EqualFold(h[:7], "bearer ") &&
		subtle.ConstantTimeCompare([]byte(h[7:]), []byte(s.token)) == 1
}

// clientAPI guards the client endpoints: token, the connection switch, and the device's
// EquipmentId, whose traffic is metered.
func (s *Server) clientAPI(next http.HandlerFunc) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !s.tokenOK(r) {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		if s.state.paused() {
			http.Error(w, "connections paused", http.StatusServiceUnavailable)
			return
		}
		id, err := url.PathUnescape(r.Header.Get("X-Equipment-Id"))
		id = strings.TrimSpace(id)
		if err != nil || !validEquipmentID(id) {
			http.Error(w, "equipment id required", http.StatusBadRequest)
			return
		}
		id = s.state.touch(id, clientIP(r))
		gen := s.state.gen.Load()
		mw := &meteredWriter{ResponseWriter: w, s: s, id: id, gen: gen}
		if r.Body != nil {
			r.Body = &meteredBody{ReadCloser: r.Body, s: s, id: id, gen: gen}
		}
		next(mw, r.WithContext(context.WithValue(r.Context(), deviceCtxKey{}, id)))
	})
}

var errCut = errors.New("connections paused")

// meteredWriter counts download traffic and fails once connections are cut, which ends
// in-flight downloads (http.ServeContent does not watch the request context).
type meteredWriter struct {
	http.ResponseWriter
	s   *Server
	id  string
	gen int64
}

func (m *meteredWriter) Write(p []byte) (int, error) {
	if m.s.state.gen.Load() != m.gen {
		return 0, errCut
	}
	n, err := m.ResponseWriter.Write(p)
	m.s.state.addTraffic(m.id, 0, int64(n))
	return n, err
}

func (m *meteredWriter) Unwrap() http.ResponseWriter { return m.ResponseWriter }

// meteredBody counts upload traffic and fails once connections are cut.
type meteredBody struct {
	io.ReadCloser
	s   *Server
	id  string
	gen int64
}

func (m *meteredBody) Read(p []byte) (int, error) {
	if m.s.state.gen.Load() != m.gen {
		return 0, errCut
	}
	n, err := m.ReadCloser.Read(p)
	m.s.state.addTraffic(m.id, int64(n), 0)
	return n, err
}

// clientIP is the peer address; behind a local reverse proxy (Caddy / Nginx on the same
// host), the address it appended to X-Forwarded-For.
func clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		host = r.RemoteAddr
	}
	if ip := net.ParseIP(host); ip != nil && ip.IsLoopback() {
		if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
			parts := strings.Split(xff, ",")
			if last := strings.TrimSpace(parts[len(parts)-1]); last != "" {
				return last
			}
		}
	}
	return host
}

func (s *Server) handlePing(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, map[string]any{"status": "ok", "totp": s.state.totpSecret() != "", "equipmentId": deviceOf(r)})
}

// handleTOTPVerify checks a code for the client's lock screen.
func (s *Server) handleTOTPVerify(w http.ResponseWriter, r *http.Request) {
	var req struct{ Code string }
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	secret := s.state.totpSecret()
	if secret == "" {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	ip := clientIP(r)
	if s.limiter.blocked(ip) {
		http.Error(w, "too many attempts", http.StatusTooManyRequests)
		return
	}
	if !s.state.verifyTOTP(secret, req.Code) {
		s.limiter.fail(ip)
		http.Error(w, "wrong code", http.StatusForbidden)
		return
	}
	s.limiter.success(ip)
	w.WriteHeader(http.StatusNoContent)
}
