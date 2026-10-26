package main

import (
	"bytes"
	"compress/gzip"
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
	"time"
)

const (
	testToken  = "test-token-0123456789"
	testDevice = "XZT1-DB1"
)

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
	req.Header.Set("X-Equipment-Id", testDevice)
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

// A rejected completion ends the session: its temp file is removed now rather than after a day,
// and asking again is "not found" rather than a failure on the closed file.
func TestUploadRejectedAtCompleteIsDiscarded(t *testing.T) {
	s, ts := newTestServer(t)
	putFile(t, s, "ro/.keep", "")
	resp := do(t, ts, "POST", "/api/upload/init", strings.NewReader(`{"path":"ro/n.txt","size":1}`))
	var init struct{ ID string }
	json.NewDecoder(resp.Body).Decode(&init)
	tmp := s.uploads.get(init.ID).tmpPath
	status(t, do(t, ts, "PUT", "/api/upload/"+init.ID+"?offset=0", strings.NewReader("n")), http.StatusNoContent)
	if err := s.state.setReadOnly("ro", true); err != nil {
		t.Fatal(err)
	}
	status(t, do(t, ts, "POST", "/api/upload/"+init.ID+"/complete", strings.NewReader("{}")), http.StatusForbidden)
	if s.uploads.get(init.ID) != nil {
		t.Error("session kept")
	}
	if _, err := os.Stat(tmp); !os.IsNotExist(err) {
		t.Errorf("temp file kept: %v", err)
	}
	status(t, do(t, ts, "POST", "/api/upload/"+init.ID+"/complete", strings.NewReader("{}")), http.StatusNotFound)
}

func TestUploadCompleteTwice(t *testing.T) {
	_, ts := newTestServer(t)
	resp := do(t, ts, "POST", "/api/upload/init", strings.NewReader(`{"path":"x.txt","size":1}`))
	var init struct{ ID string }
	json.NewDecoder(resp.Body).Decode(&init)
	status(t, do(t, ts, "PUT", "/api/upload/"+init.ID+"?offset=0", strings.NewReader("x")), http.StatusNoContent)
	status(t, do(t, ts, "POST", "/api/upload/"+init.ID+"/complete", strings.NewReader("{}")), http.StatusOK)
	status(t, do(t, ts, "POST", "/api/upload/"+init.ID+"/complete", strings.NewReader("{}")), http.StatusNotFound)
}

// A completion holds its session's lock while it removes the session; the hourly cleanup must not
// wait for that lock while holding the manager's, or both (and every later upload) hang.
func TestUploadCleanupDoesNotDeadlockWithComplete(t *testing.T) {
	s, ts := newTestServer(t)
	resp := do(t, ts, "POST", "/api/upload/init", strings.NewReader(`{"path":"x.txt","size":1}`))
	var init struct{ ID string }
	json.NewDecoder(resp.Body).Decode(&init)
	sess := s.uploads.get(init.ID)

	sess.mu.Lock() // a completion in progress
	cleaned := make(chan struct{})
	go func() {
		s.uploads.cleanupStale(24 * time.Hour)
		close(cleaned)
	}()
	// Give the cleanup time to reach the session (with the bug it then holds the manager for good).
	for deadline := time.Now().Add(200 * time.Millisecond); time.Now().Before(deadline); {
		if !s.uploads.mu.TryLock() {
			break
		}
		s.uploads.mu.Unlock()
	}
	got := make(chan *uploadSession)
	go func() { got <- s.uploads.get(init.ID) }() // what the completion does next
	select {
	case <-got:
	case <-time.After(5 * time.Second):
		sess.mu.Unlock() // let it all finish, so the test reports instead of hanging
		<-got
		t.Fatal("deadlock: the cleanup holds the manager while waiting for the session")
	}
	sess.mu.Unlock()
	<-cleaned
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
	if items := s.trash.list(); len(items) != 1 || items[0].Path != "d" || !items[0].IsDir || items[0].DeletedBy != testDevice {
		t.Fatalf("trash = %+v", items)
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

func TestTreeGzipAndReadOnly(t *testing.T) {
	s, ts := newTestServer(t)
	putFile(t, s, "p/RO/a.txt", "A")
	putFile(t, s, "x.txt", "X")
	if err := s.state.setReadOnly("p/ro", true); err != nil { // case-insensitive match
		t.Fatal(err)
	}
	want := map[string]bool{"p": false, "p/RO": true, "p/RO/a.txt": true, "x.txt": false}
	check := func(r io.Reader) {
		t.Helper()
		var tree []Entry
		if err := json.NewDecoder(r).Decode(&tree); err != nil {
			t.Fatal(err)
		}
		got := map[string]bool{}
		for _, e := range tree {
			got[e.Path] = e.ReadOnly
		}
		if fmt.Sprint(got) != fmt.Sprint(want) {
			t.Fatalf("tree read-only = %v, want %v", got, want)
		}
	}

	// Setting Accept-Encoding by hand turns off the transport's transparent decompression.
	resp := do(t, ts, "GET", "/api/tree", nil, "Accept-Encoding", "gzip")
	if resp.Header.Get("Content-Encoding") != "gzip" {
		t.Fatalf("Content-Encoding = %q", resp.Header.Get("Content-Encoding"))
	}
	gz, err := gzip.NewReader(resp.Body)
	if err != nil {
		t.Fatal(err)
	}
	check(gz)

	resp = do(t, ts, "GET", "/api/tree", nil, "Accept-Encoding", "identity")
	if resp.Header.Get("Content-Encoding") != "" {
		t.Fatalf("uncompressed request got Content-Encoding %q", resp.Header.Get("Content-Encoding"))
	}
	check(resp.Body)
}

func TestAcceptsGzip(t *testing.T) {
	for h, want := range map[string]bool{
		"": false, "gzip": true, "deflate, gzip, br": true, "GZIP;q=0.5": true, "gzip;q=0": false, "gzip; q=0.000": false, "br": false,
	} {
		r := httptest.NewRequest("GET", "/", nil)
		r.Header.Set("Accept-Encoding", h)
		if acceptsGzip(r) != want {
			t.Errorf("acceptsGzip(%q) = %v", h, !want)
		}
	}
}

func TestWorthLogging(t *testing.T) {
	for _, c := range []struct {
		method, url, rng string
		want             bool
	}{
		{"PUT", "/api/upload/x?offset=0", "", false},
		{"GET", "/api/ping", "", false},
		{"GET", "/admin/api/status", "", false},
		{"GET", "/api/file?path=a", "", true},
		{"GET", "/api/file?path=a", "bytes=0-4194303", true},
		{"GET", "/api/file?path=a", "bytes=4194304-8388607", false},
		{"GET", "/admin/api/fs/file?path=a&download=1", "bytes=100-", false},
		{"POST", "/api/move", "", true},
		{"GET", "/api/tree", "", true},
	} {
		r := httptest.NewRequest(c.method, c.url, nil)
		if c.rng != "" {
			r.Header.Set("Range", c.rng)
		}
		if worthLogging(r) != c.want {
			t.Errorf("%s %s (Range %q) logged = %v", c.method, c.url, c.rng, !c.want)
		}
	}
}
