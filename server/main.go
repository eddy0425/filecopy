// FileCopy server: a tiny file store with token auth, Range downloads,
// parallel chunked uploads and a web control panel at /admin. Standard library only.
package main

import (
	"encoding/json"
	"flag"
	"log"
	"net/http"
	"os"
	"path/filepath"
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
}

type Server struct {
	root          string // absolute data dir
	token         string
	adminPassword string
	now           func() time.Time
	uploads       *uploadManager
	state         *stateStore
	trash         *trashStore
	sessions      *sessionStore
	limiter       *failLimiter

	diskMu sync.Mutex
	disk   diskStatus
	diskAt time.Time
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

func logRequests(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		if r.Method != http.MethodPut { // chunk PUTs are too noisy
			log.Printf("%s %s %s (%v)", r.RemoteAddr, r.Method, r.URL.RequestURI(), time.Since(start).Round(time.Millisecond))
		}
	})
}
