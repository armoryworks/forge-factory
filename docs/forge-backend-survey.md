# Forge Backend Survey — for factory-game backend reuse

Phase 0 discovery. Scope: `/home/daniel/dev/forge/{forge-api,forge-db,forge-ui,forge-deploy}`.

## Acceptance-bar answers (plan.md "must answer")

- **What forge-api/forge-db/forge-deploy offer to reuse:** forge-api's MRP domain (Part/BOM/Job/WorkCenter/Inventory — §2) as the sim's item/recipe/machine vocabulary; forge-deploy's docker-compose pattern as the deployment shape; forge-db's schema-as-code tree if we want the factory's own tables tracked the same way. None of it is a drop-in game engine — all of it needs the adapter layer in §5/§6.
- **Persistence story:** PostgreSQL 17 + pgvector (`pgvector/pgvector:pg17`, forge-deploy `docker-compose.yml:177`). Two competing sources of schema truth today: forge-api's EF Core migrations (live, authoritative for the running app) and forge-db's desired-state SQL tree + `pg-schema-diff` harness (declared target state, 293 tables, **not yet** the enforced source — README states the EF→forge-db drift-check CI and cutover are still pending). Practical read: the DB is a shared Postgres instance; a factory schema can live in its own tables in that instance without fighting either migration system, but should not go through EF Core's audit/soft-delete conventions (§ below) for hot sim state.
- **Does anything force a language/runtime choice on the factory build?** No hard fork-lock. forge-api is .NET 9, forge-ui is Angular/TypeScript, forge-db's harness is .NET — but the factory sim only needs a Postgres client (every mainstream language has one) and, optionally, HTTP/WebSocket access to forge-api. Nothing in the schema or API requires the game engine itself to be .NET. The soft constraint is Phase 3's "builds and runs the forge way" (docker-compose, sibling-repo layout) — that's a deployment/process convention, not a language mandate. See engine-choice.md for the actual engine decision.

## Answers to inventory.md Q1

**Q1 — runtime constraints forge-api/forge-db impose (latency, request model, auth):**
- **Request model:** forge-api is synchronous REST-over-HTTP (ASP.NET Core/Kestrel, MediatR CQRS — one command/query per HTTP call). There is no batch/streaming mutation endpoint for game-loop-style writes. SignalR hubs (`BoardHub`, `TimerHub`, `NotificationHub`, `ChatHub`, `AccountingHub`) exist for server→client push but are each hand-built for one domain; none stream generic tick deltas.
- **Latency/overhead per request:** every controller action goes through role `[Authorize]` + `[RequiresCapability("CAP-...")]` checks, and — non-negotiable per forge-api/CLAUDE.md — **every mutating MediatR handler writes an `ActivityLog` row**. That's a DB write-amplification tax (audit log + soft-delete bookkeping) on every single mutation, fine for human-paced ERP actions (a few writes/sec across all users) but wrong for a 20–60 Hz sim tick moving hundreds of items/sec across belts.
- **Auth:** JWT bearer per request (ASP.NET Identity, optional SSO/MFA), or a separate lighter kiosk RFID/NFC/barcode+PIN flow for shop-floor terminals. Both are per-request/session auth, not designed for a always-on background sim process — a sim service would want a service-account token or to bypass HTTP auth entirely by talking to Postgres directly.
- **forge-db specifically imposes no runtime constraint** — it's a dev/CI/deploy-time schema tool (`baseline`/`plan`/`verify`/`apply` against pg-schema-diff), not something the running app links against. It only matters if the factory's tables are added to its tracked schema tree.

## Answer to inventory.md Q2 (adapter recommendation)

**Does the API shape fit real-time sim reads/writes?** No. REST+CQRS with per-request auth/capability checks and mandatory audit-log writes on every mutation is built for human-rate ERP transactions, not a fixed-timestep sim ticking many times a second. Forcing sim ticks through `JobsController`/`InventoryController` would mean an ActivityLog row (and capability check) per belt-item move — that doesn't survive contact with a real tick rate.

**Recommended adapter:** a thin, separate sim-state service/layer, not a fork of forge-api:
1. The sim engine owns its own tick loop and in-memory/authoritative game state (per plan.md's "sim is authoritative" rule) — it does not read forge-api per tick.
2. It persists checkpoints (not per-tick deltas) directly to its own Postgres tables — either via a lightweight repository bypassing EF Core's audit/soft-delete pipeline, or via forge-db-tracked tables reached with a minimal driver — no `ActivityLog`, no capability gate, on the hot path.
3. Anything that needs forge's real MRP data (Part/BOM/WorkCenter as recipe/machine source data) is read once at load/seed time through normal forge-api REST calls — cold path, fine for its latency profile.
4. Live state reaches any UI via a dedicated WebSocket/SignalR hub (same pattern as `BoardHub`) broadcasting compact deltas, not REST polling.
5. Auth for the sim service is a service-account/internal-network concern, separate from user-facing JWT/kiosk auth — those two auth models fully coexist without touching forge-api's capability system.

## 1. Running it locally + tech stack

- **Stack:** .NET 9 API (MediatR/CQRS, FluentValidation, EF Core + Npgsql, SignalR, Hangfire, Mapperly, Serilog) + PostgreSQL 17/pgvector + MinIO (S3-compatible) + Angular 21 frontend (standalone components, zoneless/signals, Angular Material). `forge-db` is a separate schema-as-code project (desired-state SQL tree + `pg-schema-diff` harness, .NET 10 tooling) — dev/CI/deploy-time only, not linked into the running app; see the persistence-story note below.
- **Repo layout is a multi-repo umbrella:** `forge/` is a thin wrapper repo; siblings `forge-ui`, `forge-api`, `forge-deploy`, `forge-test`, `forge-voice` clone in as siblings via `forge/bootstrap.sh`. This checkout already has all siblings present.
- **Run:** from `forge-deploy/`, `docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d` (or `./setup.sh` per `forge-deploy` docs). Dev overlay gives hot-reload (`dotnet watch`, `ng serve` via poll-based FS watch) and re-exposes ports on LAN. Default ports: UI `4200`, API `5000`, Postgres `5432`, MinIO `9000`/`9001`. `.env.example` in `forge-deploy` has the full var list.
- **Local CI gates** (forge-api/CLAUDE.md is authoritative): UI — `npm run lint && npm run lint:i18n && npm run test -- --watch=false`; API — `dotnet build --configuration Release -warnaserror && dotnet test`.
- **Convention right now:** direct-push to `main`, no branch/PR ceremony (flipped 2026-05-07, pre-beta). Applies to this factory work too unless told otherwise.

## 2. Data models / endpoints usable as a factory-game backend

This is a full job-shop MRP/ERP, not a toy — the domain maps almost 1:1 onto factory-game concepts:

| Game concept | Forge entity/endpoint |
|---|---|
| Item / material | `Part` (`PartsController`) — PartNumber, Name, ProcurementSource (Make/Buy/Subcontract/Phantom), InventoryClass (Raw/Component/Subassembly/FinishedGood/Consumable/Tool), TraceabilityType, weight/dimensions in SI units |
| Recipe | `BOMLine`/`BomRevision`/`BomRevisionLine` — versioned bills of material per Part |
| Production order | `Job` (`JobsController`) — JobNumber, CurrentStage (`JobStage`), Priority, BomRevisionIdAtRelease (pins the recipe snapshot), EstimatedMaterialCost/LaborCost/BurdenCost, ParentJob/ChildJobs for sub-jobs, `JobPart`, `JobSubtask` |
| Inventory / stock | `InventoryController` — location tree, bins (`BinContent`, `BinMovement`), lot tracking (`LotRecord`, `LotConsumption`), serials (`SerialNumber`, `SerialHistory`) |
| Machines / stations | `WorkCenter`, `WorkCenterCalendar`, `WorkCenterShift`, `WorkCenterQualification` (`WorkCentersController`) |
| Scheduling / throughput | `SchedulingController` — `POST /api/v1/scheduling/run` and `/simulate` (forward/backward finite-capacity scheduler with priority rules), `MrpController` (MRP planning), `PlanningCyclesController` |
| Shop floor / real-time ops | `ShopFloorController`, `ShopFloorMachineController`, `KanbanCardsController`, `AndonController` (andon alerts), `ScannerController` (barcode/RFID scan-in) |
| Users / workers | `UsersController`, `EmployeesController`, role model: Admin/Manager/OfficeManager/Engineer/ProductionWorker |
| Real-time push | SignalR hubs: `BoardHub`, `TimerHub`, `NotificationHub`, `ChatHub`, `AccountingHub` |

Full controller list (150+) covers far more than needed (accounting, CRM, payroll, EDI, compliance) — irrelevant to the game and should be ignored/stubbed.

## 3. Business logic in forge-ui worth reusing/mirroring

- `forge-ui/src/app/features/shop-floor/` — kiosk clock-in (`shop-floor-clock`), barcode/RFID/scan identification (`scan/inventory-scan.component.ts`, `models/scan-identification.model.ts`, `models/scan-device.model.ts`), shop-floor overview model. Directly reusable UX pattern for a factory-game "operator station."
- `forge-ui/src/app/features/scheduling/` and `kanban/` — visual job-board/kanban logic (drives `BoardHub` for live updates) is the closest existing analog to a factory-game production line view.
- `forge-ui/src/app/features/inventory/` — bin/location tree UI, stock movement patterns.

## 4. Auth story

- ASP.NET Identity + JWT bearer tokens. `POST /api/v1/auth/login`, `GET /api/v1/auth/me`, `POST /api/v1/auth/setup` (first-run bootstrap), `GET /api/v1/auth/status`.
- Also supports SSO (Google/Microsoft/OIDC) and TOTP MFA, plus a separate tiered **kiosk auth** (RFID/NFC/barcode + PIN) for shop-floor terminals — likely the better model for a game's "operator" login than full user auth.
- Authorization is two-layered: `[Authorize(Roles = "...")]` (coarse role check) + `[RequiresCapability("CAP-...")]` (fine-grained capability/module gating, e.g. `CAP-INV-CORE`, `CAP-PLAN-CAPACITY`). A game backend would need its own capability keys or to bypass this system entirely.

## 5. Gaps — what an adapter layer must add for real-time sim state

- **No tick/simulation loop.** Scheduling is a batch "run/simulate" optimizer over static due dates, not a live game clock advancing production progress second-by-second. A game needs a tick service (Hangfire background job or a new lightweight loop) driving Job/WorkCenter state transitions.
- **SignalR hubs exist but are domain-specific** (board updates, timers, notifications) — none broadcast granular sim-state deltas (machine utilization %, WIP position, throughput/sec). A new hub (or reuse `BoardHub` pattern) would be needed for game-loop push.
- **No player/session/economy concepts** — no currency, score, unlock progression, or session/save-state model. All entities are enterprise-audit-oriented (`BaseAuditableEntity`, soft-delete, ActivityLog on every mutation) — heavier than a game needs; an adapter should decide whether to inherit that overhead or bypass it for hot-path game entities.
- **Auth/capability system is enterprise-grade overkill** for a single-player or small-multiplayer factory game; needs a thin adapter (e.g., one "player" role, skip capability checks) rather than adopting the full RBAC surface.
- **No public/game-facing API surface** — every controller assumes an internal ERP client; CORS, rate limiting, and a game-appropriate DTO shape (compact, tick-friendly) don't exist yet.

## Recommended integration approach

Don't build the game inside forge-api. Instead: stand up a **thin adapter/BFF layer** (new minimal API project or a dedicated controller area) that (a) authenticates via the existing kiosk/JWT auth reusing forge's user model, (b) maps Part→Item, BomRevision→Recipe, Job→ProductionOrder, WorkCenter→Machine, BinContent→Stockpile onto simplified game DTOs, and (c) owns the missing pieces itself: a tick loop, a game-state SignalR hub, and any economy/progression model — layered on top of forge's EF Core entities rather than forking them. This reuses forge's real MRP domain logic (BOM explosion, lot/serial tracking, WorkCenter capacity) as the "physics" of the factory sim while keeping game-specific concerns (session state, real-time broadcast cadence, simplified auth) isolated from the enterprise ERP surface.
