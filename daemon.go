package main

import (
	_ "embed"
	"encoding/json"
	"net"
	"net/http"
	"os"
	"os/exec"
	"runtime"
	"time"

	"fyne.io/systray"
)

//go:embed build/appicon.png
var trayIconPNG []byte

//go:embed build/windows/icon.ico
var trayIconICO []byte

// runDaemon is the default (headless) mode: it owns the bridge, serves a local
// control API for the window, and shows a tray/menubar icon. No WebView.
func runDaemon() {
	// Single instance: grab the control port. If it's already taken, a daemon is
	// running — just open the config window and exit.
	ln, err := net.Listen("tcp", daemonAddr)
	if err != nil {
		openWindow()
		return
	}
	app := NewApp()
	go app.serveControl(ln)
	app.autoStart()
	runTray(app) // blocks on the main thread (required for the menubar)
}

// serveControl exposes the bound methods to the window over localhost.
func (a *App) serveControl(ln net.Listener) {
	mux := http.NewServeMux()
	mux.HandleFunc("/rpc", func(w http.ResponseWriter, r *http.Request) {
		var req struct {
			M string            `json:"M"`
			A []json.RawMessage `json:"A"`
		}
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			http.Error(w, "bad request", http.StatusBadRequest)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]any{"r": a.dispatch(req.M, req.A)})
	})
	http.Serve(ln, mux)
}

// dispatch maps an RPC method name + JSON args to the real App method.
func (a *App) dispatch(m string, args []json.RawMessage) any {
	s := func(i int) (v string) {
		if i < len(args) {
			json.Unmarshal(args[i], &v)
		}
		return
	}
	n := func(i int) (v int) {
		if i < len(args) {
			json.Unmarshal(args[i], &v)
		}
		return
	}
	b := func(i int) (v bool) {
		if i < len(args) {
			json.Unmarshal(args[i], &v)
		}
		return
	}
	switch m {
	case "GetStatus":
		return a.GetStatus()
	case "GetConfig":
		return a.GetConfig()
	case "SetPeerIP":
		return a.SetPeerIP(s(0))
	case "SetRole":
		return a.SetRole(s(0))
	case "SetDirection":
		return a.SetDirection(s(0))
	case "SetVolume":
		return a.SetVolume(n(0))
	case "ApplyParams":
		return a.ApplyParams(n(0), n(1), n(2))
	case "Toggle":
		return a.Toggle()
	case "Verify":
		return a.Verify()
	case "DiscoverPeers":
		return a.DiscoverPeers()
	case "SetAutoStart":
		return a.SetAutoStart(b(0))
	}
	return nil
}

// openWindow launches this same binary in window mode (the Wails config window).
func openWindow() {
	exe, err := os.Executable()
	if err != nil {
		return
	}
	_ = exec.Command(exe, "--window").Start()
}

func runTray(app *App) {
	onReady := func() {
		if runtime.GOOS == "windows" {
			systray.SetIcon(trayIconICO)
		} else {
			systray.SetIcon(trayIconPNG)
		}
		systray.SetTooltip("hearken")
		mStatus := systray.AddMenuItem("starting…", "")
		mStatus.Disable()
		mOpen := systray.AddMenuItem("Open hearken", "Open the config window")
		systray.AddSeparator()
		mQuit := systray.AddMenuItem("Quit hearken", "Stop the bridge and quit")

		go func() {
			for {
				select {
				case <-mOpen.ClickedCh:
					openWindow()
				case <-mQuit.ClickedCh:
					app.Stop()
					systray.Quit()
					return
				}
			}
		}()
		go func() {
			for {
				s := app.GetStatus()
				t := "Idle"
				if s.Active {
					if s.PeerConnected {
						t = "Streaming · peer connected"
					} else {
						t = "Streaming · waiting for peer"
					}
				}
				mStatus.SetTitle(t)
				time.Sleep(2 * time.Second)
			}
		}()
	}
	systray.Run(onReady, func() { os.Exit(0) })
}
