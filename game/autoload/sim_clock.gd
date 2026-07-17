extends Node

# Fixed-tick simulation clock, fully decoupled from render/frame rate.
# Renderers/UI must only READ tick_count — never advance it.
#
# TICK_RATE must match data/recipes-v0.toml's [meta] tick_hz (math spec is
# authoritative). Validated at startup rather than just duplicated, since a
# silent drift between the two would desync every rate/duration derived from
# tick_hz (recipes-v0.toml header, factory-math-v0.md §1).
const TICK_RATE: float = 60.0
const TICK_INTERVAL: float = 1.0 / TICK_RATE

# Math spec §1.4: if the sim can't hit TICK_RATE, it slows down -- it does
# not drop ticks and does not rescale dt. So catch-up is capped per frame,
# and the leftover backlog is left in _accumulator (never zeroed) to drain
# across subsequent frames. Zeroing it would silently rescale dt and break
# the §3.1 remainder-carry guarantee.
const MAX_CATCHUP_TICKS: int = 8

var tick_count: int = 0
var _accumulator: float = 0.0

func _ready() -> void:
	_validate_tick_rate_against_recipes()

func _process(delta: float) -> void:
	_accumulator += delta
	var ticks_this_frame := 0
	while _accumulator >= TICK_INTERVAL and ticks_this_frame < MAX_CATCHUP_TICKS:
		_accumulator -= TICK_INTERVAL
		_advance_tick()
		ticks_this_frame += 1

func _advance_tick() -> void:
	tick_count += 1

# Purpose-built single-key scan, not a general TOML parser (Godot has none,
# and adding a third-party one is exactly the plugin risk D4 exists to
# avoid -- see inventory B20). D13's TOML->JSON build step is superseded by
# D14 (the C# sim core parses TOML directly), but that doesn't help this
# GDScript-side check -- Godot still has no parser either way, so this scan
# stays. Reads data/recipes-v0.toml, which sits one level above the Godot
# project root.
func _validate_tick_rate_against_recipes() -> void:
	var game_dir: String = ProjectSettings.globalize_path("res://")
	var toml_path: String = (game_dir + "../data/recipes-v0.toml").simplify_path()
	var f := FileAccess.open(toml_path, FileAccess.READ)
	if f == null:
		push_error("SimClock: cannot open %s to validate tick_hz (err=%d)" % [toml_path, FileAccess.get_open_error()])
		return

	var found_hz: int = -1
	while not f.eof_reached():
		var line: String = f.get_line()
		var stripped: String = line.strip_edges()
		if stripped.begins_with("tick_hz"):
			var parts: PackedStringArray = stripped.split("=")
			if parts.size() >= 2:
				var value_part: String = parts[1].strip_edges()
				var hash_idx: int = value_part.find("#")
				if hash_idx >= 0:
					value_part = value_part.substr(0, hash_idx).strip_edges()
				found_hz = value_part.to_int()
			break
	f.close()

	if found_hz == -1:
		push_error("SimClock: tick_hz not found in %s" % toml_path)
		return
	if found_hz != int(TICK_RATE):
		push_error("SimClock: TICK_RATE=%d does not match recipes-v0.toml tick_hz=%d" % [int(TICK_RATE), found_hz])
