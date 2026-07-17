extends Node2D

# Placement / hover targeting — isometric-design.md §2.
#
# Turns the mouse into a world cell and draws a ghost highlight there. This is the
# ground-plane pick: hovering a tall machine highlights the tile the cursor is
# GEOMETRICALLY over, not the tile whose sprite happens to be drawn under the cursor (§2).
# Sprite-accurate picking only matters if we ship multi-level.
#
# THE TRAP this is built to avoid: using the raw viewport mouse position as if it were
# world space. That is correct at zoom 1.0 with the camera at the origin — i.e. exactly
# the state you develop in — and silently wrong at every other zoom and pan. It composes
# with the A2 camera (zoom-to-cursor, drag) only if the canvas transform is inverted
# first. HOVER_CHECK below pins that, and negative-controls it against the naive version.
#
# Never pick via colour-buffer readback (§2): it couples picking to draw order, breaks
# under post-processing, and stalls the GPU. The inverse transform is 6 flops.

const Iso = preload("res://scripts/iso.gd")
const BuildingDefs = preload("res://scripts/building_defs.gd")

const OK_FILL := Color(1.0, 1.0, 1.0, 0.22)
const OK_LINE := Color(1.0, 1.0, 1.0, 0.85)
# Blocked state is NOT signalled by colour alone — the outline also goes dashed-heavy.
# §6.2 makes redundant non-colour encoding non-negotiable, and red/green is exactly the
# pair a meaningful fraction of this audience cannot separate.
const BAD_FILL := Color(0.9, 0.2, 0.2, 0.28)
const BAD_LINE := Color(1.0, 0.35, 0.35, 0.95)
const BAD_LINE_WIDTH := 2.5

@export var entity_layer_path: NodePath

var _hover_cell: Vector2i = Vector2i.ZERO
var _has_hover: bool = false
var _def_index: int = 0
var _dir: int = Iso.DIR_E  # ghost facing; only meaningful for directional defs
var _entity_layer: Node = null

func _ready() -> void:
	if entity_layer_path != NodePath():
		_entity_layer = get_node_or_null(entity_layer_path)
	_run_hover_check()

func current_def() -> Dictionary:
	return BuildingDefs.get_def(_def_index)

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		var k: InputEventKey = event
		# Keys 1..N select a building type. No build menu yet — the slice needs the
		# placement path proven, not UI chrome.
		var n: int = k.keycode - KEY_1
		if n >= 0 and n < BuildingDefs.count():
			_def_index = n
			queue_redraw()
		elif k.keycode == KEY_R:
			# Rotates the GHOST only. transport-v0.md §1: belts are one-way and "reversal is
			# a rebuild, not a runtime state" — so there is deliberately no path here that
			# re-faces a placed belt. Re-facing = remove + place, and the sim's lane model
			# depends on that being true.
			_dir = Iso.rotate_cw(_dir, -1 if k.shift_pressed else 1)
			queue_redraw()
	elif event is InputEventMouseButton:
		var mb: InputEventMouseButton = event
		if mb.button_index == MOUSE_BUTTON_LEFT and mb.pressed and _entity_layer != null:
			# A click on a blocked cell is a normal event, not an error: place() returns
			# -1 and we simply do nothing.
			_entity_layer.place(current_def(), _hover_cell, _dir)
			queue_redraw()

func _process(_delta: float) -> void:
	# get_global_mouse_position() already applies the canvas (camera) transform, so this
	# is the correct, camera-composed path. It is the same inverse HOVER_CHECK exercises.
	var cell: Vector2i = cell_at_viewport(get_viewport().get_mouse_position())
	if not _has_hover or cell != _hover_cell:
		_hover_cell = cell
		_has_hover = true
		queue_redraw()

# Viewport pixel -> world cell. The canvas transform inverse is what makes this hold under
# any zoom/pan; Iso.screen_to_world then maps canvas pixels to fractional tile coords, and
# floor() picks the containing cell.
func cell_at_viewport(viewport_pos: Vector2) -> Vector2i:
	var canvas_pos: Vector2 = get_canvas_transform().affine_inverse() * viewport_pos
	return Iso.screen_to_cell(canvas_pos)

# The naive version this must not be: treats viewport pixels as world pixels. Kept as
# HOVER_CHECK's negative control, NOT as a fallback. Do not call it.
func _cell_at_viewport_naive(viewport_pos: Vector2) -> Vector2i:
	return Iso.screen_to_cell(viewport_pos)

func _draw() -> void:
	if not _has_hover:
		return
	var def: Dictionary = current_def()
	# The ghost spans the type's real footprint, so what the player sees is what place()
	# will claim. A 1x1 ghost for a 3x3 machine is how you ship a placement system that
	# lies about its own footprint.
	var x: float = float(_hover_cell.x)
	var y: float = float(_hover_cell.y)
	var w: float = float(def.w)
	var h: float = float(def.h)
	var n: Vector2 = Iso.world_to_screen(x, y)
	var east: Vector2 = Iso.world_to_screen(x + w, y)
	var s: Vector2 = Iso.world_to_screen(x + w, y + h)
	var west: Vector2 = Iso.world_to_screen(x, y + h)

	var blocked: bool = _entity_layer != null and not _entity_layer.can_place(def, _hover_cell)
	var fill: Color = BAD_FILL if blocked else OK_FILL
	var line: Color = BAD_LINE if blocked else OK_LINE
	var width: float = BAD_LINE_WIDTH if blocked else 1.0

	draw_colored_polygon(PackedVector2Array([n, east, s, west]), fill)
	draw_polyline(PackedVector2Array([n, east, s, west, n]), line, width)

	# A one-way belt placed without seeing its facing is placed blind, and since §1 makes
	# reversal a rebuild, a wrong facing costs a remove+replace rather than a keypress. The
	# ghost has to show it BEFORE the click.
	if BuildingDefs.is_directional(def):
		_draw_ghost_arrow(x + w * 0.5, y + h * 0.5, line)

func _draw_ghost_arrow(cx: float, cy: float, colour: Color) -> void:
	var centre: Vector2 = Iso.world_to_screen(cx, cy)
	var step: Vector2i = Iso.dir_vector(_dir)
	# Project the facing through the transform rather than keeping a per-direction table of
	# screen offsets — a second copy of the projection is exactly the B31/B43 drift.
	var ahead: Vector2 = Iso.world_to_screen(cx + float(step.x) * 0.5, cy + float(step.y) * 0.5)
	var forward: Vector2 = ahead - centre
	var side := Vector2(-forward.y, forward.x) * 0.45
	var tip: Vector2 = centre + forward * 0.8
	var back: Vector2 = centre - forward * 0.3
	draw_line(back, tip, colour, 2.0)
	draw_polyline(PackedVector2Array([tip - forward * 0.35 - side, tip, tip - forward * 0.35 + side]),
		colour, 2.0)

# --- HOVER_CHECK -----------------------------------------------------------------------
#
# Pure transform math over the real canvas transform, so it runs headless.
#
# For every zoom stop the slice uses, at fractional camera offsets (the case that breaks
# naive implementations, and the state zoom-to-cursor leaves the camera in), project a
# known cell's centre out to a viewport position and require cell_at_viewport() to invert
# it back to that same cell. Round-tripping through the real transform is the point: it
# tests the code path hover actually uses, not a re-derivation of it.
#
# NEGATIVE CONTROL: the naive viewport-as-world version must FAIL this. If it passed, the
# fixture would not be exercising zoom/pan at all and a green result would mean nothing —
# the B18 failure mode.
func _run_hover_check() -> void:
	var cam: Camera2D = get_viewport().get_camera_2d()
	if cam == null:
		print("HOVER_CHECK result=SKIP reason=no_camera")
		return

	var saved_pos: Vector2 = cam.position
	var saved_zoom: Vector2 = cam.zoom

	var zooms: Array[float] = [1.0, 0.5, 0.25, 2.0]
	# Fractional offsets: zoom-to-cursor leaves the camera on non-integer positions, which
	# is precisely where a sloppy inverse drifts by a cell.
	var offsets: Array[Vector2] = [
		Vector2.ZERO, Vector2(0.5, 0.25), Vector2(37.3, -11.7), Vector2(-420.9, 138.4),
	]
	var cells: Array[Vector2i] = [
		Vector2i(0, 0), Vector2i(1, 0), Vector2i(0, 1), Vector2i(4, 4),
		Vector2i(-3, 2), Vector2i(7, -5),
	]

	var checked: int = 0
	var failed: int = 0
	var naive_agreed: int = 0

	for z in zooms:
		for off in offsets:
			cam.zoom = Vector2(z, z)
			cam.position = saved_pos + off
			# force_update_scroll makes the canvas transform reflect the new camera state
			# now, rather than at the next frame — otherwise we would test stale values.
			cam.force_update_scroll()
			for c in cells:
				# Cell centre -> canvas px -> viewport px, using the same transform the
				# renderer uses.
				var canvas_pt: Vector2 = Iso.world_to_screen(float(c.x) + 0.5, float(c.y) + 0.5)
				var viewport_pt: Vector2 = get_canvas_transform() * canvas_pt
				checked += 1
				if cell_at_viewport(viewport_pt) != c:
					failed += 1
				if _cell_at_viewport_naive(viewport_pt) == c:
					naive_agreed += 1

	cam.position = saved_pos
	cam.zoom = saved_zoom
	cam.force_update_scroll()

	# The naive path agrees only in the degenerate identity case (zoom 1.0, zero offset).
	# If it agreed everywhere, the fixture never left that case and proves nothing.
	var control_caught: bool = naive_agreed < checked
	var result: String = "PASS" if (failed == 0 and control_caught) else "FAIL"
	print("HOVER_CHECK checked=%d failed=%d naive_agreed=%d/%d control_caught=%s result=%s" \
		% [checked, failed, naive_agreed, checked, control_caught, result])
	if failed > 0:
		print("HOVER_CHECK  -> cursor->cell is wrong under zoom/pan; the canvas transform " +
			"is not being inverted (§2).")
	if not control_caught:
		print("HOVER_CHECK  -> the naive viewport-as-world path passed too, so this " +
			"fixture is not exercising zoom/pan. Check is vacuous, not passing.")
