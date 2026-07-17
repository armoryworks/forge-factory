# factory adapter (skeleton)

Separate service per adapter-contract-v0.md — **not** a controller area inside
forge-api (B4/D6). Owns the adapter's own forge kiosk credential; the Godot
client never talks to forge-api or Postgres directly (B3).

.NET 10 (only SDK available in this environment; the task allowed .NET 9 but
this host only has 10 installed — see forge-api's own `global.json` for the
same constraint).

## Run

```bash
cd factory/adapter
dotnet run
```

Requires forge-api reachable at `ForgeApi:BaseUrl` (default
`http://127.0.0.1:5000`) and the dev credential in `appsettings.json`
(`GAME-ADAPTER-01` / `4242`) — a dedicated low-privilege Engineer-role kiosk
user created in the local dev DB for this purpose (not a real employee
account). Override via `ForgeApi__Barcode` / `ForgeApi__Pin` env vars for
other environments; don't put a real credential in appsettings.json.

## Endpoints

- `GET /health`
- `POST /cold-load` — fetch Part -> item, BomRevision -> recipe (D6 mapping),
  write `../data/live-import.json` (sibling of this dir). One-shot, cold path
  only — never called by the sim on the tick path.
- `POST /checkpoint` — `{partId, delta, locationId?, reason?}`. Positive delta
  calls forge-api `receive-stock`, negative calls `use-stock`. Stub: callable,
  not wired to a timer. All persistence is forge-api HTTP (B12) — this service
  has no Postgres client and owns no tables.

## Known gaps (see inventory.md for detail)

- WorkCenter (machine) mapping skipped — recipes carry no category/machine
  assignment. Logged, not blocking the slice.
- Only 2 seeded Parts exist in this dev DB and neither had BOM data; one BOM
  line (`RAW-00001` under `ASM-00001`, qty 3) was added live via the API so
  `/cold-load` has real recipe content to emit, not just items.
