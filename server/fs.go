package main

import (
	"encoding/json"
	"errors"
	"io"
	"io/fs"
	"net/http"
	"net/url"
	"os"
	"path"
	"path/filepath"
	"strings"
	"time"
)

const uploadDirName = ".uploads"

var errBadPath = errors.New("invalid path")

// isInternal reports whether a top-level name is one of the server's own directories.
func isInternal(first string) bool {
	return strings.EqualFold(first, uploadDirName) || strings.EqualFold(first, internalDirName)
}

// cleanRel normalizes a client path ("a/b", "/a/b", "a\\b") to a slash-separated
// path relative to the root. It never escapes the root and never refers to
// the root itself or the internal directories.
func cleanRel(p string) (string, error) {
	p = strings.ReplaceAll(p, "\\", "/")
	rel := strings.TrimPrefix(path.Clean("/"+p), "/")
	if rel == "" || rel == "." {
		return "", errBadPath
	}
	first, _, _ := strings.Cut(rel, "/")
	if isInternal(first) || strings.ContainsRune(rel, 0) {
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

// registerFileRoutes mounts the file API under prefix ("/api" for clients, "/admin/api/fs" for the panel).
func (s *Server) registerFileRoutes(mux *http.ServeMux, prefix string, wrap func(http.HandlerFunc) http.Handler) {
	mux.Handle("GET "+prefix+"/tree", wrap(s.handleTree))
	mux.Handle("GET "+prefix+"/file", wrap(s.handleDownload))
	mux.Handle("DELETE "+prefix+"/file", wrap(s.handleDelete))
	mux.Handle("POST "+prefix+"/move", wrap(s.handleMove))
	mux.Handle("POST "+prefix+"/mkdir", wrap(s.handleMkdir))
	mux.Handle("POST "+prefix+"/upload/init", wrap(s.handleUploadInit))
	mux.Handle("PUT "+prefix+"/upload/{id}", wrap(s.handleUploadChunk))
	mux.Handle("POST "+prefix+"/upload/{id}/complete", wrap(s.handleUploadComplete))
}

// isClient reports whether the request came through the client API (read-only folders apply)
// rather than the control panel.
func isClient(r *http.Request) bool { return deviceOf(r) != "" }

func (s *Server) denyReadOnly(w http.ResponseWriter) {
	http.Error(w, "read-only folder", http.StatusForbidden)
}

type Entry struct {
	Path     string `json:"path"`
	IsDir    bool   `json:"isDir"`
	Size     int64  `json:"size"`
	Mtime    int64  `json:"mtime"` // unix milliseconds
	ReadOnly bool   `json:"readOnly,omitempty"`
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
		if isInternal(rel) {
			return filepath.SkipDir
		}
		info, err := d.Info()
		if err != nil {
			return nil
		}
		if !info.IsDir() && !info.Mode().IsRegular() {
			return nil // skip symlinks, devices, ...
		}
		e := Entry{Path: rel, IsDir: info.IsDir(), Mtime: info.ModTime().UnixMilli(), ReadOnly: s.state.isReadOnly(rel)}
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
	abs, rel, err := s.safeJoin(r.URL.Query().Get("path"))
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
	if r.URL.Query().Get("download") == "1" { // browser download from the panel
		name := strings.ReplaceAll(url.QueryEscape(path.Base(rel)), "+", "%20")
		w.Header().Set("Content-Disposition", "attachment; filename*=UTF-8''"+name)
	}
	// ServeContent handles Range / If-Range, enabling resume and parallel chunks.
	http.ServeContent(w, r, "", info.ModTime(), f)
}

// handleDelete moves the item into the recycle bin.
func (s *Server) handleDelete(w http.ResponseWriter, r *http.Request) {
	abs, rel, err := s.safeJoin(r.URL.Query().Get("path"))
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if isClient(r) && s.state.touchesReadOnly(rel) {
		s.denyReadOnly(w)
		return
	}
	if _, err := os.Lstat(abs); errors.Is(err, fs.ErrNotExist) {
		w.WriteHeader(http.StatusNoContent) // already gone
		return
	}
	by := deviceOf(r)
	if by == "" {
		by = panelActor
	}
	if err := s.trash.put(abs, rel, by); err != nil {
		httpFsError(w, err)
		return
	}
	if !isClient(r) {
		s.state.movedPath(rel, "")
	}
	w.WriteHeader(http.StatusNoContent)
}

// within reports whether rel is base or inside it.
func within(rel, base string) bool { return rel == base || strings.HasPrefix(rel, base+"/") }

func (s *Server) handleMove(w http.ResponseWriter, r *http.Request) {
	var req struct{ From, To string }
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	from, fromRel, err1 := s.safeJoin(req.From)
	to, toRel, err2 := s.safeJoin(req.To)
	if err1 != nil || err2 != nil {
		http.Error(w, errBadPath.Error(), http.StatusBadRequest)
		return
	}
	if isClient(r) && (s.state.touchesReadOnly(fromRel) || s.state.isReadOnly(toRel)) {
		s.denyReadOnly(w)
		return
	}
	if strings.HasPrefix(strings.ToLower(toRel), strings.ToLower(fromRel)+"/") {
		http.Error(w, "cannot move a folder into itself", http.StatusBadRequest)
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
	if !isClient(r) {
		s.state.movedPath(fromRel, toRel) // read-only folders follow a rename in the panel
	}
	w.WriteHeader(http.StatusNoContent)
}

// handleCopy recursively copies a file or folder (panel only).
func (s *Server) handleCopy(w http.ResponseWriter, r *http.Request) {
	var req struct{ From, To string }
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	from, fromRel, err1 := s.safeJoin(req.From)
	to, toRel, err2 := s.safeJoin(req.To)
	if err1 != nil || err2 != nil {
		http.Error(w, errBadPath.Error(), http.StatusBadRequest)
		return
	}
	if within(strings.ToLower(toRel), strings.ToLower(fromRel)) {
		http.Error(w, "cannot copy a folder into itself", http.StatusBadRequest)
		return
	}
	if _, err := os.Stat(from); err != nil {
		httpFsError(w, err)
		return
	}
	if _, err := os.Lstat(to); err == nil {
		http.Error(w, "already exists", http.StatusConflict)
		return
	}
	if err := copyTree(from, to); err != nil {
		httpFsError(w, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// copyTree copies regular files and folders from src to dst, keeping modification times.
func copyTree(src, dst string) error {
	type dirTime struct {
		path string
		t    time.Time
	}
	var dirs []dirTime
	err := filepath.WalkDir(src, func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		target := filepath.Join(dst, strings.TrimPrefix(p, src))
		info, err := d.Info()
		if err != nil {
			return err
		}
		switch {
		case info.IsDir():
			dirs = append(dirs, dirTime{target, info.ModTime()})
			return os.MkdirAll(target, 0o755)
		case info.Mode().IsRegular():
			if err := copyFile(p, target); err != nil {
				return err
			}
			return os.Chtimes(target, info.ModTime(), info.ModTime())
		}
		return nil // skip symlinks, devices, ...
	})
	if err != nil {
		return err
	}
	for i := len(dirs) - 1; i >= 0; i-- { // children first: creating them touched the parents
		os.Chtimes(dirs[i].path, dirs[i].t, dirs[i].t)
	}
	return nil
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	out, err := os.OpenFile(dst, os.O_CREATE|os.O_WRONLY|os.O_EXCL, 0o644)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

func (s *Server) handleMkdir(w http.ResponseWriter, r *http.Request) {
	var req struct{ Path string }
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	abs, rel, err := s.safeJoin(req.Path)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if isClient(r) && s.state.isReadOnly(rel) {
		s.denyReadOnly(w)
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
