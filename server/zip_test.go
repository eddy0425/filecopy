package main

import (
	"archive/zip"
	"bytes"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// zipContents downloads a zip from the panel and returns name -> content ("/" suffix for folders).
func zipContents(t *testing.T, a *admin, paths ...string) (map[string]string, *http.Response) {
	t.Helper()
	q := url.Values{"path": paths}
	resp := a.do("GET", "/admin/api/fs/zip?"+q.Encode(), "")
	status(t, resp, http.StatusOK)
	b, _ := io.ReadAll(resp.Body)
	zr, err := zip.NewReader(bytes.NewReader(b), int64(len(b)))
	if err != nil {
		t.Fatal(err)
	}
	got := map[string]string{}
	for _, f := range zr.File {
		rc, err := f.Open()
		if err != nil {
			t.Fatal(err)
		}
		c, _ := io.ReadAll(rc)
		rc.Close()
		got[f.Name] = string(c)
	}
	return got, resp
}

func TestZipFolder(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "p/报告/a.txt", "A")
	putFile(t, s, "p/b.bin", string(bytes.Repeat([]byte{0, 1, 2}, 50000)))
	os.MkdirAll(filepath.Join(s.root, "p", "empty"), 0o755)
	putFile(t, s, "other.txt", "not included")

	got, resp := zipContents(t, a, "p")
	want := map[string]string{
		"p/": "", "p/报告/": "", "p/报告/a.txt": "A", "p/empty/": "",
		"p/b.bin": string(bytes.Repeat([]byte{0, 1, 2}, 50000)),
	}
	if len(got) != len(want) {
		t.Fatalf("%d entries, want %d", len(got), len(want))
	}
	for k, v := range want {
		if c, ok := got[k]; !ok || c != v {
			t.Fatalf("%s: present %v, %d bytes, want %d", k, ok, len(c), len(v))
		}
	}
	if cd := resp.Header.Get("Content-Disposition"); cd != "attachment; filename*=UTF-8''p.zip" {
		t.Fatalf("Content-Disposition = %q", cd)
	}
}

func TestZipSeveralItems(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "d/x.txt", "X")
	putFile(t, s, "d/sub/y.txt", "Y")
	putFile(t, s, "top.txt", "T")

	got, resp := zipContents(t, a, "d/x.txt", "d/sub", "top.txt", "top.txt")
	want := map[string]string{"x.txt": "X", "sub/": "", "sub/y.txt": "Y", "top.txt": "T"}
	if len(got) != len(want) {
		t.Fatalf("entries = %v", got)
	}
	for k, v := range want {
		if got[k] != v {
			t.Fatalf("%s = %q, want %q", k, got[k], v)
		}
	}
	if cd := resp.Header.Get("Content-Disposition"); !strings.HasPrefix(cd, "attachment; filename*=UTF-8''FileCopy-") {
		t.Fatalf("Content-Disposition = %q", cd)
	}
}

func TestZipAbortsOnReadError(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "d/a.txt", "A")
	putFile(t, s, "d/b.txt", "B")
	makeUnreadable(t, filepath.Join(s.root, "d", "b.txt"))

	// The connection is cut: before the response arrives when nothing was flushed yet (as here,
	// the zip is tiny), otherwise while reading the body. Either way it must not end normally.
	req, _ := http.NewRequest("GET", ts.URL+"/admin/api/fs/zip?path=d", nil)
	req.AddCookie(&http.Cookie{Name: sessionCookie, Value: a.cookie})
	resp, err := http.DefaultClient.Do(req)
	if err == nil {
		_, err = io.ReadAll(resp.Body)
		resp.Body.Close()
	}
	if err == nil {
		t.Fatal("a truncated zip was delivered as a complete response")
	}
}

func TestZipRejectsBadRequests(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "a.txt", "A")
	for _, q := range []string{"", "?path=.filecopy", "?path=.uploads", "?path=a.txt&path=.filecopy/state.json"} {
		status(t, a.do("GET", "/admin/api/fs/zip"+q, ""), http.StatusBadRequest)
	}
	status(t, a.do("GET", "/admin/api/fs/zip?path=missing", ""), http.StatusNotFound)
	status(t, (&admin{t: t, ts: ts}).do("GET", "/admin/api/fs/zip?path=a.txt", ""), http.StatusUnauthorized)
	status(t, do(t, ts, "GET", "/api/fs/zip?path=a.txt", nil), http.StatusNotFound) // not on the client API

	// A symlink is not part of the file API (it could point outside the data dir).
	if err := os.Symlink(filepath.Join(s.root, "a.txt"), filepath.Join(s.root, "link.txt")); err == nil {
		status(t, a.do("GET", "/admin/api/fs/zip?path=link.txt", ""), http.StatusBadRequest)
	}
}
