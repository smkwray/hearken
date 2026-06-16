//go:build windows

package main

import (
	"os/exec"
	"syscall"
)

// hideWindow prevents spawned console processes from flashing a window.
func hideWindow(c *exec.Cmd) {
	c.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000} // CREATE_NO_WINDOW
}
