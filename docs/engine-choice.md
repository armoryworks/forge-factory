# Engine/Toolchain Choice — Isometric Factory Game

Phase 0 discovery. Timeboxed hardware/tooling check (not theoretical) on this box.

## Local environment (verified)

- OS: Ubuntu 26.04 LTS, kernel 7.0.0-27, x86_64
- GPU: AMD Radeon RX 9070 XT (RDNA4/Navi 48), 16GB VRAM, Mesa 26.0.3
  - OpenGL: direct rendering yes, core profile up to 4.6
  - Vulkan: instance v1.4.341, driver present and enumerable
  - This is a high-end GPU — no rendering-backend constraint for any candidate.
- Compilers: gcc/g++ 15.2.0 present. **No rustc/cargo installed** (would need `rustup` install for Bevy).
- Runtimes: Node v22.22.1 + npm 9.2.0 present. Python 3.14.4 present.
- Package managers: apt, snap, flatpak all available.
- Godot: not installed, but `snap find godot` shows `godot-4` (4.7, ken-vandine, official-ish) readily installable. No flatpak match.
- LÖVE: not installed; available via apt/flatpak in Ubuntu 26.04 (not separately verified, low risk — small package).
- No existing engine/project scaffolding in `/home/daniel/dev/forge/factory` yet (docs dir only).

## Candidates evaluated

### Godot 4.x (GDScript/C#) — engine, GDExtension for native perf
- Install: `sudo snap install godot-4` → 4.7 stable, zero extra toolchain needed.
- Isometric: native `TileMap`/`TileMapLayer` isometric mode, well-documented, first-class editor support for iso tile authoring.
- HTTP backend integration: built-in `HTTPRequest` / `HTTPClient` nodes — talking to a local HTTP API is a few lines of GDScript, no extra libs.
- Rendering: Vulkan (Forward+) and GL compatibility renderers both map cleanly to this GPU; RX 9070 XT is far above what Godot needs.
- Perf ceiling for "hardcore" scale (thousands of belts/entities): GDScript alone can bottleneck at very high entity counts, but C# or GDExtension (C++) escape hatches exist for hot paths (belt/inserter simulation).
- Ecosystem: large, mature, strong docs, active community; used for Factorio-like/iso factory games before.
- Risk: moderate learning curve for scene/node model if new to Godot; otherwise low risk.

### Bevy (Rust, ECS)
- Install: needs `rustup` + cargo (not present) — extra setup step, longer build times, but straightforward on this box.
- Architecturally excellent fit for factory-sim entity/component workloads (ECS is the natural model for belts/machines/items).
- Isometric tilemaps: via third-party `bevy_ecs_tilemap` — functional but less turnkey/polished than Godot's built-in tilemap, and Bevy's own APIs still churn across versions (higher integration risk).
- HTTP backend integration: needs an async runtime + `reqwest`/`ureq` wired into Bevy's scheduler manually — doable but more plumbing than Godot's HTTPRequest node.
- No visual editor — everything is code; slower iteration for level/tile authoring.
- Best raw performance ceiling of all candidates, but higher time-to-first-playable and more DIY risk given the timebox/solo-build context.

### Phaser 3 / PixiJS (JS/TS)
- Install: zero extra — Node 22 + npm already present.
- HTTP backend integration: trivial (`fetch`), and pairs naturally with a Node-based local API.
- Isometric: Phaser has isometric plugins (`phaser3-plugin-isometric` et al.) but they're community-maintained and less actively kept up than Godot's core support.
- Runs in-browser or via Electron; good for quick iteration and cross-platform reach.
- Perf ceiling for a "hardcore" large-scale factory sim (thousands of moving entities/frame) is the weakest of the three in a canvas/WebGL 2D context — would likely need to push simulation logic entirely server-side (which fits the "local HTTP API backend" framing) and keep the client as a thin renderer.
- Ecosystem large for general 2D web games, thinner specifically for isometric factory-style games.

### LÖVE (Lua)
- Considered but not deep-dived: lighter weight than the above, good for small/simple 2D games, but weaker isometric tilemap tooling and thinner ecosystem for a large, UI-heavy factory sim. Dropped early — doesn't outperform Godot on any axis for this use case.

## Recommendation

**Godot 4.7 (via snap, GDScript to start, escape to C#/GDExtension for hot paths later).**

Rationale: zero-friction install on this box (no missing toolchain like Bevy's Rust), built-in isometric TileMap support (no third-party plugin risk like Phaser/Bevy), built-in HTTPRequest node makes talking to the local HTTP API trivial, a real editor for fast level/tile iteration, and a rendering backend (Vulkan/GL) that comfortably outclasses what this GPU needs to provide headroom for scale. Bevy is the stronger long-term perf/ECS story for a truly massive factory sim, but its setup cost (Rust toolchain install, manual async HTTP wiring, less mature iso-tilemap plugin) isn't justified yet at Phase 0 — revisit if Godot's GDScript hits a real perf wall.

Next step (not done yet, per scope): scaffold a Godot 4.7 project once approved.
