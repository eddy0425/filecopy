package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha1"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base32"
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"
)

// TOTP (RFC 6238): HMAC-SHA1, 30 s steps, 6 digits — what authenticator apps use by default.
const (
	totpStep   = 30
	totpDigits = 6
	totpSkew   = 1 // accept codes one step early or late (clock drift)
)

var b32 = base32.StdEncoding.WithPadding(base32.NoPadding)

func newTOTPSecret() string {
	b := make([]byte, 20)
	rand.Read(b)
	return b32.EncodeToString(b)
}

func totpURI(secret string) string {
	return "otpauth://totp/FileCopy?secret=" + secret + "&issuer=FileCopy"
}

func totpCode(key []byte, step int64) string {
	mac := hmac.New(sha1.New, key)
	var msg [8]byte
	binary.BigEndian.PutUint64(msg[:], uint64(step))
	mac.Write(msg[:])
	sum := mac.Sum(nil)
	off := sum[len(sum)-1] & 0x0f
	v := binary.BigEndian.Uint32(sum[off:]) & 0x7fffffff
	return fmt.Sprintf("%0*d", totpDigits, v%1000000)
}

// matchTOTP returns the time step the code is valid for, or -1.
func matchTOTP(secret, code string, now time.Time) int64 {
	code = strings.ReplaceAll(strings.TrimSpace(code), " ", "")
	key, err := b32.DecodeString(strings.ToUpper(secret))
	if err != nil || len(code) != totpDigits {
		return -1
	}
	cur := now.Unix() / totpStep
	for d := int64(-totpSkew); d <= totpSkew; d++ {
		if subtle.ConstantTimeCompare([]byte(totpCode(key, cur+d)), []byte(code)) == 1 {
			return cur + d
		}
	}
	return -1
}

type totpResult int

const (
	totpOK     totpResult = iota
	totpWrong             // counts as a failed attempt
	totpReused            // a valid code that was already used (e.g. to unlock a client): wait for the next one
)

// verifyTOTP checks a code against secret; each time step is accepted only once.
func (s *stateStore) verifyTOTP(secret, code string) totpResult {
	step := matchTOTP(secret, code, s.now())
	if step < 0 {
		return totpWrong
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.usedSecret != secret || s.usedSteps == nil { // a new secret (TOTP re-enabled): old steps don't count
		s.usedSecret, s.usedSteps = secret, map[int64]bool{}
	}
	// Each step is used once. A set rather than "the latest step": a code for the next step (a phone
	// clock running ahead) must not block the current code entered on another device.
	if s.usedSteps[step] {
		return totpReused
	}
	cur := s.now().Unix() / totpStep
	for k := range s.usedSteps {
		if k < cur-totpSkew {
			delete(s.usedSteps, k) // no longer accepted anyway
		}
	}
	s.usedSteps[step] = true
	return totpOK
}

// codeUsed reports whether code is valid for secret but already used, without using it up.
func (s *stateStore) codeUsed(secret, code string) bool {
	step := matchTOTP(secret, code, s.now())
	if step < 0 {
		return false
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.usedSecret == secret && s.usedSteps[step]
}

// refuseUsedCode answers a used code before the password is checked: answering it only after a
// correct password would tell whether the password was correct.
func (s *Server) refuseUsedCode(w http.ResponseWriter, secret, code string) bool {
	if secret == "" || !s.state.codeUsed(secret, code) {
		return false
	}
	http.Error(w, "code already used", http.StatusConflict)
	return true
}

// checkCode verifies a TOTP code for a login-like request, applying the per-IP lockout.
// It writes the error response and returns false when the request must stop.
func (s *Server) checkCode(w http.ResponseWriter, ip, secret, code string, wrongStatus int, wrongMsg string) bool {
	switch s.state.verifyTOTP(secret, code) {
	case totpOK:
		return true
	case totpReused:
		http.Error(w, "code already used", http.StatusConflict)
	default:
		s.limiter.fail(ip)
		http.Error(w, wrongMsg, wrongStatus)
	}
	return false
}

// ---------- failed attempt limiting ----------

const (
	maxFailures = 5
	lockoutTime = 5 * time.Minute
)

// failLimiter locks an IP out for a while after repeated wrong passwords / codes.
type failLimiter struct {
	now func() time.Time
	mu  sync.Mutex
	m   map[string]*failEntry
}

type failEntry struct {
	count int
	last  time.Time
	until time.Time
}

func newFailLimiter(now func() time.Time) *failLimiter {
	return &failLimiter{now: now, m: map[string]*failEntry{}}
}

func (l *failLimiter) blocked(ip string) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	e := l.m[ip]
	return e != nil && l.now().Before(e.until)
}

func (l *failLimiter) fail(ip string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.now()
	for k, e := range l.m { // forget old entries
		if now.Sub(e.last) > lockoutTime && now.After(e.until) {
			delete(l.m, k)
		}
	}
	e := l.m[ip]
	if e == nil {
		e = &failEntry{}
		l.m[ip] = e
	}
	e.count++
	e.last = now
	if e.count >= maxFailures {
		e.count = 0
		e.until = now.Add(lockoutTime)
	}
}

func (l *failLimiter) success(ip string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	delete(l.m, ip)
}

// ---------- admin password hashing ----------

const passwordIterations = 100000

// hashPassword returns "sha256$iterations$salt$hash" (iterated, salted SHA-256; standard library only).
func hashPassword(pw string) string {
	salt := make([]byte, 16)
	rand.Read(salt)
	return fmt.Sprintf("sha256$%d$%s$%s", passwordIterations, hex.EncodeToString(salt),
		hex.EncodeToString(stretch(salt, pw, passwordIterations)))
}

func stretch(salt []byte, pw string, n int) []byte {
	h := sha256.Sum256(append(append([]byte{}, salt...), pw...))
	for i := 1; i < n; i++ {
		h = sha256.Sum256(h[:])
	}
	return h[:]
}

func checkPasswordHash(encoded, pw string) bool {
	parts := strings.Split(encoded, "$")
	if len(parts) != 4 || parts[0] != "sha256" {
		return false
	}
	n, err1 := strconv.Atoi(parts[1])
	salt, err2 := hex.DecodeString(parts[2])
	want, err3 := hex.DecodeString(parts[3])
	if err1 != nil || err2 != nil || err3 != nil || n < 1 {
		return false
	}
	return subtle.ConstantTimeCompare(stretch(salt, pw, n), want) == 1
}
