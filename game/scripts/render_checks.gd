extends Node

# Boot-time render checks. Windowed only — they read the rendered framebuffer, so a
# headless run skips them.
#
# These exist because of B31: a plausible-sounding claim about the renderer ("Godot's iso
# layout matches §2, we get the transform for free") went unmeasured and was wrong by
# 389px. Every claim below was one I had only reasoned about. Reasoning about the renderer
# is how B31 happened; a boot check is ~20 lines.
#
#   FILTER_CHECK  — B22 / isometric-design.md §1: Nearest filtering is live at render
#                   time, held across real camera motion at the zoom stops the slice uses.
#   OVERLAY_CHECK — isometric-design.md §6.3 / §7 Q4: screen-space overlays under a
#                   CanvasLayer do not pan or scale with the world camera.

# Zoom stops the vertical slice actually uses. 0.125 is excluded: at that stop §6.4 stops
# drawing entities at all, so it is not a filtering question.
const SLICE_ZOOMS: Array[float] = [1.0, 0.5, 0.25]

# Sub-pixel camera drift per frame. Deliberately non-round in both axes so the sampler
# lands on many different phase offsets rather than cycling through a few.
const DRIFT_PER_FRAME := Vector2(0.37, 0.19)
const MOTION_FRAMES := 12

const SAMPLE_HALF_W := 220
const SAMPLE_HALF_H := 130
const COLOR_EPSILON := 0.02

# Must match terrain_layer.gd's placeholder fill.
const TILE_FILL := Color(0.25, 0.55, 0.35, 1.0)

# Opt-in flag. These checks are NOT part of a normal boot — see enabled().
const FLAG := "--render-checks"

var _done: bool = false

# Run only when explicitly asked: `tools/godot4 --path game -- --render-checks`.
#
# They must not run on a normal boot. Each frame does a full-viewport GPU readback, which
# stalls the frame loop hard — measured: 40 ticks in 3.8s where 229 were owed. That is the
# sim clock *correctly* reporting a starved loop (§1.4: if the sim cannot hit rate the game
# slows down, it does not drop ticks), but it turns SIM_CHECK red for a reason that has
# nothing to do with the sim. A harness that breaks the thing it measures is worse than no
# harness: it trains people to ignore a failing check.
static func enabled() -> bool:
	return OS.get_cmdline_user_args().has(FLAG)

func run(camera: Camera2D, overlay: CanvasItem, world_node: CanvasItem,
		non_terrain: Array = []) -> void:
	if _done or DisplayServer.get_name() == "headless":
		return
	_done = true
	await _filter_check(camera, non_terrain)
	await _overlay_check(camera, overlay, world_node)
	# Nothing else to do in this mode, and the caller suppressed SIM_CHECK.
	get_tree().quit(0)

# --- B22 -------------------------------------------------------------------------------
#
# WHY NOT "PAN AROUND AND LOOK": shimmer is a perceptual symptom of a mechanical cause. By
# eye it is unrepeatable, unavailable to CI, and unavailable to an agent with no eyes on
# the window — "it looked fine to me" is an assertion, not a measurement, and asserting is
# what caused B31.
#
# The mechanism: under NEAREST the sampler returns a verbatim texel, so every rendered
# pixel must be a colour already in the atlas (or the background). Under LINEAR, texels
# blend along the diamond's diagonal edges and emit intermediate colours present in
# neither. Those intermediates ARE the shimmer — as the camera drifts sub-pixel they churn
# frame to frame. So: count pixels that are neither. Nearest => 0 at every phase offset.
#
# Motion matters, and not only because it was asked for: a single static frame samples ONE
# sub-pixel phase. Nearest and Linear coincide at texel-aligned phases, so a single lucky
# frame can pass under Linear. Drifting the camera sweeps many phases per zoom stop.
#
# SCOPE — this tests the TERRAIN TILE SAMPLER and nothing else, so every non-terrain node
# is hidden for the duration. That is not tidiness, it is required for the check to mean
# anything: the two-colour net below only holds if the framebuffer contains only terrain.
#
# This is inventory B41 arriving early, and it is worth understanding rather than working
# around. B41 predicted the net would go slack when REAL art landed. It went slack the
# moment the dummy entity prisms landed: 642,790 "blended" px, and the check confidently
# printed "default_texture_filter is not Nearest" — a false positive with a WRONG
# diagnosis pointing at an innocent setting. A check that misreports its own subject is
# more dangerous than no check. Hiding non-terrain restores the invariant honestly.
#
# KNOWN LIMIT, still open (B41): this only defers the problem. The invariant is "the
# sampled region contains exactly two colours", which survives entities being hidden but
# NOT terrain itself gaining real §5 art (face shading, per-category tint). At that point
# the net must become a palette-membership test against the generated atlas. Do not read a
# later PASS on textured terrain as proof of anything.
func _filter_check(camera: Camera2D, non_terrain: Array) -> void:
	var saved_pos: Vector2 = camera.position
	var saved_zoom: Vector2 = camera.zoom

	var restore: Array = []
	for n in non_terrain:
		if n is CanvasItem and n.visible:
			restore.append(n)
			n.visible = false

	var total: int = 0
	var blended: int = 0
	var worst_zoom: float = -1.0
	var worst_blended: int = 0

	for z in SLICE_ZOOMS:
		camera.zoom = Vector2(z, z)
		var zoom_blended: int = 0
		for i in range(MOTION_FRAMES):
			# Real camera motion, sub-pixel: the pan a player does, sampled at a new phase
			# offset every frame.
			camera.position = saved_pos + DRIFT_PER_FRAME * float(i)
			await RenderingServer.frame_post_draw
			var img: Image = camera.get_viewport().get_texture().get_image()
			if img == null:
				print("FILTER_CHECK result=SKIP reason=no_viewport_image")
				camera.position = saved_pos
				camera.zoom = saved_zoom
				_restore_visible(restore)
				return
			var counts: Array = _count_blended(img)
			total += int(counts[0])
			zoom_blended += int(counts[1])
		blended += zoom_blended
		if zoom_blended > worst_blended:
			worst_blended = zoom_blended
			worst_zoom = z

	camera.position = saved_pos
	camera.zoom = saved_zoom
	_restore_visible(restore)

	var result: String = "PASS" if blended == 0 else "FAIL"
	print("FILTER_CHECK zooms=%s frames_per_zoom=%d sampled=%d blended_px=%d result=%s" \
		% [str(SLICE_ZOOMS), MOTION_FRAMES, total, blended, result])
	if result == "FAIL":
		print("FILTER_CHECK  -> worst at zoom=%.3f (%d blended px). Tile edges are being " \
			% [worst_zoom, worst_blended] +
			"interpolated; default_texture_filter is not Nearest at render time (B22/B2).")

func _restore_visible(nodes: Array) -> void:
	for n in nodes:
		n.visible = true

func _count_blended(img: Image) -> Array:
	var bg: Color = ProjectSettings.get_setting(
		"rendering/environment/defaults/default_clear_color", Color(0, 0, 0, 1))
	# Sample around the grid, away from the CanvasLayer label — its text is antialiased and
	# would contribute blended pixels that say nothing about the tile sampler.
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
	return [total, blended]

# --- §6.3 / §7 Q4 ----------------------------------------------------------------------
#
# The review asserted that moving TickLabel under a CanvasLayer keeps overlays in screen
# space, and §6.3 requires alert markers to stay fixed-size and unoccluded at any zoom —
# that is the whole reason a 0.125-zoom factory stays diagnosable. Both were reasoned, not
# measured. This is on the slice path (the slice has an overlay and now has a camera), so
# per B31's lesson it gets a check rather than a promise.
#
# NEGATIVE CONTROL: also assert the WORLD node moved. Otherwise a camera that silently
# failed to move would leave the overlay looking correctly pinned and the check would pass
# for the wrong reason — the B18 failure mode.
func _overlay_check(camera: Camera2D, overlay: CanvasItem, world_node: CanvasItem) -> void:
	await RenderingServer.frame_post_draw
	var overlay_before: Vector2 = overlay.get_global_transform_with_canvas().origin
	var world_before: Vector2 = world_node.get_global_transform_with_canvas().origin

	var saved_pos: Vector2 = camera.position
	var saved_zoom: Vector2 = camera.zoom
	camera.position = saved_pos + Vector2(400.0, 250.0)
	camera.zoom = Vector2(0.25, 0.25)
	await RenderingServer.frame_post_draw

	var overlay_after: Vector2 = overlay.get_global_transform_with_canvas().origin
	var world_after: Vector2 = world_node.get_global_transform_with_canvas().origin

	camera.position = saved_pos
	camera.zoom = saved_zoom

	var overlay_drift: float = (overlay_after - overlay_before).length()
	var world_drift: float = (world_after - world_before).length()

	# Overlay must be pinned AND the world must have moved.
	var result: String = "PASS" if (overlay_drift < 0.001 and world_drift > 1.0) else "FAIL"
	print("OVERLAY_CHECK overlay_drift=%.4f px world_drift=%.1f px result=%s" \
		% [overlay_drift, world_drift, result])
	if overlay_drift >= 0.001:
		print("OVERLAY_CHECK  -> overlay moved with the camera; it is in world space, " +
			"not under a CanvasLayer (§6.3).")
	elif world_drift <= 1.0:
		print("OVERLAY_CHECK  -> world did not move, so the overlay being pinned proves " +
			"nothing. Check is vacuous, not passing.")

func _near(a: Color, b: Color) -> bool:
	return absf(a.r - b.r) < COLOR_EPSILON \
		and absf(a.g - b.g) < COLOR_EPSILON \
		and absf(a.b - b.b) < COLOR_EPSILON
