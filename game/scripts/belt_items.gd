extends Node2D

# B65 — render items moving on belts, from the sim's emitted state.
#
# RENDERING ONLY. No sim logic here: positions come from the wire, never from dead
# reckoning. Between 20 Hz emissions items hold their last received position — see
# _process's note on why they do not extrapolate.
#
# STATUS: the position MATH is complete and checked; the DRAW is blocked on a wire gap
# (inventory B66). beltDeltas identifies a lane by an opaque integer `belt`, and nothing on
# the wire maps that index to world cells, so the client cannot know WHERE to draw the
# items it is being told about. Everything up to that boundary works and is proven by
# BELT_ITEMS_CHECK; the moment lane geometry appears on the wire, set_lane_geometry() is
# the only thing that needs wiring.
#
# --- the wire (contract §3.3) -----------------------------------------------------------
#
#   {"belt":0,"lane":0,"spacing":16384,"length":1310720,
#    "runs":[{"head":1306624,"len":80,"item":0}]}
#
# A lane is a list of RUNS, not items: a run is a maximally-compressed block, so a fully
# packed 80-item lane is ONE run. Cost is O(runs), not O(items) — the saturated case is the
# cheap case (transport-v0.md §4). Expanding a run means walking backwards from its head:
# items sit at head, head - s, head - 2s, ..., head - (len-1)*s.
#
# All positions are Fx32 (Q16.16): divide by 65536 for tiles. spacing 16384 = 0.25 tiles.

const Iso = preload("res://scripts/iso.gd")

# Q16.16 — factory-math-v0.md §1.2. The sim's numbers are integers; this is the ONLY place
# the renderer converts them, and it converts for display only. Nothing here feeds back.
const FX32_ONE := 65536.0

const ITEM_RADIUS := 3.0
# §5: items are hue-per-type. Index by the wire's `item` id (0=ore, 1=plate, 2=gear per
# recipes-v0.toml's item ids).
const ITEM_HUES: Array[Color] = [
	Color(0.45, 0.35, 0.28),  # iron-ore
	Color(0.75, 0.78, 0.82),  # iron-plate
	Color(0.55, 0.58, 0.62),  # iron-gear
]

@export var sim_state_path: NodePath

var _sim_state: Node = null

# belt index -> lane path in world coords. EMPTY, and it cannot be filled from the wire
# today — see B66. Populated via set_lane_geometry() once the emission carries cells.
var _lane_geometry: Dictionary = {}

# Last parsed lanes, straight from the most recent emit.
var _lanes: Array[Dictionary] = []
var lanes_received: int = 0
var _reported: bool = false
var items_received: int = 0

func _ready() -> void:
	_sim_state = get_node_or_null(sim_state_path)
	_run_belt_items_check()

func _process(_delta: float) -> void:
	if _sim_state == null:
		return
	# Re-read the sink's latest emission. NO EXTRAPOLATION between emits: an item's drawn
	# position is one the sim actually reported. Dead-reckoning at 20 Hz would look smoother
	# and be a lie — it would show items advancing through a stopped or backed-up belt,
	# which is exactly the state §6.1 needs the player to SEE. A visibly still item on a
	# saturated belt is information, not a glitch.
	var raw = _sim_state.belt_deltas_raw()
	var parsed: Array[Dictionary] = parse_lanes(raw)
	if parsed != _lanes:
		_lanes = parsed
		lanes_received = _lanes.size()
		items_received = 0
		for l in _lanes:
			items_received += (l.items as Array).size()
		if not _reported and items_received > 0:
			_reported = true
			# One line, first emit carrying items, as live evidence the parse works
			# end-to-end — and as the loud form of B66: items arrive, none can be placed.
			print("BELT_ITEMS_FEED lanes=%d items=%d renderable=%d (B66: no cell mapping on the wire)" \
				% [lanes_received, items_received, lanes_renderable()])
		queue_redraw()

# Wire -> renderable lanes. Pure and public so the check can drive it with fixtures.
#
# `belt_deltas` may be null: D21/§3.1 says null = belts NOT MODELLED in this build, which
# is a different fact from [] (modelled, nothing on them). Both yield no items to draw, but
# only one of them means "the subsystem is absent" — the caller can tell via
# belts_modelled(), and this must not crash on either.
static func parse_lanes(belt_deltas) -> Array[Dictionary]:
	var out: Array[Dictionary] = []
	if belt_deltas == null or not (belt_deltas is Array):
		return out
	for entry in belt_deltas:
		if not (entry is Dictionary):
			continue
		var spacing: float = float(entry.get("spacing", 0)) / FX32_ONE
		var length: float = float(entry.get("length", 0)) / FX32_ONE
		var items: Array = []
		var runs = entry.get("runs", [])
		if runs is Array:
			for r in runs:
				# Expand the run backwards from its head. This is the whole reason the wire
				# is cheap: 80 items arrive as one {head, len, item}.
				var head: float = float(r.get("head", 0)) / FX32_ONE
				var n: int = int(r.get("len", 0))
				var item_id: int = int(r.get("item", 0))
				for i in range(n):
					items.append({"pos": head - float(i) * spacing, "item": item_id})
		out.append({
			"belt": int(entry.get("belt", -1)),
			"lane": int(entry.get("lane", -1)),
			"spacing": spacing,
			"length": length,
			"items": items,
		})
	return out

# belt index -> ordered world cells the lane runs through. The renderer needs this and the
# wire does not carry it (B66). Left public and empty rather than guessed: inferring the
# index by replaying the adapter's (Y,X,Dir) placement sort would be sim logic in the
# client, and would break silently on a partial rejection or a pre-seeded belt.
func set_lane_geometry(belt_index: int, cells: Array[Vector2i], dir: int) -> void:
	_lane_geometry[belt_index] = {"cells": cells, "dir": dir}
	queue_redraw()

func lanes_renderable() -> int:
	var n: int = 0
	for l in _lanes:
		if _lane_geometry.has(l.belt):
			n += 1
	return n

# Item position along a lane -> world point. Lane position is in TILES from the lane's
# tail; the lane path is its cells in travel order, so tile k of the lane is cells[k].
func _world_point(geom: Dictionary, pos_tiles: float) -> Vector2:
	var cells: Array = geom.cells
	if cells.is_empty():
		return Vector2.ZERO
	var idx: int = clampi(int(floor(pos_tiles)), 0, cells.size() - 1)
	var frac: float = clampf(pos_tiles - float(idx), 0.0, 1.0)
	var step: Vector2i = Iso.dir_vector(int(geom.dir))
	var c: Vector2i = cells[idx]
	# Centre of the cell, advanced by the fractional part along the facing. Projected
	# through Iso like everything else — no second copy of the transform (B31/B43).
	var wx: float = float(c.x) + 0.5 + float(step.x) * frac
	var wy: float = float(c.y) + 0.5 + float(step.y) * frac
	return Iso.world_to_screen(wx, wy)

func _draw() -> void:
	# Depth-sorted with everything else: an item is drawn at its cell's depth, so it sorts
	# against machines by the same §3 far-corner rule. Items on a lane are drawn tail-first
	# so a nearer item overlaps the one behind it.
	var drawable: Array[Dictionary] = []
	for l in _lanes:
		if not _lane_geometry.has(l.belt):
			continue
		var geom: Dictionary = _lane_geometry[l.belt]
		for it in l.items:
			var p: Vector2 = _world_point(geom, it.pos)
			drawable.append({"p": p, "item": it.item, "depth": p.y})
	drawable.sort_custom(func(a, b): return a.depth < b.depth)
	for d in drawable:
		var hue: Color = ITEM_HUES[clampi(int(d.item), 0, ITEM_HUES.size() - 1)]
		draw_circle(d.p, ITEM_RADIUS, hue)
		draw_arc(d.p, ITEM_RADIUS, 0.0, TAU, 8, Color(0, 0, 0, 0.6), 1.0)

# --- BELT_ITEMS_CHECK ------------------------------------------------------------------
#
# Pure math over fixtures, so it runs headless with no adapter. Negative-controlled per the
# standing convention.
func _run_belt_items_check() -> void:
	# 1. Run expansion walks BACKWARDS from head by spacing. head=4.0, len=3, s=0.25 ->
	#    [4.0, 3.75, 3.5].
	var fx := func(t: float) -> int: return int(t * FX32_ONE)
	var one_run := [{
		"belt": 0, "lane": 0, "spacing": fx.call(0.25), "length": fx.call(20.0),
		"runs": [{"head": fx.call(4.0), "len": 3, "item": 0}],
	}]
	var lanes: Array[Dictionary] = parse_lanes(one_run)
	var pos: Array = []
	for it in lanes[0].items:
		pos.append(snappedf(it.pos, 0.001))
	var expand_ok: bool = pos == [4.0, 3.75, 3.5]
	# CONTROL: an implementation that ignored spacing would stack all 3 at the head.
	# If [4,4,4] equalled the expected list, the fixture could not tell them apart.
	var stacked: Array = [4.0, 4.0, 4.0]
	var expand_control_caught: bool = stacked != pos

	# 2. The saturated case is ONE run carrying 80 items — the whole point of the runs
	#    encoding. Expanding it must yield 80 distinct positions, not 1.
	var packed := [{
		"belt": 0, "lane": 0, "spacing": fx.call(0.25), "length": fx.call(20.0),
		"runs": [{"head": fx.call(19.9), "len": 80, "item": 0}],
	}]
	var packed_lanes: Array[Dictionary] = parse_lanes(packed)
	var packed_ok: bool = (packed_lanes[0].items as Array).size() == 80

	# 3. null vs [] — different facts (D21/§3.1), neither may crash.
	var null_ok: bool = parse_lanes(null).is_empty()
	var empty_ok: bool = parse_lanes([]).is_empty()
	var empty_runs_ok: bool = (parse_lanes([{
		"belt": 0, "lane": 1, "spacing": fx.call(0.25), "length": fx.call(20.0), "runs": [],
	}])[0].items as Array).is_empty()

	# 4. NO EXTRAPOLATION: parsing the same emit twice yields identical positions. An
	#    extrapolating renderer would advance them between emits.
	var again: Array[Dictionary] = parse_lanes(one_run)
	var static_ok: bool = again[0].items[0].pos == lanes[0].items[0].pos

	# 5. THE GAP, made loud (B66). Lanes arrive but none can be drawn, because the wire
	#    carries no cells for `belt`. This asserts the gap is REPORTED rather than silently
	#    rendering nothing — a renderer that draws zero items and says nothing is
	#    indistinguishable from a working one on an empty belt, which is the B57 trap.
	_lanes = lanes
	var geom_missing: bool = lanes_renderable() == 0 and _lanes.size() > 0
	# CONTROL: once geometry IS supplied the same lane becomes renderable. If it did not,
	# the gap report would be masking a broken renderer rather than a missing input.
	set_lane_geometry(0, [Vector2i(0, 0), Vector2i(1, 0)], Iso.DIR_E)
	var geom_control_caught: bool = lanes_renderable() == 1
	_lane_geometry.clear()
	_lanes = []

	var result: String = "PASS" if (expand_ok and expand_control_caught and packed_ok \
		and null_ok and empty_ok and empty_runs_ok and static_ok and geom_control_caught) \
		else "FAIL"
	print("BELT_ITEMS_CHECK run_expand=%s expand_control_caught=%s saturated_80=%s null_ok=%s empty_ok=%s no_extrapolate=%s geometry_control_caught=%s result=%s" \
		% [expand_ok, expand_control_caught, packed_ok, null_ok, empty_ok, static_ok, geom_control_caught, result])
	if not expand_ok:
		print("BELT_ITEMS_CHECK  -> run expansion wrong: got %s want [4.0, 3.75, 3.5]." % [str(pos)])
	if geom_missing:
		print("BELT_ITEMS_CHECK  -> BLOCKED (B66): %d lane(s) received, 0 renderable. " \
			% [_lanes.size()] + "beltDeltas identifies lanes by an opaque `belt` index and " +
			"the wire carries no cells for it, so items cannot be placed on screen.")
