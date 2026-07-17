extends Node2D

# Q1 proof (isometric-design.md §7 disqualifier #1, the hard gate): does
# Godot give explicit per-sprite draw order sufficient to implement §3's
# far-corner depth key -- not just origin, and not an engine-imposed order?
#
# Reproduces the exact "classic bug" §3 names: a big machine's ORIGIN-only
# depth sorts it as if it were a 1x1 tile, hiding it behind something that
# should visually be behind it. Then fixes it with the far-corner formula.
const TILE_W := 64.0
const TILE_H := 32.0
const BIG := 1000.0
const SCREEN_OFFSET := Vector2(400, 200)

func _world_to_screen(x: float, y: float) -> Vector2:
	var sx: float = (x - y) * (TILE_W / 2.0)
	var sy: float = (x + y) * (TILE_H / 2.0)
	return SCREEN_OFFSET + Vector2(sx, sy)

func _footprint_bbox(ox: float, oy: float, w: float, h: float) -> Rect2:
	var corners: Array[Vector2] = [
		_world_to_screen(ox, oy),
		_world_to_screen(ox + w, oy),
		_world_to_screen(ox, oy + h),
		_world_to_screen(ox + w, oy + h),
	]
	var min_pt: Vector2 = corners[0]
	var max_pt: Vector2 = corners[0]
	for pt in corners:
		min_pt.x = min(min_pt.x, pt.x)
		min_pt.y = min(min_pt.y, pt.y)
		max_pt.x = max(max_pt.x, pt.x)
		max_pt.y = max(max_pt.y, pt.y)
	return Rect2(min_pt, max_pt - min_pt)

func _depth_far_corner(ox: float, oy: float, w: float, h: float, z: float = 0.0) -> float:
	return (ox + w) + (oy + h) + z * BIG

func _depth_origin_only(ox: float, oy: float) -> float:
	return ox + oy

func _color_close(a: Color, b: Color, eps: float = 0.05) -> bool:
	return abs(a.r - b.r) < eps and abs(a.g - b.g) < eps and abs(a.b - b.b) < eps

func _ready() -> void:
	await get_tree().process_frame
	await get_tree().process_frame

	# A: big 5x5 machine at (0,0). B: small 1x1 machine at (3,3), fully
	# inside A's footprint -- A should visually occlude B there.
	var a_bbox := _footprint_bbox(0, 0, 5, 5)
	var b_bbox := _footprint_bbox(3, 3, 1, 1)

	var a_rect := ColorRect.new()
	a_rect.color = Color(1, 0, 0) # red = A (the big machine)
	a_rect.position = a_bbox.position
	a_rect.size = a_bbox.size
	add_child(a_rect)

	var b_rect := ColorRect.new()
	b_rect.color = Color(0, 1, 0) # green = B (the small machine)
	b_rect.position = b_bbox.position
	b_rect.size = b_bbox.size
	add_child(b_rect)

	var sample_screen := _world_to_screen(3.5, 3.5) # inside both footprints
	var sx := int(round(sample_screen.x))
	var sy := int(round(sample_screen.y))

	# --- Naive origin-only sort (the classic bug §3 warns about) ---
	a_rect.z_index = int(_depth_origin_only(0, 0)) # 0
	b_rect.z_index = int(_depth_origin_only(3, 3)) # 6
	await get_tree().process_frame
	await get_tree().process_frame
	var img_naive := get_viewport().get_texture().get_image()
	var color_naive := img_naive.get_pixel(sx, sy)

	# --- Correct far-corner sort (spec §3) ---
	a_rect.z_index = int(_depth_far_corner(0, 0, 5, 5)) # 10
	b_rect.z_index = int(_depth_far_corner(3, 3, 1, 1)) # 8
	await get_tree().process_frame
	await get_tree().process_frame
	var img_correct := get_viewport().get_texture().get_image()
	var color_correct := img_correct.get_pixel(sx, sy)

	var naive_is_green: bool = _color_close(color_naive, Color(0, 1, 0)) # bug reproduced
	var correct_is_red: bool = _color_close(color_correct, Color(1, 0, 0)) # bug fixed
	var pass_: bool = naive_is_green and correct_is_red

	print("ENGINE_CHECK_Q1_FARCORNER result=%s naive_top=%s correct_top=%s bug_reproduced=%s bug_fixed=%s" % [
		"PASS" if pass_ else "FAIL", color_naive, color_correct, naive_is_green, correct_is_red,
	])
	get_tree().quit(0 if pass_ else 1)
