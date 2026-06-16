//go:build !windows

package main

import "os/exec"

// hideWindow is a no-op off Windows (GUI children don't spawn windows).
func hideWindow(c *exec.Cmd) {}
