//go:build !linux && !darwin && !windows

package main

import "errors"

func diskSpace(string) (free, total uint64, err error) {
	return 0, 0, errors.New("disk space not supported on this platform")
}
