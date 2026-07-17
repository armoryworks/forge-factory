extends TileMapLayer

# RENDER BOUNDARY — see docs/isometric-design.md §3 and docs/iso-review-scaffold.md A1.
#
# This TileMapLayer owns render layers 0-2 ONLY: terrain, ground decals, and flat
# coplanar entities (belts, pipes, foundations). Those layers are coplanar, so they draw
# in raw grid iteration order at zero sort cost. That is what a tilemap is good at.
#
# Machines and items (layers 3-4) DO NOT belong here and must never be added to it.
# They live in a separately-managed sorted draw list keyed on the footprint's FAR corner
# (x+w + y+h + z*BIG) with a stable entity-id tiebreak. A tilemap is a grid of cells: it
# has no concept of a multi-tile entity's far corner, and it sorts per-cell. Putting a
# 3x3 assembler here sorts it as if it were a 1x1 at its origin, which draws it behind
# belts that are visually in front of it — the classic isometric failure §3 exists to
# prevent.
#
# Phase 2 stub: procedurally-generated placeholder grid. No art assets — the tile is
# rasterized at runtime (isometric-design.md §5: placeholders are generated, never
# borrowed).
const GRID_SIZE := 5
const TILE_W := 64
const TILE_H := 32

func _ready() -> void:
	var tile_set := TileSet.new()
	tile_set.tile_shape = TileSet.TILE_SHAPE_ISOMETRIC
	tile_set.tile_size = Vector2i(TILE_W, TILE_H)

	var tex := ImageTexture.create_from_image(_make_diamond_tile())

	var source := TileSetAtlasSource.new()
	source.texture = tex
	source.texture_region_size = Vector2i(TILE_W, TILE_H)
	source.create_tile(Vector2i(0, 0))
	var source_id := tile_set.add_source(source)

	self.tile_set = tile_set

	for x in range(GRID_SIZE):
		for y in range(GRID_SIZE):
			set_cell(Vector2i(x, y), source_id, Vector2i(0, 0))

# Rasterize the isometric cell as a diamond with transparent corners. A fully-opaque
# rectangle is wrong: adjacent iso cells overlap by half a tile, so opaque neighbours
# paint over each other and the grid renders as one blob instead of N diamonds.
# Inside test is |dx/(W/2)| + |dy/(H/2)| <= 1 about the tile centre.
func _make_diamond_tile() -> Image:
	var img := Image.create(TILE_W, TILE_H, false, Image.FORMAT_RGBA8)
	img.fill(Color(0.0, 0.0, 0.0, 0.0))

	var fill := Color(0.25, 0.55, 0.35, 1.0)
	var half_w: float = float(TILE_W) * 0.5
	var half_h: float = float(TILE_H) * 0.5

	for py in range(TILE_H):
		for px in range(TILE_W):
			# Pixel centres, so the diamond is symmetric about the tile centre.
			var dx: float = (float(px) + 0.5) - half_w
			var dy: float = (float(py) + 0.5) - half_h
			var d: float = absf(dx) / half_w + absf(dy) / half_h
			if d <= 1.0:
				img.set_pixel(px, py, fill)

	return img
