# Isometric Design Constraints

Status: Phase 0 discovery draft. Engine-agnostic — no assumption beyond "we can draw textured quads/sprites in a sorted order and read mouse position."

Audience: the hardcore factory player. They will run 5000+ machines, zoom out to plan, zoom in to debug a single belt, and expect to diagnose a throughput problem by *looking* at the factory, not by opening a stats panel.

---

## 1. Grid and projection

### Decision: dimetric 2:1, not true isometric.

True isometric (30° axes, tile ratio ~1:1.732) is geometrically honest and wrong for us. Dimetric 2:1 (26.565°, `atan(0.5)`) is the choice:

- Tile diamond is exactly `2w × w` pixels. Every world→screen transform is integer math: shifts and adds, no trig, no rounding drift. This matters at 5000 machines/frame.
- Diagonal lines step exactly 2px across / 1px down — pixel-perfect diagonals with no anti-aliasing shimmer when the camera pans. True iso produces irrational slopes that alias on every belt edge, and belts are ~40% of what's on screen.
- Every existing tool, art pipeline, and player expectation for this genre is 2:1.

The cost: the projection is visually "squashed" versus real 3D. Nobody has ever noticed or cared.

### Tile size: 64×32 base.

`TILE_W = 64`, `TILE_H = 32`, at zoom 1.0.

- 64 is divisible by 2/4/8/16 — sub-tile alignment (belt lanes at 16px, item slots at 8px) stays integral at zoom 1.0, 0.5, and 0.25.
- A 3×3 assembler occupies 192px across — enough for a legible recipe icon plus a progress ring at 1.0 zoom.
- 64×32 keeps a 1080p viewport at ~30×34 tiles visible at 1.0 zoom, ~120×136 at 0.25. That's the right planning-vs-debugging span.

### Height / elevation

Reserve `TILE_Z = 16px` per elevation unit (screen-space Y offset per level). We are not committing to multi-level factories in v1, but every transform below takes a `z` and every sprite anchors as if `z` exists. Retrofitting elevation into a 2D-only transform is a rewrite; carrying an unused `z` costs one add.

---

## 2. Coordinate transforms

World space is a right-handed tile grid `(x, y, z)`, x increasing toward screen-right-down, y toward screen-left-down. Fractional world coords are legal and required (items sit *between* tiles on belts).

### World → screen

```
sx = (x - y) * (TILE_W / 2)
sy = (x + y) * (TILE_H / 2) - z * TILE_Z
```

At `TILE_W=64, TILE_H=32`: `sx = (x-y)*32`, `sy = (x+y)*16 - z*16`.

### Screen → world (z = 0 ground plane)

```
x = (sx / (TILE_W/2) + sy / (TILE_H/2)) / 2
y = (sy / (TILE_H/2) - sx / (TILE_W/2)) / 2
```

Then `floor()` for the tile index. This is the *ground-plane* pick and is what mouse hover should use 95% of the time — hovering a tall machine should highlight the tile the cursor is geometrically over, not the tile whose sprite happens to be drawn under the cursor. Sprite-accurate picking (raycast down through elevations) is only needed if we ship multi-level.

**Do not implement picking via color-buffer readback.** It couples picking to render order, breaks under any post-process, and stalls the GPU. The inverse transform is 6 flops.

### Screen origin

Camera holds a world-space focus point; transforms produce screen coords relative to that, then a single viewport translate. Keep the camera in *world* space, not screen space — otherwise zoom-to-cursor becomes a fight.

---

## 3. Depth sorting

This is the part that kills isometric projects. Get it right in Phase 0 or pay forever.

### The rule: sort by layer first, then by depth key within layer.

Layers are a hard, explicit enum drawn back-to-front. Nothing is sorted *across* layers:

| # | Layer | Contents | Sort within layer |
|---|-------|----------|-------------------|
| 0 | Terrain | ground tiles, ore patches | grid order, no sort |
| 1 | Ground decals | belt-under-machine shadow, marks, selection tint | grid order |
| 2 | Flat entities | belts, pipes-underground, rails, foundations | grid order |
| 3 | Items on belts | the moving stuff | depth key |
| 4 | Machines | assemblers, furnaces, inserters, chests | depth key |
| 5 | Overlays | alerts, throughput ribbons, ghosts, range circles | insertion order |
| 6 | UI | screen-space, no transform | — |

Layers 0–2 are flat and coplanar: they can be drawn in pure grid iteration order (`for y: for x:`) with zero sorting cost, which is the bulk of the tiles. Only layers 3 and 4 pay for a sort, and they're a small fraction of entities.

### Depth key

For a sorted entity with footprint origin `(x, y)`, size `(w, h)`, elevation `z`:

```
depth = (x + w) + (y + h) + z * BIG
```

i.e. sort by the **far corner** of the footprint, not the origin. Sorting multi-tile machines by origin is the classic bug: a 3×3 assembler at (0,0) sorts as if it were a 1×1 at (0,0) and gets drawn behind a belt at (2,2) that is visually in front of it.

Ties break on: layer index, then `y`, then `x`, then stable entity id. Stable tiebreak is non-negotiable — an unstable sort makes two overlapping machines flicker z-order between frames, which reads as a rendering bug and destroys trust.

### Insert an entity into the sort, don't re-sort the world

Entities are static (machines don't move) except items on belts. Keep layer 4 in a **sorted structure maintained on placement/removal** — O(log n) insert, zero per-frame cost. Only layer 3 (items) re-sorts per frame, and items are naturally almost-sorted (they move along a belt monotonically), so insertion sort on a nearly-sorted array is effectively O(n).

### Tall machines and the overlap problem

A machine taller than its footprint will occlude machines behind it. This is correct and desired. It becomes a problem when a player can't see a machine that's alarming. Mitigation, in priority order:

1. **Keep sprites short.** Design constraint: no entity sprite exceeds `footprint_height + 1.5 tiles` of visual height. This is a rule for the art bible, not a renderer feature.
2. **Alert markers live on layer 5** and are never occluded, ever. If a machine is starved, its marker floats above everything.
3. **X-ray on hover** — optional later; hold a modifier and machines drop to 40% alpha. Do not build in Phase 0.

We explicitly reject per-pixel depth (drawing to a depth buffer from sprite Y) — it fights alpha blending on every belt edge and buys us nothing at 2:1 with a short-sprite rule.

---

## 4. Camera

### Model: orthographic, axis-aligned, no rotation.

**No camera rotation in v1.** Rotation quadruples the art requirement (every directional machine needs 4 renders per rotation state), doubles the belt-direction sprite set, and makes blueprint sharing ambiguous. Hardcore factory players have overwhelmingly demonstrated they'd rather have one canonical orientation they can read instantly than four they have to re-parse. Revisit only if a real player complaint emerges.

### Zoom

Discrete-friendly continuous zoom, clamped `[0.125, 2.0]`, snapping to power-of-two stops on scroll-wheel notches (`0.125, 0.25, 0.5, 1.0, 2.0`) with smooth interpolation *between* stops during the animation. Power-of-two stops keep 64px tiles landing on integer pixel boundaries — critical for placeholder-art crispness and, later, pixel art.

Zoom-to-cursor, not zoom-to-center. Anchor the world point under the mouse and keep it fixed. This is what every factory player has muscle memory for.

Zoom thresholds drive LOD (see §6):
- `>= 1.0` — full detail, per-item sprites, recipe icons
- `0.5` — items become dots, recipe icons become color chips
- `0.25` — machines become colored blocks, belts become flow lines
- `0.125` — factory becomes a heatmap; individual entities are not drawn

### Pan

- Middle-drag (world-space grab: the world point under the cursor stays under the cursor).
- Edge-scroll: **off by default**, opt-in. It fights UI panel edges and it's a common rage-quit trigger.
- WASD, speed scaled by `1/zoom` so screen-space pan velocity is constant across zoom levels.
- Momentum/inertia: no. Precision beats feel for this audience.

Camera bounds: clamp to the generated-world AABB plus one viewport of slack. Do not hard-lock — players building near the frontier need to see the void they're about to expand into.

---

## 5. Placeholder-art strategy

We build the whole game with zero artists and swap art in later without touching game code. This is a hard architectural requirement, not a temporary hack.

### Rule: every sprite is fetched through an asset key, never a path.

`sprite("assembler_2.working.n")` → resolves via a manifest. Placeholder generator and real art register into the same manifest. Changing the art pipeline is a manifest swap.

### Placeholders are procedurally generated at boot, not authored files.

A ~200-line generator that renders, into a texture atlas:

- **Machines** — a flat-shaded extruded diamond prism at the machine's footprint and a declared height. Top face is a hue derived from a hash of the entity id (stable across runs); left face 80% brightness, right face 60%. That fake-lighting triple instantly reads as 3D and is the entire visual grammar we need.
- **Category tinting** — hue is *not* purely hashed; it's `category_base_hue + hash_jitter(±15°)`. Smelting is orange-family, assembly blue-family, logistics yellow-family, fluids green-family. Players learn the factory's zones by color before we have a single piece of art.
- **Labels** — machine's short name rendered on the top face at zoom >= 1.0. A "PLACEHOLDER" build renders text on everything; this makes screenshots ugly and unambiguous, which is exactly right — nobody mistakes it for finished work.
- **Belts** — a flat diamond with an animated chevron pattern in the direction of travel. Direction and speed must be legible from the placeholder; belt direction ambiguity is the #1 factory-game readability failure.
- **Items** — 16px circles, hue by item type, with a 2-3 char label at high zoom.

### Why this beats free/temp asset packs

Placeholder art that is *deliberately synthetic* keeps the team honest about what's shipped. Borrowed art creates a false sense of doneness, licensing risk, and a silent dependency on someone else's grid size. The generator also gives us: every new machine is free (declare footprint + category + height, it draws itself), and a stable visual identity that real art must *match* rather than redefine.

### Contract for the eventual artist

Codify now, in the manifest schema:
- Anchor point: bottom-center of the footprint diamond, at world `(x+w/2, y+h/2, 0)`.
- Canvas: `footprint_w*64 × (footprint_h*32 + height*16)` px, at zoom 1.0.
- 4 directional variants for directional entities, `n/e/s/w`, where `n` = "output faces screen-upper-right."
- Animation frames declared in the manifest, not implied by filename.

If the artist has to ask where the anchor is, we've already failed.

---

## 6. Readability rules for dense factories

The design goal: **a player must diagnose a throughput problem from a screenshot.** Not a tooltip, not a panel — the pixels. Everything below serves that.

### 6.1 Throughput is a first-class visual, not a stat

Belts encode their own saturation. At zoom >= 0.5, belt chevrons animate at the *actual* item rate, not a fixed rate. A backed-up belt has visibly still items. A starved belt has visible gaps. This is the single highest-leverage readability feature in the genre and it must be in the renderer from day one — it's free (the items are already there) and retrofitting it means rebuilding the belt renderer.

### 6.2 The saturation overlay (layer 5)

A toggle that recolors every belt by throughput as a fraction of its rated capacity:
- **Red** — saturated (backed up, >95%). The problem is *downstream*.
- **Green** — flowing at capacity (85–95%). This is what "good" looks like.
- **Yellow → grey** — starved (<85%, shading to grey at 0). The problem is *upstream*.

Note the semantics: red is not "bad," red is "the constraint is not here." A hardcore player reading a red-to-green transition instantly knows where the bottleneck is. This deserves the same care as the main render path.

Colorblind: never encode throughput by hue alone. Saturation state also drives chevron density and a dash pattern. Every color channel in this doc must have a redundant non-color encoding. Non-negotiable — a meaningful fraction of this audience is red-green colorblind and the overlay is red-green.

### 6.3 Alert markers never occlude, never scale

Starved/full/no-power markers are drawn at layer 5 in **screen space** at a fixed size, clamped to the viewport edge when off-screen (with a direction arrow). At 0.125 zoom the factory is unreadable but the alerts are not — that's the point. Cluster markers within ~24px into a count badge, or a large broken factory becomes a wall of icons.

### 6.4 LOD is a readability feature, not a perf feature

The zoom LOD tiers in §4 exist because *detail at the wrong zoom is noise*. At 0.25 zoom the player is asking "which district is broken," and per-item sprites actively obstruct that answer. So at 0.25:
- Machines → flat category-colored blocks (the category hue from §5 pays off here)
- Belts → 1px flow lines colored by saturation
- Items → not drawn at all
- Alerts → still full size

Perf is a happy side effect. Design the tiers by *what question the player is asking at that zoom*, and the perf budget takes care of itself.

### 6.5 Grid legibility at density

- Ground tile contrast must stay under ~15% luminance variation. Ore patches and terrain are *background*; the moment terrain competes with entities for attention, a dense factory becomes unparseable. This kills most pretty terrain ideas, correctly.
- A 1px grid line at every tile is too noisy at scale. Draw a subtle line every tile at zoom >= 1.0, and a stronger line every 8 tiles at all zooms. The 8-tile major grid is what players actually use for alignment and blueprint sizing.
- Machine outlines: a 1px dark outline on every sorted-layer entity. This is the cheapest possible fix for "where does one machine end and the next begin" in a wall of same-category machines, and at 2:1 with flat shading it's what makes the whole thing read.

### 6.6 Motion budget

Everything that moves draws the eye, so movement must mean something. Items on belts move (meaningful). Machine working animations move (meaningful — a stopped machine is visibly stopped). Decorative animation — smoke, flickering, idle bobbing — is **banned** unless it encodes state. In a 5000-machine factory, ambient motion is a denial-of-service on the player's attention.

---

## 7. Open questions for the engine decision

These are the questions the isometric design puts to whoever picks the engine. Answering "no" to any is disqualifying, not merely inconvenient.

1. Can we control draw order explicitly per-sprite, or does the engine impose its own batching order? (Explicit order is required — §3 is the whole game.)
2. Can we draw ~20k sprites/frame with per-sprite tint, in one or few batches? (Category tint + saturation overlay both need per-sprite color.)
3. Can we generate textures procedurally at boot into an atlas? (§5 depends on it.)
4. Can we render screen-space overlays after world-space content with a different transform? (§6.3.)
5. Is integer/pixel-snapped rendering available at arbitrary zoom? (Diagonal shimmer, §1.)

---

## 8. What Phase 0 commits to

- Dimetric 2:1, 64×32 tiles, `TILE_Z=16`, `z` carried through all transforms even though v1 is single-level.
- Layered sorting with far-corner depth keys and stable tiebreaks; layers 0–2 unsorted grid order.
- No camera rotation; power-of-two zoom `[0.125, 2.0]`; zoom-to-cursor; no edge-scroll by default.
- Procedural placeholder art via an asset-key manifest; no third-party temp assets.
- Belt animation rate = real item rate, from day one.
- Redundant non-color encoding on every color channel.

Deferred, deliberately: multi-level elevation, x-ray on hover, camera rotation, per-pixel depth.
