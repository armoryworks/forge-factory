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
4. **Launch with `tools/godot4_mono/godot4_mono` for anything that touches a
   C# script, not `tools/godot4`.** The standard build (B14) has no Mono
   module at all. **Correction, measured under B51:** this is scene-scoped,
   not project-scoped — the standard build still opens `game/` and runs
   `main.tscn` fine (nothing in it references a `.cs` script), but any scene
   that *does* attach a C# script (`scenes/sim_hub_client_check.tscn`, the
   throwaway hosting check) fails to load on it. Switch binaries per-scene,
   not globally.
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

## B51: SignalR client package

`game/Factory.csproj` adds `Microsoft.AspNetCore.SignalR.Client` **8.0.29**
(pinned; open-source, MIT, official ASP.NET Core package; latest 8.0.x at
time of pinning, matching the `net8.0` `TargetFramework`) to consume the
adapter's `/hubs/sim` hub (`game/SimHubClient.cs`). Real gotcha hit and
fixed: SignalR's `connection.On<T>(...)` callbacks run on the connection's
own background thread, never the Godot main thread — calling `EmitSignal`
directly from one throws `"The caller thread can't call the function
emit_signalp()"`. Fix is `Callable.From(() => ...).CallDeferred()` around
every signal emission / Node-visible state write in the callback.

## Not decided here

Whether the sim core's tick loop replaces `SimClock` outright or `SimClock`
becomes a thin GDScript wrapper calling into the C# core — that's a design
call for whoever does the conversion, not prep-doc scope.
