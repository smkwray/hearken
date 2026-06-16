package main

import (
	"embed"
	"net"
	"os"

	"github.com/wailsapp/wails/v2"
	"github.com/wailsapp/wails/v2/pkg/options"
	"github.com/wailsapp/wails/v2/pkg/options/assetserver"
)

//go:embed all:frontend/dist
var assets embed.FS

// bindingsMode is set true only when compiled with `-tags bindings` (see
// bindings_build.go) so `wails build`'s binding generator reaches wails.Run.
var bindingsMode bool

// One binary, two modes:
//   (no args)  -> headless daemon: owns the bridge + a tray/menubar icon, NO WebView (low RAM).
//   --window   -> the Wails config window (a thin client to the daemon); frees its WebView on close.
func main() {
	if bindingsMode || (len(os.Args) > 1 && os.Args[1] == "--window") {
		runWindow()
		return
	}
	runDaemon()
}

func runWindow() {
	// Single config window at a time: hold a lock port for the window's lifetime.
	// A second "Open" then exits immediately instead of stacking windows/Dock icons.
	if !bindingsMode {
		ln, err := net.Listen("tcp", windowLockAddr)
		if err != nil {
			return // a window is already open
		}
		defer ln.Close()
	}

	app := NewWindowApp(daemonURL)
	err := wails.Run(&options.App{
		Title:            "hearken",
		Width:            440,
		Height:           600,
		MinWidth:         440,
		MinHeight:        540,
		MaxWidth:         440,
		MaxHeight:        820,
		AssetServer:      &assetserver.Options{Assets: assets},
		BackgroundColour: &options.RGBA{R: 18, G: 20, B: 27, A: 1},
		// No Dock icon: hearken is a menubar agent (LSUIElement in the Info.plist).
		OnStartup:  app.startup,
		OnShutdown: app.shutdown,
		Bind:       []interface{}{app},
	})
	if err != nil {
		println("Error:", err.Error())
	}
}
