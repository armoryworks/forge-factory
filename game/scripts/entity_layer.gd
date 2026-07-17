extends Node2D

# Render layers 3-4 — the sorted draw list. See isometric-design.md §3 and the boundary
# comment in terrain_layer.gd.
#
# This is the half a TileMapLayer CANNOT do: entities here are multi-tile, so they sort on
# the FAR CORNER of their footprint (Iso.depth_key), not their origin, and not by node Y.
# Terrain (layers 0-2) stays in the tilemap where grid order is free.
#
# Occupancy below is DUMMY data — this layer does not know the sim exists and must not.
# It renders whatever list it is handed; wiring it to real entities is Phase 4's job.

const Iso = preload("res://scripts/iso.gd")
const BuildingDefs = preload("res://scripts/building_defs.gd")

# Placeholder art grammar (§5): flat-shaded extruded prism. Top face at the category hue,
# left face 80% brightness, right 60%. That fake-lighting triple is the whole visual
# vocabulary we need before an artist exists.
const FACE_LEFT_MUL := 0.8
const FACE_RIGHT_MUL := 0.6

var _entities: Array[Dictionary] = []

# cell (Vector2i) -> entity id. The authority on what is occupied.
#
# Every cell of a footprint is registered, not just the origin. Registering only the
# origin is the placement-side twin of §3's origin-sorting bug: a 3x3 assembler would
# claim one cell and happily accept a belt placed inside its own body. PLACE_CHECK
# negative-controls exactly that.
var _occupancy: Dictionary = {}
var _next_id: int = 1

func _ready() -> void:
	_seed_dummy_entities()
	_run_depth_check()
	_run_place_check()

func add_entity(id: int, x: int, y: int, w: int, h: int, height: float, hue: Color) -> void:
	_entities.append({
		"id": id, "x": x, "y": y, "w": w, "h": h, "height": height, "hue": hue,
	})
	queue_redraw()

# --- placement API ---------------------------------------------------------------------

func footprint_cells(def: Dictionary, origin: Vector2i) -> Array[Vector2i]:
	var cells: Array[Vector2i] = []
	for dx in range(int(def.w)):
		for dy in range(int(def.h)):
			cells.append(Vector2i(origin.x + dx, origin.y + dy))
	return cells

func can_place(def: Dictionary, origin: Vector2i) -> bool:
	for c in footprint_cells(def, origin):
		if _occupancy.has(c):
			return false
	return true

# Returns the new entity id, or -1 if blocked. Callers must treat -1 as "nothing happened"
# rather than assuming success — a click on an occupied cell is a normal event, not an
# error.
func place(def: Dictionary, origin: Vector2i) -> int:
	if not can_place(def, origin):
		return -1
	var id: int = _next_id
	_next_id += 1
	add_entity(id, origin.x, origin.y, int(def.w), int(def.h), float(def.height), def.hue)
	for c in footprint_cells(def, origin):
		_occupancy[c] = id
	return id

func entity_count() -> int:
	return _entities.size()

const SEED_COUNT := 5

# Seed the slice's shape: source -> belt -> machine -> output (D3). Placed through the
# same place() path a click uses, so the seed cannot drift from the interactive behaviour
# or silently violate occupancy.
func _seed_dummy_entities() -> void:
	_place_named("burner-miner", Vector2i(0, 0))
	_place_named("belt-1", Vector2i(2, 0))
	_place_named("belt-1", Vector2i(2, 1))
	_place_named("stone-furnace", Vector2i(3, 0))
	_place_named("assembler-1", Vector2i(0, 3))

func _place_named(name: String, origin: Vector2i) -> void:
	for i in range(BuildingDefs.count()):
		var d: Dictionary = BuildingDefs.get_def(i)
		if d.name == name:
			place(d, origin)
			return

# Total order: depth, then y, then x, then id. The id tiebreak is not decoration —
# Array.sort_custom is not guaranteed stable, so without a total order two equal-depth
# entities could swap z-order between frames. That reads as a rendering bug and destroys
# trust in the view (§3).
func _sort_key_less(a: Dictionary, b: Dictionary) -> bool:
	var da: float = Iso.depth_key(a.x, a.y, a.w, a.h, 0.0)
	var db: float = Iso.depth_key(b.x, b.y, b.w, b.h, 0.0)
	if da != db:
		return da < db
	if a.y != b.y:
		return a.y < b.y
	if a.x != b.x:
		return a.x < b.x
	return a.id < b.id

func sorted_entities() -> Array[Dictionary]:
	var out: Array[Dictionary] = _entities.duplicate()
	out.sort_custom(_sort_key_less)
	return out

func _draw() -> void:
	for e in sorted_entities():
		_draw_prism(e)

# Extruded diamond prism over the entity's footprint. Uses Iso for every coordinate — no
# local TILE_W/TILE_H copies, which is the drift iso.gd exists to prevent.
func _draw_prism(e: Dictionary) -> void:
	var lift := Vector2(0.0, -e.height * float(Iso.TILE_Z))
	# Footprint corners in world space, then to screen. Order matters: this walks the
	# diamond N -> E -> S -> W so the polygon is convex.
	var n: Vector2 = Iso.world_to_screen(float(e.x), float(e.y))
	var east: Vector2 = Iso.world_to_screen(float(e.x + e.w), float(e.y))
	var s: Vector2 = Iso.world_to_screen(float(e.x + e.w), float(e.y + e.h))
	var west: Vector2 = Iso.world_to_screen(float(e.x), float(e.y + e.h))

	var hue: Color = e.hue
	var left := Color(hue.r * FACE_LEFT_MUL, hue.g * FACE_LEFT_MUL, hue.b * FACE_LEFT_MUL)
	var right := Color(hue.r * FACE_RIGHT_MUL, hue.g * FACE_RIGHT_MUL, hue.b * FACE_RIGHT_MUL)

	# Walls first, then the lifted top face over them.
	draw_colored_polygon(PackedVector2Array([west, s, s + lift, west + lift]), left)
	draw_colored_polygon(PackedVector2Array([s, east, east + lift, s + lift]), right)
	draw_colored_polygon(
		PackedVector2Array([n + lift, east + lift, s + lift, west + lift]), hue)
	# 1px outline — §6.5: the cheapest fix for "where does one machine end and the next
	# begin" in a wall of same-category machines.
	draw_polyline(
		PackedVector2Array([n + lift, east + lift, s + lift, west + lift, n + lift]),
		Color(0, 0, 0, 0.5), 1.0)

# --- DEPTH_CHECK -----------------------------------------------------------------------
#
# Pure math, so it runs headless. Proves §3's far-corner key orders entities correctly AND
# that the origin-only key — the classic bug — does not. Per the standing convention, the
# check is negative-controlled: if the deliberately-wrong sort ALSO produced the right
# answer, this fixture cannot discriminate and the check is vacuous, not passing.
#
# Fixture: entity 1 is a 3x3 at (0,0), entity 2 is a 1x1 at (0,3).
#   Screen depth is driven by x+y. Entity 1's footprint reaches far corner (3,3) -> its
#   lowest screen point is (3+3)*TILE_H/2 = 96px. Entity 2 reaches (1,4) -> 80px. Lower on
#   screen = nearer the viewer, so entity 1 must draw AFTER (in front of) entity 2.
#   far-corner: 1 -> (0+3)+(0+3) = 6, 2 -> (0+1)+(3+1) = 5. Order [2, 1]. Correct.
#   origin-only: 1 -> 0+0 = 0, 2 -> 0+3 = 3. Order [1, 2]. WRONG — the 3x3 sorts as if it
#   were a 1x1 at its origin and hides behind something it should occlude.
func _run_depth_check() -> void:
	var a := {"id": 1, "x": 0, "y": 0, "w": 3, "h": 3}
	var b := {"id": 2, "x": 0, "y": 3, "w": 1, "h": 1}

	var far_order: Array = _order_by(
		[a, b], func(e): return Iso.depth_key(e.x, e.y, e.w, e.h, 0.0))
	var origin_order: Array = _order_by([a, b], func(e): return float(e.x + e.y))

	var far_ok: bool = far_order == [2, 1]
	# NEGATIVE CONTROL: the wrong key must produce the wrong answer here.
	var control_caught: bool = origin_order != [2, 1]
	var stable_ok: bool = _check_tie_stability()

	var result: String = "PASS" if (far_ok and control_caught and stable_ok) else "FAIL"
	print("DEPTH_CHECK far_corner=%s origin_control_caught=%s tie_stable=%s result=%s" \
		% [far_ok, control_caught, stable_ok, result])
	if not far_ok:
		print("DEPTH_CHECK  -> far-corner key mis-ordered the fixture; got %s want [2, 1]. " \
			% [str(far_order)] + "§3's depth key is broken.")
	if not control_caught:
		print("DEPTH_CHECK  -> origin-only sort produced the RIGHT order, so this fixture " +
			"cannot tell the two apart. Check is vacuous, not passing.")
	if not stable_ok:
		print("DEPTH_CHECK  -> equal-depth entities did not sort deterministically; the " +
			"id tiebreak is not producing a total order (§3).")

# --- PLACE_CHECK -----------------------------------------------------------------------
#
# Pure logic over a scratch layer, so it runs headless.
#
# Proves multi-tile occupancy is registered across the WHOLE footprint, not just the
# origin — the placement-side twin of §3's origin bug, and just as invisible: an
# origin-only grid accepts every overlap that does not land exactly on the origin cell, so
# a 3x3 assembler will happily swallow belts placed inside its own body.
#
# NEGATIVE CONTROL: the same fixture is run against an origin-only occupancy rule, which
# MUST accept an overlap that the real rule rejects. If both rejected it, the fixture is
# not exercising multi-tile at all and a green result would mean nothing.
func _run_place_check() -> void:
	var asm: Dictionary = {"w": 3, "h": 3}
	var belt: Dictionary = {"w": 1, "h": 1}
	var origin := Vector2i(10, 10)
	# Inside the assembler's body but NOT its origin cell — the case origin-only misses.
	var inside := Vector2i(11, 11)

	var scratch := {}
	for c in footprint_cells(asm, origin):
		scratch[c] = 1

	var real_rejects: bool = not _free_in(scratch, belt, inside)
	# Origin-only: registers just the origin cell, so (11,11) looks free.
	var origin_only := {origin: 1}
	var control_accepts: bool = _free_in(origin_only, belt, inside)
	# Sanity: a genuinely free cell must still be placeable, or "rejects everything" would
	# pass the first assertion for the wrong reason.
	var free_ok: bool = _free_in(scratch, belt, Vector2i(20, 20))

	# The seed goes through place(), which returns -1 silently when blocked. Without this,
	# an over-eager occupancy rule could reject the entire factory and every other
	# assertion here would still pass against an empty layer.
	var seeded_ok: bool = entity_count() == SEED_COUNT

	var result: String = "PASS" if (real_rejects and control_accepts and free_ok and seeded_ok) \
		else "FAIL"
	print("PLACE_CHECK footprint_blocks=%s origin_only_control_accepts=%s free_cell_ok=%s seeded=%d/%d result=%s" \
		% [real_rejects, control_accepts, free_ok, entity_count(), SEED_COUNT, result])
	if not seeded_ok:
		print("PLACE_CHECK  -> the seeded slice did not fully place (%d/%d); place() is " \
			% [entity_count(), SEED_COUNT] + "rejecting valid placements.")
	if not real_rejects:
		print("PLACE_CHECK  -> a 1x1 was accepted INSIDE a 3x3's footprint; occupancy is " +
			"registering the origin only, not the whole footprint.")
	if not control_accepts:
		print("PLACE_CHECK  -> the origin-only control also rejected, so this fixture " +
			"cannot tell the two rules apart. Check is vacuous, not passing.")
	if not free_ok:
		print("PLACE_CHECK  -> a free cell was rejected; can_place is refusing everything.")

func _free_in(grid: Dictionary, def: Dictionary, origin: Vector2i) -> bool:
	for c in footprint_cells(def, origin):
		if grid.has(c):
			return false
	return true

func _order_by(items: Array, key: Callable) -> Array:
	var copy: Array = items.duplicate()
	copy.sort_custom(func(p, q):
		var kp: float = key.call(p)
		var kq: float = key.call(q)
		if kp != kq:
			return kp < kq
		return p.id < q.id)
	var ids: Array = []
	for it in copy:
		ids.append(it.id)
	return ids

# Two entities with an IDENTICAL depth key must still land in a deterministic order, from
# any input order — that is what the id tiebreak buys. Feed the same pair in both input
# orders and require the same output.
func _check_tie_stability() -> bool:
	var p := {"id": 7, "x": 1, "y": 0, "w": 1, "h": 1}
	var q := {"id": 3, "x": 0, "y": 1, "w": 1, "h": 1}
	# Same depth by construction: (1+1)+(0+1) == (0+1)+(1+1).
	if Iso.depth_key(p.x, p.y, p.w, p.h, 0.0) != Iso.depth_key(q.x, q.y, q.w, q.h, 0.0):
		return false
	var forward: Array = _order_by([p, q], func(e): return Iso.depth_key(e.x, e.y, e.w, e.h, 0.0))
	var backward: Array = _order_by([q, p], func(e): return Iso.depth_key(e.x, e.y, e.w, e.h, 0.0))
	return forward == backward
