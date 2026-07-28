#!/usr/bin/env bash
# Build hearken for macOS and sign it with the stable self-signed cert so the
# microphone (TCC) grant persists across rebuilds. Run scripts/make-signing-cert.sh
# once first to create the identity. Pass extra args straight through to wails build.
set -euo pipefail
cd "$(dirname "$0")/.."
NAME="${HEARKEN_SIGN_ID:-hearken-selfsign}"
WAILS="${WAILS:-$HOME/go/bin/wails}"
APP="build/bin/hearken.app"

# Wails reads the macOS application icon only from build/appicon.png.
# Keep that generated build input tied to the canonical Hearken artwork.
cp assets/icon-1024.png build/appicon.png

# ensure the Go toolchain is on PATH for wails
for g in "$HOME/sdk/go/bin" "$HOME/go/bin" /usr/local/go/bin /opt/homebrew/bin; do
  [ -x "$g/go" ] && export PATH="$g:$PATH" && break
done

if ! security find-identity -p codesigning | grep -qi "$NAME"; then
  echo "Signing identity '$NAME' not found — run scripts/make-signing-cert.sh first." >&2
  exit 1
fi

"$WAILS" build "$@"
codesign --force --deep -s "$NAME" --identifier com.wails.hearken "$APP"
echo "--- signature ---"
codesign -dv "$APP" 2>&1 | grep -iE 'Authority=|Identifier=' || true
echo "Built + signed $APP with '$NAME'."
