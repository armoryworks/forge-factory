extends RefCounted

# Canonical isometric constants and world<->screen transforms.
#
# Consume via `const Iso = preload("res://scripts/iso.gd")`, NOT via class_name. A
# class_name identifier resolves through the editor-generated global class cache, which
# does not exist on a fresh headless/CI checkout — it parse-errors the whole scene tree
# before SIM_CHECK can run. preload resolves at parse time with no cache. (Learned the
# hard way; don't "clean this up" into a class_name.)
# See docs/isometric-design.md §1 (projection) and §2 (transforms).
#
# THE ONE PLACE these constants live. Do not re-declare TILE_W/TILE_H/TILE_Z in a
# renderer script — the moment a second file owns a copy they drift silently.
#
# Projection is dimetric 2:1: TILE_W == 2 * TILE_H. That ratio is load-bearing, not
# cosmetic — it makes every transform below integer shifts/adds and gives pixel-perfect
# 2-across/1-down diagonals. Changing it to a non-2:1 ratio breaks §1's guarantees.
const TILE_W := 64
const TILE_H := 32

# Screen-space Y offset per elevation level.
#
# v1 IS SINGLE-LEVEL: every caller passes z = 0 today, and nothing renders elevated.
# The z term is carried anyway, deliberately — §1/§8 commit to it because retrofitting
# elevation into a 2D-only transform is a rewrite, while carrying an unused z costs one
# add. Do NOT "simplify" it away on the grounds that it is always zero.
const TILE_Z := 16

# World (tile, fractional allowed — items sit BETWEEN tiles on belts) -> screen.
#
# This is §2's canonical transform and the ONLY one the renderer should use. It does NOT
# match Godot's TileMapLayer.map_to_local — an earlier comment here claimed it did, which
# was the B31 error (map_to_local uses staggered cells; see cell_for_world below). The two
# agree only once world coords are converted through cell_for_world, and
# terrain_layer._check_transform_agreement proves that agreement to sub-0.001px on every
# boot rather than asserting it.
static func world_to_screen(x: float, y: float, z: float = 0.0) -> Vector2:
	var sx: float = (x - y) * (float(TILE_W) * 0.5)
	var sy: float = (x + y) * (float(TILE_H) * 0.5) - z * float(TILE_Z)
	return Vector2(sx, sy)

# Screen -> world on the z = 0 GROUND PLANE.
#
# This is the ground-plane pick, and it is what mouse hover should use (§2): hovering a
# tall machine should highlight the tile the cursor is geometrically over, not the tile
# whose sprite happens to be drawn under the cursor. Sprite-accurate picking (raycast
# down through elevations) is only needed if we ship multi-level.
#
# Never implement picking via colour-buffer readback — it couples picking to draw order,
# breaks under post-processing, and stalls the GPU. This is 6 flops.
static func screen_to_world(s: Vector2) -> Vector2:
	var a: float = s.x / (float(TILE_W) * 0.5)
	var b: float = s.y / (float(TILE_H) * 0.5)
	return Vector2((a + b) * 0.5, (b - a) * 0.5)

# Tile index containing a screen point, on the ground plane.
static func screen_to_cell(s: Vector2) -> Vector2i:
	var w: Vector2 = screen_to_world(s)
	return Vector2i(floori(w.x), floori(w.y))

# Godot cell index for a world tile — see inventory B31.
#
# MEASURED, NOT ASSUMED. Godot's isometric TileMapLayer does NOT address cells the way
# §2 does, and the difference is not a change of basis. At 64x32 / DIAMOND_DOWN /
# HORIZONTAL offset, it uses STAGGERED (offset) coordinates: cy is a screen row, and odd
# rows are shifted half a tile right. Measured placement:
#     sx = TILE_W/2 + cx*TILE_W + (cy odd ? TILE_W/2 : 0)
#     sy = TILE_H/2 + cy*TILE_H/2
# The tell is that stepping +cy zigzags in x (32 -> 64 -> 32), which no linear transform
# does. §2 instead uses true diamond axes: sx = (x-y)*TILE_W/2, sy = (x+y)*TILE_H/2.
#
# We keep §2 canonical — it is the basis the §3 depth key and the engine-independent sim
# assume (D5/D8), and a staggered basis makes "the far corner of a 3x3 footprint" nearly
# unexpressible. Godot's scheme is confined to the tilemap boundary by this function.
# Inverting the two placements against each other:
#     cy = x + y - 1
#     cx = ((x - y) - 1 - (cy mod 2)) / 2
# The numerator is always even (x-y and x+y share parity), so the division is exact.
# Note `cy mod 2` must be a POSITIVE modulo — GDScript's % truncates toward zero and
# returns -1 for odd negative cy, which silently misplaces every tile above the origin.
#
# terrain_layer.gd re-proves this to sub-0.001px on every boot rather than trusting the
# derivation above. Layers 3-4 use world_to_screen directly and never touch any of this.
static func cell_for_world(x: int, y: int) -> Vector2i:
	var cy: int = x + y - 1
	var cx: int = ((x - y) - 1 - posmod(cy, 2)) / 2
	return Vector2i(cx, cy)

# Depth sort key — §3. Sorts on the FAR corner of the footprint, NOT the origin.
# Sorting a 3x3 machine by its origin sorts it as if it were a 1x1 there, and draws it
# behind entities that are visually in front of it. Ties break on layer, then y, then x,
# then a stable entity id (an unstable sort flickers z-order between frames).
static func depth_key(x: float, y: float, w: float, h: float, z: float = 0.0) -> float:
	return (x + w) + (y + h) + z * 1000.0
