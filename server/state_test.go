package main

import (
	"encoding/json"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestValidEquipmentID(t *testing.T) {
	for id, want := range map[string]bool{
		"XZT1-DB1":              true,
		"车间一号机":                 true,
		strings.Repeat("机", 64): true, // 64 characters (192 bytes)
		strings.Repeat("😀", 64): true, // characters, as the client counts them
		strings.Repeat("a", 65): false,
		"a\x7fb":                false,
		"":                      false,
		"a/b":                   false,
		"a:b":                   false,
		"a\tb":                  false,
		"<script>":              false,
	} {
		if got := validEquipmentID(id); got != want {
			t.Errorf("validEquipmentID(%q) = %v, want %v", id, got, want)
		}
	}
}

func TestCorruptStateRefusesToStart(t *testing.T) {
	dir := t.TempDir()
	os.MkdirAll(filepath.Join(dir, internalDirName), 0o755)
	os.WriteFile(statePath(dir), []byte("{not json"), 0o600)
	if _, err := NewServer(dir, testToken); err == nil {
		t.Fatal("a corrupt state file must not be silently replaced (it holds the TOTP secret)")
	}
}

func TestStatePersistsSettings(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "ro/a.txt", "a")
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"ro","readOnly":true}`), http.StatusNoContent)
	status(t, a.do("POST", "/admin/api/password", `{"old":"`+testToken+`","new":"panel-pass-1"}`), http.StatusNoContent)

	s2, err := NewServer(s.root, testToken)
	if err != nil {
		t.Fatal(err)
	}
	if !s2.state.isReadOnly("ro/a.txt") || !s2.checkAdminPassword("panel-pass-1") || s2.checkAdminPassword(testToken) {
		t.Fatal("settings not reloaded")
	}
}

func TestRenameBackToPendingID(t *testing.T) {
	s, ts := newTestServer(t)
	status(t, doAs(t, ts, "A", "", "GET", "/api/ping", nil), http.StatusOK)
	a := mustLogin(t, ts)
	status(t, a.do("POST", "/admin/api/devices/rename", `{"id":"A","newId":"B"}`), http.StatusNoContent)
	status(t, a.do("POST", "/admin/api/devices/rename", `{"id":"B","newId":"C"}`), http.StatusNoContent)
	// The client still on "A" goes straight to "C" (no chain through "B").
	var ping struct{ EquipmentID string }
	json.NewDecoder(doAs(t, ts, "A", "", "GET", "/api/ping", nil).Body).Decode(&ping)
	if ping.EquipmentID != "C" {
		t.Fatalf("got %q", ping.EquipmentID)
	}
	// Renaming back to the old ID before the client noticed: nothing pending any more.
	status(t, a.do("POST", "/admin/api/devices/rename", `{"id":"C","newId":"A"}`), http.StatusNoContent)
	json.NewDecoder(doAs(t, ts, "A", "", "GET", "/api/ping", nil).Body).Decode(&ping)
	if d := deviceStatus(t, s, "A"); ping.EquipmentID != "A" || len(d.Pending) != 0 {
		t.Fatalf("ping %q, device %+v", ping.EquipmentID, d)
	}
}

func TestReadOnlyCaseAndAncestorMoves(t *testing.T) {
	s, ts := newTestServer(t)
	a := mustLogin(t, ts)
	putFile(t, s, "Docs/Spec/a.txt", "a")
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"Docs/Spec","readOnly":true}`), http.StatusNoContent)
	for _, c := range []struct{ method, url, body string }{
		{"DELETE", "/api/file?path=docs/spec/a.txt", ""},
		{"POST", "/api/move", `{"from":"DOCS","to":"other"}`},
		{"POST", "/api/upload/init", `{"path":"docs/SPEC/new.txt","size":1}`},
		{"POST", "/api/mkdir", `{"path":"Docs/Spec"}`},
	} {
		var body io.Reader
		if c.body != "" {
			body = strings.NewReader(c.body)
		}
		status(t, do(t, ts, c.method, c.url, body), http.StatusForbidden)
	}
	// Unsetting with the path as listed (the panel sends it exactly; case only matters on Linux).
	status(t, a.do("POST", "/admin/api/readonly", `{"path":"Docs/Spec","readOnly":false}`), http.StatusNoContent)
	if len(s.state.readOnlyFolders()) != 0 {
		t.Fatalf("still read-only: %v", s.state.readOnlyFolders())
	}
}

func TestRenameOntoPendingOldIDRefused(t *testing.T) {
	_, ts := newTestServer(t)
	status(t, doAs(t, ts, "A", "", "GET", "/api/ping", nil), http.StatusOK)
	status(t, doAs(t, ts, "C", "", "GET", "/api/ping", nil), http.StatusOK)
	a := mustLogin(t, ts)
	status(t, a.do("POST", "/admin/api/devices/rename", `{"id":"A","newId":"B"}`), http.StatusNoContent)
	// "A" is still the ID machine A sends until it picks up "B".
	status(t, a.do("POST", "/admin/api/devices/rename", `{"id":"C","newId":"A"}`), http.StatusConflict)
	var ping struct{ EquipmentID string }
	json.NewDecoder(doAs(t, ts, "A", "", "GET", "/api/ping", nil).Body).Decode(&ping)
	if ping.EquipmentID != "B" {
		t.Fatalf("machine A told %q", ping.EquipmentID)
	}
}

func TestReadOnlyCaseSensitiveFilesystem(t *testing.T) {
	st, _ := loadState(filepath.Join(t.TempDir(), "s.json"), time.Now)
	st.foldCase = false // Linux: "A" and "a" are different folders
	st.setReadOnly("A", true)
	st.movedPath("a", "c") // renaming the writable "a" must not take "A"'s flag along
	st.movedPath("a", "")
	if err := st.setReadOnly("a", false); err != errReadOnlyElsewhere { // still enforced through "A"
		t.Fatalf("unset a = %v", err)
	}
	if got := st.readOnlyFolders(); len(got) != 1 || got[0] != "A" {
		t.Fatalf("read-only = %v", got)
	}
	if !st.isReadOnly("A/x") {
		t.Fatal("A lost protection")
	}
	st.foldCase = true // Windows: the same folder
	st.movedPath("a", "c")
	if got := st.readOnlyFolders(); len(got) != 1 || got[0] != "c" {
		t.Fatalf("read-only after case-insensitive move = %v", got)
	}
}

func TestReadOnlyCaseVariantsBothListedCanBeUnset(t *testing.T) {
	st, _ := loadState(filepath.Join(t.TempDir(), "s.json"), time.Now)
	st.foldCase = false
	st.setReadOnly("Docs", true)
	st.setReadOnly("docs", true)
	if got := st.readOnlyFolders(); len(got) != 2 {
		t.Fatalf("read-only = %v", got)
	}
	if err := st.setReadOnly("docs", false); err != nil {
		t.Fatalf("unset docs = %v", err)
	}
	if got := st.readOnlyFolders(); len(got) != 0 {
		t.Fatalf("read-only = %v", got)
	}
}

func TestEquipmentIDReservedNames(t *testing.T) {
	for _, id := range []string{"CON", "nul", "COM1", "lpt9", "con.txt", "AUX.x"} {
		if validEquipmentID(id) {
			t.Errorf("%q accepted (not a valid Windows file name for the backup archive)", id)
		}
	}
	for _, id := range []string{"CONSOLE", "COM10", "NULL-1"} {
		if !validEquipmentID(id) {
			t.Errorf("%q rejected", id)
		}
	}
}

func TestSubPathCountsSegments(t *testing.T) {
	// "\u212a" (Kelvin sign, 3 bytes) folds to "k" (1 byte): byte offsets would cut "ro" short.
	for _, c := range []struct{ rel, dir, want string }{
		{"\u212a/ro", "k", "ro"},
		{"k/ro/x", "\u212a", "ro/x"},
		{"a/b", "a/b", ""},
		{"a/b/c", "a", "b/c"},
	} {
		if !pathPrefix(c.rel, c.dir, true) {
			t.Fatalf("pathPrefix(%q, %q) = false", c.rel, c.dir)
		}
		if got := subPath(c.rel, c.dir); got != c.want {
			t.Errorf("subPath(%q, %q) = %q, want %q", c.rel, c.dir, got, c.want)
		}
	}
}
