package main

/*
#cgo LDFLAGS: -framework Cocoa
void hearkenInstallReopenHandler(void); // reopen_darwin.m
*/
import "C"

// hearkenReopen runs on the AppKit main thread inside the Apple-event dispatch,
// so it hands the process spawn to a goroutine rather than doing it there. Same
// shape as the menu-click path, which already calls openWindow off the main thread.
//
//export hearkenReopen
func hearkenReopen() { go openWindow() }

// installReopenHandler makes Spotlight/Raycast/Launchpad able to open the config
// window. Launching an already-running bundle does not start a second process —
// LaunchServices reactivates the running daemon and sends it a reopen Apple
// event instead, so without this handler "open hearken" is silently a no-op.
func installReopenHandler() { C.hearkenInstallReopenHandler() }
