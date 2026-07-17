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
const EntityLayer = preload("res://scripts/entity_layer.gd")

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
	_run_belt_check()

func add_entity(id: int, x: int, y: int, w: int, h: int, height: float, hue: Color,
		dir: int = -1, type_name: String = "") -> void:
	# dir = -1 means "not directional". A furnace has no facing in v0; storing 0 would look
	# like north to any consumer that reads it.
	_entities.append({
		"id": id, "x": x, "y": y, "w": w, "h": h, "height": height, "hue": hue,
		"dir": dir, "name": type_name,
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

# Placed-belt data, shaped to feed the adapter later (B49/B51). Deliberately {cell, dir}
# and nothing else: that is the minimum the sim needs to build transport-v0 §1 lanes, and
# anything more here would be render state leaking into a sim contract.
#
# NOTE ON VOCABULARY, because the collision is easy to miss: transport-v0's `Run` is a
# block of ITEMS WITHIN A LANE (§1: head/len/item), NOT a line of belt tiles. A line of
# belts placed by drag is N separate 1x1 belt entities; the sim's job is to coalesce
# contiguous same-facing belts into a Lane of length L. This export does not pre-coalesce
# — that is the sim's model to build, not the client's to guess.
func belts_for_adapter() -> Array[Dictionary]:
	var out: Array[Dictionary] = []
	for e in _entities:
		if e.name == "belt-1":
			out.append({"cell": Vector2i(e.x, e.y), "dir": e.dir})
	return out

# Returns the new entity id, or -1 if blocked. Callers must treat -1 as "nothing happened"
# rather than assuming success — a click on an occupied cell is a normal event, not an
# error.
func place(def: Dictionary, origin: Vector2i, dir: int = -1) -> int:
	if not can_place(def, origin):
		return -1
	var id: int = _next_id
	_next_id += 1
	var stored_dir: int = Iso.rotate_cw(dir, 0) if BuildingDefs.is_directional(def) else -1
	add_entity(id, origin.x, origin.y, int(def.w), int(def.h), float(def.height), def.hue,
		stored_dir, String(def.name))
	for c in footprint_cells(def, origin):
		_occupancy[c] = id
	return id

func entity_count() -> int:
	return _entities.size()

# Cells along a drag, from `from` to `to`, snapped to ONE axis. Factory players expect an
# axis-locked run, not a diagonal staircase: a belt line is a lane, and a staircase would
# produce a chain of alternating facings that transport-v0 cannot coalesce into one Lane.
# The dominant axis wins so a slightly-off drag still yields a straight run.
#
# Returns cells in travel order (first = start), so the caller can face each belt at the
# next cell in the line.
func drag_cells(from: Vector2i, to: Vector2i) -> Array[Vector2i]:
	var delta: Vector2i = to - from
	var cells: Array[Vector2i] = []
	if absi(delta.x) >= absi(delta.y):
		var stepx: int = signi(delta.x)
		for i in range(absi(delta.x) + 1):
			cells.append(Vector2i(from.x + i * stepx, from.y))
	else:
		var stepy: int = signi(delta.y)
		for i in range(absi(delta.y) + 1):
			cells.append(Vector2i(from.x, from.y + i * stepy))
	return cells

# The facing implied by a drag: the direction of travel along the locked axis. A zero-length
# drag has no implied facing, so the caller's current ghost dir stands.
func drag_dir(from: Vector2i, to: Vector2i, fallback: int) -> int:
	var delta: Vector2i = to - from
	if delta == Vector2i.ZERO:
		return fallback
	var step: Vector2i
	if absi(delta.x) >= absi(delta.y):
		step = Vector2i(signi(delta.x), 0)
	else:
		step = Vector2i(0, signi(delta.y))
	for d in range(Iso.DIR_COUNT):
		if Iso.dir_vector(d) == step:
			return d
	return fallback

# Place a belt run. Blocked cells are SKIPPED, not fatal: dragging across an existing
# machine should lay belt either side of it rather than silently placing nothing, which is
# what a hard fail would do and what a player would read as the tool being broken.
# Returns how many were placed.
func place_run(def: Dictionary, from: Vector2i, to: Vector2i, fallback_dir: int) -> int:
	var dir: int = drag_dir(from, to, fallback_dir)
	var placed: int = 0
	for c in drag_cells(from, to):
		if place(def, c, dir) != -1:
			placed += 1
	return placed

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

	if int(e.dir) >= 0:
		_draw_direction_chevron(e, lift)

# Direction chevron on the top face. §5 names belt-direction ambiguity the #1 factory-game
# readability failure, and it must be legible from the PLACEHOLDER, not deferred to real
# art — a belt you cannot read the facing of is not a placeholder for a belt, it is a
# yellow diamond.
func _draw_direction_chevron(e: Dictionary, lift: Vector2) -> void:
	var cx: float = float(e.x) + float(e.w) * 0.5
	var cy: float = float(e.y) + float(e.h) * 0.5
	var centre: Vector2 = Iso.world_to_screen(cx, cy) + lift
	var step: Vector2i = Iso.dir_vector(int(e.dir))
	# Project the facing THROUGH the same transform the entity is drawn with, rather than
	# hand-picking screen offsets per direction: a hand-picked table is a second copy of
	# the projection and drifts from it (B31/B43).
	var ahead: Vector2 = Iso.world_to_screen(cx + float(step.x) * 0.5, cy + float(step.y) * 0.5) + lift
	var forward: Vector2 = ahead - centre
	var side := Vector2(-forward.y, forward.x) * 0.5

	var tip: Vector2 = centre + forward * 0.75
	var back: Vector2 = centre - forward * 0.25
	draw_polyline(PackedVector2Array([back - side, tip, back + side]), Color(0.1, 0.1, 0.1, 0.9), 2.0)

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

# --- BELT_CHECK ------------------------------------------------------------------------
#
# Pure logic + transform math, so it runs headless. Covers the two things belt placement can
# get silently wrong: DIRECTION and OCCUPANCY.
#
# The direction half is the interesting one. It does not restate DIR_VECTORS — restating a
# table against itself is the B18 vacuity trap. It projects each facing THROUGH
# Iso.world_to_screen and asserts the screen quadrant matches the §5 art contract's names
# (N = output faces screen-upper-right). That is a real claim that can be wrong, and it is
# exactly the claim B31 proved I should not assert from memory.
func _run_belt_check() -> void:
	# Use the REAL defs, not a fixture dict — a fixture drifts from what ships (and, first
	# time round, silently lacked height/hue).
	var belt: Dictionary = BuildingDefs.find("belt-1")

	# 1. Each named facing lands in the screen quadrant §5 says it should.
	var quadrant_detail: String = ""
	var quadrant_ok: bool = _quadrants_match(Iso.DIR_VECTORS)

	# 2. Rotation cycles all four and returns home; posmod means backwards is safe too.
	var seen := {}
	var d: int = Iso.DIR_N
	for i in range(Iso.DIR_COUNT):
		seen[d] = true
		d = Iso.rotate_cw(d)
	var cycle_ok: bool = seen.size() == Iso.DIR_COUNT and d == Iso.DIR_N
	var back_ok: bool = Iso.rotate_cw(Iso.DIR_N, -1) == Iso.DIR_W

	# 3. Occupancy: a belt cannot be placed on an occupied cell REGARDLESS of facing.
	#    Facing is not part of occupancy — two belts crossing the same cell is a collision
	#    even at right angles, and a rule that compared dir would silently allow it.
	var scratch := EntityLayer.new()
	var first: int = scratch.place(belt, Vector2i(50, 50), Iso.DIR_N)
	var same_dir: int = scratch.place(belt, Vector2i(50, 50), Iso.DIR_N)
	var cross_dir: int = scratch.place(belt, Vector2i(50, 50), Iso.DIR_E)
	var free_dir: int = scratch.place(belt, Vector2i(51, 50), Iso.DIR_E)
	var occ_ok: bool = first > 0 and same_dir == -1 and cross_dir == -1 and free_dir > 0

	# 4. dir is STORED, not dropped, and non-directional types store -1 rather than a
	#    meaningless 0 that a consumer would read as north.
	var belts: Array[Dictionary] = scratch.belts_for_adapter()
	var stored_ok: bool = belts.size() == 2 \
		and belts[0].cell == Vector2i(50, 50) and belts[0].dir == Iso.DIR_N \
		and belts[1].cell == Vector2i(51, 50) and belts[1].dir == Iso.DIR_E
	var furnace: Dictionary = BuildingDefs.find("stone-furnace")
	scratch.place(furnace, Vector2i(60, 60), Iso.DIR_N)
	var furnace_e: Dictionary = scratch.entity_for_test(Vector2i(60, 60))
	var nondir_ok: bool = not furnace_e.is_empty() and int(furnace_e.dir) == -1
	scratch.free()

	# 5. Drag runs (B52 stretch). Three things a run can get silently wrong:
	#    axis-lock, facing-follows-travel, and what happens across an obstacle.
	var drag := EntityLayer.new()
	# Axis-locked: a mostly-east drag with y drift must NOT staircase. A diagonal run would
	# alternate facings and could not coalesce into one transport-v0 Lane.
	var sloppy: Array[Vector2i] = drag.drag_cells(Vector2i(0, 0), Vector2i(4, 1))
	var axis_ok: bool = sloppy.size() == 5
	for c in sloppy:
		if c.y != 0:
			axis_ok = false
	# Facing follows travel, both ways along both axes — not the ghost's stale dir.
	var dir_e_ok: bool = drag.drag_dir(Vector2i(0, 0), Vector2i(3, 0), Iso.DIR_N) == Iso.DIR_E
	var dir_w_ok: bool = drag.drag_dir(Vector2i(3, 0), Vector2i(0, 0), Iso.DIR_N) == Iso.DIR_W
	var dir_s_ok: bool = drag.drag_dir(Vector2i(0, 0), Vector2i(0, 3), Iso.DIR_N) == Iso.DIR_S
	var dir_n_ok: bool = drag.drag_dir(Vector2i(0, 3), Vector2i(0, 0), Iso.DIR_E) == Iso.DIR_N
	# Zero-length drag keeps the ghost's dir: a click is a zero-length drag, and it must not
	# silently re-face the belt the player already aimed.
	var dir_zero_ok: bool = drag.drag_dir(Vector2i(2, 2), Vector2i(2, 2), Iso.DIR_W) == Iso.DIR_W
	# Across an obstacle: place_run SKIPS blocked cells rather than aborting. Put a 2x2
	# furnace mid-run and require the run to lay belt either side of it, not give up.
	drag.place(BuildingDefs.find("stone-furnace"), Vector2i(2, 0), -1)
	var laid: int = drag.place_run(belt, Vector2i(0, 0), Vector2i(5, 0), Iso.DIR_E)
	# cells 0..5 = 6; furnace occupies x=2,3 at y=0 => 4 belts land.
	var skip_ok: bool = laid == 4
	var run_facing_ok: bool = true
	for b in drag.belts_for_adapter():
		if b.dir != Iso.DIR_E:
			run_facing_ok = false
	var drag_ok: bool = axis_ok and dir_e_ok and dir_w_ok and dir_s_ok and dir_n_ok \
		and dir_zero_ok and skip_ok and run_facing_ok
	drag.free()

	# NEGATIVE CONTROL: run the SAME assertion against a deliberately wrong table — N and S
	# swapped, the most plausible real mistake (someone "fixes" a chevron pointing the wrong
	# way by flipping the enum instead of the art). It must fail. If a swapped table also
	# passed, the quadrant check is not reading the transform at all and its green means
	# nothing.
	var swapped: Array[Vector2i] = [
		Iso.DIR_VECTORS[Iso.DIR_S], Iso.DIR_VECTORS[Iso.DIR_E],
		Iso.DIR_VECTORS[Iso.DIR_N], Iso.DIR_VECTORS[Iso.DIR_W],
	]
	var control_caught: bool = not _quadrants_match(swapped)

	var result: String = "PASS" if (quadrant_ok and cycle_ok and back_ok and occ_ok \
		and stored_ok and nondir_ok and drag_ok and control_caught) else "FAIL"
	print("BELT_CHECK quadrants=%s swapped_table_control_caught=%s cycle=%s rot_back=%s occupancy=%s dir_stored=%s nondir_minus1=%s drag_run=%s result=%s" \
		% [quadrant_ok, control_caught, cycle_ok, back_ok, occ_ok, stored_ok, nondir_ok, drag_ok, result])
	if not drag_ok:
		print("BELT_CHECK  -> drag run wrong: axis_lock=%s dirE=%s dirW=%s dirS=%s dirN=%s zero=%s skip_blocked=%s facing=%s" \
			% [axis_ok, dir_e_ok, dir_w_ok, dir_s_ok, dir_n_ok, dir_zero_ok, skip_ok, run_facing_ok])
	if not control_caught:
		print("BELT_CHECK  -> an N/S-swapped direction table ALSO passed the quadrant test, " +
			"so it is not reading the transform. Check is vacuous, not passing.")
	if not quadrant_ok:
		print("BELT_CHECK  -> a facing does not project where §5's art contract says:%s. " \
			% [quadrant_detail] + "DIR_VECTORS disagrees with world_to_screen.")
	if not occ_ok:
		print("BELT_CHECK  -> occupancy accepted an overlapping belt (first=%d same=%d cross=%d free=%d); " \
			% [first, same_dir, cross_dir, free_dir] + "crossing belts are a collision at any facing.")
	if not stored_ok:
		print("BELT_CHECK  -> belts_for_adapter() lost cell/dir; the sim cannot build lanes from this.")

# Does `table` place each facing in the screen quadrant §5's art contract names for it?
# Takes the table as a parameter precisely so BELT_CHECK can run it against a wrong one and
# prove it fails — a check that only ever sees the right answer proves nothing (B18).
func _quadrants_match(table: Array[Vector2i]) -> bool:
	var origin: Vector2 = Iso.world_to_screen(0.0, 0.0)
	# Expected SIGN of the screen-space step per facing: n=(+,-) e=(+,+) s=(-,+) w=(-,-).
	var want: Array[Vector2] = [
		Vector2(1, -1), Vector2(1, 1), Vector2(-1, 1), Vector2(-1, -1),
	]
	for i in range(Iso.DIR_COUNT):
		var v: Vector2i = table[i]
		var scr: Vector2 = Iso.world_to_screen(float(v.x), float(v.y)) - origin
		if signf(scr.x) != want[i].x or signf(scr.y) != want[i].y:
			return false
	return true

# Test-only accessor: the entity occupying a cell. Not for render use.
func entity_for_test(cell: Vector2i) -> Dictionary:
	var id: int = _occupancy.get(cell, -1)
	for e in _entities:
		if e.id == id:
			return e
	return {}

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
