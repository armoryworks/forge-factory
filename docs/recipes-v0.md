# v0 Recipe Set — rationale and validation proof

Canonical data: **[`../data/recipes-v0.toml`](../data/recipes-v0.toml)**. That file is the source of
truth; this document explains it and proves it loads. Spec: [`factory-math-v0.md`](./factory-math-v0.md).

TOML is the **authored source of truth**, and — after D14 — also what the engine reads.

An earlier draft of this doc said the set was "parsed by the sim core with `serde` + `toml`". That
was wrong and is logged as **B20**: `serde` is a Rust crate, and the then-locked Godot 4.7 (**D4**)
ships no TOML parser — only JSON and `ConfigFile` (INI-like, not TOML). The record is kept because
it is why the next two decisions exist.

**D13** resolved it with a TOML->JSON build step. **D14 then superseded D13**: the sim core host
is C# (B7), and the C# core parses this TOML directly via `sim/Forge.Sim/Content.cs`. There is no
GDScript consumer of content data, so the build step went to zero and has been removed.

`recipes-v0.toml` is therefore both the authored source AND what the engine reads. The loader reads
it key-by-key and runs the §2.4 rules at load; see `Content.cs`. Note it does not use a NuGet TOML
parser -- see `MiniToml.cs` for why, and for the limits of what that reader accepts.

Chosen over JSON-as-authored (no comments -- the derivations have to live beside the numbers) and
over RON (content is edited by designers, not only programmers).

---

## 1. Scope

One chain, three recipes:

```
iron-ore  --(smelt, 1:1)-->  iron-plate  --(craft, 2:1)-->  iron-gear
   ^
   mine (extraction)
```

This is the §3.2 worked example promoted to real content. It is deliberately the smallest set that
still exercises every mechanic the vertical slice needs to prove:

| Mechanic | How this set exercises it |
|---|---|
| Machine rate (§3.1) | Three distinct `(d, σ)` pairs spanning 0.55–2.5 crafts/s |
| Remainder carry (§3.1) | `mine-iron-ore` has a non-integer craft period — see §2 |
| Fx32 truncation (§1.2) | `σ = 0.55` is unrepresentable; the bias is real and measured |
| Ratio trilemma (§7.1) | The ore leg's `ceil` leaves 4.3% slack — a real decision, at N=3 recipes |
| Buffers + backpressure (§4) | Stall gear output → `Blocked` ripples back through all three stages |
| Belt cap (§3.3) | 10 ore/s exceeds one lane (7.5/s) but not one belt (15/s) |

Three recipes is enough because backpressure needs ≥2 hops to show a ripple and the ratio trilemma
needs ≥1 inexact rate. Both are satisfied. Adding a fourth recipe would add tuning surface, not
coverage.

## 2. Why `mine-iron-ore` is deliberately inexact

`σ = 0.55` in Q16.16 is `0.55 × 65536 = 36044.8`, which truncates to **36044** under the rounding
mode pinned in §1.2. So:

```
GOAL        = 60 << 16          = 3_932_160
σ_raw       = 36_044
craft period = GOAL / σ_raw     = 109.0933... ticks     (not an integer)
R           = 60 · σ_raw / GOAL = 0.549988 crafts/s     (nominal 0.55; bias −0.0022%)
```

First craft lands at tick 110 with `36_044 × 110 − 3_932_160 = 32_680` carried forward. If the
engine reset `progress` to 0 instead of carrying, this recipe would run at `60/110 = 0.5454/s` — a
**0.82% throughput loss** against a spec that promises 0.549988. The v0 set therefore fails loudly
if §3.1's carry is ever regressed, which is the point of including it.

The other two recipes divide evenly (`192<<16 / 131072 = 96` ticks; `30<<16 / 81920 = 24` ticks), so
they pin the exact-rate path. One inexact, two exact: both branches covered.

## 3. Reference build — 5 gear/s

Applying §3.2's `n = ceil(T / (b · R))` down the chain:

| Stage | Recipe | `d` | `σ` | `R` (crafts/s) | Demand | `n` | Capacity | `u` | Slack |
|---|---|---|---|---|---|---|---|---|---|
| Gears | 2 plate → 1 gear | 30 | 1.25 | 2.5 | 5 gear/s | **2** | 5.0/s | 100% | 0% |
| Plates | 1 ore → 1 plate | 192 | 2.0 | 0.625 | 10 plate/s | **16** | 10.0/s | 100% | 0% |
| Ore | → 1 ore | 60 | 0.55 | 0.549988 | 10 ore/s | **19** | 10.449768/s | 95.70% | **4.30%** |

Upstream demand propagates as `T_j = a_{j,r} · n · R`: 2 assemblers × 2.5/s × 2 plate = 10 plate/s;
16 furnaces × 0.625/s × 1 ore = 10 ore/s.

The ore leg is the interesting one. `10 / 0.549988 = 18.18` miners, so `ceil` → 19, and the 19th
miner runs at 18% duty. The player's three options from §7.1, all live at N=3 recipes:

- **round up** (19 miners): 4.3% of mining capital idles;
- **round down** (18 miners): capacity 9.90/s, gears drop to 4.95/s — you decide 1% is acceptable;
- **re-architect**: shift `σ` (a speed module on the miners) until the ratio lands clean.

**Acceptance test for the slice:** build this in a headless world, run 10k ticks, assert measured
gear output is 5.0/s ± the stated slack and that per-stage `u` matches the table. A divergence is a
bug in §3.1, §3.3, or §4 — not a tolerance to widen. This is the LP-vs-sim equivalence check of
§3.4 at its smallest useful size.

## 4. Validation proof (spec §2.4)

All six load-time rules, checked against `recipes-v0.toml`:

| # | Rule | Status |
|---|---|---|
| 1 | Inputs/outputs reference live ids | **Pass** — items 0,1,2 declared; recipes reference only those |
| 2 | `duration ≥ 1` | **Pass** — 60, 192, 30 |
| 3 | Every non-raw item reachable from `Raw` | **Pass** — `Raw = {iron-ore}`; plate ← ore (r1), gear ← plate (r2) |
| 4 | Every cycle passes the spectral-radius test | **Pass, vacuously** — the graph is a DAG; no cycles to test |
| 5 | Stack sizes / capacities non-zero | **Pass** — 50, 100, 100 |
| 6 | Every recipe unlockable from the start tech set | **Pass** — tech `start` unlocks [0,1,2]; no dead content |

Rule 4 passing vacuously is a known v0 gap, not an oversight: the DAG case cannot exercise the
cycle validator, so **the spectral-radius check is untested by this content set**. It needs either a
unit test with a synthetic cyclic fixture or a v1 recipe set containing a real loop (enrichment,
byproduct return). Flagging for the inventory as **`cycle-validator-untested`** — v0 content cannot
cover it. `Content.Validate` *detects* cycles and fails naming the loop, but it does not implement
the spectral-radius test; cyclic content is **rejected** rather than validated, which is the safe
direction until the real test exists.

The rules are enforced by `sim/Forge.Sim/Content.cs` at load, and negative tests in
`GoldenTests.cs` assert that R1 and R6 actually reject — a validator never shown a violation is not
a verified validator. Writing those tests found a real bug: a dangling item id crashed the R4 cycle
walk instead of reporting R1, burying the actionable error. §2.4 says the error message quality *is*
the API, so unknown ids are now skipped in the R4 walk and R1 reports them.

## 5. Notes for later

- **D6 / MRP seeding (deferred, not now):** this set is hand-written, but the shape is deliberately
  the same as forge MRP data — `Part` → item, `BomRevision` → recipe, BOM line quantity → `a_{i,r}`.
  A cold-path importer can generate content in this format later. Two mismatches to resolve when
  that happens: MRP BOMs carry no `duration`/`category` (routing data, if it exists, would have to
  supply them), and real BOMs *do* contain cycles (rework/regrind loops), which is exactly the
  content that would exercise validation rule 4.
- `σ = 0.55` for the miner is `[CAL]` and low-confidence — chosen to reproduce §3.2's ~0.55/s while
  landing inexact in Q16.16. If it moves, the §3 table and the spec's §3.2 table move with it.
