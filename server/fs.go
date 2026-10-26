package main

import (
	"encoding/json"
	"errors"
	"io/fs"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"strings"
	"time"
)

const uploadDirName = ".uploads"

var errBadPath = errors.New("invalid path")

// cleanRel normalizes a client path ("a/b", "/a/b", "a\\b") to a slash-separated
// path relative to the root. It never escapes the root and never refers to
// the root itself or the internal upload directory.
func cleanRel(p string) (string, error) {
	p = strings.ReplaceAll(p, "\\", "/")
	rel := strings.TrimPrefix(path.Clean("/"+p), "/")
	if rel == "" || rel == "." {
		return "", errBadPath
	}
	first, _, _ := strings.Cut(rel, "/")
	if strings.EqualFold(first, uploadDirName) || strings.ContainsRune(rel, 0) {
		return "", errBadPath
	}
	return rel, nil
}

// safeJoin maps a client path to an absolute path inside the data dir.
func (s *Server) safeJoin(p string) (string, string, error) {
	rel, err := cleanRel(p)
	if err != nil {
		return "", "", err
	}
	abs := filepath.Join(s.root, filepath.FromSlash(rel))
	if !strings.HasPrefix(abs, s.root+string(filepath.Separator)) {
		return "", "", errBadPath
	}
	return abs, rel, nil
}

type Entry struct {
	Path  string `json:"path"`
	IsDir bool   `json:"isDir"`
	Size  int64  `json:"size"`
	Mtime int64  `json:"mtime"` // unix milliseconds
}

func (s *Server) handleTree(w http.ResponseWriter, r *http.Request) {
	entries := []Entry{}
	err := filepath.WalkDir(s.root, func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			return nil // skip unreadable entries
		}
		if p == s.root {
			return nil
		}
		rel := filepath.ToSlash(strings.TrimPrefix(p, s.root+string(filepath.Separator)))
		if rel == uploadDirName {
			return filepath.SkipDir
		}
		info, err := d.Info()
		if err != nil {
			return nil
		}
		if !info.IsDir() && !info.Mode().IsRegular() {
			return nil // skip symlinks, devices, ...
		}
		e := Entry{Path: rel, IsDir: info.IsDir(), Mtime: info.ModTime().UnixMilli()}
		if !e.IsDir {
			e.Size = info.Size()
		}
		entries = append(entries, e)
		return nil
	})
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, entries)
}

func (s *Server) handleDownload(w http.ResponseWriter, r *http.Request) {
	abs, _, err := s.safeJoin(r.URL.Query().Get("path"))
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	f, err := os.Open(abs)
	if err != nil {
		httpFsError(w, err)
		return
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil || info.IsDir() {
		http.Error(w, "not a file", http.StatusBadRequest)
		return
	}
	w.Header().Set("Content-Type", "application/octet-stream")
	// ServeContent handles Range / If-Range, enabling resume and parallel chunks.
	http.ServeContent(w, r, "", info.ModTime(), f)
}

func (s *Server) handleDelete(w http.ResponseWriter, r *http.Request) {
	abs, _, err := s.safeJoin(r.URL.Query().Get("path"))
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if err := os.RemoveAll(abs); err != nil {
		httpFsError(w, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleMove(w http.ResponseWriter, r *http.Request) {
	var req struct{ From, To string }
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	from, _, err1 := s.safeJoin(req.From)
	to, _, err2 := s.safeJoin(req.To)
	if err1 != nil || err2 != nil {
		http.Error(w, errBadPath.Error(), http.StatusBadRequest)
		return
	}
	// Never replace an existing item; a case-only rename on a case-insensitive filesystem is the same file.
	fromInfo, err := os.Stat(from)
	if err != nil {
		httpFsError(w, err)
		return
	}
	if toInfo, err := os.Stat(to); err == nil && !os.SameFile(fromInfo, toInfo) {
		http.Error(w, "already exists", http.StatusConflict)
		return
	}
	if err := os.MkdirAll(filepath.Dir(to), 0o755); err != nil {
		httpFsError(w, err)
		return
	}
	if err := os.Rename(from, to); err != nil {
		httpFsError(w, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleMkdir(w http.ResponseWriter, r *http.Request) {
	var req struct{ Path string }
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	abs, _, err := s.safeJoin(req.Path)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if err := os.MkdirAll(abs, 0o755); err != nil {
		httpFsError(w, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// conflictName returns "dir/name (conflict 20260102-150405).ext".
func conflictName(abs string, t time.Time) string {
	ext := filepath.Ext(abs)
	base := strings.TrimSuffix(abs, ext)
	return base + " (conflict " + t.Format("20060102-150405") + ")" + ext
}

func httpFsError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, fs.ErrNotExist):
		http.Error(w, "not found", http.StatusNotFound)
	case errors.Is(err, fs.ErrExist):
		http.Error(w, "already exists", http.StatusConflict)
	default:
		http.Error(w, err.Error(), http.StatusInternalServerError)
	}
}
