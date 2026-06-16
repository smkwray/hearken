# hearken — new-device setup (agent prompt)

Paste this to an agent (e.g. Claude Code) running ON the new device. It sets up **hearken**,
a system-audio bridge between two machines (currently Mac⇄Windows; same-OS is scaffolded).

---

You are setting up **hearken** on a new device. Install dependencies and get audio flowing,
as turnkey as possible. The app assumes its dependencies are present — your job is to install
them, then build/place the app.

**1. Detect the OS and clone/copy this repo locally.**

**2. macOS** — run `bash install/install-mac.sh`. It installs **BlackHole 2ch** + **ffmpeg**,
compiles the native helpers (`hear-capture`, `make-bridge-out`, `find-output-index`) into
`~/audio-bridge/`, creates a stable code-signing cert, and builds + signs `hearken.app`.
- BlackHole is a HAL driver and needs an **admin** install; if `brew --cask` hangs (wedged
  Spotlight/`mds`), install the signed pkg from https://existential.audio/blackhole/ directly.
  The installer also loads a **login agent** that runs hearken **headless** (a menubar icon, no
  window). Tell the user to click the **menubar icon → Open hearken**, then click **Allow** on the
  microphone prompt. This is the ONE step you cannot script (macOS TCC requires a human click).
- **Verify capture is real**, not silence: read the host's `:45000` stream and check the s16le
  sample **min/max** — `min=0 max=0` means the mic grant didn't take (the #1 failure mode).

**3. Windows** — run `powershell -ExecutionPolicy Bypass -File install\install-windows.ps1`.
It compiles `capture.exe` + `play.exe` (NAudio) into `%USERPROFILE%\audio-bridge\lib\`, builds
`hearken.exe`, and registers a **logon task** that runs the daemon headless (system-tray icon).
Needs .NET Framework 4.x (present on Win10/11).

**4. Networking** — install **Tailscale** and log in on both devices (gives each a stable
`100.x.y.z` that traverses NAT and prefers a direct LAN path), **or** use a plain LAN IP.

**5. Pair** — one device is **Host** (listens), the other **Client** (dials). Auto picks host on
macOS, client elsewhere; for same-OS pairs set one to Host and one to Client explicitly. Click the
**menubar/tray icon → Open hearken**; on the **client**, type the **host's** IP into "Peer IP"
(or press **Scan**), Save. The host auto-starts; the client auto-connects on every login.

The app runs as a **headless daemon + tray** (one binary, two modes): no args = daemon (bridge +
tray + control API on 127.0.0.1:47600, no WebView ≈ 25 MB); `--window` = the config window
(proxies to the daemon, frees its WebView on close).

**6. Verify end-to-end** — play audio on one device, confirm it's audible on the other. If
Windows is silent, the cause is almost always the macOS host's mic permission (step 2 verify).

## Notes / gotchas
- Don't add an explicit resampler before `WasapiOut` on Windows — feed raw 48k/16/2 and let
  WasapiOut's internal DMO resampler convert (an explicit MediaFoundationResampler renders
  inaudibly to BT endpoints).
- Same-OS (Win↔Win, Mac↔Mac) needs helper modes that are **stubbed** — see the `UNIMPLEMENTED`
  TODOs in `app.go` `buildCmd` (`capture.exe --listen`, `play.exe --listen`, `hear-capture` dial).
