# Adapter Contract v0 — BFF surface for the factory vertical slice

Scope: **one ore source → one belt → one machine → one output**, per plan.md's Phase 6
definition of done. Not the full entity map — see forge-backend-survey.md for that.
This is the minimal set of operations the Godot client needs from the adapter/BFF
layer to run that slice against real forge data.

Empirically verified 2026-07-17: local stack up via
`docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d` (forge-deploy).
Confirmed live — kiosk login (`POST /api/v1/auth/kiosk-login`) → JWT → authenticated
`GET /api/v1/parts` returned real seeded data (200, 2 parts). Verification used a
throwaway test user (barcode `GAMETEST01`, role `Engineer`), created and deleted via
direct SQL against the dev Postgres container — not a forge-api endpoint, since no
self-serve "create kiosk user" API exists yet (see Gaps).

## Legend

- **HTTP** = adapter calls forge-api over REST (cold path — setup/seed, not per-tick).
- **EF direct** = adapter reads forge's `AppDbContext`/entities directly in-process
  (only viable if the adapter is itself a .NET service sharing the DbContext; if the
  adapter is a separate process/language, everything becomes HTTP or raw SQL against
  the shared Postgres instance instead).
- Per the survey's adapter recommendation: the sim tick loop never calls any of these
  per-tick. These are load-time (item/recipe defs) and checkpoint-time (inventory,
  production-run) operations only.

## 1. Auth (game operator session)

| Op | Call | Notes |
|---|---|---|
| Login | HTTP `POST /api/v1/auth/kiosk-login` `{barcode, pin}` → JWT | Reuse forge's kiosk flow as-is; matches "operator station" framing from the survey. No self-serve endpoint to provision a kiosk barcode/pin today — provisioning is a gap (see below). |
| Session | Bearer JWT on all subsequent calls, 8h expiry (`KioskLoginHandler`, `TimeSpan.FromHours(8)`) | Adapter should refresh/re-login before expiry for long-running game sessions; forge-api has `POST /api/v1/auth/refresh`. |

## 2. Item defs — `Part` (load-time, cold path)

| Op | Call | Maps to |
|---|---|---|
| List items | HTTP `GET /api/v1/parts?page=&pageSize=` | Item catalog. Confirmed live: returns `partNumber`, `name`, `status`, `procurementSource`, `inventoryClass`, `bomLineCount`. |
| Item detail | HTTP `GET /api/v1/parts/{id}` | Full item def for the sim's static item table. |

For v0, the adapter reads Parts **once at load/seed time** and caches a flattened
item table client-side (or in its own service). It does not re-fetch per tick.

## 3. Recipe — `BOMLine` / `BomRevision` (load-time, cold path)

| Op | Call | Maps to |
|---|---|---|
| Get BOM for a part | HTTP `GET /api/v1/parts/{id}/bom/revisions` then `GET /api/v1/parts/{id}/bom/revisions/{revId}` | Recipe = list of `(ChildPartId, Quantity, SourceType)` rows on `BOMLine`. `SourceType` (Make/Buy/Stock) tells the adapter whether a component is itself craftable — relevant for the "one machine" slice choosing a single-level recipe. |
| BOM at job release | HTTP `GET /api/v1/jobs/{id}/bom-at-release` | Only needed once the slice has a live production order; returns the pinned `BomRevisionIdAtRelease` snapshot so recipe changes mid-run don't retroactively alter an in-flight order. |

v0 needs exactly one recipe (one machine, one recipe) — fetch it once, cache it.

## 4. Machine — `WorkCenter` (load-time, cold path)

| Op | Call | Maps to |
|---|---|---|
| Get work center | HTTP `GET /api/v1/work-centers/{id}` (or list, filter to the one used) | Machine identity/capacity metadata for the slice's single machine. v0 does not need `WorkCenterCalendar`/`WorkCenterShift` (scheduling capacity) — those are post-slice. |

## 5. Inventory — read/write (checkpoint-time)

These are the primitives the survey flagged as the right hot-path-adjacent layer:
additive/subtractive stock ops that don't require a PO or shipment.

| Op | Call | Maps to |
|---|---|---|
| Read stock | HTTP `GET /api/v1/inventory/locations/{locationId}/contents` or `GET /api/v1/inventory/parts` | Belt/stockpile on-hand read, at checkpoint cadence (e.g. once/sec), not per tick. |
| Add stock (belt output → stockpile) | HTTP `POST /api/v1/inventory/receive-stock` `{partId, locationId, quantity, ...}` | Machine output landing in a bin. |
| Consume stock (machine input) | HTTP `POST /api/v1/inventory/use-stock` `{partId, locationId, quantity, ...}` | Recipe input consumption. Server-enforced invariant: on-hand can never drop below reserved (S-RI1) — adapter must handle the 409/400 if the sim and forge's view of stock drift. |
| Manual correction | HTTP `POST /api/v1/inventory/adjust` (Admin/Manager only) | Debug/dev tool for the vertical slice, not part of normal sim flow. |

**Adapter's real job here**: the sim's authoritative belt/stockpile state lives in
the sim process, ticking many times/sec. The adapter periodically (checkpoint, not
per-tick) reconciles sim state to forge inventory via `receive-stock`/`use-stock`
calls — batched, not one call per item-on-belt. Exact checkpoint cadence is a
factory-math-v0.md concern, not this contract's.

## 6. Production order lifecycle — `Job` + `ProductionRun`

This is the best-fitting existing lifecycle primitive in forge for "a machine is
running a recipe right now":

| Op | Call | Maps to |
|---|---|---|
| Create order | HTTP `POST /api/v1/jobs` (`CreateJobCommand`) | One production order for the slice (part = the recipe's output). |
| Start a run | HTTP `POST /api/v1/jobs/{id}/production-runs` `{partId, targetQuantity, operatorId, notes}` | "Machine starts producing N units." `RequiresCapability("CAP-MFG-COMPLETE")`. |
| Update progress | HTTP `PUT /api/v1/jobs/{id}/production-runs/{runId}` `{completedQuantity, scrapQuantity, status, setupTimeMinutes, runTimeMinutes}` | Checkpoint-time progress push — completed-unit counter, not per-tick. |
| Receive to stock | HTTP `POST /api/v1/jobs/{id}/production-runs/{runId}/receive-to-stock` | Closes the loop: finished units land in inventory (ties back to §5). |

v0 slice does **not** need `explode-bom` (multi-level job tree — out of scope for
one-machine), `JobStage`/kanban movement, or job scheduling (`SchedulingController`)
— those are post-slice.

## 7. What the adapter must own itself (not in forge-api at all)

Per the survey: none of the above is a tick loop. The adapter/sim owns:
- The fixed-timestep tick and all belt/machine/item state *between* checkpoints.
- A push channel to the Godot client for per-tick deltas (forge's SignalR hubs are
  domain-specific — `BoardHub` etc. — and not reused for this; a new lightweight
  channel, shape TBD by engine-choice.md, is out of scope here).
- Determinism/replay (seed + input log) — entirely sim-side, forge has no concept of this.

## Gaps found during this pass

- **No self-serve kiosk-user provisioning endpoint.** `POST /api/v1/auth/kiosk-login`
  exists but nothing lets the adapter create a barcode+PIN identity via API — today
  that's an admin UI action (`set-pin` requires an already-authenticated session) or
  direct DB insert (what verification used). For an automated dev/CI bring-up, this
  is a blocker for a fully scripted "spin up a fresh forge + game" flow. Logged in
  inventory.md.
- **`WorkCenter` read confirmed to exist as a controller but not exercised live in
  this pass** (time-boxed) — endpoint shape assumed from `WorkCentersController`
  naming convention (`GET /api/v1/work-centers/{id}`), not curl-verified. Low risk
  (matches every other controller's convention) but flagging since everything else
  in this doc was hit live.
