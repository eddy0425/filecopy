package main

import (
	"encoding/json"
	"errors"
	"io/fs"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestTrashRestoreRecreatesParent(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "a/b/c.txt", "c")
	status(t, a.do("DELETE", "/admin/api/fs/file?path=a/b/c.txt", ""), http.StatusNoContent)
	status(t, a.do("DELETE", "/admin/api/fs/file?path=a", ""), http.StatusNoContent)
	var file TrashItem
	for _, it := range s.trash.list() {
		if it.Path == "a/b/c.txt" {
			file = it
		}
	}
	status(t, a.do("POST", "/admin/api/trash/restore", `{"id":"`+file.ID+`"}`), http.StatusOK)
	if b, _ := os.ReadFile(filepath.Join(s.root, "a", "b", "c.txt")); string(b) != "c" {
		t.Fatal("not restored under recreated parents")
	}
	// Left in the bin: folder "a" (only an empty "b" by the time it was deleted).
	if items := s.trash.list(); len(items) != 1 || items[0].Path != "a" || s.trash.usage() != 0 {
		t.Fatalf("trash = %+v", items)
	}
}

func TestTrashHidesEntryWithoutItem(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "a.txt", "a")
	status(t, a.do("DELETE", "/admin/api/fs/file?path=a.txt", ""), http.StatusNoContent)
	id := s.trash.list()[0].ID
	dir, _ := s.trash.itemDir(id)
	os.RemoveAll(filepath.Join(dir, "item")) // as if the restore's cleanup had failed
	if items := s.trash.list(); len(items) != 0 {
		t.Fatalf("leftover entry listed: %+v", items)
	}
	status(t, a.do("POST", "/admin/api/trash/restore", `{"id":"`+id+`"}`), http.StatusNotFound)
	old := s.now().Add(-trashRetention - time.Hour)
	os.Chtimes(dir, old, old)
	s.trash.purge()
	if _, err := os.Stat(dir); err == nil {
		t.Fatal("leftover entry not purged")
	}
}

func TestRestoredNameStopsOnLstatError(t *testing.T) {
	file := filepath.Join(t.TempDir(), "f")
	if err := os.WriteFile(file, nil, 0o644); err != nil {
		t.Fatal(err)
	}
	done := make(chan string)
	go func() { done <- restoredName(filepath.Join(file, "a.txt"), time.Now()) }() // ENOTDIR on Linux
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("restoredName loops when Lstat fails with something other than not-exist")
	}
}

func TestTrashRestoreSameSecondDoesNotOverwrite(t *testing.T) {
	s, ts := newTestServer(t)
	withClock(s) // time stands still
	a := mustLogin(t, ts)
	for _, v := range []string{"one", "two"} {
		putFile(t, s, "f.txt", v)
		status(t, a.do("DELETE", "/admin/api/fs/file?path=f.txt", ""), http.StatusNoContent)
	}
	putFile(t, s, "f.txt", "current")
	for _, it := range s.trash.list() {
		status(t, a.do("POST", "/admin/api/trash/restore", `{"id":"`+it.ID+`"}`), http.StatusOK)
	}
	got := map[string]bool{}
	files, _ := filepath.Glob(filepath.Join(s.root, "f*.txt"))
	for _, f := range files {
		b, _ := os.ReadFile(f)
		got[string(b)] = true
	}
	if !got["one"] || !got["two"] || !got["current"] {
		t.Fatalf("contents after restore: %v (%v)", got, files)
	}
}

func TestTrashRestoreKeepsReadOnly(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "p/ro/a.txt", "a")
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"p/ro","readOnly":true}`), http.StatusNoContent)
	status(t, a.do("DELETE", "/admin/api/fs/file?path=p", ""), http.StatusNoContent)
	if len(s.state.readOnlyFolders()) != 0 {
		t.Fatal("deleted folder still listed as read-only")
	}
	putFile(t, s, "p/new.txt", "taken") // the original path is taken: restored under another name
	resp := a.do("POST", "/admin/api/trash/restore", `{"id":"`+s.trash.list()[0].ID+`"}`)
	status(t, resp, http.StatusOK)
	var r struct{ Path string }
	json.NewDecoder(resp.Body).Decode(&r)
	if got := s.state.readOnlyFolders(); len(got) != 1 || got[0] != r.Path+"/ro" {
		t.Fatalf("read-only after restore to %q = %v", r.Path, got)
	}
	status(t, do(t, ts, "DELETE", "/api/file?path="+url.QueryEscape(r.Path+"/ro/a.txt"), nil), http.StatusForbidden)
}

// A file that appears at the target after the free-name check (a client upload) is not replaced.
func TestRenameNoReplaceKeepsNewFile(t *testing.T) {
	dir := t.TempDir()
	src, dst := filepath.Join(dir, "src"), filepath.Join(dir, "dst")
	os.WriteFile(src, []byte("restored"), 0o644)
	os.WriteFile(dst, []byte("uploaded"), 0o644)
	if err := renameNoReplace(src, dst); !errors.Is(err, fs.ErrExist) {
		t.Fatalf("err = %v, want ErrExist", err)
	}
	if b, _ := os.ReadFile(dst); string(b) != "uploaded" {
		t.Fatalf("dst = %q", b)
	}
	if b, _ := os.ReadFile(src); string(b) != "restored" {
		t.Fatalf("src = %q", b)
	}
	os.Remove(dst)
	if err := renameNoReplace(src, dst); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Lstat(src); !errors.Is(err, fs.ErrNotExist) {
		t.Fatalf("src still there: %v", err)
	}
	if b, _ := os.ReadFile(dst); string(b) != "restored" {
		t.Fatalf("dst = %q", b)
	}
}
