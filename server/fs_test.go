package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func TestAdminDownloadAndUpload(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "d/报告 1.txt", "hello")
	resp := a.do("GET", "/admin/api/fs/file?path=d/%E6%8A%A5%E5%91%8A%201.txt&download=1", "")
	status(t, resp, http.StatusOK)
	if cd := resp.Header.Get("Content-Disposition"); cd != "attachment; filename*=UTF-8''%E6%8A%A5%E5%91%8A%201.txt" {
		t.Fatalf("Content-Disposition = %q", cd)
	}
	if b, _ := io.ReadAll(resp.Body); string(b) != "hello" {
		t.Fatalf("body %q", b)
	}
	// Client downloads carry no Content-Disposition.
	if cd := do(t, ts, "GET", "/api/file?path=d/%E6%8A%A5%E5%91%8A%201.txt", nil).Header.Get("Content-Disposition"); cd != "" {
		t.Fatalf("client download Content-Disposition = %q", cd)
	}

	// Chunked upload through the panel routes (what the web page does).
	data := bytes.Repeat([]byte("p"), 10000)
	resp = a.do("POST", "/admin/api/fs/upload/init", `{"path":"up/x.bin","size":10000,"mtime":1700000000000}`)
	status(t, resp, http.StatusOK)
	var init struct{ ID string }
	json.NewDecoder(resp.Body).Decode(&init)
	for off := 0; off < len(data); off += 4000 {
		req, _ := http.NewRequest("PUT", fmt.Sprintf("%s/admin/api/fs/upload/%s?offset=%d", ts.URL, init.ID, off), bytes.NewReader(data[off:min(off+4000, len(data))]))
		req.Header.Set("X-Requested-With", "filecopy")
		req.AddCookie(&http.Cookie{Name: sessionCookie, Value: a.cookie})
		r, _ := http.DefaultClient.Do(req)
		status(t, r, http.StatusNoContent)
	}
	status(t, a.do("POST", "/admin/api/fs/upload/"+init.ID+"/complete", `{"baseMtime":0}`), http.StatusOK)
	if b, _ := os.ReadFile(filepath.Join(s.root, "up", "x.bin")); !bytes.Equal(b, data) {
		t.Fatal("upload content")
	}
	// The panel is not a device: no traffic recorded for it.
	for _, d := range s.state.devices() {
		if d.ID != testDevice {
			t.Fatalf("unexpected device %+v", d)
		}
	}
}

func TestCleanRelTrailingDotOrSpace(t *testing.T) {
	for _, p := range []string{"A.", "A ", "A./b", "a/B. /c"} {
		_, err := cleanRel(p)
		if runtime.GOOS == "windows" && err == nil {
			t.Errorf("cleanRel(%q) should fail on Windows (the name would refer to a different item)", p)
		}
	}
	if _, err := cleanRel("a.b/c.txt"); err != nil {
		t.Fatal(err)
	}
}

func TestIsShortName(t *testing.T) {
	for _, p := range []string{"FILECO~1", "READON~2", "REPORT~1.TXT", "a~10", "只读资~1"} {
		if !isShortName(p) {
			t.Errorf("isShortName(%q) = false, want true", p)
		}
	}
	for _, p := range []string{"a~b", "backup~", "v1.0~beta.txt", "~1x", "Backup~2023", "报表~3.xlsx", "a~1.tar.gz"} {
		if isShortName(p) {
			t.Errorf("isShortName(%q) = true, want false", p)
		}
	}
}

// Short names are rejected only when they alias a different item on disk.
func TestShortNameAliases(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("8.3 aliases exist only on Windows")
	}
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "Readonly folder/a.txt", "a")
	if _, err := os.Lstat(filepath.Join(s.root, "READON~1")); err != nil {
		t.Skip("8.3 name generation is off on this volume")
	}
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"Readonly folder","readOnly":true}`), http.StatusNoContent)
	status(t, do(t, ts, "DELETE", "/api/file?path=READON~1/a.txt", nil), http.StatusBadRequest)
	status(t, do(t, ts, "GET", "/api/file?path=FILECO~1/state.json", nil), http.StatusBadRequest)
	// Nothing has the name yet: creating it makes a real name, usable afterwards.
	status(t, do(t, ts, "POST", "/api/mkdir", strings.NewReader(`{"path":"NEWDIR~1"}`)), http.StatusNoContent)
	if _, _, err := s.safeJoin("NEWDIR~1"); err != nil {
		t.Fatal(err)
	}
}

func TestTreeListsRealShortFormNames(t *testing.T) {
	s, ts := newTestServer(t)
	putFile(t, s, "ok.txt", "a")
	putFile(t, s, "REPORT~1.TXT", "b") // its own name, not an alias: clients must not lose it
	resp := do(t, ts, "GET", "/api/tree", nil)
	status(t, resp, http.StatusOK)
	var entries []Entry
	json.NewDecoder(resp.Body).Decode(&entries)
	if len(entries) != 2 {
		t.Fatalf("tree = %+v, want ok.txt and REPORT~1.TXT", entries)
	}
	resp = do(t, ts, "GET", "/api/file?path=REPORT~1.TXT", nil)
	status(t, resp, http.StatusOK)
	if b, _ := io.ReadAll(resp.Body); string(b) != "b" {
		t.Fatalf("body %q", b)
	}
}

func TestReadOnlyTrailingDotBypass(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("Windows path normalization")
	}
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "A/B/f.txt", "f")
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"A/B","readOnly":true}`), http.StatusNoContent)
	status(t, do(t, ts, "DELETE", "/api/file?path=A.", nil), http.StatusBadRequest)
	status(t, do(t, ts, "POST", "/api/move", strings.NewReader(`{"from":"A ","to":"X"}`)), http.StatusBadRequest)
	if _, err := os.Stat(filepath.Join(s.root, "A", "B", "f.txt")); err != nil {
		t.Fatal("read-only folder removed")
	}
}
