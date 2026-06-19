#!/bin/bash
###############################################################################
# Cross-builds JB Theatre Tools for Windows from macOS/Linux.
# Produces self-contained single .exe files (no .NET install needed on Windows).
#   dist/win-x64/JBTheatreTools.exe    -> Intel/AMD PCs (+ ARM64 via emulation)
#   dist/win-arm64/JBTheatreTools.exe  -> native ARM64 PCs (Surface, etc.)
# Usage:  bash build-win.sh
###############################################################################
set -euo pipefail
cd "$(dirname "$0")"

COMMON=(-c Release
        --self-contained true
        -p:PublishSingleFile=true
        -p:IncludeNativeLibrariesForSelfExtract=true
        -p:EnableCompressionInSingleFile=true
        -p:DebugType=none
        -p:DebugSymbols=false)

rm -rf dist
for RID in win-x64 win-arm64; do
    echo "==> Publishing $RID…"
    dotnet publish "${COMMON[@]}" -r "$RID" -o "dist/$RID"
done

echo "==> Done:"
ls -lh dist/win-x64/JBTheatreTools.exe dist/win-arm64/JBTheatreTools.exe
