package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestClientIP(t *testing.T) {
	for _, c := range []struct {
		remote, xff, want string
		trustProxy        bool
	}{
		{"203.0.113.5:1234", "", "203.0.113.5", false},
		{"203.0.113.5:1234", "1.2.3.4", "203.0.113.5", true},                   // public peer: header could be forged
		{"127.0.0.1:1234", "1.2.3.4", "1.2.3.4", false},                        // local reverse proxy
		{"127.0.0.1:1234", "9.9.9.9, 1.2.3.4", "1.2.3.4", false},               // the proxy appends the real peer last
		{"192.168.1.20:1234", "1.2.3.4", "192.168.1.20", false},                // LAN peer: could dodge the lockout
		{"172.17.0.1:1234", "1.2.3.4", "1.2.3.4", true},                        // Docker bridge gateway, trusted by config
		{"[::1]:1234", "2001:db8::1", "2001:db8::1", false},                    // IPv6
		{"127.0.0.1:1234", "<img src=x onerror=alert(1)>", "127.0.0.1", false}, // not an address: ignored
	} {
		r := httptest.NewRequest("GET", "/", nil)
		r.RemoteAddr = c.remote
		if c.xff != "" {
			r.Header.Set("X-Forwarded-For", c.xff)
		}
		s := &Server{trustProxy: c.trustProxy}
		if got := s.clientIP(r); got != c.want {
			t.Errorf("clientIP(%s, %q, trustProxy=%v) = %q, want %q", c.remote, c.xff, c.trustProxy, got, c.want)
		}
	}
}

func TestUnicodeEquipmentIDOverHTTP(t *testing.T) {
	s, ts := newTestServer(t)
	id := strings.Repeat("机", 30)
	req, _ := http.NewRequest("GET", ts.URL+"/api/ping", nil)
	req.Header.Set("Authorization", "Bearer "+testToken)
	req.Header.Set("X-Equipment-Id", "%E6%9C%BA"+strings.Repeat("%E6%9C%BA", 29)) // URL-encoded, as the client sends it
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	status(t, resp, http.StatusOK)
	deviceStatus(t, s, id)
}

// A code already used (e.g. to unlock a client) is reported as such and does not count as a failed attempt.
func TestTOTPReuse(t *testing.T) {
	s, ts := newTestServer(t)
	clock := withClock(s)
	secret := newTOTPSecret()
	s.state.update(func(st *persistedState) error { st.TOTPSecret = secret; return nil })
	code := codeAt(t, secret, clock.now())

	status(t, do(t, ts, "POST", "/api/totp/verify", strings.NewReader(`{"code":"`+code+`"}`)), http.StatusNoContent)
	for i := 0; i < maxFailures+1; i++ {
		status(t, do(t, ts, "POST", "/api/totp/verify", strings.NewReader(`{"code":"`+code+`"}`)), http.StatusConflict)
	}
	_, resp := login(t, ts, testToken, code)
	status(t, resp, http.StatusConflict)
	// The same answer with a wrong password: a used code must not reveal whether the password is right.
	_, resp = login(t, ts, "wrong-password", code)
	status(t, resp, http.StatusConflict)
	a, resp := login(t, ts, testToken, codeAt(t, secret, clock.now().Add(-30*time.Second)))
	status(t, resp, http.StatusNoContent)
	status(t, a.do("POST", "/admin/api/password", `{"old":"wrong-password","new":"panel-pass-1","code":"`+code+`"}`), http.StatusConflict)
	clock.add(30 * time.Second)
	_, resp = login(t, ts, testToken, codeAt(t, secret, clock.now())) // not locked out by the reuses
	status(t, resp, http.StatusNoContent)
}

func TestSessionLogoutAndTOTPDisable(t *testing.T) {
	s, ts := newTestServer(t)
	clock := withClock(s)
	anon := &admin{t: t, ts: ts}
	var info struct{ LoggedIn, TOTP bool }
	json.NewDecoder(anon.do("GET", "/admin/api/session", "").Body).Decode(&info)
	if info.LoggedIn || info.TOTP {
		t.Fatalf("anonymous session info %+v", info)
	}
	a := mustLogin(t, ts)
	json.NewDecoder(a.do("GET", "/admin/api/session", "").Body).Decode(&info)
	if !info.LoggedIn {
		t.Fatal("should be logged in")
	}

	status(t, a.do("POST", "/admin/api/totp/enable", `{"code":"123456"}`), http.StatusBadRequest) // setup first
	var setup struct{ Secret string }
	json.NewDecoder(a.do("POST", "/admin/api/totp/setup", "").Body).Decode(&setup)
	status(t, a.do("POST", "/admin/api/totp/enable", `{"code":"`+codeAt(t, setup.Secret, clock.now())+`"}`), http.StatusNoContent)
	clock.add(30 * time.Second)
	status(t, a.do("POST", "/admin/api/totp/disable", `{"code":"000000"}`), http.StatusForbidden)
	status(t, a.do("POST", "/admin/api/totp/disable", `{"code":"`+codeAt(t, setup.Secret, clock.now())+`"}`), http.StatusNoContent)
	if s.state.totpSecret() != "" {
		t.Fatal("totp not disabled")
	}

	status(t, a.do("POST", "/admin/api/logout", ""), http.StatusNoContent)
	status(t, a.do("GET", "/admin/api/status", ""), http.StatusUnauthorized)
}

func TestPasswordHash(t *testing.T) {
	h := hashPassword("secret-1")
	if !checkPasswordHash(h, "secret-1") || checkPasswordHash(h, "secret-2") || checkPasswordHash("garbage", "secret-1") {
		t.Fatal("password hash")
	}
	if hashPassword("secret-1") == h {
		t.Fatal("hash should be salted")
	}
}

func TestFailLimiterForgets(t *testing.T) {
	c := &fakeClock{t: time.Unix(1800000000, 0)}
	l := newFailLimiter(c.now)
	for i := 0; i < maxFailures-1; i++ {
		l.fail("1.1.1.1")
	}
	if l.blocked("1.1.1.1") {
		t.Fatal("blocked too early")
	}
	l.fail("1.1.1.1")
	if !l.blocked("1.1.1.1") || l.blocked("2.2.2.2") {
		t.Fatal("lockout per IP")
	}
	c.add(lockoutTime + time.Second)
	if l.blocked("1.1.1.1") {
		t.Fatal("lockout should expire")
	}
	l.fail("1.1.1.1")
	l.success("1.1.1.1")
	for i := 0; i < maxFailures-1; i++ {
		l.fail("1.1.1.1")
	}
	if l.blocked("1.1.1.1") {
		t.Fatal("success should reset the count")
	}
}

// Findings from review: each test below reproduces one of them.

func TestTOTPNextStepDoesNotBlockCurrent(t *testing.T) {
	now := time.Unix(1800000000, 0)
	st, _ := loadState(filepath.Join(t.TempDir(), "s.json"), func() time.Time { return now })
	secret := newTOTPSecret()
	if st.verifyTOTP(secret, codeAt(t, secret, now.Add(30*time.Second))) != totpOK { // clock running ahead
		t.Fatal("next-step code rejected")
	}
	if st.verifyTOTP(secret, codeAt(t, secret, now)) != totpOK { // another device with an accurate clock
		t.Fatal("current-step code blocked by the earlier use of the next step")
	}
	if st.verifyTOTP(secret, codeAt(t, secret, now)) != totpReused {
		t.Fatal("replay accepted")
	}
}

func TestTOTPCannotBeReenrolledWhileEnabled(t *testing.T) {
	s, ts := newTestServer(t)
	clock := withClock(s)
	a := mustLogin(t, ts)
	var setup struct{ Secret string }
	json.NewDecoder(a.do("POST", "/admin/api/totp/setup", "").Body).Decode(&setup)
	status(t, a.do("POST", "/admin/api/totp/enable", `{"code":"`+codeAt(t, setup.Secret, clock.now())+`"}`), http.StatusNoContent)
	// A stolen session must not swap in its own secret (disabling needs the current code).
	status(t, a.do("POST", "/admin/api/totp/setup", ""), http.StatusConflict)
	status(t, a.do("POST", "/admin/api/totp/enable", `{"code":"123456"}`), http.StatusConflict)
	if s.state.totpSecret() != setup.Secret {
		t.Fatal("secret replaced")
	}
}
