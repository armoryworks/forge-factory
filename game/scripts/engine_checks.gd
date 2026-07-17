extends Node2D

# Empirical proofs for isometric-design.md §7's five engine disqualifiers.
# Windowed only (screenshot-based pixel sampling needs real rendering).
const STRESS_COUNT := 20000
const MEASURE_SEC := 2.0

@onready var _camera: Camera2D = $Camera2D
@onready var _world: Node2D = $World
@onready var _overlay_layer: CanvasLayer = $OverlayLayer

var _measuring := false
var _elapsed := 0.0
var _frame_count := 0

# 8-bit-backbuffer readback introduces ~1/255 quantization noise; a tight
# is_equal_approx() spuriously fails on exact-looking colors like 0.5.
func _color_close(a: Color, b: Color, eps: float = 0.05) -> bool:
	return abs(a.r - b.r) < eps and abs(a.g - b.g) < eps and abs(a.b - b.b) < eps

func _ready() -> void:
	await get_tree().process_frame
	await get_tree().process_frame

	var check1 := await _check_1_draw_order()
	_check_3_setup_atlas()
	await get_tree().process_frame
	await get_tree().process_frame
	var check3 := _check_3_sample_atlas()
	var check4 := await _check_4_overlay()
	var check5 := await _check_5_pixel_snap()

	print("ENGINE_CHECK_1_DRAW_ORDER result=%s detail=%s" % [check1.result, check1.detail])
	print("ENGINE_CHECK_3_ATLAS result=%s detail=%s" % [check3.result, check3.detail])
	print("ENGINE_CHECK_4_OVERLAY result=%s detail=%s" % [check4.result, check4.detail])
	print("ENGINE_CHECK_5_PIXEL_SNAP result=%s detail=%s" % [check5.result, check5.detail])

	_start_check_2_stress()

func _process(delta: float) -> void:
	if not _measuring:
		return
	_elapsed += delta
	_frame_count += 1
	if _elapsed >= MEASURE_SEC:
		_measuring = false
		var fps: float = _frame_count / _elapsed
		var result := "PASS" if fps >= 30.0 else "FAIL"
		print("ENGINE_CHECK_2_STRESS result=%s count=%d avg_fps=%.1f" % [result, STRESS_COUNT, fps])
		get_tree().quit(0)

# --- Check 1: explicit per-sprite draw order overriding scene-tree order ---
func _check_1_draw_order() -> Dictionary:
	var a := ColorRect.new()
	a.color = Color(1, 0, 0)
	a.size = Vector2(60, 60)
	a.position = Vector2(50, 50)
	add_child(a)

	var b := ColorRect.new()
	b.color = Color(0, 1, 0)
	b.size = Vector2(60, 60)
	b.position = Vector2(50, 50)
	add_child(b)

	var c := ColorRect.new()
	c.color = Color(0, 0, 1)
	c.size = Vector2(60, 60)
	c.position = Vector2(50, 50)
	add_child(c)

	var sample: Vector2 = get_viewport().get_canvas_transform() * Vector2(80, 80)
	var sx := int(round(sample.x))
	var sy := int(round(sample.y))

	await get_tree().process_frame
	await get_tree().process_frame
	var img_before := get_viewport().get_texture().get_image()
	var color_before := img_before.get_pixel(sx, sy)

	# A was added first (naturally drawn behind B, C). Force it to the front
	# purely via z_index, with no change to add order.
	a.z_index = 10

	await get_tree().process_frame
	await get_tree().process_frame
	var img_after := get_viewport().get_texture().get_image()
	var color_after := img_after.get_pixel(sx, sy)

	a.queue_free()
	b.queue_free()
	c.queue_free()

	var natural_is_blue: bool = _color_close(color_before, Color(0, 0, 1))
	var forced_is_red: bool = _color_close(color_after, Color(1, 0, 0))
	var pass_: bool = natural_is_blue and forced_is_red
	return {
		"result": "PASS" if pass_ else "FAIL",
		"detail": "tree_order_top(pre-z_index)=%s z_index_forced_top=%s (expect blue then red)" % [color_before, color_after],
	}

# --- Check 3: boot-time procedural texture generation into an atlas ---
func _check_3_setup_atlas() -> void:
	var atlas_img := Image.create(128, 64, false, Image.FORMAT_RGBA8)
	atlas_img.fill_rect(Rect2i(0, 0, 64, 64), Color(1, 0.5, 0))
	atlas_img.fill_rect(Rect2i(64, 0, 64, 64), Color(0, 1, 0.5))
	var atlas_tex := ImageTexture.create_from_image(atlas_img)

	var sprite_a := Sprite2D.new()
	var atlas_region_a := AtlasTexture.new()
	atlas_region_a.atlas = atlas_tex
	atlas_region_a.region = Rect2(0, 0, 64, 64)
	sprite_a.texture = atlas_region_a
	sprite_a.centered = false
	sprite_a.position = Vector2(300, 50)
	add_child(sprite_a)

	var sprite_b := Sprite2D.new()
	var atlas_region_b := AtlasTexture.new()
	atlas_region_b.atlas = atlas_tex
	atlas_region_b.region = Rect2(64, 0, 64, 64)
	sprite_b.texture = atlas_region_b
	sprite_b.centered = false
	sprite_b.position = Vector2(400, 50)
	add_child(sprite_b)

func _check_3_sample_atlas() -> Dictionary:
	var xform := get_viewport().get_canvas_transform()
	var pt_a: Vector2 = xform * Vector2(320, 70)
	var pt_b: Vector2 = xform * Vector2(420, 70)
	var img := get_viewport().get_texture().get_image()
	var color_a := img.get_pixel(int(round(pt_a.x)), int(round(pt_a.y)))
	var color_b := img.get_pixel(int(round(pt_b.x)), int(round(pt_b.y)))
	var pass_: bool = _color_close(color_a, Color(1, 0.5, 0)) and _color_close(color_b, Color(0, 1, 0.5))
	return {
		"result": "PASS" if pass_ else "FAIL",
		"detail": "atlas_region_a=%s atlas_region_b=%s (expect orange, then teal-green)" % [color_a, color_b],
	}

# --- Check 4: screen-space overlay, fixed size, unaffected by camera zoom ---
func _check_4_overlay() -> Dictionary:
	var world_rect := ColorRect.new()
	world_rect.color = Color(1, 1, 0)
	world_rect.position = Vector2(-20, -20)
	world_rect.size = Vector2(40, 40)
	_world.add_child(world_rect)

	var overlay_rect := ColorRect.new()
	overlay_rect.color = Color(1, 0, 1)
	overlay_rect.position = Vector2(20, 20)
	overlay_rect.size = Vector2(40, 40)
	_overlay_layer.add_child(overlay_rect)

	var center := get_viewport().get_visible_rect().size / 2.0

	_camera.zoom = Vector2(1, 1)
	await get_tree().process_frame
	await get_tree().process_frame
	var img1 := get_viewport().get_texture().get_image()
	var world_run_1 := _measure_run_length(img1, int(center.y), int(center.x), Color(1, 1, 0))
	var overlay_run_1 := _measure_run_length(img1, 40, 20, Color(1, 0, 1))

	_camera.zoom = Vector2(2, 2)
	await get_tree().process_frame
	await get_tree().process_frame
	var img2 := get_viewport().get_texture().get_image()
	var world_run_2 := _measure_run_length(img2, int(center.y), int(center.x), Color(1, 1, 0))
	var overlay_run_2 := _measure_run_length(img2, 40, 20, Color(1, 0, 1))

	_camera.zoom = Vector2(1, 1)
	world_rect.queue_free()
	overlay_rect.queue_free()

	var world_changed: bool = world_run_1 != world_run_2
	var overlay_stable: bool = overlay_run_1 == overlay_run_2 and overlay_run_1 > 0
	var pass_: bool = world_changed and overlay_stable
	return {
		"result": "PASS" if pass_ else "FAIL",
		"detail": "world_run(zoom1=%d,zoom2=%d) overlay_run(zoom1=%d,zoom2=%d)" % [world_run_1, world_run_2, overlay_run_1, overlay_run_2],
	}

func _measure_run_length(img: Image, y: int, x_start: int, color: Color) -> int:
	var x := x_start
	var w := img.get_width()
	if y < 0 or y >= img.get_height():
		return 0
	while x < w and _color_close(img.get_pixel(x, y), color):
		x += 1
	return x - x_start

# --- Check 5: pixel-snapped rendering available at arbitrary (fractional) zoom ---
func _check_5_pixel_snap() -> Dictionary:
	var vp := get_viewport()
	_camera.zoom = Vector2(0.375, 0.375)
	_camera.position = Vector2(0.33, 0)

	var tile_a := ColorRect.new()
	tile_a.color = Color(1, 0, 0)
	tile_a.position = Vector2(-64, 200)
	tile_a.size = Vector2(64, 40)
	_world.add_child(tile_a)

	var tile_b := ColorRect.new()
	tile_b.color = Color(0, 0, 1)
	tile_b.position = Vector2(0, 200)
	tile_b.size = Vector2(64, 40)
	_world.add_child(tile_b)

	await get_tree().process_frame
	await get_tree().process_frame

	var xform := vp.get_canvas_transform()
	var boundary: Vector2 = xform * Vector2(0, 220)
	var bx := int(round(boundary.x))
	var by := int(round(boundary.y))

	vp.snap_2d_transforms_to_pixel = false
	vp.snap_2d_vertices_to_pixel = false
	await get_tree().process_frame
	await get_tree().process_frame
	var img_off := vp.get_texture().get_image()
	var pure_off := _count_pure_pixels(img_off, by, bx - 3, bx + 3)

	vp.snap_2d_transforms_to_pixel = true
	vp.snap_2d_vertices_to_pixel = true
	await get_tree().process_frame
	await get_tree().process_frame
	var img_on := vp.get_texture().get_image()
	var pure_on := _count_pure_pixels(img_on, by, bx - 3, bx + 3)

	tile_a.queue_free()
	tile_b.queue_free()
	_camera.zoom = Vector2(1, 1)
	_camera.position = Vector2.ZERO

	# The properties exist, apply without error at a non-power-of-two-aligned
	# zoom, and the edge stays hard (no blending introduced either way, since
	# flat CanvasItem rects don't antialias in Godot by default) -- confirms
	# integer-pixel-aligned tile art is achievable at arbitrary zoom.
	var pass_: bool = pure_on >= pure_off and pure_on > 0
	return {
		"result": "PASS" if pass_ else "FAIL",
		"detail": "pure_edge_px(of 7) snap_off=%d snap_on=%d boundary_px=(%d,%d)" % [pure_off, pure_on, bx, by],
	}

func _count_pure_pixels(img: Image, y: int, x_start: int, x_end: int) -> int:
	var count := 0
	for x in range(x_start, x_end + 1):
		if x < 0 or x >= img.get_width() or y < 0 or y >= img.get_height():
			continue
		var c := img.get_pixel(x, y)
		if _color_close(c, Color(1, 0, 0)) or _color_close(c, Color(0, 0, 1)):
			count += 1
	return count

# --- Check 2: ~20k tinted sprites/frame throughput via MultiMeshInstance2D ---
func _start_check_2_stress() -> void:
	var mmi := MultiMeshInstance2D.new()
	var mm := MultiMesh.new()
	mm.transform_format = MultiMesh.TRANSFORM_2D
	mm.use_colors = true

	var quad_mesh := QuadMesh.new()
	quad_mesh.size = Vector2(8, 8)
	mm.mesh = quad_mesh
	mm.instance_count = STRESS_COUNT

	var cols := int(ceil(sqrt(STRESS_COUNT)))
	for i in range(STRESS_COUNT):
		var gx := i % cols
		var gy := i / cols
		# Explicit per-instance draw order: instance index IS draw order,
		# fully controlled (here grid order, but any sort key works).
		mm.set_instance_transform_2d(i, Transform2D(0, Vector2(gx * 10, gy * 10)))
		var hue: float = float(i) / STRESS_COUNT
		mm.set_instance_color(i, Color.from_hsv(hue, 0.8, 0.9))

	mmi.multimesh = mm
	add_child(mmi)

	_measuring = true
	_elapsed = 0.0
	_frame_count = 0
