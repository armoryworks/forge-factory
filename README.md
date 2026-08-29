# forge-factory

A vertical-slice factory simulation built on the Forge backend: a deterministic
C# sim core, an ASP.NET Core sim-adapter service that speaks to `forge-api`, and
a Godot 4.7 isometric client that renders the sim and places belts.

The slice's target was one ore source → one belt → one machine → one output,
ticking deterministically, drawn isometrically, running against real Forge data.
That slice runs: the sim reproduces a language-independent golden vector, the
adapter hosts the sim and pushes deltas over SignalR, the client renders belt
items and surfaces placement rejections, and item and recipe definitions are
imported from real `Part` / `BomRevision` records in Forge.

## Status — read this first

This repository is a **snapshot of a time-boxed exploratory build**. Every commit
is from 2026-07-17. It is not a released product, has no packaging or deployment
story, and is not maintained here.

The architecture documented here was later **superseded**. The tick-driven
design — an independently ticking sim that checkpoints into Forge — gave way to
a reactive, event-driven client that reads and writes through Forge's own APIs.
Read what follows as a record of how the slice was reasoned about and proven,
not as a current design.

Several documents here also carry supersessions from within the run itself, and
those are marked in place:

- `docs/adapter-contract-v0.md` §2 specified an adapter-owned `factory_sim`
  Postgres schema. Struck — the adapter has no Postgres client at all; every
  checkpoint write goes through `forge-api` HTTP.
- `docs/factory-math-v0.md`'s belt sketch is superseded by `docs/transport-v0.md`.
- `docs/recipes-v0.md` records a TOML→JSON build step that was later dropped;
  the C# sim core parses `data/recipes-v0.toml` directly.

`docs/inventory.md` is the run's blocker and decision log and is the closest
thing to an authoritative history — every `B<n>` / `D<n>` cited in code comments
resolves there.

## How it fits with the other Forge repos

Forge is a multi-repo umbrella: [`forge`](https://github.com/armoryworks/forge)
is the wrapper, with `forge-api` (.NET, MRP/ERP domain), `forge-ui` (Angular),
and siblings cloned alongside it. This repository is one of those siblings and
expects `forge-api` plus its Postgres to be running locally.

Nothing was forked from `forge-api`, and nothing was added to it. The integration
is deliberately narrow, and the shape of it was the main architectural finding of
the run (`docs/forge-backend-survey.md`):

- **Cold path only.** `forge-api` is REST/CQRS with a capability check and an
  audit-log write on every mutation — right for human-paced ERP actions, wrong
  for a 60 Hz tick. So item and recipe definitions are read from it once at load
  time, never per tick.
- **The adapter is a separate service**, not a controller area inside
  `forge-api`. It owns the Forge credential and the sim; the Godot client never
  talks to `forge-api` or to Postgres.
- **Persistence is HTTP.** Checkpointed stock deltas go back through
  `forge-api`'s `receive-stock` / `use-stock` endpoints. The adapter owns no
  database tables.

The mapping used is `Part` → item and `BomRevision` → recipe.
`WorkCenter` → machine was surveyed but not wired.

## Layout

```
sim/         Forge.Sim — deterministic sim core (net10.0). Q16.16 fixed point (Fx32),
             belts and lanes, content loader, state hasher, checkpoint round-trip.
             Forge.Sim.Tests is the xunit suite, including the golden-vector gate.
adapter/     ASP.NET Core minimal API (net10.0). Hosts the sim tick loop, exposes the
             HTTP surface and the /hubs/sim SignalR hub, and is the only thing that
             talks to forge-api.
game/        Godot 4.7 (.NET) client (net8.0). Isometric tilemap, camera, placement
             cursor, belt item rendering, HUD, and a SignalR client for the hub.
data/        recipes-v0.toml (the canonical authored content), golden-v0.json
             (generated), live-import.json (cold-load output).
tools/       refsim_v0.py (Python reference implementation, generates the golden
             vector), simprobe (SignalR wire-contract probe), run_checks.sh,
             Godot binary fetchers, a .NET hosting check.
bench/       Q16.16 throughput micro-benchmark used for the GDScript-vs-C# sizing call.
docs/        Specs, discovery reports, and the blocker/decision log.
```

The game assembly targets `net8.0` (required by `Godot.NET.Sdk` 4.7) while
`Forge.Sim` targets `net10.0`, so the game does **not** reference the sim core
directly — it consumes sim state over the wire. That TFM mismatch is an open item
in `docs/inventory.md`.

## Prerequisites

- .NET 10 SDK — `sim/`, `adapter/`, and `tools/simprobe`
- Godot **4.7, .NET ("mono") build** — the main scene attaches C# scripts, so the
  standard build cannot load it. Fetch it locally with
  `tools/fetch_godot_mono.sh` (downloads into `tools/godot4_mono/`, no sudo).
- Python 3.11+ (`tomllib`) — only to regenerate the golden vector
- A running `forge-api` and Postgres — only for the cold path and checkpoints.
  The sim, its tests, and the client's checks all run without them.

## Running it

**Sim core tests** — the acceptance gate. `GoldenTests` replays
`data/golden-v0.json` and fails on any hash divergence.

```bash
dotnet test sim/Forge.Sim.Tests/Forge.Sim.Tests.csproj
```

**Adapter.** Configuration is in `adapter/appsettings.json`: `ForgeApi:BaseUrl`
(default `http://127.0.0.1:5000`), the kiosk credential, the cold-load part ids,
and `Sim:ContentPath`. Override the credential with `ForgeApi__Barcode` /
`ForgeApi__Pin` rather than editing the file. Content is validated at startup and
the process exits non-zero if it fails.

```bash
ASPNETCORE_URLS=http://127.0.0.1:5299 dotnet run --project adapter
```

Port 5299 is what the Godot client and the HUD default to. It is also
deliberately not 5000 — that belongs to `forge-api`.

Endpoints:

| | |
|---|---|
| `GET /health` | liveness |
| `GET /sim/state` | full snapshot including the state hash; read-only, does not advance the sim |
| `POST /sim/belts` | belt placement, applied on a tick boundary |
| `POST /sim/checkpoint` | save a snapshot of the sim's own world state; returns the tick it was saved at |
| `POST /cold-load` | read Parts/BOMs from `forge-api`, write `data/live-import.json` |
| `POST /checkpoint` | write a stock delta back to `forge-api` (a Forge inventory concern, unrelated to the above) |
| `/hubs/sim` | SignalR: `sim.tick`, `sim.checkpointed`, `sim.error` |

To give the cold path real content to import, seed Forge with the ore → plate →
gear chain that mirrors `data/recipes-v0.toml`. The script is idempotent and
writes through `forge-api` HTTP only:

```bash
BASE_URL=http://127.0.0.1:5000 adapter/seed-recipes.sh
```

**Wire-contract probe** — connects to a running adapter and asserts on the actual
SignalR payloads, which no unit test can do:

```bash
dotnet run --project tools/simprobe [hubUrl] [seconds]   # default http://127.0.0.1:5299/hubs/sim, 5s
```

**Godot client checks.** Run them through the wrapper, not by invoking Godot
directly: a C# script that fails to load makes Godot drop the node, run the
remaining checks, and exit 0 — a green run with the sim feed silently missing.
`run_checks.sh` asserts on the output instead of the exit code.

```bash
tools/fetch_godot_mono.sh          # one-time
tools/run_checks.sh                # headless
tools/run_checks.sh --render-checks  # windowed framebuffer checks
tools/run_checks.sh --binary tools/godot4   # negative control: must FAIL
```

To open the project in the editor, point the .NET Godot binary at `game/`.

**Regenerating the golden vector** (only when the spec or content changes — the
checked-in hashes are the contract):

```bash
python3 tools/refsim_v0.py > /dev/null   # rewrites data/golden-v0.json
```

The reference implementation is in Python on purpose: Python was never a
candidate host, so the vector cannot encode host-specific behavior.

## Conventions worth knowing

Two rules shaped most of the code and are worth carrying forward:

- **The sim is authoritative; the renderer is a view.** No game state lives in
  the client. The sim is a pure function of state and tick — integer ticks,
  fixed point, no wall clock, no floats.
- **Every check must be negative-controlled.** Break the condition deliberately
  and confirm the check fails before trusting that it passes. A check that has
  never been seen to fail is decoration, and the run produced several concrete
  examples of exactly that.

## License

Apache License 2.0 — see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).
