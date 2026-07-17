#!/usr/bin/env bash
# B57 — the canonical way to run game/'s boot checks.
#
# WHY THIS EXISTS: the exit code lies.
#
# main.tscn references res://SimHubClient.cs (B55). Run under the STANDARD Godot build,
# which has no .NET module, Godot prints
#     ERROR: No loader found for resource: res://SimHubClient.cs
#     ERROR: res://scenes/main.tscn:29 - Parse Error: [ext_resource] referenced ...
# then DROPS the SimHubClient and SimState nodes, runs whatever checks remain, and exits
# 0. Every surviving check passes. To any caller that reads $? -- CI, a script, a human
# skimming -- that is a clean green run, with the sim feed silently absent from the scene.
#
# A dropped node cannot fail a check it never ran. So this wrapper does not trust the exit
# code: it asserts on the OUTPUT.
#
# Usage:
#   tools/run_checks.sh                       # canonical: mono binary, main scene
#   tools/run_checks.sh --binary tools/godot4 # negative control: must FAIL
#   tools/run_checks.sh --render-checks       # windowed FILTER/OVERLAY pass
set -uo pipefail
cd "$(dirname "$0")/.."

# The mono binary, not tools/godot4. main.tscn attaches a C# script, so the standard
# build cannot load it -- see mono-conversion-notes.md step 4.
BINARY="tools/godot4_mono/godot4_mono"
RENDER_CHECKS=0

while [ $# -gt 0 ]; do
	case "$1" in
		--binary) BINARY="$2"; shift 2 ;;
		--render-checks) RENDER_CHECKS=1; shift ;;
		*) echo "unknown arg: $1" >&2; exit 2 ;;
	esac
done

# Every check the main scene must emit. This list is the real assertion: a node that
# silently fails to load emits nothing, and "nothing" is exactly what the exit code
# cannot see. Naming them individually means a dropped node is a named failure rather
# than an absence nobody notices.
#
# SIM_STATE_CHECK is the canary for the C#-loader trap specifically: it lives on the
# SimState node, which is the node that disappears when SimHubClient.cs will not load.
EXPECTED_CHECKS=(
	"ISO_CHECK"
	"DEPTH_CHECK"
	"PLACE_CHECK"
	"BELT_CHECK"
	"HOVER_CHECK"
	"SIM_STATE_CHECK"
	"BELT_SYNC_CHECK"
	"BELT_ITEMS_CHECK"
	"HUD_CHECK"
	"REJECTION_CHECK"
)
if [ "$RENDER_CHECKS" -eq 1 ]; then
	# SIM_CHECK is deliberately ABSENT in this mode, not missing: render_checks.gd's
	# per-frame GPU readbacks starve the frame loop, so main.gd suppresses SIM_CHECK
	# rather than let it report a real slowdown caused entirely by the harness (B46).
	# The two cannot share a run. Expecting it here would make this wrapper demand a
	# check that is correctly not there -- which it did on first write, and which this
	# wrapper caught.
	EXPECTED_CHECKS+=("FILTER_CHECK" "OVERLAY_CHECK")
else
	EXPECTED_CHECKS+=("SIM_CHECK")
fi

if [ ! -x "$BINARY" ]; then
	echo "run_checks: binary not found or not executable: $BINARY" >&2
	echo "run_checks: fetch it with tools/fetch_godot_mono.sh" >&2
	exit 2
fi

OUT="$(mktemp)"
trap 'rm -f "$OUT"' EXIT

if [ "$RENDER_CHECKS" -eq 1 ]; then
	# --disable-vsync is NOT a perf tweak here, it is a correctness requirement for any
	# windowed run on this box (B40/B47). With vsync on and no compositor presenting, each
	# swapchain present blocks ~1s: 60 frames takes 59.6s (~1 fps) on an idle machine.
	# Measured, same 60 frames, same moment: vsync on 59.64s -> vsync off 1.00s.
	#
	# It does not affect what this harness measures. FILTER_CHECK/OVERLAY_CHECK read
	# framebuffer CONTENT; vsync governs presentation timing only, so the pixels are
	# identical either way -- it just stops the run taking a minute per 60 frames.
	timeout 180 "$BINARY" --disable-vsync --path game -- --render-checks > "$OUT" 2>&1
else
	timeout 120 "$BINARY" --headless --path game > "$OUT" 2>&1
fi
GODOT_EXIT=$?

fail=0
note() { echo "  [FAIL] $1"; fail=1; }
ok()   { echo "  [PASS] $1"; }

echo "run_checks: $BINARY"

# 1. Resource-loader errors. This is the exact signature of the silent-pass trap, and it
#    never reaches the exit code.
if grep -q "No loader found" "$OUT"; then
	note "no 'No loader found' in output -- a script failed to load and its node was DROPPED"
	grep -n "No loader found\|Parse Error" "$OUT" | head -3 | sed 's/^/         /'
else
	ok "no 'No loader found' in output"
fi

# 2. Every expected check actually ran. Catches a dropped node generally, not just the
#    C# case: any node that fails to load stops emitting, and silence is not success.
for c in "${EXPECTED_CHECKS[@]}"; do
	if grep -q "^${c} " "$OUT"; then
		ok "$c emitted"
	else
		note "$c did not run -- its node is missing, or it crashed before reporting"
	fi
done

# 3. No check reported FAIL. Cheap, and the reason the checks print a verdict at all.
if grep -q "result=FAIL" "$OUT"; then
	note "a check reported result=FAIL"
	grep -n "result=FAIL" "$OUT" | head -5 | sed 's/^/         /'
else
	ok "no check reported result=FAIL"
fi

# 4. And only now, the exit code -- last, because it is the weakest signal here.
if [ "$GODOT_EXIT" -eq 0 ]; then
	ok "godot exited 0"
else
	note "godot exited $GODOT_EXIT"
fi

if [ "$fail" -ne 0 ]; then
	echo "run_checks: FAILED"
	exit 1
fi
echo "run_checks: OK -- all checks ran and passed"
exit 0
