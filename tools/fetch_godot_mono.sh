#!/usr/bin/env bash
# Fetches the Godot 4.7 stable Linux .NET-enabled ("mono") editor binary into
# tools/godot4_mono/. No sudo/snap required -- same root-free pattern as
# fetch_godot.sh. This is the build the in-engine C# sim host (D14/D15)
# needs; the plain fetch_godot.sh binary has no .NET support at all.
set -euo pipefail
cd "$(dirname "$0")"

URL="https://github.com/godotengine/godot/releases/download/4.7-stable/Godot_v4.7-stable_mono_linux_x86_64.zip"

curl -sL -o godot_mono.zip "$URL"
rm -rf godot4_mono
unzip -o -q godot_mono.zip -d godot4_mono
rm godot_mono.zip
BIN="godot4_mono/Godot_v4.7-stable_mono_linux_x86_64/Godot_v4.7-stable_mono_linux.x86_64"
chmod +x "$BIN"
ln -sf "Godot_v4.7-stable_mono_linux_x86_64/Godot_v4.7-stable_mono_linux.x86_64" godot4_mono/godot4_mono
./godot4_mono/godot4_mono --version
