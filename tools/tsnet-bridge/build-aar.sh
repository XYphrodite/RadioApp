#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT_AAR="$SCRIPT_DIR/tsnet.aar"
export ANDROID_HOME="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}"
export ANDROID_SDK="$ANDROID_HOME"

echo "==> tsnet-bridge: Go"
go version

echo "==> gomobile"
if ! command -v gomobile >/dev/null 2>&1; then
  echo "gomobile not found — installing"
  go install golang.org/x/mobile/cmd/gomobile@latest
  go install golang.org/x/mobile/cmd/gobind@latest
  export PATH="$PATH:$(go env GOPATH)/bin"
fi
gomobile version || gomobile init

if [ ! -d "$ANDROID_SDK" ]; then
  echo "WARN: Android SDK not found at $ANDROID_SDK — build may fail"
fi

cd "$SCRIPT_DIR"
echo "==> go mod tidy"
go mod tidy

echo "==> gomobile bind -target android/arm64,android/amd64 -androidapi 21 -o $OUTPUT_AAR"
gomobile bind -target android/arm64,android/amd64 -androidapi 21 \
  -ldflags='-checklinkname=0 -extldflags=-Wl,-z,max-page-size=16384,-z,common-page-size=16384' \
  -o "$OUTPUT_AAR" ./...

if [ ! -f "$OUTPUT_AAR" ]; then
  echo "AAR not created: $OUTPUT_AAR" >&2
  exit 1
fi
ls -lh "$OUTPUT_AAR"

DEST_DIR="$SCRIPT_DIR/../../src/RadioApp.Maui/Platforms/Android/libs"
mkdir -p "$DEST_DIR"
cp "$OUTPUT_AAR" "$DEST_DIR/tsnet.aar"
echo "==> Copied to $DEST_DIR/tsnet.aar"
