package main

import (
	"archive/zip"
	"compress/flate"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log"
	"net/http"
	"net/url"
	"os"
	"path"
	"path/filepath"
	"strings"
)

// handleZip streams the selected files and folders (?path=a&path=b) as one zip, for downloading
// folders from the panel. Nothing is compressed: most of the data (7z backups) already is, and the
// server's CPU is better spent elsewhere. The entries still use Deflate (with stored blocks) rather
// than Store, because streamed Store entries need a data descriptor that some unzip tools mishandle.
func (s *Server) handleZip(w http.ResponseWriter, r *http.Request) {
	type item struct {
		abs, rel string
		info     fs.FileInfo
	}
	var items []item
	seen := map[string]bool{}
	for _, p := range r.URL.Query()["path"] {
		abs, rel, err := s.safeJoin(p)
		if err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		info, err := os.Lstat(abs)
		if err != nil {
			httpFsError(w, err)
			return
		}
		if !info.IsDir() && !info.Mode().IsRegular() { // symlinks, devices: not part of the file API
			http.Error(w, errBadPath.Error(), http.StatusBadRequest)
			return
		}
		if name := strings.ToLower(path.Base(rel)); !seen[name] { // same name twice would clash in the zip
			seen[name] = true
			items = append(items, item{abs, rel, info})
		}
	}
	if len(items) == 0 {
		http.Error(w, errBadPath.Error(), http.StatusBadRequest)
		return
	}

	name := path.Base(items[0].rel) + ".zip"
	if len(items) > 1 {
		name = "FileCopy-" + s.now().Format("20060102-1504") + ".zip"
	}
	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Disposition", "attachment; filename*=UTF-8''"+strings.ReplaceAll(url.QueryEscape(name), "+", "%20"))

	zw := zip.NewWriter(w)
	zw.RegisterCompressor(zip.Deflate, func(out io.Writer) (io.WriteCloser, error) {
		return flate.NewWriter(out, flate.NoCompression)
	})
	err := func() error {
		for _, it := range items {
			if err := s.zipItem(zw, it.abs, it.rel, it.info); err != nil {
				return err
			}
		}
		return zw.Close()
	}()
	if err != nil {
		// The headers are out. Ending the response normally would hand the browser a truncated zip
		// as a finished download: abort the connection so it reports a failed one instead.
		log.Printf("zip: %v", err)
		panic(http.ErrAbortHandler)
	}
}

// zipItem adds a file, or a folder with everything the file API can address in it, named from its base name.
func (s *Server) zipItem(zw *zip.Writer, abs, rel string, info fs.FileInfo) error {
	base := path.Base(rel)
	if !info.IsDir() {
		return zipFile(zw, abs, base, info)
	}
	if err := zipDir(zw, base, info); err != nil {
		return err
	}
	return s.walkData(abs, func(sub string, info fs.FileInfo) error {
		name := base + sub[len(rel):]
		if info.IsDir() {
			return zipDir(zw, name, info)
		}
		return zipFile(zw, filepath.Join(s.root, filepath.FromSlash(sub)), name, info)
	})
}

func zipDir(zw *zip.Writer, name string, info fs.FileInfo) error {
	_, err := zw.CreateHeader(&zip.FileHeader{Name: name + "/", Method: zip.Store, Modified: info.ModTime()})
	return err
}

func zipFile(zw *zip.Writer, abs, name string, info fs.FileInfo) error {
	f, err := os.Open(abs)
	if errors.Is(err, fs.ErrNotExist) {
		return nil // removed while zipping: left out, as the tree would leave it out now
	}
	if err != nil {
		return fmt.Errorf("%s: %w", name, err)
	}
	defer f.Close()
	wr, err := zw.CreateHeader(&zip.FileHeader{Name: name, Method: zip.Deflate, Modified: info.ModTime()})
	if err != nil {
		return err
	}
	if _, err = io.Copy(wr, f); err != nil {
		return fmt.Errorf("%s: %w", name, err)
	}
	return nil
}
