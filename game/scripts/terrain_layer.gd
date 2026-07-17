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
#
# Tile dimensions come from Iso (scripts/iso.gd) — the single owner. This layer is the
# z = 0 ground plane and is legitimately 2D; the z term lives in Iso.world_to_screen for
# the layer-3/4 draw list, which is where elevation will actually land.
const Iso = preload("res://scripts/iso.gd")
const GRID_SIZE := 5

func _ready() -> void:
	var tile_set := TileSet.new()
	tile_set.tile_shape = TileSet.TILE_SHAPE_ISOMETRIC
	tile_set.tile_size = Vector2i(Iso.TILE_W, Iso.TILE_H)

	var tex := ImageTexture.create_from_image(_make_diamond_tile())

	var source := TileSetAtlasSource.new()
	source.texture = tex
	source.texture_region_size = Vector2i(Iso.TILE_W, Iso.TILE_H)
	source.create_tile(Vector2i(0, 0))
	var source_id := tile_set.add_source(source)

	self.tile_set = tile_set

	# Cells are addressed in §2 world coords and converted at the tilemap boundary —
	# Godot's cell basis is not §2's (B23).
	for x in range(GRID_SIZE):
		for y in range(GRID_SIZE):
			set_cell(Iso.cell_for_world(x, y), source_id, Vector2i(0, 0))

	_check_transform_agreement()

# Terrain renders through Godot's tilemap, but machines and items (layers 3-4) will render
# through Iso.world_to_screen. If those two disagree about where world tile (x, y) lands on
# screen, machines float off the ground they stand on — a bug that would surface as "the
# art is subtly wrong" long after anyone remembers why.
#
# This check exists because the assumption it tests was WRONG when first written: the
# review asserted Godot's iso layout matched §2 and we got the transform for free. It does
# not (B23). Keep the check — it is the only thing pinning the two bases together, and it
# costs one boot-time loop over a 5x5 grid.
func _check_transform_agreement() -> void:
	var worst: float = 0.0
	for x in range(-GRID_SIZE, GRID_SIZE + 1):
		for y in range(-GRID_SIZE, GRID_SIZE + 1):
			var via_tilemap: Vector2 = map_to_local(Iso.cell_for_world(x, y))
			var via_iso: Vector2 = Iso.world_to_screen(float(x), float(y), 0.0)
			var err: float = (via_tilemap - via_iso).length()
			if err > worst:
				worst = err
	var result: String = "PASS" if worst < 0.001 else "FAIL"
	print("ISO_CHECK max_transform_err=%.6f px result=%s" % [worst, result])

# Rasterize the isometric cell as a diamond with transparent corners. A fully-opaque
# rectangle is wrong: adjacent iso cells overlap by half a tile, so opaque neighbours
# paint over each other and the grid renders as one blob instead of N diamonds.
# Inside test is |dx/(W/2)| + |dy/(H/2)| <= 1 about the tile centre.
func _make_diamond_tile() -> Image:
	var img := Image.create(Iso.TILE_W, Iso.TILE_H, false, Image.FORMAT_RGBA8)
	img.fill(Color(0.0, 0.0, 0.0, 0.0))

	var fill := Color(0.25, 0.55, 0.35, 1.0)
	var half_w: float = float(Iso.TILE_W) * 0.5
	var half_h: float = float(Iso.TILE_H) * 0.5

	for py in range(Iso.TILE_H):
		for px in range(Iso.TILE_W):
			# Pixel centres, so the diamond is symmetric about the tile centre.
			var dx: float = (float(px) + 0.5) - half_w
			var dy: float = (float(py) + 0.5) - half_h
			var d: float = absf(dx) / half_w + absf(dy) / half_h
			if d <= 1.0:
				img.set_pixel(px, py, fill)

	return img
