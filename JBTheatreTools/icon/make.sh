#!/bin/bash
###############################################################################
# Regenerates all app icons from make_icon.swift.
#   icon_1024.png                       -> master art (Swift/AppKit)
#   ../AppIcon.icns                     -> macOS app icon (iconutil)
#   ../../JBTheatreToolsWin/app.ico     -> Windows app icon (PIL)
# Run:  bash make.sh   (macOS only — needs swift, sips, iconutil, python3+Pillow)
###############################################################################
set -euo pipefail
cd "$(dirname "$0")"

echo "==> Rendering master art…"
swift make_icon.swift

echo "==> Building AppIcon.iconset…"
ICONSET="AppIcon.iconset"
rm -rf "$ICONSET"; mkdir "$ICONSET"
sips -z 16   16   icon_1024.png --out "$ICONSET/icon_16x16.png"      >/dev/null
sips -z 32   32   icon_1024.png --out "$ICONSET/icon_16x16@2x.png"   >/dev/null
sips -z 32   32   icon_1024.png --out "$ICONSET/icon_32x32.png"      >/dev/null
sips -z 64   64   icon_1024.png --out "$ICONSET/icon_32x32@2x.png"   >/dev/null
sips -z 128  128  icon_1024.png --out "$ICONSET/icon_128x128.png"    >/dev/null
sips -z 256  256  icon_1024.png --out "$ICONSET/icon_128x128@2x.png" >/dev/null
sips -z 256  256  icon_1024.png --out "$ICONSET/icon_256x256.png"    >/dev/null
sips -z 512  512  icon_1024.png --out "$ICONSET/icon_256x256@2x.png" >/dev/null
sips -z 512  512  icon_1024.png --out "$ICONSET/icon_512x512.png"    >/dev/null
cp icon_1024.png "$ICONSET/icon_512x512@2x.png"

echo "==> iconutil -> ../AppIcon.icns"
iconutil -c icns "$ICONSET" -o ../AppIcon.icns

echo "==> PIL -> ../../JBTheatreToolsWin/app.ico"
python3 - <<'PY'
from PIL import Image
img = Image.open("icon_1024.png").convert("RGBA")
sizes = [(16,16),(32,32),(48,48),(64,64),(128,128),(256,256)]
img.save("../../JBTheatreToolsWin/app.ico", format="ICO", sizes=sizes)
print("Wrote app.ico")
PY

echo "==> Done."
