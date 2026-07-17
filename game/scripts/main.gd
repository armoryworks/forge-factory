extends Node2D

# Self-check: after TEST_DURATION_SEC of real time, compare SimClock.tick_count
# against the expected tick count for that elapsed time. Verifies the fixed-tick
# sim loop advances independently of render frame rate.
const TEST_DURATION_SEC := 3.0

const RenderChecks = preload("res://scripts/render_checks.gd")

# Overlays live under a CanvasLayer (screen space) so they do not pan or scale with the
# world camera — isometric-design.md §6.3.
@onready var _tick_label: Label = $UILayer/TickLabel
@onready var _camera: Camera2D = $Camera2D
@onready var _terrain: TileMapLayer = $TerrainLayer

var _start_msec := 0
var _checked := false

func _ready() -> void:
	_start_msec = Time.get_ticks_msec()
	# B22 + §6.3/§7-Q4 render checks — opt-in via `-- --render-checks`, windowed only.
	# _terrain is passed as OVERLAY_CHECK's negative control: it must move when the camera
	# does, or "the overlay stayed put" proves nothing.
	#
	# SIM_CHECK is suppressed in this mode on purpose. The checks do a full-viewport GPU
	# readback per frame, which starves the frame loop and makes the sim fall behind rate
	# by design — SIM_CHECK would report a real slowdown caused entirely by the harness.
	# The two cannot share a run, so they don't: `--render-checks` measures the renderer,
	# a plain run measures the clock.
	if RenderChecks.enabled():
		_checked = true
		var checks: Node = RenderChecks.new()
		add_child(checks)
		checks.run(_camera, _tick_label, _terrain)

func _process(_delta: float) -> void:
	# Renderer/UI only READS sim state, never advances it.
	_tick_label.text = "ticks: %d" % SimClock.tick_count

	if _checked:
		return

	var elapsed := (Time.get_ticks_msec() - _start_msec) / 1000.0
	if elapsed >= TEST_DURATION_SEC:
		_checked = true
		_run_self_check(elapsed)

func _run_self_check(elapsed: float) -> void:
	var expected := SimClock.TICK_RATE * elapsed
	var actual := float(SimClock.tick_count)
	var allowed_drift: float = max(2.0, SimClock.TICK_RATE * 0.05 * elapsed)
	var diff: float = abs(actual - expected)
	var result: String = "PASS" if diff <= allowed_drift else "FAIL"
	print("SIM_CHECK elapsed=%.3f expected_ticks=%.1f actual_ticks=%.0f diff=%.1f allowed_drift=%.1f result=%s" % [elapsed, expected, actual, diff, allowed_drift, result])
	if DisplayServer.get_name() == "headless":
		get_tree().quit(0 if result == "PASS" else 1)
