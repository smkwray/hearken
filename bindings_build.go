//go:build bindings

package main

// Under `wails build` binding generation the app is compiled with `-tags bindings`
// and RUN so wails.Run can emit the bound-method JSON and exit. Force the window
// path so we reach wails.Run — the daemon's systray.Run would block the generator
// forever (it never returns), hanging the build.
func init() { bindingsMode = true }
