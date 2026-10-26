package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"sync"
	"time"
)

const (
	defaultChunkSize = 4 << 20
	maxChunkSize     = 64 << 20
)

type uploadSession struct {
	id       string
	rel      string // target path relative to root
	size     int64
	mtime    int64 // unix ms
	tmpPath  string
	client   bool // started through the client API: read-only folders apply
	file     *os.File
	mu       sync.Mutex
	received map[int64]int64 // offset -> length of chunks written
	lastUsed time.Time
}

type uploadManager struct {
	dir      string
	mu       sync.Mutex
	sessions map[string]*uploadSession
}

func newUploadManager(dir string) *uploadManager {
	return &uploadManager{dir: dir, sessions: map[string]*uploadSession{}}
}

func (m *uploadManager) get(id string) *uploadSession {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.sessions[id]
}

func (m *uploadManager) remove(id string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.sessions, id)
}

// closeAll releases all open temp files (used on shutdown and in tests).
func (m *uploadManager) closeAll() {
	m.mu.Lock()
	defer m.mu.Unlock()
	for id, s := range m.sessions {
		s.file.Close()
		delete(m.sessions, id)
	}
}

// cleanupStale removes abandoned sessions and leftover temp files.
func (m *uploadManager) cleanupStale(maxAge time.Duration) {
	cutoff := time.Now().Add(-maxAge)
	m.mu.Lock()
	active := map[string]bool{}
	for id, s := range m.sessions {
		s.mu.Lock()
		if s.lastUsed.Before(cutoff) {
			s.file.Close()
			os.Remove(s.tmpPath)
			delete(m.sessions, id)
		} else {
			active[filepath.Base(s.tmpPath)] = true
		}
		s.mu.Unlock()
	}
	m.mu.Unlock()

	files, _ := os.ReadDir(m.dir)
	for _, f := range files {
		if active[f.Name()] {
			continue
		}
		if info, err := f.Info(); err == nil && info.ModTime().Before(cutoff) {
			os.Remove(filepath.Join(m.dir, f.Name()))
		}
	}
}

func (s *Server) handleUploadInit(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Path  string `json:"path"`
		Size  int64  `json:"size"`
		Mtime int64  `json:"mtime"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.Size < 0 {
		http.Error(w, "bad request", http.StatusBadRequest)
		return
	}
	_, rel, err := s.safeJoin(req.Path)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if isClient(r) && s.state.isReadOnly(rel) {
		s.denyReadOnly(w)
		return
	}
	idBytes := make([]byte, 16)
	rand.Read(idBytes)
	id := hex.EncodeToString(idBytes)

	tmp := filepath.Join(s.uploads.dir, id)
	f, err := os.OpenFile(tmp, os.O_CREATE|os.O_RDWR|os.O_EXCL, 0o644)
	if err != nil {
		httpFsError(w, err)
		return
	}
	if err := f.Truncate(req.Size); err != nil {
		f.Close()
		os.Remove(tmp)
		httpFsError(w, err)
		return
	}
	sess := &uploadSession{
		id: id, rel: rel, size: req.Size, mtime: req.Mtime,
		tmpPath: tmp, client: isClient(r), file: f, received: map[int64]int64{}, lastUsed: time.Now(),
	}
	s.uploads.mu.Lock()
	s.uploads.sessions[id] = sess
	s.uploads.mu.Unlock()

	writeJSON(w, map[string]any{"id": id, "chunkSize": defaultChunkSize})
}

func (s *Server) handleUploadChunk(w http.ResponseWriter, r *http.Request) {
	sess := s.uploads.get(r.PathValue("id"))
	if sess == nil {
		http.Error(w, "upload session not found", http.StatusNotFound)
		return
	}
	offset, err := strconv.ParseInt(r.URL.Query().Get("offset"), 10, 64)
	if err != nil || offset < 0 || offset > sess.size {
		http.Error(w, "bad offset", http.StatusBadRequest)
		return
	}
	maxLen := sess.size - offset
	if maxLen > maxChunkSize {
		maxLen = maxChunkSize
	}
	body := http.MaxBytesReader(w, r.Body, maxLen)
	// Write directly at offset; chunks may arrive in parallel and out of order.
	n, err := io.Copy(&offsetWriter{f: sess.file, off: offset}, body)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	sess.mu.Lock()
	sess.received[offset] = n
	sess.lastUsed = time.Now()
	sess.mu.Unlock()
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleUploadComplete(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	sess := s.uploads.get(id)
	if sess == nil {
		http.Error(w, "upload session not found", http.StatusNotFound)
		return
	}
	var req struct {
		// BaseMtime is the server mtime the client last saw for this path
		// (0 for a new file). If the server copy changed since, it is kept as
		// a conflict copy instead of being silently overwritten.
		BaseMtime int64 `json:"baseMtime"`
	}
	_ = json.NewDecoder(r.Body).Decode(&req)

	sess.mu.Lock()
	defer sess.mu.Unlock()
	if sess.client && s.state.isReadOnly(sess.rel) { // made read-only while uploading
		s.denyReadOnly(w)
		return
	}
	if !coversAll(sess.received, sess.size) {
		http.Error(w, "upload incomplete", http.StatusConflict)
		return
	}
	if err := sess.file.Sync(); err != nil {
		httpFsError(w, err)
		return
	}
	sess.file.Close()

	if !notAlias(s.root, sess.rel) { // a long name created since init got this as its 8.3 alias
		http.Error(w, errBadPath.Error(), http.StatusBadRequest)
		return
	}
	abs := filepath.Join(s.root, filepath.FromSlash(sess.rel))
	if err := os.MkdirAll(filepath.Dir(abs), 0o755); err != nil {
		httpFsError(w, err)
		return
	}
	if info, err := os.Stat(abs); err == nil && !info.IsDir() &&
		req.BaseMtime != info.ModTime().UnixMilli() {
		cp := conflictName(abs, time.Now())
		if err := os.Rename(abs, cp); err != nil {
			httpFsError(w, err)
			return
		}
		log.Printf("conflict: kept previous version of %s as %s", sess.rel, filepath.Base(cp))
	}
	if err := os.Rename(sess.tmpPath, abs); err != nil {
		httpFsError(w, err)
		return
	}
	mt := time.UnixMilli(sess.mtime)
	if sess.mtime == 0 {
		mt = time.Now()
	}
	os.Chtimes(abs, mt, mt)
	s.uploads.remove(id)

	info, err := os.Stat(abs)
	if err != nil {
		httpFsError(w, err)
		return
	}
	writeJSON(w, Entry{Path: sess.rel, Size: info.Size(), Mtime: info.ModTime().UnixMilli()})
}

// coversAll reports whether the received chunks cover [0, size).
func coversAll(chunks map[int64]int64, size int64) bool {
	var covered int64
	for {
		if covered >= size {
			return true
		}
		best := int64(-1)
		for off, n := range chunks {
			if off <= covered && off+n > covered && off+n > best {
				best = off + n
			}
		}
		if best < 0 {
			return false
		}
		covered = best
	}
}

type offsetWriter struct {
	f   *os.File
	off int64
}

func (o *offsetWriter) Write(p []byte) (int, error) {
	n, err := o.f.WriteAt(p, o.off)
	o.off += int64(n)
	return n, err
}
