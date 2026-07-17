# Adapter Contract v0 — sim-adapter service for the factory vertical slice

Scope: one ore source → one belt → one machine → one output (plan.md Phase 6). Not
the full entity map (see forge-backend-survey.md).

**Topology, ratified (inventory B3/B4):** the adapter is a **separate service**, not
a controller area inside forge-api. It owns **all** Postgres access, including sim
checkpoints. The Godot client never touches Postgres directly — no DB client, no
GDExtension. The client speaks **HTTP + one live-delta hub** to the adapter only.

```
Godot client ──HTTP/WS──▶ Adapter service ──HTTP──▶ forge-api (cold path: item/recipe/machine defs)
                                │
                                └──SQL──▶ Postgres, factory_sim schema (checkpoints; adapter-owned, EF-bypassed)
```

## 1. Cold-path reads from forge-api (load/seed time only, never per-tick)

Auth: adapter logs in once via `POST /api/v1/auth/kiosk-login` `{barcode, pin}` →
JWT (8h expiry, `POST /api/v1/auth/refresh` before expiry). This is the adapter's
*own* service credential — unrelated to player/operator auth (§3).

| Read | Endpoint | Response shape (verified against forge-api source, `forge.core/Models/`) |
|---|---|---|
| Item defs | `GET /api/v1/parts?page=&pageSize=` | `{items:[{id,partNumber,name,description,revision,status,procurementSource,inventoryClass,bomLineCount,createdAt,effectivePrice,effectivePriceCurrency}], totalCount,page,pageSize}` — confirmed live 2026-07-17. |
| Recipe (BOM) | `GET /api/v1/parts/{id}/bom/revisions/{revId}` | `BomRevisionDetailResponseModel: {id,partId,revisionNumber,effectiveDate,notes,isCurrent,entries:[BomRevisionLineResponseModel]}`; each entry: `{id,partId,partNumber,partDescription,quantity,unitOfMeasure,operationId,referenceDesignator,sourceType,leadTimeDays,notes,sortOrder}`. `sourceType` ∈ Make/Buy/Stock. |
| Machine | `GET /api/v1/work-centers` (list; filter client-side, no `/{id}` route confirmed) | `WorkCenterResponseModel: {id,name,code,description,dailyCapacityHours,efficiencyPercent,numberOfMachines,laborCostPerHour,burdenRatePerHour,isActive,assetId,assetName,companyLocationId,locationName,sortOrder}`. |

Adapter fetches these once at startup/seed, caches in its own tables (§2), and never
re-hits forge-api on the tick path. v0 needs exactly one Part (output), one BOM
revision (recipe), one WorkCenter (machine) — fetch once, done.

## 2. Checkpoint persistence — SUPERSEDED by D10, no adapter-owned Postgres

**This section originally specified an adapter-owned `factory_sim` Postgres schema
(own tables, EF-audit bypassed). That design is superseded — see inventory.md D10
("Amends D6... The sim owns no Postgres tables for the slice") and B12 (accepted).
Kept below, struck through, for history; do not build against it.**

~~New schema `factory_sim`, owned and migrated by the adapter... [own-tables SQL
sketch, dropped]~~

**Current design (D10):** the adapter has **no Postgres client at all**. Every
checkpoint write is a forge-api HTTP call — `POST /api/v1/inventory/receive-stock`
/ `use-stock` for stock deltas — batched at ~1/s checkpoint cadence, never per
tick. This is what `factory/adapter/ForgeApiClient.WriteCheckpointDeltaAsync` /
`POST /checkpoint` actually implement, and it is live-verified against forge-api
(inventory.md B24). The replay log (seed + input stream, Phase 4's determinism
exit condition) is a **flat file on the sim side** — outside this service, nothing
for the adapter to persist.

D10's reversal trigger, if it ever fires: checkpoint volume outgrowing forge-api's
write path. Not expected at slice scale — not a live concern for v0.

## 3. Live-delta hub (adapter → Godot client)

New hub, adapter-hosted (SignalR or plain WebSocket — engine-choice.md's call), not
`BoardHub`/etc. — those are forge-api's kanban/notification concerns, unrelated.

| Event | Payload | Cadence |
|---|---|---|
| `sim.tick` | `{tick, beltDeltas, machineState, stock}` | **20 Hz** — every 3rd tick at `tick_hz = 60`. Configurable via `Sim:EmitEveryNTicks`. |
| `sim.checkpointed` | `{tick}` | Once per checkpoint write (§2), so the client can correlate. |
| `sim.error` | `{message}` | On adapter-side fault (e.g. forge-api unreachable during a cold-path fetch). |

Client never sends sim-state mutations over the hub — inputs (place belt, etc.) are
adapter HTTP calls (own endpoint set, not specified here — post-slice UI concern);
the hub is push-only from adapter to client.

### 3.1 `sim.tick` payload shapes

Ratified by **D21**. Shapes were previously named but never defined, so the doc and the
implementation agreed only by omission.

```jsonc
{
  "tick": 1234,              // sim tick this emit reflects. Monotonic, +EmitEveryNTicks per emit.
  "beltDeltas": null,        // null = BELTS NOT MODELLED IN THIS BUILD. See below.
  "machineState": {          // §4.2 state tallies, aggregate counts per machine class
    "miners":     { "Crafting": 3, "Starved": 1 },
    "furnaces":   { "Crafting": 2 },
    "assemblers": { "Blocked": 1 }
  },
  "stock": {                 // ABSOLUTE buffer levels, not deltas
    "ironOre": 412, "ironPlate": 96, "ironGear": 7
  }
}
```

**`stock` is absolute levels, not deltas** (D21 amendment). A level is O(1) per item type —
three ints, constant forever — so the "a snapshot would grow without bound" worry that
motivated deltas does not apply to levels; it applies to event logs. Absolute levels are
*strictly more informative* than deltas at identical cost: the client derives a delta from
two consecutive samples, but cannot derive a level from deltas without a baseline it has no
way to obtain. They are also self-healing — every emit is a full resync, so a dropped
message costs one frame of staleness instead of permanent silent divergence.

**`beltDeltas`: `null` means the subsystem is not modelled in this build. `[]` means it is
modelled and nothing changed this tick.** These are different facts and a client must be
able to tell them apart; v0 emits `null` because `golden-v0.md` §3 does not model transport.
A v0 client renders no belts either way, but a v1 client that sees `[]` will correctly
render an empty-but-present belt network, and one that sees `null` knows to render nothing
and, if it needs belts, to fail loudly rather than silently show an empty factory.

**`machineState` is aggregate tallies, not per-machine state.** Sufficient for v0, which has
no spatial machines — but see D21's forward note: §6.1 (a stopped machine is visibly
stopped) and §6.3 (per-machine alert markers) need `{entityId: state}`, and that is a
breaking change the client must be re-cut against when the sim gains entity identity.

### 3.2 Resync and gap detection

The hub is `Clients.All` broadcast with no replay: a disconnect loses messages permanently.
The client's contract for recovering:

1. **Baseline on connect** — `GET /sim/state` for a coherent snapshot (taken under the sim
   lock, so `tick` and state never tear).
2. **Detect gaps from `tick`** — consecutive emits differ by exactly `EmitEveryNTicks`. Any
   other jump means emits were missed. No separate sequence number is needed; `tick` already
   carries it, provided the client knows the push rate (published in `/sim/state`).
3. **Recover** — for `stock` and `machineState`, recovery is automatic: both are absolute, so
   the next emit is already a full resync and a gap costs one frame of staleness. Re-fetch
   `/sim/state` only to re-baseline anything genuinely incremental.

**When belts land, they may legitimately be deltas** — belt contents are large and a
full-snapshot-per-tick at 20 Hz is a real cost, unlike three ints. That is the case where
step 3 stops being free, so the delta contract must be specified *with* them, not after:
belt deltas must be tagged with the `tick` they apply at, must cover exactly the half-open
interval `(tick - EmitEveryNTicks, tick]` so a snapshot at any tick can be reconciled without
double-counting, and `/sim/state` must return full belt contents to re-baseline against. A
delta stream without an anchor and a snapshot to rebuild from is unrecoverable, which is the
trap `stock` was walking into.

## 4. Auth separation

Two independent auth domains, never conflated:

| Who | Credential | Talks to |
|---|---|---|
| Adapter service | Its own forge kiosk credential (§1) — a service identity, not tied to any player | forge-api only, cold path |
| Game operator (player) | Adapter-issued session token (shape TBD — does **not** have to be a forge JWT; the adapter can mint its own, since it's the only thing checking it) | Adapter's HTTP + hub endpoints only; never sent to forge-api directly |

Rationale (per D7/Q4 finding): forge's kiosk login is only an alternate
*credential-entry* mechanism — the JWT it issues still carries full
ASP.NET Identity role/capability claims and forge-api still gates every endpoint
with `[Authorize(Roles)]` + `[RequiresCapability]`. A player token must not be a
raw forge JWT passed through, or the game inherits forge's RBAC/capability surface
by accident. The adapter terminates forge auth at its own boundary and issues its
own lightweight player session.

## 5. What's explicitly out of this contract

Multi-level BOM explosion, `JobStage`/kanban, `SchedulingController`, WorkCenter
calendars/shifts, kiosk-user self-provisioning (open gap, logged as inventory B11) —
all post-slice or pre-existing gaps, not needed for one-machine v0.
