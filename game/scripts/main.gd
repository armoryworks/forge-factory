extends Node2D

# Self-check: after TEST_DURATION_SEC of real time, compare SimClock.tick_count
# against the expected tick count for that elapsed time. Verifies the fixed-tick
# sim loop advances independently of render frame rate.
const TEST_DURATION_SEC := 3.0

# Overlays live under a CanvasLayer (screen space) so they do not pan or scale with the
# world camera — isometric-design.md §6.3.
@onready var _tick_label: Label = $UILayer/TickLabel

var _start_msec := 0
var _checked := false

func _ready() -> void:
	_start_msec = Time.get_ticks_msec()

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
