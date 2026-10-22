// FileCopy server: a tiny file store with token auth, Range downloads and
// parallel chunked uploads. Standard library only.
package main

import (
	"encoding/json"
	"flag"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"time"
)

type Config struct {
	Listen  string `json:"listen"`
	DataDir string `json:"dataDir"`
	Token   string `json:"token"`
	TLSCert string `json:"tlsCert,omitempty"`
	TLSKey  string `json:"tlsKey,omitempty"`
}

type Server struct {
	root    string // absolute data dir
	token   string
	uploads *uploadManager
}

func NewServer(dataDir, token string) (*Server, error) {
	root, err := filepath.Abs(dataDir)
	if err != nil {
		return nil, err
	}
	if err := os.MkdirAll(filepath.Join(root, uploadDirName), 0o755); err != nil {
		return nil, err
	}
	s := &Server{root: root, token: token}
	s.uploads = newUploadManager(filepath.Join(root, uploadDirName))
	return s, nil
}

func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /api/tree", s.handleTree)
	mux.HandleFunc("GET /api/file", s.handleDownload)
	mux.HandleFunc("DELETE /api/file", s.handleDelete)
	mux.HandleFunc("POST /api/move", s.handleMove)
	mux.HandleFunc("POST /api/mkdir", s.handleMkdir)
	mux.HandleFunc("POST /api/upload/init", s.handleUploadInit)
	mux.HandleFunc("PUT /api/upload/{id}", s.handleUploadChunk)
	mux.HandleFunc("POST /api/upload/{id}/complete", s.handleUploadComplete)
	mux.HandleFunc("GET /api/ping", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, map[string]string{"status": "ok"})
	})
	return s.auth(logRequests(mux))
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
	s.uploads.cleanupStale(24 * time.Hour)
	go func() {
		for range time.Tick(time.Hour) {
			s.uploads.cleanupStale(24 * time.Hour)
		}
	}()

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
