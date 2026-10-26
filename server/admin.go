package main

import (
	"crypto/rand"
	"crypto/subtle"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io/fs"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

//go:embed admin/index.html
var adminPage []byte

const (
	sessionCookie  = "filecopy_admin"
	sessionTTL     = 12 * time.Hour
	minPasswordLen = 8
	panelActor     = "控制面板"
	diskCacheTTL   = 60 * time.Second
)

type adminSession struct {
	expires     time.Time
	totpPending string // secret shown during TOTP setup, saved once confirmed with a code
}

type sessionStore struct {
	mu sync.Mutex
	m  map[string]*adminSession
}

func (s *Server) sessionFor(r *http.Request) (string, *adminSession) {
	c, err := r.Cookie(sessionCookie)
	if err != nil {
		return "", nil
	}
	s.sessions.mu.Lock()
	defer s.sessions.mu.Unlock()
	sess := s.sessions.m[c.Value]
	if sess == nil {
		return "", nil
	}
	if s.now().After(sess.expires) {
		delete(s.sessions.m, c.Value)
		return "", nil
	}
	return c.Value, sess
}

type adminCtx struct {
	id   string
	sess *adminSession
}

// adminOnly requires a panel session; state-changing requests also need the X-Requested-With
// header, which a cross-site form cannot send (on top of the SameSite=Strict cookie).
func (s *Server) adminOnly(next func(http.ResponseWriter, *http.Request, adminCtx)) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		id, sess := s.sessionFor(r)
		if sess == nil {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		if r.Method != http.MethodGet && r.Header.Get("X-Requested-With") != "filecopy" {
			http.Error(w, "missing X-Requested-With", http.StatusForbidden)
			return
		}
		next(w, r, adminCtx{id, sess})
	})
}

// adminFS adapts the shared file handlers for the panel.
func (s *Server) adminFS(h http.HandlerFunc) http.Handler {
	return s.adminOnly(func(w http.ResponseWriter, r *http.Request, _ adminCtx) { h(w, r) })
}

func (s *Server) registerAdminRoutes(mux *http.ServeMux) {
	mux.HandleFunc("GET /admin", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/admin/", http.StatusFound)
	})
	mux.HandleFunc("GET /admin/{$}", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Write(adminPage)
	})
	mux.HandleFunc("GET /admin/api/session", s.handleSessionInfo)
	mux.HandleFunc("POST /admin/api/login", s.handleLogin)
	mux.Handle("POST /admin/api/logout", s.adminOnly(s.handleLogout))
	mux.Handle("POST /admin/api/password", s.adminOnly(s.handlePassword))
	mux.Handle("GET /admin/api/status", s.adminOnly(s.handleStatus))
	mux.Handle("POST /admin/api/connections", s.adminOnly(s.handleConnections))
	mux.Handle("POST /admin/api/devices/reset", s.adminOnly(s.handleDeviceReset))
	mux.Handle("POST /admin/api/devices/rename", s.adminOnly(s.handleDeviceRename))
	mux.Handle("DELETE /admin/api/devices", s.adminOnly(s.handleDeviceDelete))
	mux.Handle("POST /admin/api/totp/setup", s.adminOnly(s.handleTOTPSetup))
	mux.Handle("POST /admin/api/totp/enable", s.adminOnly(s.handleTOTPEnable))
	mux.Handle("POST /admin/api/totp/disable", s.adminOnly(s.handleTOTPDisable))
	mux.Handle("GET /admin/api/trash", s.adminOnly(s.handleTrashList))
	mux.Handle("POST /admin/api/trash/restore", s.adminOnly(s.handleTrashRestore))
	mux.Handle("DELETE /admin/api/trash", s.adminOnly(s.handleTrashDelete))
	mux.Handle("POST /admin/api/trash/empty", s.adminOnly(s.handleTrashEmpty))
	mux.Handle("POST /admin/api/readonly", s.adminOnly(s.handleReadOnly))
	mux.Handle("POST /admin/api/fs/copy", s.adminFS(s.handleCopy))
	s.registerFileRoutes(mux, "/admin/api/fs", s.adminFS)
}

func decodeJSON(w http.ResponseWriter, r *http.Request, v any) bool {
	if err := json.NewDecoder(r.Body).Decode(v); err != nil {
		http.Error(w, "bad request", http.StatusBadRequest)
		return false
	}
	return true
}

// ---------- login ----------

func (s *Server) handleSessionInfo(w http.ResponseWriter, r *http.Request) {
	_, sess := s.sessionFor(r)
	writeJSON(w, map[string]bool{"loggedIn": sess != nil, "totp": s.state.totpSecret() != ""})
}

// checkAdminPassword: the password set in the panel, else adminPassword from config.json, else the token.
func (s *Server) checkAdminPassword(pw string) bool {
	s.state.mu.Lock()
	hash := s.state.st.AdminPasswordHash
	s.state.mu.Unlock()
	if hash != "" {
		return checkPasswordHash(hash, pw)
	}
	want := s.adminPassword
	if want == "" {
		want = s.token
	}
	return subtle.ConstantTimeCompare([]byte(pw), []byte(want)) == 1
}

func isHTTPS(r *http.Request) bool {
	return r.TLS != nil || strings.EqualFold(r.Header.Get("X-Forwarded-Proto"), "https")
}

func (s *Server) handleLogin(w http.ResponseWriter, r *http.Request) {
	var req struct{ Password, Code string }
	if !decodeJSON(w, r, &req) {
		return
	}
	ip := clientIP(r)
	if s.limiter.blocked(ip) {
		http.Error(w, "too many attempts", http.StatusTooManyRequests)
		return
	}
	secret := s.state.totpSecret()
	if !s.checkAdminPassword(req.Password) || secret != "" && !s.state.verifyTOTP(secret, req.Code) {
		s.limiter.fail(ip)
		http.Error(w, "wrong password or code", http.StatusUnauthorized)
		return
	}
	s.limiter.success(ip)
	b := make([]byte, 32)
	rand.Read(b)
	id := hex.EncodeToString(b)
	s.sessions.mu.Lock()
	now := s.now()
	for k, v := range s.sessions.m {
		if now.After(v.expires) {
			delete(s.sessions.m, k)
		}
	}
	s.sessions.m[id] = &adminSession{expires: now.Add(sessionTTL)}
	s.sessions.mu.Unlock()
	http.SetCookie(w, &http.Cookie{
		Name: sessionCookie, Value: id, Path: "/admin", MaxAge: int(sessionTTL.Seconds()),
		HttpOnly: true, SameSite: http.SameSiteStrictMode, Secure: isHTTPS(r),
	})
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleLogout(w http.ResponseWriter, r *http.Request, a adminCtx) {
	s.sessions.mu.Lock()
	delete(s.sessions.m, a.id)
	s.sessions.mu.Unlock()
	http.SetCookie(w, &http.Cookie{Name: sessionCookie, Value: "", Path: "/admin", MaxAge: -1})
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handlePassword(w http.ResponseWriter, r *http.Request, a adminCtx) {
	var req struct{ Old, New, Code string }
	if !decodeJSON(w, r, &req) {
		return
	}
	ip := clientIP(r)
	if s.limiter.blocked(ip) {
		http.Error(w, "too many attempts", http.StatusTooManyRequests)
		return
	}
	secret := s.state.totpSecret()
	if !s.checkAdminPassword(req.Old) || secret != "" && !s.state.verifyTOTP(secret, req.Code) {
		s.limiter.fail(ip)
		http.Error(w, "wrong password or code", http.StatusForbidden)
		return
	}
	s.limiter.success(ip)
	if len([]rune(req.New)) < minPasswordLen {
		http.Error(w, "password too short", http.StatusBadRequest)
		return
	}
	hash := hashPassword(req.New)
	if err := s.state.update(func(st *persistedState) error { st.AdminPasswordHash = hash; return nil }); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	s.sessions.mu.Lock()
	for k := range s.sessions.m { // sign out everywhere else
		if k != a.id {
			delete(s.sessions.m, k)
		}
	}
	s.sessions.mu.Unlock()
	w.WriteHeader(http.StatusNoContent)
}

// ---------- status, connections, devices ----------

type diskStatus struct {
	Used  int64  `json:"used"`  // data files
	Trash int64  `json:"trash"` // recycle bin
	Free  uint64 `json:"free"`
	Total uint64 `json:"total"`
}

func (s *Server) diskStatus() diskStatus {
	s.diskMu.Lock()
	defer s.diskMu.Unlock()
	if s.now().Before(s.diskAt.Add(diskCacheTTL)) {
		return s.disk
	}
	var used int64
	filepath.WalkDir(s.root, func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			return nil
		}
		if d.IsDir() && p != s.root && isInternal(d.Name()) && filepath.Dir(p) == s.root {
			return filepath.SkipDir
		}
		if d.Type().IsRegular() {
			if info, err := d.Info(); err == nil {
				used += info.Size()
			}
		}
		return nil
	})
	ds := diskStatus{Used: used, Trash: s.trash.usage()}
	ds.Free, ds.Total, _ = diskSpace(s.root)
	s.disk, s.diskAt = ds, s.now()
	return ds
}

func (s *Server) handleStatus(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	writeJSON(w, map[string]any{
		"accepting": !s.state.paused(),
		"totp":      s.state.totpSecret() != "",
		"https":     isHTTPS(r),
		"devices":   s.state.devices(),
		"readOnly":  s.state.readOnlyFolders(),
		"disk":      s.diskStatus(),
		"now":       s.now().UnixMilli(),
	})
}

func (s *Server) handleConnections(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	var req struct{ Accept bool }
	if !decodeJSON(w, r, &req) {
		return
	}
	if err := s.state.setPaused(!req.Accept); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func deviceError(w http.ResponseWriter, err error) {
	switch {
	case err == nil:
		w.WriteHeader(http.StatusNoContent)
	case errors.Is(err, errNoDevice):
		http.Error(w, err.Error(), http.StatusNotFound)
	case errors.Is(err, errIDTaken):
		http.Error(w, err.Error(), http.StatusConflict)
	case errors.Is(err, errBadDevice):
		http.Error(w, err.Error(), http.StatusBadRequest)
	default:
		http.Error(w, err.Error(), http.StatusInternalServerError)
	}
}

func (s *Server) handleDeviceReset(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	var req struct {
		ID  string
		All bool
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	if req.ID == "" && !req.All {
		http.Error(w, "id or all required", http.StatusBadRequest)
		return
	}
	deviceError(w, s.state.resetTraffic(req.ID))
}

func (s *Server) handleDeviceRename(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	var req struct{ ID, NewID string }
	if !decodeJSON(w, r, &req) {
		return
	}
	deviceError(w, s.state.renameDevice(req.ID, req.NewID))
}

func (s *Server) handleDeviceDelete(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	deviceError(w, s.state.deleteDevice(r.URL.Query().Get("id")))
}

// ---------- TOTP ----------

func (s *Server) handleTOTPSetup(w http.ResponseWriter, r *http.Request, a adminCtx) {
	secret := newTOTPSecret()
	uri := totpURI(secret)
	qr, err := encodeQR([]byte(uri))
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	s.sessions.mu.Lock()
	a.sess.totpPending = secret
	s.sessions.mu.Unlock()
	writeJSON(w, map[string]string{"secret": secret, "uri": uri, "qrSvg": qr.svg()})
}

func (s *Server) handleTOTPEnable(w http.ResponseWriter, r *http.Request, a adminCtx) {
	var req struct{ Code string }
	if !decodeJSON(w, r, &req) {
		return
	}
	s.sessions.mu.Lock()
	secret := a.sess.totpPending
	s.sessions.mu.Unlock()
	if secret == "" {
		http.Error(w, "run setup first", http.StatusBadRequest)
		return
	}
	if !s.state.verifyTOTP(secret, req.Code) {
		http.Error(w, "wrong code", http.StatusForbidden)
		return
	}
	if err := s.state.update(func(st *persistedState) error { st.TOTPSecret = secret; return nil }); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	s.sessions.mu.Lock()
	a.sess.totpPending = ""
	s.sessions.mu.Unlock()
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleTOTPDisable(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	var req struct{ Code string }
	if !decodeJSON(w, r, &req) {
		return
	}
	ip := clientIP(r)
	if s.limiter.blocked(ip) {
		http.Error(w, "too many attempts", http.StatusTooManyRequests)
		return
	}
	secret := s.state.totpSecret()
	if secret != "" && !s.state.verifyTOTP(secret, req.Code) {
		s.limiter.fail(ip)
		http.Error(w, "wrong code", http.StatusForbidden)
		return
	}
	s.limiter.success(ip)
	if err := s.state.update(func(st *persistedState) error { st.TOTPSecret = ""; return nil }); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// ---------- recycle bin, read-only folders ----------

func (s *Server) handleTrashList(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	writeJSON(w, s.trash.list())
}

func (s *Server) handleTrashRestore(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	var req struct{ ID string }
	if !decodeJSON(w, r, &req) {
		return
	}
	it, err := s.trash.get(req.ID)
	if err != nil {
		http.Error(w, err.Error(), http.StatusNotFound)
		return
	}
	abs, rel, err := s.safeJoin(it.Path)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if _, err := os.Lstat(abs); err == nil { // the original location is taken again
		abs = restoredName(abs, s.now())
		rel = filepath.ToSlash(strings.TrimPrefix(abs, s.root+string(filepath.Separator)))
	}
	if err := s.trash.restore(req.ID, abs); err != nil {
		httpFsError(w, err)
		return
	}
	writeJSON(w, map[string]string{"path": rel})
}

func (s *Server) handleTrashDelete(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	if err := s.trash.remove(r.URL.Query().Get("id")); err != nil {
		http.Error(w, err.Error(), http.StatusNotFound)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleTrashEmpty(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	if err := s.trash.empty(); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleReadOnly(w http.ResponseWriter, r *http.Request, _ adminCtx) {
	var req struct {
		Path     string
		ReadOnly bool
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	abs, rel, err := s.safeJoin(req.Path)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if info, err := os.Stat(abs); req.ReadOnly && (err != nil || !info.IsDir()) {
		http.Error(w, "not a folder", http.StatusBadRequest)
		return
	}
	if err := s.state.setReadOnly(rel, req.ReadOnly); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}
