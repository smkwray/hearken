package main

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ============================================================================
// hearken — turnkey audio-bridge controller.
// The app OWNS the bridge: it spawns the capture/playback tools as child
// processes on launch, monitors + restarts them, and stops them on quit.
// Dependencies are installed separately (see install/ scripts); the app just
// detects + drives them.
//
//   Direction (user's POV):
//     mac2win = Mac audio heard on Windows   (Mac runs hear-capture server :45000;
//                                              Windows runs ffplay client -> :45000)
//     win2mac = Windows audio heard on Mac   (Mac runs ffmpeg player server :45001;
//                                              Windows runs capture.exe client -> :45001)
//     both    = bidirectional
//   Mac is the TCP server; Windows is the client and is the side that needs the peer IP.
// ============================================================================

const hearPort = 45000
const talkPort = 45001
const blackholeUID = "BlackHole2ch_UID"
const daemonAddr = "127.0.0.1:47600" // headless daemon's local control API
const daemonURL = "http://" + daemonAddr
const windowLockAddr = "127.0.0.1:47601" // single-instance lock for the config window

type Config struct {
	PeerIP    string `json:"peerIP"`
	Role      string `json:"role"`      // "host" | "client" | "" (auto: host on macOS, client elsewhere)
	Direction string `json:"direction"` // both | hostToClient (legacy mac2win) | clientToHost (legacy win2mac)
	SndBufKB  int    `json:"sndBufKB"`
	CaptureMs int    `json:"captureMs"`
	RecvBufKB int    `json:"recvBufKB"`
	PlayoutMs int    `json:"playoutMs"` // playout jitter-buffer cap (ms) — the main crackle vs latency knob
	VolumePct int    `json:"volumePct"` // playback gain on THIS device, 0-100 (100 = unity)
	AutoStart bool   `json:"autoStart"`
}

func defaultConfig() Config {
	return Config{PeerIP: "", Role: "", Direction: "both", SndBufKB: 16, CaptureMs: 21, RecvBufKB: 16, PlayoutMs: 250, VolumePct: 100, AutoStart: true}
}

// playoutArg renders the playout buffer cap (ms, clamped 80-800) for play.exe.
func playoutArg(cfg Config) string {
	v := cfg.PlayoutMs
	if v < 80 {
		v = 80
	}
	if v > 800 {
		v = 800
	}
	return strconv.Itoa(v)
}

// gainArg renders this device's playback gain (0.000–1.000) for play.exe / ffmpeg.
func gainArg(cfg Config) string {
	v := cfg.VolumePct
	if v < 0 {
		v = 0
	}
	if v > 100 {
		v = 100
	}
	return strconv.FormatFloat(float64(v)/100, 'f', 3, 64)
}

// isHost reports whether THIS machine listens for the peer (host) or dials it (client).
// Role decoupled from OS so any pair works; "" = auto (macOS has the BlackHole capture
// rig so it defaults to host; other platforms default to client).
func isHost(cfg Config) bool {
	switch cfg.Role {
	case "host":
		return true
	case "client":
		return false
	default:
		return runtime.GOOS == "darwin"
	}
}

func roleName(cfg Config) string {
	if isHost(cfg) {
		return "host"
	}
	return "client"
}

// legsForDirection maps a direction to which audio streams are active.
// hostAudio = host's audio -> client (hearPort); clientAudio = client's audio -> host (talkPort).
// Accepts the new names and the legacy mac/win names (Mac was always host).
func legsForDirection(dir string) (hostAudio, clientAudio bool) {
	switch dir {
	case "hostToClient", "mac2win":
		return true, false
	case "clientToHost", "win2mac":
		return false, true
	default: // "both"
		return true, true
	}
}

func containsRole(rs []role, want role) bool {
	for _, r := range rs {
		if r == want {
			return true
		}
	}
	return false
}

type App struct {
	ctx    context.Context
	mu     sync.Mutex
	cfg    Config
	active bool
	cancel context.CancelFunc
	wg     sync.WaitGroup
	note   string
	daemon string       // "" = run the bridge locally (daemon mode); non-"" = proxy to that URL (window mode)
	hc     *http.Client // window mode HTTP client
}

// NewApp creates the daemon-mode App that actually owns the bridge.
func NewApp() *App {
	a := &App{cfg: defaultConfig()}
	a.cfg = a.loadConfig()
	return a
}

// NewWindowApp creates the window-mode App: a thin proxy that forwards every
// bound call to the headless daemon over its local HTTP API. The frontend and
// Wails bindings are identical to daemon mode — only the implementation differs.
func NewWindowApp(url string) *App {
	return &App{daemon: url, hc: &http.Client{Timeout: 8 * time.Second}}
}

// rpc forwards a method call to the daemon and returns its raw JSON result.
func (a *App) rpc(method string, args ...any) json.RawMessage {
	body, _ := json.Marshal(map[string]any{"M": method, "A": args})
	resp, err := a.hc.Post(a.daemon+"/rpc", "application/json", bytes.NewReader(body))
	if err != nil {
		logf("window rpc %s failed: %v", method, err)
		return nil
	}
	defer resp.Body.Close()
	var out struct {
		R json.RawMessage `json:"r"`
	}
	json.NewDecoder(resp.Body).Decode(&out)
	return out.R
}

// startup is the Wails hook for WINDOW mode — a thin client. The daemon owns the
// bridge, so this only stores the context; it must NOT auto-start anything.
func (a *App) startup(ctx context.Context) { a.ctx = ctx }

// shutdown: closing the config window must NOT stop the daemon's bridge.
func (a *App) shutdown(ctx context.Context) {
	if a.daemon == "" {
		a.Stop() // only relevant if ever run standalone
	}
}

// autoStart launches the bridge if configured. Called by the DAEMON at startup
// (host always; client once a peer IP is set).
func (a *App) autoStart() {
	deps := a.CheckDeps()
	logf("daemon startup os=%s home=%s abDir=%s autostart=%v deps=%v", runtime.GOOS, home(), abDir(), a.cfg.AutoStart, deps)
	if a.cfg.AutoStart && len(deps) == 0 {
		if isHost(a.cfg) || a.cfg.PeerIP != "" {
			go a.Start()
		}
	}
}

// ---- paths / helpers ----------------------------------------------------

func home() string { h, _ := os.UserHomeDir(); return h }
func abDir() string { return filepath.Join(home(), "audio-bridge") }
func exists(p string) bool { _, err := os.Stat(p); return err == nil }

func ffmpegPath() string {
	if runtime.GOOS == "darwin" {
		if p := filepath.Join(home(), "bin", "ffmpeg"); exists(p) {
			return p
		}
	}
	return "ffmpeg"
}
func ffplayPath() string {
	if runtime.GOOS == "darwin" {
		if p := filepath.Join(home(), "bin", "ffplay"); exists(p) {
			return p
		}
	}
	return "ffplay"
}
func captureExe() string { return filepath.Join(abDir(), "lib", "capture.exe") }
func playExe() string    { return filepath.Join(abDir(), "lib", "play.exe") }

func run(timeout time.Duration, name string, args ...string) (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	c := exec.CommandContext(ctx, name, args...)
	hideWindow(c) // no console-window flash on Windows
	out, err := c.CombinedOutput()
	return string(out), err
}

func logf(format string, args ...any) {
	d, _ := os.UserConfigDir()
	dir := filepath.Join(d, "hearken")
	os.MkdirAll(dir, 0o755)
	f, err := os.OpenFile(filepath.Join(dir, "hearken.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return
	}
	defer f.Close()
	fmt.Fprintf(f, time.Now().Format("15:04:05 ")+format+"\n", args...)
}

// ---- config persistence -------------------------------------------------

func configPath() string {
	d, _ := os.UserConfigDir()
	return filepath.Join(d, "hearken", "config.json")
}
func (a *App) loadConfig() Config {
	c := defaultConfig()
	if b, err := os.ReadFile(configPath()); err == nil {
		_ = json.Unmarshal(b, &c)
	}
	return c
}
func (a *App) saveConfig() {
	os.MkdirAll(filepath.Dir(configPath()), 0o755)
	b, _ := json.MarshalIndent(a.cfg, "", "  ")
	_ = os.WriteFile(configPath(), b, 0o644)
}

// ---- dependency detection ----------------------------------------------

// CheckDeps returns the list of missing dependencies for this OS.
func (a *App) CheckDeps() []string {
	var miss []string
	if runtime.GOOS == "darwin" {
		if !exists("/Library/Audio/Plug-Ins/HAL/BlackHole2ch.driver") {
			miss = append(miss, "BlackHole 2ch (audio driver)")
		}
		if !exists(filepath.Join(abDir(), "hear-capture")) {
			miss = append(miss, "hear-capture")
		}
		if !exists(filepath.Join(abDir(), "make-bridge-out")) {
			miss = append(miss, "make-bridge-out")
		}
		if !exists(filepath.Join(abDir(), "find-output-index")) {
			miss = append(miss, "find-output-index")
		}
		if _, err := run(2*time.Second, ffmpegPath(), "-version"); err != nil {
			miss = append(miss, "ffmpeg")
		}
	} else {
		if !exists(captureExe()) {
			miss = append(miss, "capture.exe")
		}
		if !exists(playExe()) {
			miss = append(miss, "play.exe")
		}
	}
	return miss
}

// ---- status -------------------------------------------------------------

type Status struct {
	OS            string   `json:"os"`
	Self          string   `json:"self"`
	Peer          string   `json:"peer"`
	PeerIP        string   `json:"peerIP"`
	Active        bool     `json:"active"`
	BlackHole     bool     `json:"blackHole"`
	BridgeOut     bool     `json:"bridgeOut"`
	HearUp        bool     `json:"hearUp"`
	TalkUp        bool     `json:"talkUp"`
	PeerConnected bool     `json:"peerConnected"`
	PingMs        int      `json:"pingMs"`
	Direction     string   `json:"direction"`
	SndBufKB      int      `json:"sndBufKB"`
	CaptureMs     int      `json:"captureMs"`
	RecvBufKB     int      `json:"recvBufKB"`
	PlayoutMs     int      `json:"playoutMs"`
	VolumePct     int      `json:"volumePct"`
	AutoStart     bool     `json:"autoStart"`
	MissingDeps   []string `json:"missingDeps"`
	Note          string   `json:"note"`
	Role            string `json:"role"`            // resolved: "host" (listens) | "client" (dials)
	RoleMode        string `json:"roleMode"`        // raw setting: "" (auto) | "host" | "client"
	SelfTailscaleIP string `json:"selfTailscaleIP"` // this device's Tailscale IP (for the peer to dial)
	SelfLANIP       string `json:"selfLANIP"`       // this device's LAN IP
}

// PeerInfo is a Tailscale peer that is reachable AND has a hearken host port open.
type PeerInfo struct {
	IP   string `json:"ip"`
	Name string `json:"name"`
	OS   string `json:"os"`
}

func (a *App) GetStatus() Status {
	if a.daemon != "" {
		var s Status
		json.Unmarshal(a.rpc("GetStatus"), &s)
		return s
	}
	a.mu.Lock()
	cfg := a.cfg
	active := a.active
	note := a.note
	a.mu.Unlock()

	s := Status{
		OS: runtime.GOOS, PeerIP: cfg.PeerIP, Active: active, PingMs: -1,
		Direction: cfg.Direction, SndBufKB: cfg.SndBufKB, CaptureMs: cfg.CaptureMs,
		RecvBufKB: cfg.RecvBufKB, PlayoutMs: cfg.PlayoutMs, VolumePct: cfg.VolumePct, AutoStart: cfg.AutoStart,
		MissingDeps: a.CheckDeps(), Note: note,
	}
	s.Role = roleName(cfg)
	s.RoleMode = cfg.Role
	s.SelfTailscaleIP, s.SelfLANIP = selfIPs()
	if runtime.GOOS == "darwin" {
		s.Self, s.Peer = "Mac", "Windows"
		s.BlackHole = exists("/Library/Audio/Plug-Ins/HAL/BlackHole2ch.driver")
		s.BridgeOut = bridgeOutIsDefault()
	} else {
		s.Self, s.Peer = "Windows", "Mac"
		s.BlackHole, s.BridgeOut = true, true // n/a on Windows
	}
	if isHost(cfg) {
		// green while listening OR while a peer is connected (streaming)
		s.HearUp = portListening(hearPort) || portEstablished(hearPort)
		s.TalkUp = portListening(talkPort) || portEstablished(talkPort)
		s.PeerConnected = portEstablished(hearPort) || portEstablished(talkPort)
	} else {
		s.HearUp = connEstablishedToPeer(cfg.PeerIP, hearPort)
		s.TalkUp = connEstablishedToPeer(cfg.PeerIP, talkPort)
		s.PeerConnected = s.HearUp || s.TalkUp
	}
	if cfg.PeerIP != "" {
		s.PingMs = pingPeer(cfg.PeerIP)
	}
	return s
}

// ---- self IP + peer discovery -------------------------------------------

// selfIPs returns this device's Tailscale (100.64/10 CGNAT) and LAN (RFC1918) IPv4s.
func selfIPs() (tsIP, lanIP string) {
	addrs, _ := net.InterfaceAddrs()
	for _, a := range addrs {
		var ip net.IP
		switch v := a.(type) {
		case *net.IPNet:
			ip = v.IP
		case *net.IPAddr:
			ip = v.IP
		}
		ip4 := ip.To4()
		if ip4 == nil || ip4.IsLoopback() || ip4.IsLinkLocalUnicast() {
			continue
		}
		if ip4[0] == 100 && ip4[1] >= 64 && ip4[1] <= 127 { // Tailscale CGNAT range
			if tsIP == "" {
				tsIP = ip4.String()
			}
		} else if ip4.IsPrivate() {
			if lanIP == "" {
				lanIP = ip4.String()
			}
		}
	}
	return
}

type tsStatus struct {
	Peer map[string]tsPeer `json:"Peer"`
}
type tsPeer struct {
	HostName     string   `json:"HostName"`
	OS           string   `json:"OS"`
	TailscaleIPs []string `json:"TailscaleIPs"`
	Online       bool     `json:"Online"`
}

func firstIPv4(ips []string) string {
	for _, s := range ips {
		if strings.Contains(s, ".") && !strings.Contains(s, ":") {
			return s
		}
	}
	return ""
}

// probeHearken reports whether a hearken host port is open on ip. Non-disruptive:
// the OS completes the TCP handshake into the listen backlog without stealing an
// in-progress client from an accept-first capturer.
func probeHearken(ip string, timeout time.Duration) bool {
	for _, p := range []int{hearPort, talkPort} {
		c, err := net.DialTimeout("tcp", fmt.Sprintf("%s:%d", ip, p), timeout)
		if err == nil {
			c.Close()
			return true
		}
	}
	return false
}

// DiscoverPeers lists online Tailscale peers with a hearken host port open — i.e.
// hosts this machine can connect to. Bound for the UI "Scan" button.
func (a *App) DiscoverPeers() []PeerInfo {
	if a.daemon != "" {
		var p []PeerInfo
		json.Unmarshal(a.rpc("DiscoverPeers"), &p)
		return p
	}
	type cand struct{ ip, name, os string }
	selfTS, selfLAN := selfIPs()
	self := map[string]bool{}
	if selfTS != "" {
		self[selfTS] = true
	}
	if selfLAN != "" {
		self[selfLAN] = true
	}
	seen := map[string]bool{}
	var cands []cand

	// 1) Tailscale peers (carry hostnames/OS)
	if out, err := run(6*time.Second, tailscaleBin(), "status", "--json"); err == nil {
		var st tsStatus
		if json.Unmarshal([]byte(out), &st) == nil {
			for _, peer := range st.Peer {
				if !peer.Online {
					continue
				}
				ip := firstIPv4(peer.TailscaleIPs)
				if ip == "" || self[ip] || seen[ip] {
					continue
				}
				seen[ip] = true
				cands = append(cands, cand{ip, peer.HostName, peer.OS})
			}
		}
	}
	// 2) LAN subnet hosts (no Tailscale needed)
	for _, ip := range lanCandidates() {
		if self[ip] || seen[ip] {
			continue
		}
		seen[ip] = true
		cands = append(cands, cand{ip, "", ""})
	}

	// Probe every candidate for an open hearken port, bounded concurrency.
	var wg sync.WaitGroup
	var mu sync.Mutex
	found := []PeerInfo{}
	sem := make(chan struct{}, 128)
	for _, c := range cands {
		wg.Add(1)
		sem <- struct{}{}
		go func(c cand) {
			defer wg.Done()
			defer func() { <-sem }()
			if probeHearken(c.ip, 500*time.Millisecond) {
				name := c.name
				if name == "" {
					name = "LAN host"
				}
				mu.Lock()
				found = append(found, PeerInfo{IP: c.ip, Name: name, OS: c.os})
				mu.Unlock()
			}
		}(c)
	}
	wg.Wait()
	sort.Slice(found, func(i, j int) bool { return found[i].IP < found[j].IP })
	logf("discover: %d hearken host(s) found of %d candidates", len(found), len(cands))
	return found
}

// lanCandidates returns every host IP on this machine's private /24-or-smaller
// subnets (for hearken discovery without Tailscale).
func lanCandidates() []string {
	var out []string
	addrs, _ := net.InterfaceAddrs()
	for _, a := range addrs {
		ipnet, ok := a.(*net.IPNet)
		if !ok {
			continue
		}
		ip4 := ipnet.IP.To4()
		if ip4 == nil || !ip4.IsPrivate() {
			continue
		}
		ones, bits := ipnet.Mask.Size()
		if bits != 32 || ones < 24 { // only scan subnets up to 256 addresses
			continue
		}
		base := binary.BigEndian.Uint32(ip4.Mask(ipnet.Mask))
		count := uint32(1) << uint(32-ones)
		for i := uint32(1); i < count-1; i++ { // skip network + broadcast
			c := make(net.IP, 4)
			binary.BigEndian.PutUint32(c, base+i)
			out = append(out, c.String())
		}
	}
	return out
}

// ---- start / stop (process supervision) --------------------------------

type role int

// Roles are defined by (transport side × audio leg), independent of OS:
//   hearPort (45000) carries the HOST's audio -> client.
//   talkPort (45001) carries the CLIENT's audio -> host.
const (
	roleHostCapServe   role = iota // host: capture my audio, SERVE on hearPort (listen+accept)
	roleHostPlayServe              // host: LISTEN on talkPort, play received audio
	roleClientPlayDial             // client: DIAL hearPort, play received audio
	roleClientCapDial              // client: DIAL talkPort, send my captured audio
)

// rolesForDirection picks this machine's roles from the direction + whether it is the host.
func rolesForDirection(dir string, host bool) []role {
	hostAudio, clientAudio := legsForDirection(dir)
	var rs []role
	if host {
		if hostAudio {
			rs = append(rs, roleHostCapServe)
		}
		if clientAudio {
			rs = append(rs, roleHostPlayServe)
		}
	} else {
		if hostAudio {
			rs = append(rs, roleClientPlayDial)
		}
		if clientAudio {
			rs = append(rs, roleClientCapDial)
		}
	}
	return rs
}

// Start launches the bridge per current config (idempotent).
func (a *App) Start() string {
	a.mu.Lock()
	if a.active {
		a.mu.Unlock()
		return "already running"
	}
	cfg := a.cfg
	if !isHost(cfg) && cfg.PeerIP == "" {
		a.mu.Unlock()
		return "Set the host's IP first."
	}
	if m := a.CheckDeps(); len(m) > 0 {
		a.mu.Unlock()
		return "Missing dependencies: " + strings.Join(m, ", ")
	}
	a.disableLegacyServices() // migration: don't let old launchd/task fight us
	ctx, cancel := context.WithCancel(context.Background())
	a.cancel = cancel
	a.active = true
	a.note = "starting…"
	roles := rolesForDirection(cfg.Direction, isHost(cfg))
	a.mu.Unlock()

	// Bridge Out (BlackHole multi-output) is only needed when this Mac captures its own audio.
	if runtime.GOOS == "darwin" && containsRole(roles, roleHostCapServe) {
		a.ensureBridgeOut()
	}
	for _, r := range roles {
		a.wg.Add(1)
		go a.supervise(ctx, r, cfg)
	}
	a.mu.Lock()
	a.note = "running"
	a.mu.Unlock()
	return "Started (" + cfg.Direction + ")"
}

// Stop tears down all child processes.
func (a *App) Stop() string {
	a.mu.Lock()
	c := a.cancel
	a.cancel = nil
	a.active = false
	a.note = "stopped"
	a.mu.Unlock()
	if c != nil {
		c()
	}
	a.wg.Wait()
	return "Stopped"
}

// supervise runs one role's child, restarting it if it exits while active.
func (a *App) supervise(ctx context.Context, r role, cfg Config) {
	defer a.wg.Done()
	for {
		if ctx.Err() != nil {
			return
		}
		cmd := a.buildCmd(ctx, r, cfg)
		if cmd == nil {
			// Unimplemented role for this OS (a stubbed same-OS cell). Don't spin.
			logf("supervise role=%d: no command on this OS — not supervising", r)
			return
		}
		hideWindow(cmd) // no console-window flash on Windows
		logf("supervise role=%d exec=%s args=%v", r, cmd.Path, cmd.Args[1:])
		d, _ := os.UserConfigDir()
		if lf, e := os.OpenFile(filepath.Join(d, "hearken", "hearken.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644); e == nil {
			cmd.Stdout, cmd.Stderr = lf, lf
			err := cmd.Run()
			lf.Close()
			logf("supervise role=%d exited err=%v", r, err)
		} else {
			err := cmd.Run()
			logf("supervise role=%d exited (no logfile) err=%v", r, err)
		}
		if ctx.Err() != nil {
			return
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(time.Second): // brief backoff, then relaunch
		}
	}
}

// buildCmd resolves a role to the actual child process for THIS OS. The matrix has
// four working cells (Mac host + Windows client, the original bridge) and three
// future cells stubbed with TODOs so same-OS pairing can be completed + tested later.
func (a *App) buildCmd(ctx context.Context, r role, cfg Config) *exec.Cmd {
	mac := runtime.GOOS == "darwin"
	switch r {

	case roleHostCapServe: // capture my system audio, serve it on hearPort
		if mac {
			c := exec.CommandContext(ctx, filepath.Join(abDir(), "hear-capture"),
				strconv.Itoa(hearPort), blackholeUID)
			c.Env = append(os.Environ(),
				fmt.Sprintf("BRIDGE_SNDBUF=%d", cfg.SndBufKB*1024),
				fmt.Sprintf("BRIDGE_AQ_BUF_BYTES=%d", cfg.CaptureMs*48*4))
			return c
		}
		// TODO(win-host): capture.exe needs a server mode:
		//   capture.exe --listen <port>  (WASAPI loopback -> accept TCP -> stream s16le/48k/stereo)
		logf("UNIMPLEMENTED: Windows host capture-serve (needs capture.exe --listen %d)", hearPort)
		return nil

	case roleHostPlayServe: // listen on talkPort, play received audio
		if mac {
			return ffmpegPlay(ctx, fmt.Sprintf("tcp://0.0.0.0:%d?listen=1", talkPort), a.realOutputIndex(), gainArg(cfg))
		}
		// TODO(win-host): play.exe needs a server mode:
		//   play.exe --listen <port>  (accept TCP -> WASAPI render to current default device)
		logf("UNIMPLEMENTED: Windows host play-serve (needs play.exe --listen %d)", talkPort)
		return nil

	case roleClientPlayDial: // dial hearPort, play received audio
		if !mac {
			// play.exe (NAudio/WASAPI) plays to the CURRENT default device and
			// re-binds on default-device change (BT headphones, device switch).
			return exec.CommandContext(ctx, playExe(), cfg.PeerIP, strconv.Itoa(hearPort), gainArg(cfg), playoutArg(cfg))
		}
		// Mac client: ffmpeg dials the host and plays to the real output device.
		return ffmpegPlay(ctx, fmt.Sprintf("tcp://%s:%d", cfg.PeerIP, hearPort), a.realOutputIndex(), gainArg(cfg))

	case roleClientCapDial: // dial talkPort, send my captured audio
		if !mac {
			return exec.CommandContext(ctx, captureExe(), cfg.PeerIP, strconv.Itoa(talkPort))
		}
		// TODO(mac-client): hear-capture needs a dial mode:
		//   connect to <peer>:<talkPort> and stream BlackHole, instead of listening.
		logf("UNIMPLEMENTED: Mac client capture-dial (needs hear-capture dial mode to %s:%d)", cfg.PeerIP, talkPort)
		return nil
	}
	return nil
}

// ffmpegPlay reads s16le/48k/stereo from a TCP input (dial "tcp://host:port" or
// listen "tcp://0.0.0.0:port?listen=1") and renders it to the macOS audiotoolbox
// device, applying playback gain (0.000–1.000) when below unity.
func ffmpegPlay(ctx context.Context, input, deviceIdx, gain string) *exec.Cmd {
	args := []string{
		"-hide_banner", "-loglevel", "warning", "-nostdin",
		"-fflags", "nobuffer", "-flags", "low_delay",
		"-f", "s16le", "-ar", "48000", "-ch_layout", "stereo",
		"-i", input,
	}
	if gain != "" && gain != "1.000" {
		args = append(args, "-af", "volume="+gain)
	}
	args = append(args, "-f", "audiotoolbox", "-audio_device_index", deviceIdx, "-y", os.DevNull)
	return exec.CommandContext(ctx, ffmpegPath(), args...)
}

func (a *App) realOutputIndex() string {
	out, err := run(4*time.Second, filepath.Join(abDir(), "find-output-index"))
	if err == nil {
		if v := strings.TrimSpace(out); v != "" {
			return v
		}
	}
	return "0"
}

func (a *App) ensureBridgeOut() {
	if !bridgeOutIsDefault() {
		run(8*time.Second, filepath.Join(abDir(), "make-bridge-out"))
	}
}

// disableLegacyServices unloads the old launchd agents / scheduled task so they
// don't double-bind the ports against the app-managed children.
func (a *App) disableLegacyServices() {
	if runtime.GOOS == "darwin" {
		uid := strconv.Itoa(os.Getuid())
		run(5*time.Second, "launchctl", "bootout", "gui/"+uid+"/com.shane.audiobridge.hear")
		run(5*time.Second, "launchctl", "bootout", "gui/"+uid+"/com.shane.audiobridge.talk")
		// sweep orphaned bridge children from a previously crashed/killed daemon
		run(3*time.Second, "pkill", "-f", "hear-capture "+strconv.Itoa(hearPort))
		run(3*time.Second, "pkill", "-f", fmt.Sprintf("tcp://0.0.0.0:%d?listen=1", talkPort))
	} else {
		run(8*time.Second, "schtasks", "/End", "/TN", "HearMac")
		run(8*time.Second, "schtasks", "/Change", "/TN", "HearMac", "/DISABLE")
		run(5*time.Second, "taskkill", "/IM", "ffplay.exe", "/F")
		// sweep orphaned bridge children from a previously crashed/killed daemon
		run(5*time.Second, "taskkill", "/IM", "play.exe", "/F")
		run(5*time.Second, "taskkill", "/IM", "capture.exe", "/F")
	}
}

// ---- config-changing bound methods -------------------------------------

func (a *App) GetConfig() Config {
	if a.daemon != "" {
		var c Config
		json.Unmarshal(a.rpc("GetConfig"), &c)
		return c
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.cfg
}

func (a *App) SetPeerIP(ip string) string {
	if a.daemon != "" {
		var r string
		json.Unmarshal(a.rpc("SetPeerIP", ip), &r)
		return r
	}
	a.mu.Lock()
	a.cfg.PeerIP = strings.TrimSpace(ip)
	a.saveConfig()
	a.mu.Unlock()
	return a.restart()
}

func (a *App) SetDirection(dir string) string {
	if a.daemon != "" {
		var r string
		json.Unmarshal(a.rpc("SetDirection", dir), &r)
		return r
	}
	a.mu.Lock()
	a.cfg.Direction = dir
	a.saveConfig()
	a.mu.Unlock()
	return a.restart()
}

// SetRole switches whether this machine is the host (listens) or client (dials).
// "" (or anything else) = auto: host on macOS, client elsewhere.
func (a *App) SetRole(r string) string {
	if a.daemon != "" {
		var res string
		json.Unmarshal(a.rpc("SetRole", r), &res)
		return res
	}
	a.mu.Lock()
	if r != "host" && r != "client" {
		r = ""
	}
	a.cfg.Role = r
	a.saveConfig()
	a.mu.Unlock()
	return a.restart()
}

func (a *App) ApplyParams(sndKB, captureMs, recvKB, playoutMs int) string {
	if a.daemon != "" {
		var r string
		json.Unmarshal(a.rpc("ApplyParams", sndKB, captureMs, recvKB, playoutMs), &r)
		return r
	}
	a.mu.Lock()
	if sndKB >= 4 {
		a.cfg.SndBufKB = sndKB
	}
	if captureMs >= 3 {
		a.cfg.CaptureMs = captureMs
	}
	if recvKB >= 4 {
		a.cfg.RecvBufKB = recvKB
	}
	if playoutMs >= 50 {
		a.cfg.PlayoutMs = playoutMs
	}
	a.saveConfig()
	a.mu.Unlock()
	return a.restart()
}

// SetVolume sets this device's playback gain (0-100) and restarts the bridge.
func (a *App) SetVolume(pct int) string {
	if a.daemon != "" {
		var r string
		json.Unmarshal(a.rpc("SetVolume", pct), &r)
		return r
	}
	a.mu.Lock()
	if pct < 0 {
		pct = 0
	}
	if pct > 100 {
		pct = 100
	}
	a.cfg.VolumePct = pct
	a.saveConfig()
	a.mu.Unlock()
	return a.restart()
}

func (a *App) SetAutoStart(on bool) string {
	if a.daemon != "" {
		var r string
		json.Unmarshal(a.rpc("SetAutoStart", on), &r)
		return r
	}
	a.mu.Lock()
	a.cfg.AutoStart = on
	a.saveConfig()
	a.mu.Unlock()
	return "saved"
}

// Toggle starts or stops the bridge.
func (a *App) Toggle() string {
	if a.daemon != "" {
		var r string
		json.Unmarshal(a.rpc("Toggle"), &r)
		return r
	}
	a.mu.Lock()
	on := a.active
	a.mu.Unlock()
	if on {
		return a.Stop()
	}
	return a.Start()
}

func (a *App) restart() string {
	a.mu.Lock()
	wasActive := a.active
	a.mu.Unlock()
	if wasActive {
		a.Stop()
		return a.Start()
	}
	return "saved"
}

// Verify pings the peer and checks for an active stream.
func (a *App) Verify() string {
	if a.daemon != "" {
		var r string
		json.Unmarshal(a.rpc("Verify"), &r)
		return r
	}
	a.mu.Lock()
	cfg := a.cfg
	a.mu.Unlock()
	pip := cfg.PeerIP
	host := isHost(cfg)

	// The host listens (the peer dials in), so no peer IP is required here.
	if host && pip == "" {
		var b strings.Builder
		if portListening(hearPort) || portListening(talkPort) {
			b.WriteString("This machine is the host — the peer connects to it (no peer IP needed here). ")
		} else {
			b.WriteString("This machine is the host, but nothing is listening yet — press Start. ")
		}
		if portEstablished(hearPort) || portEstablished(talkPort) {
			b.WriteString("Peer connected, audio stream active.")
		} else {
			b.WriteString("Waiting for a peer to connect.")
		}
		return b.String()
	}
	if pip == "" {
		return "Enter the host IP above, then Verify."
	}
	ms := pingPeer(pip)
	var b strings.Builder
	if ms >= 0 {
		fmt.Fprintf(&b, "Peer %s reachable (%d ms). ", pip, ms)
	} else {
		fmt.Fprintf(&b, "Peer %s NOT reachable via Tailscale/LAN. ", pip)
	}
	connected := false
	if host {
		connected = portEstablished(hearPort) || portEstablished(talkPort)
	} else {
		connected = connEstablishedToPeer(pip, hearPort) || connEstablishedToPeer(pip, talkPort)
	}
	if connected {
		b.WriteString("Audio stream connected.")
	} else {
		b.WriteString("No active audio stream yet.")
	}
	return b.String()
}

// ---- platform probes (OS-aware: lsof on macOS, netstat on Windows) -------

// portListening: is a local TCP port in LISTEN state (host serving)?
func portListening(port int) bool {
	if runtime.GOOS == "windows" {
		return netstatHas(fmt.Sprintf(":%d", port), "LISTENING")
	}
	out, _ := run(3*time.Second, "lsof", "-nP", fmt.Sprintf("-iTCP:%d", port), "-sTCP:LISTEN")
	return strings.Contains(out, "LISTEN")
}

// portEstablished: is a peer connected to my local port (host side)?
func portEstablished(port int) bool {
	if runtime.GOOS == "windows" {
		return netstatHas(fmt.Sprintf(":%d", port), "ESTABLISHED")
	}
	out, _ := run(3*time.Second, "lsof", "-nP", fmt.Sprintf("-iTCP:%d", port), "-sTCP:ESTABLISHED")
	return strings.Contains(out, "ESTABLISHED")
}

// connEstablishedToPeer: am I (client) connected out to peer:port?
func connEstablishedToPeer(pip string, port int) bool {
	if pip == "" {
		return false
	}
	if runtime.GOOS == "windows" {
		return netstatHas(fmt.Sprintf("%s:%d", pip, port), "ESTABLISHED")
	}
	out, _ := run(3*time.Second, "lsof", "-nP", fmt.Sprintf("-iTCP@%s:%d", pip, port), "-sTCP:ESTABLISHED")
	return strings.Contains(out, "ESTABLISHED")
}

func netstatHas(needle, state string) bool {
	out, _ := run(3*time.Second, "netstat", "-an")
	st := strings.ToUpper(state)
	for _, l := range strings.Split(out, "\n") {
		if strings.Contains(l, needle) && strings.Contains(strings.ToUpper(l), st) {
			return true
		}
	}
	return false
}

func bridgeOutIsDefault() bool {
	out, _ := run(5*time.Second, "system_profiler", "SPAudioDataType")
	inBridge := false
	for _, l := range strings.Split(out, "\n") {
		t := strings.TrimSpace(l)
		if strings.HasSuffix(t, ":") && !strings.Contains(t, ": ") {
			inBridge = strings.EqualFold(t, "Bridge Out:")
		}
		if inBridge && strings.Contains(l, "Default Output Device: Yes") {
			return true
		}
	}
	return false
}

func tailscaleBin() string {
	if runtime.GOOS == "darwin" {
		for _, p := range []string{"/opt/homebrew/bin/tailscale", "/usr/local/bin/tailscale", "/Applications/Tailscale.app/Contents/MacOS/Tailscale"} {
			if exists(p) {
				return p
			}
		}
	}
	return "tailscale"
}

func pingPeer(pip string) int {
	out, err := run(6*time.Second, tailscaleBin(), "ping", "-c", "1", pip)
	if err != nil {
		return -1
	}
	for _, tok := range strings.Fields(out) {
		if strings.HasSuffix(tok, "ms") {
			if n, err := strconv.Atoi(strings.TrimSuffix(tok, "ms")); err == nil {
				return n
			}
		}
	}
	return -1
}
