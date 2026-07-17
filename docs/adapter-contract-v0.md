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

## 2. Factory-owned Postgres schema (adapter-only, EF-audit-pipeline bypassed)

New schema `factory_sim`, owned and migrated by the adapter — not forge-db, not EF
Core, no `ActivityLog`/soft-delete/capability machinery. Plain SQL, adapter is the
only writer.

```sql
create schema factory_sim;

-- cached cold-path defs (refreshed on adapter startup, read-only after)
create table factory_sim.item_def (
  id           int primary key,        -- = forge Part.id
  part_number  text not null,
  name         text not null
);

create table factory_sim.recipe_def (
  id                int primary key generated always as identity,
  output_item_id    int not null references factory_sim.item_def(id),
  output_qty        int not null,
  work_center_id    int not null       -- = forge WorkCenter.id
);

create table factory_sim.recipe_input (
  recipe_id  int not null references factory_sim.recipe_def(id),
  item_id    int not null references factory_sim.item_def(id),
  qty        int not null,
  primary key (recipe_id, item_id)
);

-- sim checkpoints: authoritative game state, written at checkpoint cadence
-- (factory-math-v0.md's concern), never per-tick, never via EF
create table factory_sim.sim_run (
  id            uuid primary key,
  seed          bigint not null,
  started_at    timestamptz not null default now(),
  last_tick     bigint not null default 0
);

create table factory_sim.checkpoint (
  sim_run_id    uuid not null references factory_sim.sim_run(id),
  tick          bigint not null,
  belt_state    jsonb not null,   -- opaque sim-owned snapshot, adapter doesn't interpret contents
  machine_state jsonb not null,
  stock_state   jsonb not null,
  written_at    timestamptz not null default now(),
  primary key (sim_run_id, tick)
);
```

`belt_state`/`machine_state`/`stock_state` are opaque JSON the sim serializes —
the adapter's job is durability and replay lookup (`tick`), not modeling sim
internals in relational form. If forge inventory needs to reflect sim stock
(e.g. finished goods), that's a *separate* explicit sync via `POST
/api/v1/inventory/receive-stock` on forge-api (cold path, batched per checkpoint,
not per tick) — not a foreign key into forge's tables.

## 3. Live-delta hub (adapter → Godot client)

New hub, adapter-hosted (SignalR or plain WebSocket — engine-choice.md's call), not
`BoardHub`/etc. — those are forge-api's kanban/notification concerns, unrelated.

| Event | Payload | Cadence |
|---|---|---|
| `sim.tick` | `{tick, beltDeltas:[...], machineState, stockDelta}` | Render-rate push (throttled from sim-rate; exact throttle is factory-math-v0.md's tick-rate call, not this contract's). |
| `sim.checkpointed` | `{tick}` | Once per checkpoint write (§2), so the client can correlate. |
| `sim.error` | `{message}` | On adapter-side fault (e.g. forge-api unreachable during a cold-path fetch). |

Client never sends sim-state mutations over the hub — inputs (place belt, etc.) are
adapter HTTP calls (own endpoint set, not specified here — post-slice UI concern);
the hub is push-only from adapter to client.

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
