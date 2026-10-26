//go:build !windows

package main

import (
	"os"
	"testing"
)

// makeUnreadable removes all permissions from the file (root ignores them: the test is skipped then).
func makeUnreadable(t *testing.T, path string) {
	t.Helper()
	if os.Geteuid() == 0 {
		t.Skip("root can read any file")
	}
	if err := os.Chmod(path, 0); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { os.Chmod(path, 0o644) })
}
