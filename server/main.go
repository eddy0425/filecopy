// FileCopy server: a tiny file store with token auth, Range downloads,
// parallel chunked uploads and a web control panel at /admin. Standard library only.
package main

import (
	"compress/gzip"
	"encoding/json"
	"flag"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
)

type Config struct {
	Listen  string `json:"listen"`
	DataDir string `json:"dataDir"`
	Token   string `json:"token"`
	// AdminPassword is the initial control panel password (the token when empty); it can be
	// changed in the panel, which then stores its own hash in .filecopy/state.json.
	AdminPassword string `json:"adminPassword,omitempty"`
	TLSCert       string `json:"tlsCert,omitempty"`
	TLSKey        string `json:"tlsKey,omitempty"`
	// TrustProxy takes X-Forwarded-For from private addresses too, e.g. a reverse proxy reaching a
	// Docker container through the bridge (172.x). Also set by FILECOPY_TRUST_PROXY=1.
	TrustProxy bool `json:"trustProxy,omitempty"`
}

type Server struct {
	root          string // absolute data dir
	token         string
	adminPassword string
	trustProxy    bool // see Config.TrustProxy
	now           func() time.Time
	uploads       *uploadManager
	state         *stateStore
	trash         *trashStore
	sessions      *sessionStore
	limiter       *failLimiter

	restoreMu sync.Mutex // trash restores: picking a free name and moving there is one step

	diskMu   sync.Mutex
	disk     diskStatus // used and trash; free and total are read on each call
	diskAt   time.Time
	diskBusy bool // a background measurement is running
}

func NewServer(dataDir, token string) (*Server, error) {
	root, err := filepath.Abs(dataDir)
	if err != nil {
		return nil, err
	}
	trashDir := filepath.Join(root, internalDirName, "trash")
	for _, d := range []string{filepath.Join(root, uploadDirName), trashDir} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			return nil, err
		}
	}
	s := &Server{root: root, token: token, now: time.Now}
	now := func() time.Time { return s.now() } // tests swap s.now
	if s.state, err = loadState(statePath(root), now); err != nil {
		return nil, err
	}
	s.uploads = newUploadManager(filepath.Join(root, uploadDirName))
	s.trash = &trashStore{dir: trashDir, now: now}
	s.sessions = &sessionStore{m: map[string]*adminSession{}}
	s.limiter = newFailLimiter(now)
	return s, nil
}

func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	s.registerFileRoutes(mux, "/api", s.clientAPI)
	mux.Handle("GET /api/ping", s.clientAPI(s.handlePing))
	mux.Handle("POST /api/totp/verify", s.clientAPI(s.handleTOTPVerify))
	s.registerAdminRoutes(mux)
	return logRequests(mux)
}

// maintain runs hourly cleanups and saves the traffic counters periodically.
func (s *Server) maintain() {
	s.uploads.cleanupStale(24 * time.Hour)
	s.trash.purge()
	flush := time.NewTicker(30 * time.Second)
	hourly := time.NewTicker(time.Hour)
	for {
		select {
		case <-flush.C:
			if err := s.state.flush(); err != nil {
				log.Printf("save state: %v", err)
			}
		case <-hourly.C:
			s.uploads.cleanupStale(24 * time.Hour)
			s.trash.purge()
		}
	}
}

func main() {
	cfgPath := flag.String("config", "config.json", "path to config file")
	flag.Parse()

	data, err := os.ReadFile(*cfgPath)
	if err != nil {
		log.Fatalf("read config: %v", err)
	}
	var cfg Config
	if err := json.Unmarshal(data, &cfg); err != nil {
		log.Fatalf("parse config: %v", err)
	}
	if len(cfg.Token) < 16 {
		log.Fatal("config: token must be at least 16 characters")
	}
	if cfg.Listen == "" {
		cfg.Listen = ":8080"
	}
	if cfg.DataDir == "" {
		cfg.DataDir = "./data"
	}

	s, err := NewServer(cfg.DataDir, cfg.Token)
	if err != nil {
		log.Fatalf("init: %v", err)
	}
	s.adminPassword = cfg.AdminPassword
	s.trustProxy = cfg.TrustProxy || os.Getenv("FILECOPY_TRUST_PROXY") == "1"
	go s.maintain()

	srv := &http.Server{
		Addr:              cfg.Listen,
		Handler:           s.Handler(),
		ReadHeaderTimeout: 30 * time.Second,
	}
	log.Printf("FileCopy server listening on %s, data dir %s", cfg.Listen, s.root)
	if cfg.TLSCert != "" && cfg.TLSKey != "" {
		err = srv.ListenAndServeTLS(cfg.TLSCert, cfg.TLSKey)
	} else {
		err = srv.ListenAndServe()
	}
	log.Fatal(err)
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(v)
}

// writeJSONGzip is writeJSON for large responses (the tree, ~10x smaller) when the client accepts gzip.
func writeJSONGzip(w http.ResponseWriter, r *http.Request, v any) {
	w.Header().Add("Vary", "Accept-Encoding")
	if !acceptsGzip(r) {
		writeJSON(w, v)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Content-Encoding", "gzip")
	gz, _ := gzip.NewWriterLevel(w, gzip.BestSpeed)
	_ = json.NewEncoder(gz).Encode(v)
	_ = gz.Close()
}

func acceptsGzip(r *http.Request) bool {
	for _, part := range strings.Split(r.Header.Get("Accept-Encoding"), ",") {
		coding, params, _ := strings.Cut(part, ";")
		if !strings.EqualFold(strings.TrimSpace(coding), "gzip") {
			continue
		}
		k, v, ok := strings.Cut(strings.TrimSpace(params), "=")
		if !ok || !strings.EqualFold(strings.TrimSpace(k), "q") {
			return true
		}
		q, err := strconv.ParseFloat(strings.TrimSpace(v), 64)
		return err != nil || q > 0
	}
	return false
}

func logRequests(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		if worthLogging(r) {
			log.Printf("%s %s %s (%v)", r.RemoteAddr, r.Method, r.URL.RequestURI(), time.Since(start).Round(time.Millisecond))
		}
	})
}

// worthLogging leaves out the noise: chunk uploads, the panel's and clients' polling, and download
// chunks after the first, so each download is still logged once (who fetched what).
func worthLogging(r *http.Request) bool {
	switch {
	case r.Method == http.MethodPut:
		return false
	case r.URL.Path == "/api/ping" || r.URL.Path == "/admin/api/status":
		return false
	case r.Method == http.MethodGet && (r.URL.Path == "/api/file" || r.URL.Path == "/admin/api/fs/file"):
		rng := r.Header.Get("Range")
		return rng == "" || strings.HasPrefix(rng, "bytes=0-")
	}
	return true
}
