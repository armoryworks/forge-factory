extends Node2D

# B65 — render items moving on belts, from the sim's emitted state.
#
# RENDERING ONLY. No sim logic here: positions come from the wire, never from dead
# reckoning. Between 20 Hz emissions items hold their last received position — see
# _process's note on why they do not extrapolate.
#
# --- the wire (contract §3.3) -----------------------------------------------------------
#
#   {"belt":0,"lane":0,"spacing":16384,"length":1310720,
#    "cells":[{"x":0,"y":0,"dir":1}, {"x":1,"y":0,"dir":1}, …],
#    "runs":[{"head":1306624,"len":80,"item":0}]}
#
# A lane is a list of RUNS, not items: a run is a maximally-compressed block, so a fully
# packed 80-item lane is ONE run. Cost is O(runs), not O(items) — the saturated case is the
# cheap case (transport-v0.md §4). Expanding a run means walking backwards from its head:
# items sit at head, head - s, head - 2s, ..., head - (len-1)*s.
#
# All positions are Fx32 (Q16.16): divide by 65536 for tiles. spacing 16384 = 0.25 tiles.
#
# GEOMETRY (B66): `cells` is tail-first in travel order, and `cells[i]` spans [i, i+1) tiles
# along the SAME axis `runs[].head` is measured on — so `cell_index = head / 65536` and no
# other rule is needed to place an item.
#
# EVERY CELL OWNS ITS `dir`, because a chain can TURN. That is not a detail to smooth over:
# a single lane-wide direction is correct today and silently wrong the first time a player
# builds a corner (§3.3 verified an L-shape reporting dirs [1,2,2]). This code therefore
# never reads a lane-level dir — it reads the dir of the cell the item is actually in, and
# BELT_ITEMS_CHECK negative-controls exactly that against the single-dir version.

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
			# One line, first emit carrying items: live evidence of the whole path — parse,
			# geometry, placement. Post-B66 `renderable` should equal the lanes carrying
			# items; anything less means geometry is missing and items are invisible again.
			var first_pt: Vector2 = Vector2.ZERO
			for l in _lanes:
				if not (l.cells as Array).is_empty() and not (l.items as Array).is_empty():
					first_pt = _world_point(l.cells, l.items[0].pos)
					break
			print("BELT_ITEMS_FEED lanes=%d items=%d renderable=%d first_item_screen=%s" \
				% [lanes_received, items_received, lanes_renderable(), first_pt])
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
		# Geometry rides each entry (B66). Redundant across a belt's two lanes by design:
		# hoisting it would make a lane depend on a sibling entry and break D22's
		# reconstruct-from-any-single-emit rule.
		var cells: Array = []
		var raw_cells = entry.get("cells", [])
		if raw_cells is Array:
			for c in raw_cells:
				if c is Dictionary:
					cells.append({
						"cell": Vector2i(int(c.get("x", 0)), int(c.get("y", 0))),
						"dir": int(c.get("dir", -1)),
					})
		out.append({
			"belt": int(entry.get("belt", -1)),
			"lane": int(entry.get("lane", -1)),
			"spacing": spacing,
			"length": length,
			"cells": cells,
			"items": items,
		})
	return out

# B66 removed the need for an out-of-band geometry setter: cells ride the emission, so a
# lane is self-describing and a late joiner reconstructs from any single emit (D22). The
# old set_lane_geometry() is deliberately GONE rather than kept as an override — a second
# source of truth for where a belt is would be the drift trap iso.gd exists to prevent.
func lanes_renderable() -> int:
	var n: int = 0
	for l in _lanes:
		if not (l.cells as Array).is_empty():
			n += 1
	return n

# Item position along a lane -> world point.
#
# §3.3: cells are tail-first in travel order and cells[i] spans [i, i+1) on the same axis
# head is measured on, so the cell index is just floor(pos_tiles) — no other rule needed.
#
# The fractional advance uses THAT CELL'S dir, not the lane's. On a corner the cells before
# and after face differently, and an item mid-corner must follow the cell it is in. Reading
# a lane-wide dir would place every item after the first turn along the wrong axis — right
# on a straight belt, wrong the moment a player builds an L.
static func _world_point(cells: Array, pos_tiles: float) -> Vector2:
	if cells.is_empty():
		return Vector2.ZERO
	var idx: int = clampi(int(floor(pos_tiles)), 0, cells.size() - 1)
	var frac: float = clampf(pos_tiles - float(idx), 0.0, 1.0)
	var entry: Dictionary = cells[idx]
	var c: Vector2i = entry.cell
	var step: Vector2i = Iso.dir_vector(int(entry.dir))
	# Centre of the cell, advanced by the fractional part along that cell's facing.
	# Projected through Iso like everything else — no second copy of the transform.
	var wx: float = float(c.x) + 0.5 + float(step.x) * frac
	var wy: float = float(c.y) + 0.5 + float(step.y) * frac
	return Iso.world_to_screen(wx, wy)

func _draw() -> void:
	# Depth-sorted with everything else: an item is drawn at its cell's depth, so it sorts
	# against machines by the same §3 far-corner rule. Items on a lane are drawn tail-first
	# so a nearer item overlaps the one behind it.
	var drawable: Array[Dictionary] = []
	for l in _lanes:
		var cells: Array = l.cells
		if cells.is_empty():
			continue
		for it in l.items:
			var p: Vector2 = _world_point(cells, it.pos)
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

	# 5. GEOMETRY (B66). cells[i] spans [i, i+1) on the same axis as head, so the cell index
	#    is floor(pos). An item at 4.5 sits in cells[4], half a tile along THAT cell's dir.
	var straight: Array = [
		{"cell": Vector2i(0, 0), "dir": Iso.DIR_E},
		{"cell": Vector2i(1, 0), "dir": Iso.DIR_E},
		{"cell": Vector2i(2, 0), "dir": Iso.DIR_E},
	]
	# pos 1.5 -> cells[1] = (1,0), advanced 0.5 east -> world (1+0.5+0.5, 0+0.5) = (2.0, 0.5)
	var straight_pt: Vector2 = _world_point(straight, 1.5)
	var straight_ok: bool = straight_pt.is_equal_approx(Iso.world_to_screen(2.0, 0.5))

	# 6. THE CORNER — the leg that matters. §3.3: "a chain can TURN, and every cell carries
	#    its own dir"; origin+dir+len "would be correct today and silently wrong the first
	#    time a player builds a corner". An L-shape reports dirs [E, S, S].
	var corner: Array = [
		{"cell": Vector2i(0, 0), "dir": Iso.DIR_E},
		{"cell": Vector2i(1, 0), "dir": Iso.DIR_S},
		{"cell": Vector2i(1, 1), "dir": Iso.DIR_S},
	]
	# pos 1.5 -> cells[1] = (1,0) facing SOUTH -> (1+0.5, 0+0.5+0.5) = (1.5, 1.0)
	var corner_pt: Vector2 = _world_point(corner, 1.5)
	var corner_ok: bool = corner_pt.is_equal_approx(Iso.world_to_screen(1.5, 1.0))
	# CONTROL: the single-lane-dir version everyone writes first — it would advance along
	# cells[0].dir (EAST) and put the item at (2.0, 0.5) instead. If the two agreed, this
	# fixture would not be exercising the turn at all and the leg would prove nothing.
	var single_dir_pt: Vector2 = Iso.world_to_screen(2.0, 0.5)
	var corner_control_caught: bool = not corner_pt.is_equal_approx(single_dir_pt)

	# 7. A lane with no cells is not renderable — and must not crash. Belts always carry
	#    cells post-B66, but a malformed emit must degrade, not except.
	var no_cells: bool = _world_point([], 3.0) == Vector2.ZERO
	_lanes = parse_lanes(one_run)
	var unrenderable_ok: bool = lanes_renderable() == 0
	_lanes = parse_lanes([{
		"belt": 0, "lane": 0, "spacing": fx.call(0.25), "length": fx.call(3.0),
		"cells": [{"x": 0, "y": 0, "dir": 1}, {"x": 1, "y": 0, "dir": 1}],
		"runs": [{"head": fx.call(1.5), "len": 1, "item": 0}],
	}])
	var renderable_ok: bool = lanes_renderable() == 1 and (_lanes[0].cells as Array).size() == 2
	_lanes = []

	var result: String = "PASS" if (expand_ok and expand_control_caught and packed_ok \
		and null_ok and empty_ok and empty_runs_ok and static_ok and straight_ok \
		and corner_ok and corner_control_caught and no_cells and unrenderable_ok \
		and renderable_ok) else "FAIL"
	print("BELT_ITEMS_CHECK run_expand=%s expand_control_caught=%s saturated_80=%s null_ok=%s empty_ok=%s no_extrapolate=%s cell_index=%s corner_per_cell_dir=%s corner_control_caught=%s renderable=%s result=%s" \
		% [expand_ok, expand_control_caught, packed_ok, null_ok, empty_ok, static_ok, straight_ok, corner_ok, corner_control_caught, renderable_ok, result])
	if not corner_ok:
		print("BELT_ITEMS_CHECK  -> an item mid-corner used the wrong cell's dir; items " +
			"after a turn will render along the wrong axis (§3.3).")
	if not expand_ok:
		print("BELT_ITEMS_CHECK  -> run expansion wrong: got %s want [4.0, 3.75, 3.5]." % [str(pos)])

