# Converting `game/` to the .NET (mono) Godot build

**DONE 2026-07-17 (B47).** Steps 1-4 below are applied: `project.godot` has
`"C#"` + `[dotnet]`, `game/Factory.csproj` exists (`net8.0`, no
`Forge.Sim` reference yet -- see B47), the one-time build-glue step ran
clean under `tools/godot4_mono/godot4_mono`, and every existing headless/
windowed check still passes under the mono binary. Steps 5-6 (mixed
GDScript/C#, standard build for non-C# checks) are guidance, not actions --
nothing to "complete" there.

Still open, tracked as B47: wiring the actual `ProjectReference` to
`sim/Forge.Sim/Forge.Sim.csproj` hit a real TFM mismatch (`Forge.Sim` targets
`net10.0`, the game assembly must be `net8.0`) that whoever does the sim-core
integration needs to resolve first.

## Steps

1. **`game/project.godot`** — add `"C#"` to `config/features` (currently
   `PackedStringArray("4.7", "Forward Plus")`) and add a `[dotnet]` section:
   ```
   [dotnet]
   project/assembly_name="Factory"
   ```
2. **Add `game/Factory.csproj`** (or whatever `assembly_name` above says),
   modeled on `tools/dotnet_hosting_check/DotnetHostingCheck.csproj`:
   ```xml
   <Project Sdk="Godot.NET.Sdk/4.7.0">
     <PropertyGroup>
       <TargetFramework>net8.0</TargetFramework>
       <EnableDynamicLoading>true</EnableDynamicLoading>
     </PropertyGroup>
     <ItemGroup>
       <ProjectReference Include="../sim/Forge.Sim/Forge.Sim.csproj" />
     </ItemGroup>
   </Project>
   ```
   (Path/name to confirm against whatever the mathematician actually ships.)
3. **One-time build-glue step**, exactly as B23 verified:
   ```
   tools/godot4_mono/godot4_mono --headless --editor --build-solutions --quit --path game
   ```
   This generates the `.sln`/builds the C# assembly. Needed once after adding
   the `.csproj`, and again any time C# source changes structurally (new
   script files) — a plain `--headless` run rebuilds on script edits, per
   B23's testing.
4. **Launch with `tools/godot4_mono/godot4_mono` from then on, not
   `tools/godot4`.** The standard build (B14) has no Mono module at all —
   it can't load a project that references any `.cs` script, headless or
   windowed. Every doc/script that currently invokes `tools/godot4` against
   `game/` (the SIM_CHECK headless run, the engine/iso check scenes, CI if
   any lands) needs to switch to `godot4_mono` once the `.csproj` exists,
   or the project fails to open at all — not gracefully degrades.
5. **GDScript and C# coexist fine in one project** — no need to port
   `sim_clock.gd`, `terrain_layer.gd`, etc. to C#. The sim core is called
   from GDScript via Godot's normal cross-language script API (a C# class
   is just another `Node`-derived script from GDScript's point of view), or
   exposed as an autoload if the mathematician's core needs to run
   independently of any scene node.
6. **`tools/godot4` (standard build) stays for anything that doesn't touch
   C#** — the throwaway checks that don't need it (bench_fixed_point,
   iso_depth_check, engine_checks) have no reason to pay the mono build's
   extra size/startup cost.

## Not decided here

Whether the sim core's tick loop replaces `SimClock` outright or `SimClock`
becomes a thin GDScript wrapper calling into the C# core — that's a design
call for whoever does the conversion, not prep-doc scope.
