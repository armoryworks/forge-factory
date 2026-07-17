extends TileMapLayer

# Phase 2 stub: procedurally-generated placeholder isometric grid.
# No art assets — a solid-color tile is generated at runtime.
const GRID_SIZE := 5
const TILE_W := 64
const TILE_H := 32

func _ready() -> void:
	var tile_set := TileSet.new()
	tile_set.tile_shape = TileSet.TILE_SHAPE_ISOMETRIC
	tile_set.tile_size = Vector2i(TILE_W, TILE_H)

	var img := Image.create(TILE_W, TILE_H, false, Image.FORMAT_RGBA8)
	img.fill(Color(0.25, 0.55, 0.35, 1.0))
	var tex := ImageTexture.create_from_image(img)

	var source := TileSetAtlasSource.new()
	source.texture = tex
	source.texture_region_size = Vector2i(TILE_W, TILE_H)
	source.create_tile(Vector2i(0, 0))
	var source_id := tile_set.add_source(source)

	self.tile_set = tile_set

	for x in range(GRID_SIZE):
		for y in range(GRID_SIZE):
			set_cell(Vector2i(x, y), source_id, Vector2i(0, 0))
