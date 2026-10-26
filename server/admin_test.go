package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

// fakeClock lets tests control TOTP time steps, lockouts and the recycle bin's age.
type fakeClock struct {
	mu sync.Mutex
	t  time.Time
}

func (c *fakeClock) now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.t
}

func (c *fakeClock) add(d time.Duration) {
	c.mu.Lock()
	c.t = c.t.Add(d)
	c.mu.Unlock()
}

func withClock(s *Server) *fakeClock {
	c := &fakeClock{t: time.Unix(1800000000, 0)}
	s.now = c.now
	return c
}

func status(t *testing.T, resp *http.Response, want int) {
	t.Helper()
	if resp.StatusCode != want {
		b, _ := io.ReadAll(resp.Body)
		t.Fatalf("%s %s: got %d (%s), want %d", resp.Request.Method, resp.Request.URL.Path, resp.StatusCode, strings.TrimSpace(string(b)), want)
	}
}

// doAs is do() with another EquipmentId and source IP (X-Forwarded-For is trusted from loopback).
func doAs(t *testing.T, ts *httptest.Server, device, ip, method, url string, body io.Reader) *http.Response {
	t.Helper()
	req, _ := http.NewRequest(method, ts.URL+url, body)
	req.Header.Set("Authorization", "Bearer "+testToken)
	if device != "" {
		req.Header.Set("X-Equipment-Id", device)
	}
	if ip != "" {
		req.Header.Set("X-Forwarded-For", ip)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	return resp
}

type admin struct {
	t      *testing.T
	ts     *httptest.Server
	cookie string
}

func (a *admin) do(method, url, body string) *http.Response {
	a.t.Helper()
	var r io.Reader
	if body != "" {
		r = strings.NewReader(body)
	}
	req, _ := http.NewRequest(method, a.ts.URL+url, r)
	req.Header.Set("X-Requested-With", "filecopy")
	if a.cookie != "" {
		req.AddCookie(&http.Cookie{Name: sessionCookie, Value: a.cookie})
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		a.t.Fatal(err)
	}
	return resp
}

func login(t *testing.T, ts *httptest.Server, password, code string) (*admin, *http.Response) {
	t.Helper()
	body, _ := json.Marshal(map[string]string{"password": password, "code": code})
	a := &admin{t: t, ts: ts}
	resp := a.do("POST", "/admin/api/login", string(body))
	for _, c := range resp.Cookies() {
		if c.Name == sessionCookie {
			a.cookie = c.Value
		}
	}
	return a, resp
}

func mustLogin(t *testing.T, ts *httptest.Server) *admin {
	t.Helper()
	a, resp := login(t, ts, testToken, "")
	status(t, resp, http.StatusNoContent)
	return a
}

func codeAt(t *testing.T, secret string, now time.Time) string {
	key, err := b32.DecodeString(secret)
	if err != nil {
		t.Fatal(err)
	}
	return totpCode(key, now.Unix()/totpStep)
}

// wrongCode returns a code matching none of the steps accepted at now.
func wrongCode(t *testing.T, secret string, now time.Time) string {
	for _, c := range []string{"000000", "999999", "123456"} {
		if matchTOTP(secret, c, now) < 0 {
			return c
		}
	}
	t.Fatal("no wrong code")
	return ""
}

func putFile(t *testing.T, s *Server, rel, content string) {
	t.Helper()
	p := filepath.Join(s.root, filepath.FromSlash(rel))
	os.MkdirAll(filepath.Dir(p), 0o755)
	if err := os.WriteFile(p, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

// ---------- TOTP, QR ----------

func TestTOTPVectors(t *testing.T) {
	key := []byte("12345678901234567890") // RFC 6238 appendix B, SHA1
	for ts, want := range map[int64]string{
		59: "287082", 1111111109: "081804", 1111111111: "050471", 1234567890: "005924", 2000000000: "279037",
	} {
		if got := totpCode(key, ts/totpStep); got != want {
			t.Errorf("t=%d: %s, want %s", ts, got, want)
		}
	}
	secret := b32.EncodeToString(key)
	now := time.Unix(1111111111, 0)
	for _, d := range []time.Duration{-30 * time.Second, 0, 30 * time.Second} {
		if matchTOTP(secret, codeAt(t, secret, now.Add(d)), now) < 0 {
			t.Errorf("skew %v rejected", d)
		}
	}
	if matchTOTP(secret, codeAt(t, secret, now.Add(90*time.Second)), now) >= 0 {
		t.Error("code 3 steps away accepted")
	}

	st, _ := loadState(filepath.Join(t.TempDir(), "s.json"), func() time.Time { return now })
	code := codeAt(t, secret, now)
	if st.verifyTOTP(secret, code) != totpOK || st.verifyTOTP(secret, code) != totpReused {
		t.Error("code should be accepted once, then rejected as a replay")
	}
	// The previous step's code (within the skew) is a different code: usable once as well.
	if prev := codeAt(t, secret, now.Add(-30*time.Second)); st.verifyTOTP(secret, prev) != totpOK || st.verifyTOTP(secret, prev) != totpReused {
		t.Error("previous-step code should be accepted once")
	}
	if st.verifyTOTP(secret, wrongCode(t, secret, now)) != totpWrong {
		t.Error("wrong code accepted")
	}
	// A new secret (TOTP disabled and enabled again) is not blocked by steps used with the old one.
	other := newTOTPSecret()
	if st.verifyTOTP(other, codeAt(t, other, now)) != totpOK {
		t.Error("new secret rejected as replay")
	}
}

func TestQR(t *testing.T) {
	// "HELLO WORLD" 1-M data codewords and their EC codewords (thonky.com QR tutorial).
	data := []byte{32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17}
	want := []byte{196, 35, 39, 119, 235, 215, 231, 226, 93, 23}
	if got := rsRemainder(data, 10); !bytes.Equal(got, want) {
		t.Fatalf("rs = %v, want %v", got, want)
	}
	formats := []string{"101010000010010", "101000100100101", "101111001111100", "101101101001011",
		"100010111111001", "100000011001110", "100111110010111", "100101010100000"}
	for mask, f := range formats {
		if got := fmt.Sprintf("%015b", qrFormatBits(mask)); got != f {
			t.Errorf("format M/%d = %s, want %s", mask, got, f)
		}
	}
	for ver, v := range map[int]int{7: 0x07C94, 8: 0x085BC, 9: 0x09A99, 10: 0x0A4D3} {
		if got := qrVersionBits(ver); got != v {
			t.Errorf("version %d bits = %x, want %x", ver, got, v)
		}
	}
	uri := totpURI(newTOTPSecret()) // 79 bytes: version 5
	q, err := encodeQR([]byte(uri))
	if err != nil || q.size != 37 {
		t.Fatalf("size %v, %v", q, err)
	}
	if !strings.HasPrefix(q.svg(), "<svg") {
		t.Fatal("svg")
	}
	if _, err := encodeQR(make([]byte, 300)); err == nil {
		t.Fatal("too long should fail")
	}
}

// ---------- connections, devices ----------

func TestEquipmentIDRequired(t *testing.T) {
	_, ts := newTestServer(t)
	status(t, doAs(t, ts, "", "", "GET", "/api/ping", nil), http.StatusBadRequest)
	resp := do(t, ts, "GET", "/api/ping", nil)
	status(t, resp, http.StatusOK)
	var ping struct {
		Status      string
		TOTP        bool
		EquipmentID string
	}
	json.NewDecoder(resp.Body).Decode(&ping)
	if ping.Status != "ok" || ping.TOTP || ping.EquipmentID != testDevice {
		t.Fatalf("ping = %+v", ping)
	}
}

func TestPauseCutsTransfers(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	const size = 256 << 20 // larger than socket buffers, sparse so it costs nothing
	f, _ := os.Create(filepath.Join(s.root, "big.bin"))
	f.Truncate(size)
	f.Close()

	resp := do(t, ts, "GET", "/api/file?path=big.bin", nil)
	io.ReadFull(resp.Body, make([]byte, 1<<20))
	status(t, a.do("POST", "/admin/api/connections", `{"accept":false}`), http.StatusNoContent)
	n, err := io.Copy(io.Discard, resp.Body)
	if err == nil || n+(1<<20) >= size {
		t.Fatalf("download not cut: read %d more, err %v", n, err)
	}

	status(t, do(t, ts, "GET", "/api/ping", nil), http.StatusServiceUnavailable)
	status(t, a.do("GET", "/admin/api/fs/tree", ""), http.StatusOK) // the panel keeps working

	st, err := loadState(statePath(s.root), time.Now)
	if err != nil || !st.st.Paused {
		t.Fatalf("paused not persisted: %v", err)
	}
	status(t, a.do("POST", "/admin/api/connections", `{"accept":true}`), http.StatusNoContent)
	status(t, do(t, ts, "GET", "/api/ping", nil), http.StatusOK)
}

func deviceStatus(t *testing.T, s *Server, id string) DeviceStatus {
	t.Helper()
	for _, d := range s.state.devices() {
		if d.ID == id {
			return d
		}
	}
	t.Fatalf("device %s not found in %+v", id, s.state.devices())
	return DeviceStatus{}
}

func TestTrafficCounting(t *testing.T) {
	s, ts := newTestServer(t)
	data := bytes.Repeat([]byte("x"), 100000)
	upload(t, ts, "a.bin", data, 30000, 0)
	resp := do(t, ts, "GET", "/api/file?path=a.bin", nil)
	io.Copy(io.Discard, resp.Body)
	resp.Body.Close()

	d := deviceStatus(t, s, testDevice)
	// Chunk bodies plus a few small JSON requests / responses.
	if d.BytesUp < 100000 || d.BytesUp > 101000 || d.BytesDown < 100000 || d.BytesDown > 101000 {
		t.Fatalf("traffic = %+v", d)
	}
	if !d.Online || d.LastIP != "127.0.0.1" || d.UpRate == 0 {
		t.Fatalf("status = %+v", d)
	}

	if err := s.state.flush(); err != nil {
		t.Fatal(err)
	}
	st, _ := loadState(statePath(s.root), time.Now)
	if st.st.Devices[testDevice].BytesDown != d.BytesDown {
		t.Fatal("traffic not persisted")
	}

	a := mustLogin(t, ts)
	status(t, a.do("POST", "/admin/api/devices/reset", `{"id":"`+testDevice+`"}`), http.StatusNoContent)
	if d := deviceStatus(t, s, testDevice); d.BytesUp != 0 || d.BytesDown != 0 {
		t.Fatalf("after reset %+v", d)
	}
	status(t, a.do("POST", "/admin/api/devices/reset", `{"id":"nope"}`), http.StatusNotFound)
	status(t, a.do("POST", "/admin/api/devices/reset", `{"all":true}`), http.StatusNoContent)
}

func TestSpeedMeter(t *testing.T) {
	var m speedMeter
	m.add(100, 500, 0)
	m.add(101, 500, 1000)
	m.add(104, 0, 4000)
	if up, down := m.rates(104); up != 200 || down != 1000 {
		t.Fatalf("rates = %d %d", up, down)
	}
	if up, down := m.rates(106); up != 0 || down != 800 { // 100 and 101 fell out of the window
		t.Fatalf("rates later = %d %d", up, down)
	}
}

func TestDeviceRenameAndConflict(t *testing.T) {
	s, ts := newTestServer(t)
	status(t, doAs(t, ts, "OLD-1", "", "GET", "/api/ping", nil), http.StatusOK)
	status(t, doAs(t, ts, "OTHER", "", "GET", "/api/ping", nil), http.StatusOK)
	a := mustLogin(t, ts)
	status(t, a.do("POST", "/admin/api/devices/rename", `{"id":"OLD-1","newId":"OTHER"}`), http.StatusConflict)
	status(t, a.do("POST", "/admin/api/devices/rename", `{"id":"OLD-1","newId":"a:b"}`), http.StatusBadRequest)
	status(t, a.do("POST", "/admin/api/devices/rename", `{"id":"OLD-1","newId":"NEW-1"}`), http.StatusNoContent)
	if d := deviceStatus(t, s, "NEW-1"); len(d.Pending) != 1 || d.Pending[0] != "OLD-1" {
		t.Fatalf("pending = %+v", d)
	}

	// The client still sends its old ID: it is told the new one, and its traffic counts there.
	resp := doAs(t, ts, "OLD-1", "", "GET", "/api/ping", nil)
	var ping struct{ EquipmentID string }
	json.NewDecoder(resp.Body).Decode(&ping)
	if ping.EquipmentID != "NEW-1" {
		t.Fatalf("ping id = %q", ping.EquipmentID)
	}
	if d := deviceStatus(t, s, "NEW-1"); d.BytesDown == 0 {
		t.Fatal("traffic not credited to the new id")
	}
	// Once it sends the new ID the rename is done.
	status(t, doAs(t, ts, "NEW-1", "", "GET", "/api/ping", nil), http.StatusOK)
	if d := deviceStatus(t, s, "NEW-1"); len(d.Pending) != 0 {
		t.Fatalf("still pending %+v", d)
	}
	for _, d := range s.state.devices() {
		if d.ID == "OLD-1" {
			t.Fatal("old id still listed")
		}
	}

	// Same ID from two IPs at once.
	status(t, doAs(t, ts, "DUP", "10.0.0.1", "GET", "/api/ping", nil), http.StatusOK)
	status(t, doAs(t, ts, "DUP", "10.0.0.2", "GET", "/api/ping", nil), http.StatusOK)
	if d := deviceStatus(t, s, "DUP"); !d.Conflict || len(d.IPs) != 2 {
		t.Fatalf("conflict = %+v", d)
	}
	if d := deviceStatus(t, s, "OTHER"); d.Conflict {
		t.Fatalf("no conflict expected %+v", d)
	}

	status(t, a.do("DELETE", "/admin/api/devices?id=DUP", ""), http.StatusNoContent)
	status(t, a.do("DELETE", "/admin/api/devices?id=DUP", ""), http.StatusNotFound)
}

// ---------- panel login, TOTP ----------

func TestAdminLogin(t *testing.T) {
	s, ts := newTestServer(t)
	clock := withClock(s)
	anon := &admin{t: t, ts: ts}
	status(t, anon.do("GET", "/admin/api/status", ""), http.StatusUnauthorized)
	status(t, anon.do("GET", "/admin/api/fs/tree", ""), http.StatusUnauthorized)
	resp := anon.do("GET", "/admin/", "")
	status(t, resp, http.StatusOK)
	if !strings.Contains(resp.Header.Get("Content-Type"), "text/html") {
		t.Fatal("panel page content type")
	}

	// Without adminPassword in config, the token is the password.
	_, resp = login(t, ts, "wrong", "")
	status(t, resp, http.StatusUnauthorized)
	a := mustLogin(t, ts)
	status(t, a.do("GET", "/admin/api/status", ""), http.StatusOK)
	status(t, a.do("GET", "/admin/api/fs/tree", ""), http.StatusOK)

	// CSRF guard: state-changing requests need X-Requested-With.
	req, _ := http.NewRequest("POST", ts.URL+"/admin/api/fs/mkdir", strings.NewReader(`{"path":"x"}`))
	req.AddCookie(&http.Cookie{Name: sessionCookie, Value: a.cookie})
	resp, _ = http.DefaultClient.Do(req)
	status(t, resp, http.StatusForbidden)
	status(t, a.do("POST", "/admin/api/fs/mkdir", `{"path":"x"}`), http.StatusNoContent)

	// Enable TOTP.
	resp = a.do("POST", "/admin/api/totp/setup", "")
	status(t, resp, http.StatusOK)
	var setup struct{ Secret, URI, QRSvg string }
	json.NewDecoder(resp.Body).Decode(&setup)
	if setup.Secret == "" || !strings.Contains(setup.URI, setup.Secret) || !strings.HasPrefix(setup.QRSvg, "<svg") {
		t.Fatalf("setup = %+v", setup)
	}
	status(t, a.do("POST", "/admin/api/totp/enable", `{"code":"000000"}`), http.StatusForbidden)
	status(t, a.do("POST", "/admin/api/totp/enable", `{"code":"`+codeAt(t, setup.Secret, clock.now())+`"}`), http.StatusNoContent)
	if s.state.totpSecret() != setup.Secret {
		t.Fatal("secret not saved")
	}

	clock.add(30 * time.Second)
	_, resp = login(t, ts, testToken, "") // code now required
	status(t, resp, http.StatusUnauthorized)
	b, resp := login(t, ts, testToken, codeAt(t, setup.Secret, clock.now()))
	status(t, resp, http.StatusNoContent)

	// Change the password: the other session is signed out.
	clock.add(30 * time.Second)
	status(t, a.do("POST", "/admin/api/password", `{"old":"wrong","new":"new-password","code":"`+codeAt(t, setup.Secret, clock.now())+`"}`), http.StatusForbidden)
	clock.add(30 * time.Second)
	status(t, a.do("POST", "/admin/api/password", `{"old":"`+testToken+`","new":"short","code":"`+codeAt(t, setup.Secret, clock.now())+`"}`), http.StatusBadRequest)
	clock.add(30 * time.Second)
	status(t, a.do("POST", "/admin/api/password", `{"old":"`+testToken+`","new":"new-password","code":"`+codeAt(t, setup.Secret, clock.now())+`"}`), http.StatusNoContent)
	status(t, a.do("GET", "/admin/api/status", ""), http.StatusOK)
	status(t, b.do("GET", "/admin/api/status", ""), http.StatusUnauthorized)
	clock.add(30 * time.Second)
	_, resp = login(t, ts, testToken, codeAt(t, setup.Secret, clock.now()))
	status(t, resp, http.StatusUnauthorized)
	clock.add(30 * time.Second)
	_, resp = login(t, ts, "new-password", codeAt(t, setup.Secret, clock.now()))
	status(t, resp, http.StatusNoContent)

	// Lockout after repeated failures, lifted after the lockout time.
	for i := 0; i < maxFailures; i++ {
		_, resp = login(t, ts, "wrong", "")
		status(t, resp, http.StatusUnauthorized)
	}
	clock.add(30 * time.Second)
	_, resp = login(t, ts, "new-password", codeAt(t, setup.Secret, clock.now()))
	status(t, resp, http.StatusTooManyRequests)
	clock.add(lockoutTime)
	_, resp = login(t, ts, "new-password", codeAt(t, setup.Secret, clock.now()))
	status(t, resp, http.StatusNoContent)

	// Session expiry.
	clock.add(sessionTTL + time.Second)
	status(t, a.do("GET", "/admin/api/status", ""), http.StatusUnauthorized)
}

func TestAdminPasswordFromConfig(t *testing.T) {
	s, ts := newTestServer(t)
	s.adminPassword = "config-password"
	_, resp := login(t, ts, testToken, "")
	status(t, resp, http.StatusUnauthorized)
	_, resp = login(t, ts, "config-password", "")
	status(t, resp, http.StatusNoContent)
}

func TestClientTOTPVerify(t *testing.T) {
	s, ts := newTestServer(t)
	clock := withClock(s)
	status(t, do(t, ts, "POST", "/api/totp/verify", strings.NewReader(`{"code":"123"}`)), http.StatusNoContent) // not enabled

	secret := newTOTPSecret()
	s.state.update(func(st *persistedState) error { st.TOTPSecret = secret; return nil })
	resp := do(t, ts, "GET", "/api/ping", nil)
	var ping struct{ TOTP bool }
	json.NewDecoder(resp.Body).Decode(&ping)
	if !ping.TOTP {
		t.Fatal("ping should report totp")
	}
	verify := func(code string) *http.Response {
		return do(t, ts, "POST", "/api/totp/verify", strings.NewReader(`{"code":"`+code+`"}`))
	}
	status(t, verify(codeAt(t, secret, clock.now())), http.StatusNoContent)
	status(t, verify(codeAt(t, secret, clock.now())), http.StatusConflict) // replay: not a failed attempt
	wrong := wrongCode(t, secret, clock.now())
	for i := 0; i < maxFailures; i++ {
		status(t, verify(wrong), http.StatusForbidden)
	}
	clock.add(30 * time.Second)
	status(t, verify(codeAt(t, secret, clock.now())), http.StatusTooManyRequests)
}

// ---------- recycle bin, copy, read-only ----------

func TestTrash(t *testing.T) {
	s, ts := newTestServer(t)
	clock := withClock(s)
	a := mustLogin(t, ts)
	putFile(t, s, "d/a.txt", "one")
	status(t, a.do("DELETE", "/admin/api/fs/file?path=d/a.txt", ""), http.StatusNoContent)
	status(t, do(t, ts, "DELETE", "/api/file?path=d/a.txt", nil), http.StatusNoContent) // already gone

	resp := a.do("GET", "/admin/api/trash", "")
	var items []TrashItem
	json.NewDecoder(resp.Body).Decode(&items)
	if len(items) != 1 || items[0].Path != "d/a.txt" || items[0].Size != 3 || items[0].DeletedBy != panelActor {
		t.Fatalf("trash = %+v", items)
	}
	resp = do(t, ts, "GET", "/api/tree", nil)
	var tree []Entry
	json.NewDecoder(resp.Body).Decode(&tree)
	if len(tree) != 1 || tree[0].Path != "d" {
		t.Fatalf("tree = %+v", tree)
	}

	status(t, a.do("POST", "/admin/api/trash/restore", `{"id":"`+items[0].ID+`"}`), http.StatusOK)
	if b, _ := os.ReadFile(filepath.Join(s.root, "d", "a.txt")); string(b) != "one" {
		t.Fatal("not restored")
	}

	// Restoring onto a taken path picks another name.
	status(t, a.do("DELETE", "/admin/api/fs/file?path=d/a.txt", ""), http.StatusNoContent)
	putFile(t, s, "d/a.txt", "two")
	items = s.trash.list()
	resp = a.do("POST", "/admin/api/trash/restore", `{"id":"`+items[0].ID+`"}`)
	status(t, resp, http.StatusOK)
	var restored struct{ Path string }
	json.NewDecoder(resp.Body).Decode(&restored)
	if !strings.HasPrefix(restored.Path, "d/a (restored ") || !strings.HasSuffix(restored.Path, ").txt") {
		t.Fatalf("restored as %q", restored.Path)
	}
	if b, _ := os.ReadFile(filepath.Join(s.root, "d", "a.txt")); string(b) != "two" {
		t.Fatal("existing file replaced")
	}
	status(t, a.do("POST", "/admin/api/trash/restore", `{"id":"`+items[0].ID+`"}`), http.StatusNotFound)

	// Permanent delete, empty, purge.
	status(t, a.do("DELETE", "/admin/api/fs/file?path=d/a.txt", ""), http.StatusNoContent)
	status(t, a.do("DELETE", "/admin/api/trash?id="+s.trash.list()[0].ID, ""), http.StatusNoContent)
	status(t, a.do("DELETE", "/admin/api/trash?id=../../x", ""), http.StatusNotFound)
	status(t, a.do("DELETE", "/admin/api/fs/file?path="+url.QueryEscape(restored.Path), ""), http.StatusNoContent)
	status(t, a.do("POST", "/admin/api/trash/empty", ""), http.StatusNoContent)
	if len(s.trash.list()) != 0 {
		t.Fatal("not emptied")
	}
	putFile(t, s, "old.txt", "x")
	status(t, a.do("DELETE", "/admin/api/fs/file?path=old.txt", ""), http.StatusNoContent)
	clock.add(trashRetention - time.Hour)
	s.trash.purge()
	if len(s.trash.list()) != 1 {
		t.Fatal("purged too early")
	}
	clock.add(2 * time.Hour)
	s.trash.purge()
	if len(s.trash.list()) != 0 {
		t.Fatal("not purged")
	}
}

func TestCopyMove(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "src/a.txt", "A")
	putFile(t, s, "src/sub/b.txt", "B")
	mt := time.Unix(1700000000, 0)
	os.Chtimes(filepath.Join(s.root, "src", "sub", "b.txt"), mt, mt)

	status(t, a.do("POST", "/admin/api/fs/copy", `{"from":"src","to":"dst/copy"}`), http.StatusNoContent)
	info, err := os.Stat(filepath.Join(s.root, "dst", "copy", "sub", "b.txt"))
	if err != nil || !info.ModTime().Equal(mt) {
		t.Fatalf("copy: %v %v", info, err)
	}
	if b, _ := os.ReadFile(filepath.Join(s.root, "src", "a.txt")); string(b) != "A" {
		t.Fatal("source changed")
	}
	status(t, a.do("POST", "/admin/api/fs/copy", `{"from":"src/a.txt","to":"dst/copy/a.txt"}`), http.StatusConflict)
	status(t, a.do("POST", "/admin/api/fs/copy", `{"from":"src","to":"src/sub/x"}`), http.StatusBadRequest)
	status(t, a.do("POST", "/admin/api/fs/copy", `{"from":"missing","to":"x"}`), http.StatusNotFound)
	status(t, a.do("POST", "/admin/api/fs/move", `{"from":"src","to":"src/sub/x"}`), http.StatusBadRequest)
	status(t, a.do("POST", "/admin/api/fs/move", `{"from":"src","to":"dst/moved"}`), http.StatusNoContent)
	if _, err := os.Stat(filepath.Join(s.root, "dst", "moved", "sub", "b.txt")); err != nil {
		t.Fatal(err)
	}
}

func TestReadOnlyFolders(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "p/ro/a.txt", "A")
	putFile(t, s, "x.txt", "X")
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"x.txt","readOnly":true}`), http.StatusBadRequest) // not a folder
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"p/ro","readOnly":true}`), http.StatusNoContent)

	// An upload started before the folder became read-only cannot complete.
	putFile(t, s, "p/up/.keep", "")
	resp := do(t, ts, "POST", "/api/upload/init", strings.NewReader(`{"path":"p/up/n.txt","size":1}`))
	var init struct{ ID string }
	json.NewDecoder(resp.Body).Decode(&init)
	status(t, do(t, ts, "PUT", "/api/upload/"+init.ID+"?offset=0", strings.NewReader("n")), http.StatusNoContent)
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"p/up","readOnly":true}`), http.StatusNoContent)
	status(t, do(t, ts, "POST", "/api/upload/"+init.ID+"/complete", strings.NewReader("{}")), http.StatusForbidden)
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"p/up","readOnly":false}`), http.StatusNoContent)

	for _, c := range []struct{ method, url, body string }{
		{"DELETE", "/api/file?path=p/ro/a.txt", ""},
		{"DELETE", "/api/file?path=P/RO", ""},
		{"DELETE", "/api/file?path=p", ""}, // ancestor of a read-only folder
		{"POST", "/api/move", `{"from":"p/ro/a.txt","to":"y.txt"}`},
		{"POST", "/api/move", `{"from":"p","to":"q"}`},
		{"POST", "/api/move", `{"from":"x.txt","to":"p/ro/x.txt"}`},
		{"POST", "/api/mkdir", `{"path":"p/ro/new"}`},
		{"POST", "/api/upload/init", `{"path":"p/ro/new.txt","size":1}`},
	} {
		var body io.Reader
		if c.body != "" {
			body = strings.NewReader(c.body)
		}
		status(t, do(t, ts, c.method, c.url, body), http.StatusForbidden)
	}
	status(t, do(t, ts, "GET", "/api/file?path=p/ro/a.txt", nil), http.StatusOK)
	status(t, do(t, ts, "POST", "/api/mkdir", strings.NewReader(`{"path":"p/other"}`)), http.StatusNoContent)

	resp = do(t, ts, "GET", "/api/tree", nil)
	var tree []Entry
	json.NewDecoder(resp.Body).Decode(&tree)
	ro := map[string]bool{}
	for _, e := range tree {
		ro[e.Path] = e.ReadOnly
	}
	if !ro["p/ro"] || !ro["p/ro/a.txt"] || ro["p"] || ro["x.txt"] {
		t.Fatalf("tree flags = %v", ro)
	}

	// The panel is not restricted, and read-only folders follow its renames and deletes.
	status(t, a.do("POST", "/admin/api/fs/move", `{"from":"p","to":"q"}`), http.StatusNoContent)
	if got := s.state.readOnlyFolders(); len(got) != 1 || got[0] != "q/ro" {
		t.Fatalf("read-only after move = %v", got)
	}
	status(t, do(t, ts, "DELETE", "/api/file?path=q/ro/a.txt", nil), http.StatusForbidden)
	status(t, a.do("DELETE", "/admin/api/fs/file?path=q/ro", ""), http.StatusNoContent)
	if got := s.state.readOnlyFolders(); len(got) != 0 {
		t.Fatalf("read-only after delete = %v", got)
	}
}

// Unsetting a folder that stays read-only through another entry is refused, not a silent no-op.
func TestReadOnlyUnsetStillEnforced(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "A/x.txt", "x")
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"A","readOnly":true}`), http.StatusNoContent)
	s.state.foldCase = false // Linux: "a" is another folder, still read-only through "A"
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"a","readOnly":false}`), http.StatusConflict)
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"A/sub","readOnly":false}`), http.StatusConflict) // read-only ancestor
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"A","readOnly":false}`), http.StatusNoContent)
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"a","readOnly":false}`), http.StatusNoContent)
}

func TestDiskStatusRefreshesInBackground(t *testing.T) {
	s, _ := newTestServer(t)
	now := time.Now()
	s.now = func() time.Time { return now }
	putFile(t, s, "a.txt", "12345")
	if d := s.diskStatus(); d.Used != 5 || d.Total == 0 {
		t.Fatalf("first call = %+v", d)
	}
	putFile(t, s, "b.txt", "123")
	if d := s.diskStatus(); d.Used != 5 {
		t.Fatalf("cached = %+v", d)
	}

	now = now.Add(diskCacheTTL)
	if d := s.diskStatus(); d.Used != 5 { // stale: answered at once, measured in the background
		t.Fatalf("stale = %+v", d)
	}
	for deadline := time.Now().Add(5 * time.Second); s.diskStatus().Used != 8; time.Sleep(10 * time.Millisecond) {
		if time.Now().After(deadline) {
			t.Fatal("background measurement did not land")
		}
	}
}
