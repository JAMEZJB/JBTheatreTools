#!/bin/bash
###############################################################################
# Builds JBTheatreTools and assembles a double-clickable .app bundle.
# Output: JBTheatreTools.app (universal arm64 + x86_64, ad-hoc signed).
# Usage:  bash build.sh
###############################################################################
set -euo pipefail

cd "$(dirname "$0")"

APP="JB Theatre Tools.app"   # spaced, user-facing bundle name (Finder/Dock show "JB Theatre Tools")
BIN_NAME="JBTheatreTools"    # CFBundleExecutable (binary name stays compact)
CATALOG="../catalog.json"   # shared catalog at repo root

echo "==> Building universal release binary (arm64 + x86_64)…"
swift build -c release --arch arm64 --arch x86_64

BIN_PATH="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/$BIN_NAME"
if [ ! -f "$BIN_PATH" ]; then
    echo "Build did not produce $BIN_PATH" >&2
    exit 1
fi

echo "==> Assembling $APP…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_PATH" "$APP/Contents/MacOS/$BIN_NAME"
cp "Info.plist" "$APP/Contents/Info.plist"
cp "$CATALOG" "$APP/Contents/Resources/catalog.json"
if [ -f "AppIcon.icns" ]; then
    cp "AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
fi
# Per-app row icons (shown in the list before an app is installed). Bundled flat in Resources, named
# <catalog-id>.png; the launcher loads them by id and falls back to a monogram if one is missing.
if ls ../icons/*.png >/dev/null 2>&1; then
    cp ../icons/*.png "$APP/Contents/Resources/"
fi

echo "==> Ad-hoc code signing…"
codesign --force --deep --sign - "$APP"

echo "==> Done: $(pwd)/$APP"
echo "    Launch with:  open \"$(pwd)/$APP\""
