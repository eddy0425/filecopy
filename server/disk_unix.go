//go:build linux || darwin

package main

import "syscall"

// diskSpace returns the free (available to unprivileged users) and total bytes of the filesystem holding path.
func diskSpace(path string) (free, total uint64, err error) {
	var st syscall.Statfs_t
	if err := syscall.Statfs(path, &st); err != nil {
		return 0, 0, err
	}
	return st.Bavail * uint64(st.Bsize), st.Blocks * uint64(st.Bsize), nil
}
