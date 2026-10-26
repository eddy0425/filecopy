package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

const testToken = "test-token-0123456789"

func newTestServer(t *testing.T) (*Server, *httptest.Server) {
	t.Helper()
	s, err := NewServer(t.TempDir(), testToken)
	if err != nil {
		t.Fatal(err)
	}
	ts := httptest.NewServer(s.Handler())
	t.Cleanup(func() {
		ts.Close()
		s.uploads.closeAll()
	})
	return s, ts
}

func do(t *testing.T, ts *httptest.Server, method, url string, body io.Reader, hdr ...string) *http.Response {
	t.Helper()
	req, _ := http.NewRequest(method, ts.URL+url, body)
	req.Header.Set("Authorization", "Bearer "+testToken)
	for i := 0; i+1 < len(hdr); i += 2 {
		req.Header.Set(hdr[i], hdr[i+1])
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	return resp
}

func TestCleanRel(t *testing.T) {
	ok := map[string]string{
		"a/b.txt":      "a/b.txt",
		"/a/b.txt":     "a/b.txt",
		`a\b.txt`:      "a/b.txt",
		"../../etc/x":  "etc/x",
		"a/../../b":    "b",
		"./a/./b/../c": "a/c",
	}
	for in, want := range ok {
		got, err := cleanRel(in)
		if err != nil || got != want {
			t.Errorf("cleanRel(%q) = %q, %v; want %q", in, got, err, want)
		}
	}
	for _, bad := range []string{"", "/", "..", ".uploads/x", ".UPLOADS", "a\x00b"} {
		if _, err := cleanRel(bad); err == nil {
			t.Errorf("cleanRel(%q) should fail", bad)
		}
	}
}

func TestAuth(t *testing.T) {
	_, ts := newTestServer(t)
	for _, h := range []string{"", "Bearer wrong", "Bearer " + testToken + "x"} {
		req, _ := http.NewRequest("GET", ts.URL+"/api/tree", nil)
		if h != "" {
			req.Header.Set("Authorization", h)
		}
		resp, _ := http.DefaultClient.Do(req)
		if resp.StatusCode != http.StatusUnauthorized {
			t.Errorf("auth %q: got %d", h, resp.StatusCode)
		}
	}
}

func upload(t *testing.T, ts *httptest.Server, path string, data []byte, chunk int, baseMtime int64) Entry {
	t.Helper()
	initBody, _ := json.Marshal(map[string]any{"path": path, "size": len(data), "mtime": 1700000000000})
	resp := do(t, ts, "POST", "/api/upload/init", bytes.NewReader(initBody))
	var init struct{ ID string }
	json.NewDecoder(resp.Body).Decode(&init)

	// Send chunks in parallel, in reverse order, to exercise WriteAt.
	var wg sync.WaitGroup
	for off := (len(data) - 1) / chunk * chunk; off >= 0; off -= chunk {
		end := min(off+chunk, len(data))
		wg.Add(1)
		go func(off, end int) {
			defer wg.Done()
			r := do(t, ts, "PUT", fmt.Sprintf("/api/upload/%s?offset=%d", init.ID, off), bytes.NewReader(data[off:end]))
			if r.StatusCode != http.StatusNoContent {
				t.Errorf("chunk %d: %d", off, r.StatusCode)
			}
		}(off, end)
	}
	wg.Wait()

	body, _ := json.Marshal(map[string]any{"baseMtime": baseMtime})
	resp = do(t, ts, "POST", "/api/upload/"+init.ID+"/complete", bytes.NewReader(body))
	if resp.StatusCode != http.StatusOK {
		b, _ := io.ReadAll(resp.Body)
		t.Fatalf("complete: %d %s", resp.StatusCode, b)
	}
	var e Entry
	json.NewDecoder(resp.Body).Decode(&e)
	return e
}

func TestUploadDownloadRange(t *testing.T) {
	s, ts := newTestServer(t)
	data := bytes.Repeat([]byte("0123456789"), 1000) // 10000 bytes
	e := upload(t, ts, "dir/sub/file.bin", data, 3000, 0)
	if e.Size != 10000 || e.Mtime != 1700000000000 {
		t.Fatalf("entry = %+v", e)
	}

	resp := do(t, ts, "GET", "/api/file?path=dir/sub/file.bin", nil)
	got, _ := io.ReadAll(resp.Body)
	if !bytes.Equal(got, data) {
		t.Fatal("download mismatch")
	}
	resp = do(t, ts, "GET", "/api/file?path=dir/sub/file.bin", nil, "Range", "bytes=5-14")
	got, _ = io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusPartialContent || string(got) != "5678901234" {
		t.Fatalf("range: %d %q", resp.StatusCode, got)
	}

	resp = do(t, ts, "GET", "/api/tree", nil)
	var tree []Entry
	json.NewDecoder(resp.Body).Decode(&tree)
	if len(tree) != 3 { // dir, dir/sub, dir/sub/file.bin; .uploads hidden
		t.Fatalf("tree = %+v", tree)
	}

	// Traversal is clamped to root.
	resp = do(t, ts, "GET", "/api/file?path=../../../dir/sub/file.bin", nil)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("clamped path: %d", resp.StatusCode)
	}
	resp = do(t, ts, "GET", "/api/file?path=.uploads/x", nil)
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf(".uploads: %d", resp.StatusCode)
	}
	if _, err := os.Stat(filepath.Join(s.root, "dir", "sub", "file.bin")); err != nil {
		t.Fatal(err)
	}
}

func TestIncompleteUploadRejected(t *testing.T) {
	_, ts := newTestServer(t)
	initBody := `{"path":"x.bin","size":100}`
	resp := do(t, ts, "POST", "/api/upload/init", strings.NewReader(initBody))
	var init struct{ ID string }
	json.NewDecoder(resp.Body).Decode(&init)
	do(t, ts, "PUT", "/api/upload/"+init.ID+"?offset=0", bytes.NewReader(make([]byte, 50)))
	resp = do(t, ts, "POST", "/api/upload/"+init.ID+"/complete", strings.NewReader("{}"))
	if resp.StatusCode != http.StatusConflict {
		t.Fatalf("got %d", resp.StatusCode)
	}
}

func TestConflictKeepsOldVersion(t *testing.T) {
	s, ts := newTestServer(t)
	first := upload(t, ts, "a.txt", []byte("v1"), 10, 0)
	// Client knew v1: plain overwrite.
	second := upload(t, ts, "a.txt", []byte("v2"), 10, first.Mtime)
	// Stale client (base 0): v2 is preserved as conflict copy.
	upload(t, ts, "a.txt", []byte("v3"), 10, 0)
	_ = second

	files, _ := filepath.Glob(filepath.Join(s.root, "a (conflict *).txt"))
	if len(files) != 1 {
		t.Fatalf("conflict copies: %v", files)
	}
	old, _ := os.ReadFile(files[0])
	cur, _ := os.ReadFile(filepath.Join(s.root, "a.txt"))
	if string(old) != "v2" || string(cur) != "v3" {
		t.Fatalf("old=%q cur=%q", old, cur)
	}
}

func TestMoveDeleteMkdir(t *testing.T) {
	s, ts := newTestServer(t)
	upload(t, ts, "a.txt", []byte("hi"), 10, 0)
	resp := do(t, ts, "POST", "/api/move", strings.NewReader(`{"from":"a.txt","to":"d/b.txt"}`))
	if resp.StatusCode != http.StatusNoContent {
		t.Fatalf("move %d", resp.StatusCode)
	}
	upload(t, ts, "c.txt", []byte("keep"), 10, 0)
	resp = do(t, ts, "POST", "/api/move", strings.NewReader(`{"from":"d/b.txt","to":"c.txt"}`))
	if resp.StatusCode != http.StatusConflict {
		t.Fatalf("move onto existing %d", resp.StatusCode)
	}
	if b, _ := os.ReadFile(filepath.Join(s.root, "c.txt")); string(b) != "keep" {
		t.Fatalf("c.txt overwritten: %q", b)
	}
	resp = do(t, ts, "POST", "/api/move", strings.NewReader(`{"from":"missing.txt","to":"c.txt"}`))
	if resp.StatusCode != http.StatusNotFound {
		t.Fatalf("move missing source %d", resp.StatusCode)
	}
	resp = do(t, ts, "POST", "/api/mkdir", strings.NewReader(`{"path":"e/f"}`))
	if resp.StatusCode != http.StatusNoContent {
		t.Fatalf("mkdir %d", resp.StatusCode)
	}
	resp = do(t, ts, "DELETE", "/api/file?path=d", nil)
	if resp.StatusCode != http.StatusNoContent {
		t.Fatalf("delete %d", resp.StatusCode)
	}
	if _, err := os.Stat(filepath.Join(s.root, "d")); !os.IsNotExist(err) {
		t.Fatal("d should be gone")
	}
	if _, err := os.Stat(filepath.Join(s.root, "e", "f")); err != nil {
		t.Fatal(err)
	}
}

func TestCoversAll(t *testing.T) {
	if !coversAll(map[int64]int64{0: 5, 5: 5}, 10) || coversAll(map[int64]int64{0: 5, 6: 4}, 10) || !coversAll(nil, 0) {
		t.Fatal("coversAll")
	}
}
