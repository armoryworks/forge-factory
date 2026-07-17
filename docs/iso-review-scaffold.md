# Iso Scaffold — Render-Layer Review

> **Status 2026-07-17 — partially implemented.** Landed: **A1** boundary comment + `iso_map.gd` → `terrain_layer.gd`; **A2** `Camera2D` + `TickLabel` moved under a `CanvasLayer` (the *node-tree* half — the camera **controller**, i.e. zoom clamp/stops, zoom-to-cursor, pan, is still unbuilt and the A2 checklist below stands); **B1** diamond alpha; **B2** Nearest filter; **B3** `z` carried through the coordinate path, as new `scripts/iso.gd` (the one owner of `TILE_W/H/Z` + `world_to_screen`/`screen_to_world`/`depth_key`, all taking `z` though v1 passes 0).
>
> **A2 completed 2026-07-17** — `scripts/camera_controller.gd`: power-of-two stops `[0.125 … 2.0]`, zoom-to-cursor (anchored every frame of the glide, not just at the end), middle-drag world-space grab, WASD scaled by `1/zoom`, no edge-scroll, no inertia, no rotation. B22's shimmer check rode along and is **resolved** — see inventory B22; it became a falsifiable `FILTER_CHECK` rather than an eyeball pass.
>
> **B3 also invalidated this doc's own lead "green" bullet — see the retraction under Green below, and inventory B23.** Verified: `ISO_CHECK max_transform_err=0.000000 px PASS` + headless `SIM_CHECK PASS` + clean windowed Vulkan boot. Shimmer motion check deferred → inventory **B22**. Unimplemented: the A1 scout question (§7 Q1), the A2 controller, and all of C.

Reviewed: `game/scripts/iso_map.gd`, `game/scripts/main.gd`, `game/scenes/main.tscn`, `game/project.godot` @ master.
Checked against [isometric-design.md](isometric-design.md). Scaffold is ~30 lines of iso code — most findings are "not built yet," which is fine. The ones that matter are the two architectural collisions (A1, A2), which get more expensive every week they stand.

Verdict: **projection math is correct and free — Godot's isometric layout already matches our spec.** One placeholder bug makes the grid invisible, and one structural choice (TileMapLayer) will collide with §3 draw order if it's carried past terrain.

---

## Green — matches the design, no action

- **Projection.** `TileSet.TILE_SHAPE_ISOMETRIC` + `tile_size = (64, 32)` is exactly our dimetric 2:1 at the spec'd tile size. ~~Godot's default `TILE_LAYOUT_DIAMOND_DOWN` `map_to_local` computes `((x-y)*w/2, (x+y)*h/2)` — identical to isometric-design.md §2. We get the transform for free and don't need to hand-roll it for the ground plane.~~

  > **RETRACTED 2026-07-17 — this was wrong, and I asserted it from memory of Godot's docs instead of measuring.** Implementing R5 added a boot-time check comparing `map_to_local` against §2's formula; it failed by up to 389px. Godot's isometric tilemap uses **staggered/offset coordinates** (`cy` is a screen row, odd rows shift half a tile right), not §2's diamond axes. The tell: stepping `+cy` zigzags in x (32 → 64 → 32), which no linear transform does — so it isn't even a change of basis. The tile *shape* is 2:1 as claimed; the *addressing* is not ours. See inventory **B23** and `scripts/iso.gd:cell_for_world`. Cost of the wrong claim: had the render layer been built on it, machines (drawn via `Iso.world_to_screen`) would have rendered offset from the terrain they stand on, and the misplacement grows with distance from origin — a bug that reads as "the art is subtly wrong" and gets debugged for a day.
- **Procedural texture at boot.** `Image.create` → `ImageTexture.create_from_image` → `TileSetAtlasSource` in `_ready()` works. That empirically clears §7 Q3 (procedural placeholder generation) — pass this to the scout so they don't re-test it.
- **Sim/render separation.** `main.gd:_process` only reads `SimClock.tick_count`. Correct and worth preserving.

---

## A — Architectural, decide before the render layer is built

### A1. `TileMapLayer` must not carry machines, belts-with-items, or items — only terrain

**Design ref: §3.** Our sort is a single ordered entity list across layers 3–4, keyed on the footprint's **far corner** (`x+w + y+h + z*BIG`) with a stable id tiebreak. A tilemap is a grid of cells; it has no concept of a 3×3 entity with a far corner, and its Y-sort is per-cell. Sorting a 3×3 assembler as if it were a 1×1 at its origin *is* the classic bug §3 calls out — it will draw behind a belt that is visually in front of it.

This is not an argument against `TileMapLayer` — it's the right tool for layers 0–2, which §3 explicitly says are coplanar and draw in raw grid order at zero sort cost. It's an argument against letting it creep upward.

- [ ] Write down the boundary now: `TileMapLayer` owns layers 0–2 (terrain, decals, flat belts). Layers 3–4 (items, machines) are a separately-managed sorted draw list. Put this in a comment at the top of `iso_map.gd` so the next person doesn't "just add a machine layer."
- [ ] Rename `iso_map.gd` → `terrain_layer.gd` (or move to `scripts/render/terrain_layer.gd`). The name `iso_map` invites exactly the creep above.
- [ ] Flag to the scout for §7 Q1: confirm whether Godot gives explicit per-item draw order within a `CanvasItem` parent (custom sort / `z_index` / manual `draw_*` in one node) at ~20k sprites. Our §3 depends on it. If the answer is "only Y-sort by node position," we need the manual-draw path and should learn that this week, not in Phase 3.

### A2. No camera exists — and the current node tree will break when one is added

**Design ref: §4, §6.3.** `main.tscn` has no `Camera2D`; the scene renders at default viewport transform. Separately, `TickLabel` is a child of `Main` (a `Node2D`), i.e. it lives in **world space**. The moment a camera pans, the label pans with the factory. §6.3 requires overlays and alerts to be screen-space, fixed-size, and never occluded.

- [ ] Add `Camera2D` under `Main`. Camera holds a **world-space** focus point (§2) — not a screen-space offset, or zoom-to-cursor becomes a fight.
- [ ] Move `TickLabel` (and every future overlay/alert) under a `CanvasLayer`. Cheap now, a refactor of every UI node later.
- [ ] Zoom: clamp `[0.125, 2.0]`, snap to power-of-two stops (§4). Power-of-two keeps 64px tiles on integer pixel boundaries.
- [ ] Zoom-to-cursor, not zoom-to-center. Anchor the world point under the mouse.
- [ ] Pan: middle-drag world-space grab; WASD scaled by `1/zoom`. Edge-scroll **off** by default. No inertia.
- [ ] Do **not** add camera rotation (§4). If someone asks, the answer is in §4.

---

## B — Concrete bugs in the scaffold

### B1. The placeholder tile is an opaque rectangle, so the iso grid is invisible

`iso_map.gd:14-15` fills the full 64×32 `Image` with opaque green. Godot draws that region centered on each diamond cell, and adjacent iso cells overlap by half a tile — so opaque neighbors paint over each other and the 5×5 grid renders as one green blob, not 25 diamonds. The projection is right; you just can't see it.

- [ ] Rasterize a **diamond** into the image: alpha 0 outside `|dx/32| + |dy/16| > 1`. This is also the base primitive for the §5 prism generator, so it isn't throwaway work.
- [ ] While there: the §5 generator wants three faces (top face at hue, left 80% brightness, right 60%). Build the diamond as `gen_placeholder.gd` returning a texture given (footprint, category, height) rather than inlining it in the terrain layer.
- [ ] Bump `GRID_SIZE` past 5 (32+) once tiles are diamonds — a 5×5 grid can't show a sort bug, and sort bugs are what we need to see early.

### B2. Default texture filter is linear → diagonal shimmer

`project.godot` sets no `default_texture_filter`, so it defaults to linear. §1 chose 2:1 specifically for pixel-perfect 2-across/1-down diagonals; linear filtering on a panning camera gives back the shimmer we paid for that choice to avoid.

- [ ] Set `rendering/textures/canvas_textures/default_texture_filter=0` (Nearest) in `project.godot`.
- [ ] Verify at 0.5 and 0.25 zoom while panning — shimmer only shows in motion, not in screenshots. Windowed on the 9070 XT is the right place to check.

### B3. No `z` in the coordinate path

**Design ref: §1, §2.** §1 commits to carrying `z` through all transforms (`TILE_Z=16`) even though v1 is single-level, because retrofitting elevation into a 2D-only transform is a rewrite. The scaffold's cells are `Vector2i` and Godot's `map_to_local` is 2D.

- [ ] When the layer-3/4 draw list is built, its world coord is `(x, y, z)` and its screen transform applies `- z * TILE_Z`, even if every caller passes `z=0` in v1. Godot's tilemap staying 2D is fine — terrain is layer 0 and will never elevate.
- [ ] Define `TILE_W/TILE_H/TILE_Z` in one shared constants script. They're currently local to `iso_map.gd`, and the moment a second file needs them they'll be copy-pasted and drift.

---

## C — Watch items, not yet actionable

- **Ground contrast (§6.5):** the placeholder green `(0.25, 0.55, 0.35)` is a fine flat value, but §6.5 caps terrain at ~15% luminance variation. When terrain variants land, that cap is the rule — terrain is background, and the moment it competes with entities a dense factory stops being parseable.
- **§7 Q2/Q4/Q5** (20k tinted sprites, screen-space overlay pass, pixel-snap at arbitrary zoom) are untouched by the scaffold — nothing here collides with them either way. Scout's call.

---

## Handoff summary

Blocking the render layer: **A1** (draw-order ownership boundary) and **A2** (camera + CanvasLayer). Both are cheap now and structural later.
Do-now bugs: **B1** (diamond alpha — the grid is currently invisible), **B2** (nearest filter).
For the scout: §7 Q3 is empirically **cleared** by this scaffold. §7 Q1 is the one that matters most and is untested — A1 is where it lands.
