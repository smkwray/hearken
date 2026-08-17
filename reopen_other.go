//go:build !darwin

package main

// installReopenHandler is macOS-only: no other platform reactivates a running
// bundle in place of launching it.
func installReopenHandler() {}
