#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-0.1.0}"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
    echo "Version must be a semantic version without the v prefix." >&2
    exit 1
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "macOS and the Xcode command line tools are required." >&2
    exit 1
fi

ARCH="$(uname -m)"
OUTPUT="$ROOT/dist"
STAGING="$(mktemp -d "${TMPDIR:-/tmp}/pindesk-package.XXXXXX")"
trap 'rm -rf -- "$STAGING"' EXIT
APP="$STAGING/PinDesk.app"
swift build --package-path "$ROOT/apps/macos" -c release
BIN_DIR="$(swift build --package-path "$ROOT/apps/macos" -c release --show-bin-path)"
mkdir -p "$APP/Contents/MacOS" "$OUTPUT"
cp "$BIN_DIR/PinDesk" "$APP/Contents/MacOS/PinDesk"
chmod +x "$APP/Contents/MacOS/PinDesk"
cp "$ROOT/apps/macos/Info.plist" "$APP/Contents/Info.plist"
# CFBundleShortVersionString requires a numeric release version; keep prerelease details in the archive name.
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString ${VERSION%%-*}" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion ${GITHUB_RUN_NUMBER:-1}" "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"
# Swift toolchains may emit an ad-hoc signature for arm64. This is not a Developer ID signature or notarization.
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUTPUT/PinDesk-macos-$ARCH-$VERSION.zip"
echo "Created $OUTPUT/PinDesk-macos-$ARCH-$VERSION.zip (development preview; not Developer ID signed or notarized)"
