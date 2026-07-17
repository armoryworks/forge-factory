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

### 3.5 `sim.tick.inserters` — inserter state (B64)

**Additive.** §3.1's existing fields keep their names and shapes, so a client built before B64
ignores this key and keeps working. The *ideal* shape is parked for the eventual §3.1 breaking
re-cut, alongside the `beltDeltas` → `belts` rename.

```jsonc
"inserters": [
  {"id": 0, "state": "Idle",     "holding": false, "progress": 0, "swingTicks": 4,  "item": 0},
  {"id": 1, "state": "Swinging", "holding": true,  "progress": 9, "swingTicks": 20, "item": 0},
  {"id": 2, "state": "Blocked",  "holding": true,  "progress": 20, "swingTicks": 20, "item": 0}
]
```

Also on `GET /sim/state`, identically shaped — a client baselines there per §3.2 and then tracks the
hub, so the two agreeing is a requirement, not a nicety. `simprobe` cross-checks them.

**`null` vs array**, exactly as `beltDeltas` (D21): `null` = this build models no inserters; an
array = it does. The slice's reference build has none, so `null` is the honest emission there;
`Sim:Inserter=true` hosts the §10 world. Both branches are live-verified — an emission path nobody
has run is not a verified path.

**Absolute, never deltas** — D21's `stock` rule and D22's belt rule, for the third time and the same
reason: every emit is a full resync, so a dropped broadcast costs one frame of staleness rather than
permanent silent divergence. It is O(inserters) and tiny.

**Per-inserter, not aggregate tallies** — the deliberate opposite of `machineState`. Inserters are
few and individually meaningful: **D24 ratifies that a starved arm at a contended source is
information the player must see** (§10.4). Tallies would erase exactly the signal §10.4 exists to
expose — `{"Idle": 1, "Swinging": 2}` cannot tell you *which* arm is losing.

**`holding` ships even though it looks derivable — it is not.** A `Blocked` inserter is holding; so
is a `Swinging` one; an `Idle` one is not. There is no function from `state` to `holding`, so a
client drawing an item in the claw needs the bool. It is also the conservation witness (§10.1/D24):
that item is out of the source and not yet in the destination, and exists nowhere else.

`progress`/`swingTicks` ship as a pair so the client can render a swing fraction without hardcoding
a tier constant — the same reasoning as `beltDeltas.spacing` (B54).


### 3.2 Resync and gap detection

The hub is `Clients.All` broadcast with no replay: a disconnect loses messages permanently.
The client's contract for recovering:

1. **Baseline on connect** — `GET /sim/state` for a coherent snapshot (taken under the sim
   lock, so `tick` and state never tear).
2. **Detect gaps from `tick`** — consecutive emits differ by exactly `emitEveryNTicks`. Any
   other jump means emits were missed. No separate sequence number is needed; `tick` already
   carries it, provided the client knows the push rate — which `/sim/state` publishes as
   **`emitEveryNTicks`** (an integer tick delta, default 3; distinct from `tickRate`, the 60 Hz
   *sim* rate). B53 found this section claiming the rate was published when no such field existed;
   the field is now on the wire (B54). It is deliberately the raw tick delta rather than a derived
   Hz: it is the exact integer the client compares against, and a derived rate would be fractional
   the moment `tick_hz` stops dividing evenly by it. A client must **not** infer cadence
   empirically — observing a delta of 3 cannot distinguish "cadence is 3" from "cadence is 1 and I
   am dropping 2 of every 3", which is precisely the condition gap detection exists to catch.
3. **Recover** — for `stock` and `machineState`, recovery is automatic: both are absolute, so
   the next emit is already a full resync and a gap costs one frame of staleness. Re-fetch
   `/sim/state` only to re-baseline anything genuinely incremental.

~~**When belts land, they may legitimately be deltas**~~ — **SUPERSEDED BY D22. Belts are
absolute, like `stock`.** This paragraph anticipated that "belt contents are large and a
full-snapshot-per-tick at 20 Hz is a real cost, unlike three ints", and reasoned that step 3 would
stop being free. **That premise turned out to be empirically false**, and the reasoning was sound
only given it.

Belts are not stored as items. A lane is a list of **runs** — maximally compressed blocks —
so cost is O(runs), not O(items), and *the saturated case is the cheap case*
(`transport-v0.md` §4). The live payload for a fully packed 80-item lane is a single run:

```jsonc
{"belt":0,"lane":0,"spacing":16384,"length":1310720,
 "runs":[{"head":1306624,"len":80,"item":0}]}
```

Full belt state is therefore *smaller* than the bookkeeping a delta stream would need, and step 3
stays free. So D22 rules belts absolute for the same reasons D21 ruled `stock` absolute: every emit
is a full resync, a dropped message costs one frame of staleness rather than permanent silent
divergence, and a late-joining client can reconstruct belt contents from any single emit rather than
needing a baseline it has no way to obtain. The delta contract sketched above — tick-tagged,
half-open interval `(tick - emitEveryNTicks, tick]`, re-baseline from `/sim/state` — is **not
implemented and not needed**; it is kept here only to record why it was rejected.

**The field is still named `beltDeltas`.** It carries absolute state and the name is a known wart
(D22). It is not renamed yet because the client is in flight; §3.1's forward note already requires a
breaking client re-cut when `machineState` gains entity identity, and the rename to `belts` should
ride with that rather than churn the client twice.

### 3.3 `beltDeltas` geometry — where a belt IS (B66)

Each `beltDeltas` entry carries the cells its belt runs along:

```jsonc
{"belt":0, "lane":0, "spacing":16384, "length":1310720,
 "cells":[{"x":0,"y":0,"dir":1}, {"x":1,"y":0,"dir":1}, … {"x":19,"y":0,"dir":1}],
 "runs":[{"head":1306624,"len":80,"item":0}]}
```

**The gap this closes.** `belt` is an integer index and nothing on the wire mapped it to the world,
so a client could reconstruct *what* is on a belt but never *where* — half-meeting D22's own
rationale, which is that a late-joining client reconstructs from any single emit. Replaying the
sim's chain-building client-side is not an answer: it puts sim logic in the client, and it is
silently wrong on partial rejection and on seeded belts.

**Shape: `cells[]`, not `origin+dir+len`.** A chain can **turn** — §2.5 follows each cell's `Ahead`,
and every cell carries its own `dir`. `origin+dir+len` describes only a straight belt: it would be
correct today and silently wrong the first time a player builds a corner. Verified live: a posted
L-shape reports `dirs [1,2,2]`.

**Order: tail-first, travel order.** `cells[i]` is the tile spanning `[i, i+1)` tiles along the lane,
i.e. the same axis `runs[].head` is measured on. A client needs no rule beyond that to place an item:
`cell_index = head / 65536`.

**Seeded belts are covered.** Fixture belts previously had *no cells at all* — they were abstractions
with no map position, which is exactly why `belt:0` was unrenderable. They now own a declared cell
run in the sim itself. Emitting cells only on the wire would have been a lie: the sim would not have
believed the position, so a player could have built on top of the seeded belt and the two surfaces
would disagree. Registering them properly makes the world coherent — the seeded belt's cells are
`Occupied`, and a chain reaching it joins it (§2.5).

**Redundant per lane, deliberately.** Both lanes of a belt share cells, so this repeats. Hoisting it
to a belt-level object would make a lane entry depend on a sibling entry, which breaks D22's
independent-reconstructability rule. Cost noted in B66; revisit if bandwidth bites.

**Additive, and not hashed.** Geometry is emission metadata. All 20 golden hashes are byte-identical
— asserted by regeneration, as for B62/B64.


### 3.4 `POST /sim/belts` — belt placement (B56 / D23)

The game's send path. Body is the raw `belts_for_adapter()` array **verbatim and uncoalesced** —
the sim owns lane-building, so the client sends cells and never pre-groups them.

```jsonc
POST /sim/belts
[ {"cell":{"x":50,"y":50},"dir":1}, {"cell":{"x":51,"y":50},"dir":1} ]

202 Accepted
{"appliedAtTick":497, "accepted":2, "rejected":[{"cell":{"x":-5,"y":0},"reason":"off-map"}]}

400 Bad Request   — body is not an array, or an entry lacks cell/dir, or dir is outside 0..3
```

`dir` is **pinned** to `iso.gd:103-106`: **N=0, E=1, S=2, W=3**. Pinned rather than inferred because
client and sim must agree on travel direction or belts silently run backwards.

**`202`, not `204`.** The POST *enqueues*; the tick loop drains it at a tick boundary (D23). `204`
would claim a completion that has not happened. `/checkpoint` returns `204` because it is a
synchronous fire-and-forget write — a different thing.

**`appliedAtTick` is load-bearing.** It is the tick whose `sim.tick` first reflects the placement.
Without it a caller can only poll and hope, and cannot distinguish "applied at tick N" from
"silently dropped" — the same unsound inference §3.2 removes from cadence. A client should wait for
an emit with `tick >= appliedAtTick` and read the placement out of `beltDeltas`.

**Rejection is a normal event, not an error.** A refused cell returns `202` with an entry in
`rejected[]`, mirroring `entity_layer.gd:81`'s "callers must treat -1 as nothing happened". Only a
*malformed* body is a `400`. Reasons are advisory strings — clients should not bind to their text.

**Reasons (B67).** `off-map`, `occupied`, `duplicate-in-batch`. **Additive:** the shape is unchanged
(`{cell, reason}`); only the *set* of strings grew, so a pre-B67 client that had only ever seen
`off-map` still parses this and merely encounters strings it does not recognise — which is why this
section already said not to bind to the text. (`bad-dir` exists in the enum but is unreachable here:
a dir outside `0..3` is a malformed body and returns `400` before judgement.)

B56 originally reported only `off-map`, because occupancy is decided at apply time. That is now
resolved without waiting on a tick: the judgement is computed **under the tick lock, against the same
world and through the same code path (`World.Judge`) the drain will use**, so it is not a
re-implementation that can drift from the decision. The one case where the response can still differ
from the outcome is a *second* POST arriving in the same tick and sorting ahead of yours for a
contested cell (D23 merges same-tick POSTs into one batch and sorts by `(y,x,dir)`, so arrival order
does not win). For a single-client slice that cannot arise; `appliedAtTick` remains the authoritative
correlator either way.

**Determinism (D23).** Placements are applied at a tick boundary, sorted by `(cell.y, cell.x, dir)`
— never arrival order, which is wall-clock dependent and would desync two hosts that received the
same POSTs in a different network order. The sort lives in `World.ApplyBeltBatch`, not the adapter,
so the guarantee sits where it is tested.

**Lane-building.** Contiguous accepted cells (each pointing at the next) chain into ONE lane, so a
posted run of N cells becomes an N-tile belt. Chains are **not** joined to belts from earlier
batches: that would mean rebuilding a live lane and either discarding the items on it or migrating
them, and neither is specified. A cell placed adjacent to an existing belt therefore starts a
*separate* belt — a real v0 limitation, recorded in B56.


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
