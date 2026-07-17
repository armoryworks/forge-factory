#!/usr/bin/env bash
# Fetches the Godot 4.7 stable Linux editor binary into tools/godot4.
# No sudo/snap required — see docs/inventory.md for why this replaced the
# originally-planned `snap install godot-4`.
set -euo pipefail
cd "$(dirname "$0")"

URL="https://github.com/godotengine/godot/releases/download/4.7-stable/Godot_v4.7-stable_linux.x86_64.zip"

curl -sL -o godot.zip "$URL"
unzip -o -q godot.zip
mv Godot_v4.7-stable_linux.x86_64 godot4
rm godot.zip
chmod +x godot4
./godot4 --version
