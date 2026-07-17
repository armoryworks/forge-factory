extends Node

# B22 — prove the Nearest texture filter actually took effect.
#
# isometric-design.md §1 chose dimetric 2:1 specifically for pixel-perfect 2-across/
# 1-down diagonals, and B2 set default_texture_filter=0 (Nearest) to protect them.
# Linear filtering hands the shimmer back. Nothing had verified the setting was live.
#
# WHY NOT "PAN AROUND AND LOOK": shimmer is a perceptual symptom of a mechanical cause,
# it only appears in motion, and judging it by eye is unrepeatable and unavailable to CI.
# So test the CAUSE instead, which is strictly stronger:
#
#   Under NEAREST, every rendered pixel is a verbatim copy of some source texel — the
#   sampler picks one texel and returns it. The tile atlas contains exactly two colours
#   (the fill, and fully-transparent). So every on-screen pixel must be either the fill
#   colour or the background clear colour. Nothing else is reachable.
#
#   Under LINEAR, texels are blended along the diamond's diagonal edges, emitting
#   intermediate colours that exist in neither the atlas nor the background. Those
#   intermediates ARE the shimmer: as the camera pans sub-pixel, they churn frame to
#   frame.
#
# So: count pixels that are neither fill nor background. Nearest => 0. Linear => many.
# We force the sampler off the texel grid first (fractional camera offset + a zoom stop
# that minifies), because at an exactly texel-aligned identity transform both filters
# coincide and the test would pass vacuously.

const SAMPLE_HALF_W := 220
const SAMPLE_HALF_H := 130
const COLOR_EPSILON := 0.02

# Must match terrain_layer.gd's placeholder fill.
const TILE_FILL := Color(0.25, 0.55, 0.35, 1.0)

var _done: bool = false

func run(camera: Camera2D) -> void:
	if _done or DisplayServer.get_name() == "headless":
		return
	_done = true

	# Knock the sampler off the texel grid: a fractional offset at a minifying zoom stop.
	# Aligned sampling makes Nearest and Linear agree, which would pass either way.
	var saved_pos: Vector2 = camera.position
	var saved_zoom: Vector2 = camera.zoom
	camera.position = saved_pos + Vector2(0.5, 0.25)
	camera.zoom = Vector2(0.5, 0.5)

	await RenderingServer.frame_post_draw
	var img: Image = camera.get_viewport().get_texture().get_image()

	camera.position = saved_pos
	camera.zoom = saved_zoom

	if img == null:
		print("FILTER_CHECK result=SKIP reason=no_viewport_image")
		return

	var bg: Color = ProjectSettings.get_setting(
		"rendering/environment/defaults/default_clear_color", Color(0, 0, 0, 1))

	# Sample around the grid, away from the CanvasLayer label — text is rendered with
	# antialiasing and would contribute blended pixels that have nothing to do with the
	# tile sampler.
	var cx: int = img.get_width() / 2
	var cy: int = img.get_height() / 2
	var x0: int = maxi(0, cx - SAMPLE_HALF_W)
	var x1: int = mini(img.get_width(), cx + SAMPLE_HALF_W)
	var y0: int = maxi(0, cy - SAMPLE_HALF_H)
	var y1: int = mini(img.get_height(), cy + SAMPLE_HALF_H)

	var total: int = 0
	var blended: int = 0
	for py in range(y0, y1):
		for px in range(x0, x1):
			var c: Color = img.get_pixel(px, py)
			total += 1
			if not (_near(c, TILE_FILL) or _near(c, bg)):
				blended += 1

	var result: String = "PASS" if blended == 0 else "FAIL"
	print("FILTER_CHECK sampled=%d blended_px=%d result=%s" % [total, blended, result])
	if result == "FAIL":
		print("FILTER_CHECK  -> texture filtering is interpolating tile edges; " +
			"default_texture_filter is not Nearest at render time (B22/B2).")

func _near(a: Color, b: Color) -> bool:
	return absf(a.r - b.r) < COLOR_EPSILON \
		and absf(a.g - b.g) < COLOR_EPSILON \
		and absf(a.b - b.b) < COLOR_EPSILON
