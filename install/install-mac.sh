#!/usr/bin/env bash
# hearken — macOS dependency + setup installer.
# Installs everything the app needs, compiles the native helpers, then builds +
# signs hearken.app with a stable cert (so the mic grant persists across rebuilds).
#
# Assumes the helper SOURCES are vendored into the repo at mac/ (build.sh + *.swift).
# One manual step remains at the end: launch the app and click "Allow" on the mic
# prompt (TCC requires a human click — it cannot be scripted).
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
AB="$HOME/audio-bridge"
mkdir -p "$AB" "$HOME/bin"
say(){ printf "\n\033[1;36m== %s ==\033[0m\n" "$1"; }

say "1/7  Homebrew · ffmpeg · BlackHole 2ch"
command -v brew >/dev/null || { echo "Install Homebrew (https://brew.sh) then re-run." >&2; exit 1; }
command -v ffmpeg >/dev/null || brew install ffmpeg
if [ ! -e "/Library/Audio/Plug-Ins/HAL/BlackHole2ch.driver" ]; then
  brew install --cask blackhole-2ch || {
    echo "brew cask failed (wedged Spotlight/mds is a known cause). Download the signed pkg from"
    echo "https://existential.audio/blackhole/ and run it manually, then re-run this script." >&2
    exit 1; }
fi

say "2/7  Compile native helpers -> $AB"
command -v swiftc >/dev/null || { echo "Xcode CLT missing — run 'xcode-select --install' then re-run." >&2; exit 1; }
bash "$REPO/mac/build.sh"   # compiles with the right -framework flags
for f in hear-capture make-bridge-out find-output-index setdef; do
  [ -f "$REPO/mac/$f" ] && cp "$REPO/mac/$f" "$AB/$f" && echo "  installed $AB/$f"
done

say "3/7  Stable code-signing cert (persistent mic grant)"
security find-identity -p codesigning | grep -qi hearken-selfsign || bash "$REPO/scripts/make-signing-cert.sh"

say "4/7  Build + sign hearken.app"
if [ -x "$HOME/go/bin/wails" ] || command -v wails >/dev/null; then
  bash "$REPO/scripts/build-mac.sh"
else
  echo "  Go/Wails not found. Either:"
  echo "   - install Go + 'go install github.com/wailsapp/wails/v2/cmd/wails@latest', or"
  echo "   - drop a prebuilt signed hearken.app into $REPO/build/bin/ (from a GitHub release)."
fi

say "5/7  Install to /Applications (Spotlight, Raycast, Launchpad)"
BUILT="$REPO/build/bin/hearken.app"
APP="/Applications/hearken.app"
if [ -d "$BUILT" ]; then
  # A copy, not a symlink: LaunchServices and Spotlight do not index symlinked
  # bundles, so a link leaves hearken unfindable in Spotlight/Raycast/Launchpad.
  # ditto preserves the signature, so the mic grant carries over to the copy.
  pkill -f "$APP/Contents/MacOS/hearken" 2>/dev/null || true
  rm -rf "$APP"
  ditto "$BUILT" "$APP"
  xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
  /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP" || true
  echo "  installed $APP"
else
  echo "  no app at $BUILT — build it (step 4), then re-run."
fi

say "6/7  Install login agent (headless daemon + menubar icon)"
PLIST="$HOME/Library/LaunchAgents/com.hearken.daemon.plist"
# Run the installed copy, not the build output, so the daemon and anything
# launched from Spotlight/Raycast are the same binary.
APP_BIN="$APP/Contents/MacOS/hearken"
[ -d "$APP" ] || APP_BIN="$BUILT/Contents/MacOS/hearken"
cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>com.hearken.daemon</string>
  <key>ProgramArguments</key><array><string>$APP_BIN</string></array>
  <key>RunAtLoad</key><true/>
  <!-- restart only on crash, so the tray "Quit" actually stays quit -->
  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
  <key>ProcessType</key><string>Interactive</string>
  <key>LimitLoadToSessionType</key><string>Aqua</string>
</dict>
</plist>
EOF
launchctl unload "$PLIST" 2>/dev/null || true
launchctl load "$PLIST" && echo "  loaded com.hearken.daemon (starts at login; runs headless with a menubar icon)"

say "7/7  Done — finish by hand"
TSIP="$(/opt/homebrew/bin/tailscale ip -4 2>/dev/null || echo '(install + log in to Tailscale)')"
LANIP="$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || echo 'n/a')"
cat <<EOF
  • The daemon is now running headless with a hearken icon in the menubar.
  • Click the menubar icon -> "Open hearken" to configure; click "Allow" on the
    microphone prompt ONCE (the cert makes it persist).
  • This Mac defaults to HOST (it listens); the other device dials in.
  • Give the other device THIS machine's address:
        Tailscale: $TSIP
        LAN:       $LANIP
EOF
