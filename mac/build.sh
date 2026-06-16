#!/bin/bash
# Compile the Mac-side Swift helpers. Requires Xcode Command Line Tools:
#   xcode-select --install
set -e
cd "$(dirname "$0")"
swiftc -O hear-capture.swift      -o hear-capture      -framework AudioToolbox -framework Foundation
swiftc -O find-output-index.swift -o find-output-index -framework CoreAudio    -framework Foundation
swiftc -O make-bridge-out.swift   -o make-bridge-out    -framework CoreAudio    -framework Foundation
swiftc -O setdef.swift            -o setdef             -framework CoreAudio    -framework Foundation
echo "built: hear-capture, find-output-index, make-bridge-out, setdef"
