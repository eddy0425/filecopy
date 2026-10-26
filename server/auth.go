package main

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"io"
	"math"
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
		// Read the generation before the switch: setPaused stores the flag first and then bumps the
		// generation, so a request passing the check while connections are being cut is still cut.
		gen := s.state.gen.Load()
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
		id = s.state.touch(id, s.clientIP(r))
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

// readFromSegment bounds how much is sent between checks for a cut. Well below the client's 4 MB
// chunk, so a pause also cuts the chunks in flight, and the speed meter stays smooth.
const readFromSegment = 1 << 20

// ReadFrom keeps http.ServeContent on the sendfile path: it hands segments to the underlying
// ReadFrom, metering and checking for a cut between them. A LimitedReader (from io.CopyN) is
// unwrapped rather than wrapped again: sendfile looks through one LimitedReader only.
func (m *meteredWriter) ReadFrom(src io.Reader) (int64, error) {
	rf, ok := m.ResponseWriter.(io.ReaderFrom)
	if !ok {
		return io.Copy(struct{ io.Writer }{m}, src) // hide ReadFrom so io.Copy uses Write
	}
	lr, ok := src.(*io.LimitedReader)
	if !ok {
		lr = &io.LimitedReader{R: src, N: math.MaxInt64}
	}
	var total int64
	for lr.N > 0 {
		if m.s.state.gen.Load() != m.gen {
			return total, errCut
		}
		seg := min(lr.N, readFromSegment)
		n, err := rf.ReadFrom(&io.LimitedReader{R: lr.R, N: seg})
		lr.N -= n
		total += n
		m.s.state.addTraffic(m.id, 0, n)
		if err != nil || n < seg { // n < seg: end of src
			return total, err
		}
	}
	return total, nil
}

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

// clientIP is the peer address; behind a local reverse proxy (Caddy / Nginx on the same host), the
// address it appended to X-Forwarded-For. Private peers (the Docker bridge, 172.x) are trusted only
// with trustProxy: otherwise anyone on the LAN could pick an address and dodge the login lockout.
// Only valid addresses are taken: the result is shown in the panel.
func (s *Server) clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		host = r.RemoteAddr
	}
	if ip := net.ParseIP(host); ip != nil && (ip.IsLoopback() || s.trustProxy && ip.IsPrivate()) {
		if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
			parts := strings.Split(xff, ",")
			if fwd := net.ParseIP(strings.TrimSpace(parts[len(parts)-1])); fwd != nil {
				return fwd.String()
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
	ip := s.clientIP(r)
	if s.limiter.blocked(ip) {
		http.Error(w, "too many attempts", http.StatusTooManyRequests)
		return
	}
	if !s.checkCode(w, ip, secret, req.Code, http.StatusForbidden, "wrong code") {
		return
	}
	s.limiter.success(ip)
	w.WriteHeader(http.StatusNoContent)
}
