# Factory Core Math Spec — v0

Status: draft, Phase 0 discovery. Scope: the simulation kernel's numeric contract.
Non-goals: lore, art, UI chrome, netcode transport.

This document defines **data structures and update equations**. Where a number appears it is a
*calibration default*, not a law; every default is tagged `[CAL]` and belongs in a data table, not
in code.

---

## 0. Design axioms

The whole spec falls out of four commitments. If a later decision contradicts one of these, the
decision is wrong, not the axiom.

1. **The simulation is a pure function of (state, tick).** No wall-clock, no float, no hash-order
   iteration, no uninitialized reads. Same inputs → bit-identical outputs on every machine, forever.
2. **Rates are the game.** The player's mental model is items/second. Every mechanic must be
   expressible as a rate equation the player could, in principle, do on paper.
3. **Scarcity is local, not global.** There is no global item pool. An item exists at a place, and
   moving it costs throughput, space, and attention.
4. **The engine never lies about the bottleneck.** If a line runs at 83% it is because some
   equation says so, and the player can find which one.

---

## 1. Tick / update model

### 1.1 Time base

Time is an integer tick counter. There is no delta-time anywhere in simulation code.

```
TICK_HZ         = 60            [CAL]   simulation ticks per second
Tick            = u64                   monotonic, starts at 0, never resets
```

Rendering interpolates between `tick` and `tick+1` using a render-only alpha; that alpha is
**never** readable from simulation code. Enforce with module boundaries: the sim crate does not
link the clock.

### 1.2 Fixed-point arithmetic

All non-integer quantities are fixed-point. Two formats:

```
Fx32 = i32, Q16.16   scale 2^16 = 65536   — speeds, multipliers, ratios
Fx64 = i64, Q32.32   scale 2^32           — accumulators, fluid volumes
```

Rules:
- Multiply: `(a as i64 * b as i64) >> 16`, **truncating toward zero** (not banker's, not floor —
  pick one and pin it in a test).
- Divide: `((a as i64) << 16) / b as i64`, truncating. Divide-by-zero is a panic in debug, a
  content-validation error at load time — never a runtime branch.
- Never store a float in a component. Floats may exist in the renderer and in offline design tools.

Rationale: f32/f64 are deterministic per-IEEE only if you control rounding mode, FMA contraction,
and library implementations across x86/ARM/wasm. You don't. Fixed-point costs one `>>` and buys
replays, desync-free multiplayer, and cheap state hashing.

### 1.3 Update order

A tick is a sequence of **phases**. Within a phase, entities update in a deterministic order; across
phases there is a hard barrier. Phases are ordered so that each reads a stable snapshot of what it
depends on.

```
tick(w: &mut World) {
    w.tick += 1;
    phase_power(w);        // 1. compute satisfaction ratio for this tick
    phase_machines(w);     // 2. advance crafts, consume inputs, emit outputs
    phase_inserters(w);    // 3. move items between buffers/belts
    phase_belts(w);        // 4. advance transport lines
    phase_fluids(w);       // 5. pressure solve
    phase_logistics(w);    // 6. bots, trains
    phase_environment(w);  // 7. pollution diffuse, enemy AI
    debug_assert_eq!(w.hash(), w.hash());  // no interior mutability leaks
}
```

**Determinism requirements (all are testable, all get a test):**

| Requirement | Mechanism |
|---|---|
| Stable entity order | Dense `Vec` per archetype indexed by generational `EntityId`; iterate by index, never by `HashMap` |
| Stable spatial order | Chunk iteration in fixed `(cy, cx)` raster order; within-chunk by slot index |
| Reproducible randomness | Per-system `SplitMix64` seeded `(world_seed, tick, system_id)`. Never a global RNG. |
| No allocator dependence | No pointer values in logic, no `HashMap` iteration, no `sort_unstable` on non-total keys |
| Parallelism safety | Rayon allowed **only** where the phase is a pure map over disjoint chunks with no cross-chunk writes. Reductions use fixed-order trees, not `sum()`. |
| Save round-trip | `hash(load(save(w))) == hash(w)` |

`World::hash()` is an order-dependent FNV-1a/xxh3 over the raw component arrays. Replays store
`(seed, input_stream)` plus a hash checkpoint every `TICK_HZ * 60` ticks; a mismatch names the tick
and the first differing archetype. This is the single highest-leverage debugging tool in the engine
and it must exist from day one — retrofitting determinism is a rewrite.

### 1.4 Catch-up and slowdown

The sim runs at a fixed rate; the host loop accumulates real time and runs `ceil` ticks, capped:

```
MAX_CATCHUP_TICKS = 8           [CAL]
```

If the sim cannot hit 60 UPS, **the game slows down** — it does not drop ticks and it does not
scale dt. Slow-but-correct beats fast-but-divergent, and a visible UPS counter turns performance
into a legible engineering problem the player can optimize. That is a feature, not a failure mode
(see §7).

---

## 2. Item / recipe graph formalism

### 2.1 Objects

```
ItemId    = u16          // discrete, integer counts
FluidId   = u16          // continuous, Fx64 volume
RecipeId  = u16
```

Items and fluids are separate kinds because they have different conservation and transport laws.
A `Stack = (ItemId, u32)`. A `FluidParcel = (FluidId, Fx64 volume, Fx32 temperature)`.

### 2.2 The recipe graph

The production graph `G = (I ∪ R, E)` is a **directed bipartite multigraph**:

- `I` = item/fluid nodes, `R` = recipe nodes.
- Edge `(i → r)` with weight `a_{i,r} > 0`: recipe `r` consumes `a` of `i` per craft.
- Edge `(r → i)` with weight `b_{r,i} > 0`: recipe `r` produces `b` of `i` per craft.

```rust
struct Recipe {
    inputs:   SmallVec<[(ItemId, u16); 6]>,
    outputs:  SmallVec<[(ItemId, u16); 4]>,
    duration: u32,          // ticks at speed 1.0
    category: CategoryId,   // which machine kinds may run it
    flags:    RecipeFlags,  // PRODUCTIVITY_ALLOWED, HAND_CRAFTABLE, ...
}
```

### 2.3 Stoichiometry matrix

Let `S ∈ Z^{|I| × |R|}` with

```
S[i][r] = b_{r,i} − a_{i,r}          (net units of i per craft of r)
```

`S` is the object almost every interesting query reduces to:

- **Feasibility of a target `d`** (net output vector, e.g. "1 rocket/s"): does `∃ x ≥ 0` with
  `S·x = d` on intermediates and `S·x ≥ d` on the target? Solve as an LP (§3.4).
- **Cycle detection**: recipe loops (plastic→…→plastic, uranium enrichment) make `G` cyclic. The
  graph is *not* a DAG and any algorithm assuming so is a bug. Cycles are legal iff the loop's
  net-gain matrix has spectral radius `< 1` for at least one item, i.e. the loop cannot manufacture
  matter from nothing. **Validate at content-load time**, not at runtime.
- **Raw-cost basis**: with `Raw ⊂ I` the extractables, the raw cost of item `i` is the LP
  `min Σ_{j∈Raw} c_j·(S·x)_j⁻` s.t. `S·x = e_i`, `x ≥ 0`. This is the only defensible definition of
  "what does this cost" once cycles and byproducts exist — naive DAG recursion silently
  double-counts byproducts and infinite-loops on enrichment.

### 2.4 Content validation (load-time, hard fail)

1. Every recipe's inputs and outputs reference live ids.
2. `duration ≥ 1`.
3. Every non-raw item is reachable from `Raw` (no orphan intermediates).
4. Every cycle passes the spectral-radius test above.
5. Item stack sizes and fluid capacities are non-zero.
6. Every recipe is unlockable by some tech path from the starting tech set (no dead content).

Failures name the offending recipe by string id. Modders and designers hit this hourly; the error
message quality *is* the API.

---

## 3. Throughput and ratio math

This section is the heart of the spec. Everything the player optimizes lives here.

### 3.1 Machine rate

A machine has a **speed multiplier** `σ` (Fx32) and runs recipe `r` with duration `d_r` ticks.

```
σ = clamp( σ_base · (1 + Σ modules.speed + Σ beacons.speed·beacon_efficiency) , σ_min, ∞ )
σ_min = 0.2·σ_base      [CAL]   speed cannot be reduced below 20% by penalties
```

Crafting advances an integer accumulator — **no float, no dt**:

```
progress: u32                       // units of 1/65536 of a craft-tick
GOAL(r)  = d_r << 16

each tick, if inputs_reserved && !output_blocked && power_ok:
    progress += (σ · power_satisfaction) >> 16
    if progress >= GOAL(r):
        progress -= GOAL(r)         // carry the remainder — never reset to 0
        emit_outputs()
        try_reserve_inputs()
```

Carrying the remainder matters: resetting to zero loses up to one tick per craft, which at
`d_r = 0.5s` is a **3.3% throughput error** — enough that a player's hand-computed ratio disagrees
with the game, which is exactly the trust violation axiom 4 forbids.

**Machine rate:**

```
R_machine(r) = σ / d_r          crafts per tick
             = 60·σ / d_r       crafts per second        [TICK_HZ = 60]
```

**Item flow through one machine:**

```
consumption(i) = a_{i,r} · R_machine(r)      items/s
production(i)  = b_{r,i} · R_machine(r) · (1 + ρ)
```

where `ρ` is the productivity bonus. Productivity is applied as an integer-safe accumulator, not a
multiply — you cannot emit 1.4 gears:

```
prod_acc: Fx32                  // per machine, per output slot
on craft complete:
    prod_acc += ρ
    bonus = prod_acc >> 16      // whole extra crafts earned
    prod_acc -= bonus << 16
    emit(base_outputs · (1 + bonus))
```

This makes productivity exactly `ρ` in the long run with bounded error `< 1` item, and it is
deterministic. `ρ` is gated by `RecipeFlags::PRODUCTIVITY_ALLOWED` — productivity on an
intermediate is a free-matter cheat if the item has a raw-equivalent shortcut path, which is why
the flag exists and why §2.4's cycle test must run with `ρ_max` applied.

### 3.2 Ratio math (the player-facing core)

To sustain a demand of `T` items/s of item `i` from recipe `r`:

```
n = ceil( T / ( b_{r,i} · R_machine(r) · (1+ρ) ) )        machines
```

and that pulls, for each input `j`:

```
T_j = a_{j,r} · n · R_machine(r)          items/s upstream demand
```

Recursing this is the whole factory-planning game. **Worked example** `[CAL]`:

| Step | Recipe | `d` | `σ` | `R = 60σ/d` | Demand | `n = ceil(·)` | Slack |
|---|---|---|---|---|---|---|---|
| Gears | 2 plate → 1 gear | 30t | 1.25 | 2.5/s | 5 gear/s | 2 | 0% |
| Plates | 1 ore → 1 plate | 192t | 2.0 | 0.625/s | 10 plate/s | 16 | 0% |
| Ore | — | — | — | 0.55/s | 10 ore/s | 19 | 4.5% |

The gear→plate leg is clean; the ore leg is not, and `ceil` leaves 4.5% of a miner idle. **This
gap is the game.** See §7.1 — the design goal is that most ratios are *not* integers.

### 3.3 Belt throughput

A belt is modelled as **compressed runs on a lane**, not per-item entities. Two lanes per belt.

```
v_belt   : Fx32     tiles per tick
s_item   = 0.25     tiles between item centres  [CAL]
Θ_lane   = v_belt / s_item          items/tick
Θ_belt   = 2 · Θ_lane               items/tick  (both lanes)
```

`[CAL]` tiers, chosen so ratios against machine rates are mostly awkward on purpose:

| Tier | `v` (tiles/s) | `Θ_lane` (items/s) | `Θ_belt` (items/s) |
|---|---|---|---|
| I | 1.875 | 7.5 | 15 |
| II | 3.75 | 15 | 30 |
| III | 5.625 | 22.5 | 45 |

Belt state is `Vec<Run { head_pos: Fx32, len: u16, item: ItemId }>` per lane. Advance:

```
for run in lane.runs (front to back):
    gap  = (next_run.tail_pos - s_item) - run.head_pos     // ∞ for the front run
    step = min(v_belt, gap)
    run.head_pos += step
```

O(runs), not O(items). A saturated belt is **one run** — the common case is the cheap case, which
is what makes megabase-scale belt counts tractable.

**Inserter throughput** (the usual real bottleneck, and the one players most often mis-model):

```
Θ_ins = swing_capacity / (t_swing + t_pickup + t_drop)      items/s
```

`swing_capacity` is stack size ≤ tech-granted bonus. Because `t_swing` depends on arc length and
the source may be moving, `Θ_ins` is **position-dependent**: chest→chest ≠ belt→machine. The engine
computes this exactly; the UI exposes measured throughput per inserter. No hidden fudge factors.

### 3.4 Whole-factory throughput (LP)

Given machine counts `x ∈ R^{|R|}_{≥0}` (as *rates*, so fractional is meaningful — `x_r = 2.5`
means 2.5 machine-equivalents of work), sustainable operation requires:

```
maximize    c^T · (S·x)                       // c weights the target output
subject to  (S·x)_i ≥ 0        ∀ i ∉ Raw      // no intermediate deficit
            (S·x)_i ≥ −E_i     ∀ i ∈ Raw      // extraction capacity
            A·x     ≤ Θ         		  // transport capacity per link
            x       ≥ 0
```

The dual gives the **shadow price** of each constraint — literally "how much more rocket/s per
extra belt here". This is the correct backend for an in-game production planner, and the dual
prices are the honest answer to "what is my bottleneck?" Ship the solver; do not ship a heuristic
that guesses.

The LP is a *design-time and planner-time* tool. The live sim does **not** solve LPs — it runs the
local rules in §3.1–3.3 and the LP optimum emerges. Any divergence between LP prediction and
observed sim throughput is a bug in one of them, and that discrepancy is a superb automated test:
build the LP-optimal layout in a headless world, run 10k ticks, assert measured ≈ predicted within
the `ceil`/latency slack.

---

## 4. Buffering and backpressure

### 4.1 Buffer

```rust
struct Buffer {
    slots: SmallVec<[(ItemId, u32); 8]>,
    cap:   u32,       // per-slot, = stack_size · slot_count
}
```

Every buffer is **finite**. There is no infinite sink, no void chest in the core rules. Finiteness
is what makes backpressure a real force rather than a decoration.

### 4.2 Reservation protocol

Two-phase, to keep the tick order-independent within a phase:

```
try_reserve_inputs(m):                       // phase_machines, before advancing
    if ∀ (i,a) ∈ r.inputs: m.in_buf[i].avail ≥ a:
        ∀ (i,a): m.in_buf[i].reserved += a
        m.state = Crafting
    else:
        m.state = Starved(missing_item)      // recorded, surfaced in UI

emit_outputs(m):
    if ∀ (i,b) ∈ r.outputs: m.out_buf[i].free ≥ b·(1+bonus):
        commit
    else:
        m.state = Blocked(full_item)         // progress stays at GOAL, holds
```

A blocked machine **holds completed output** — it does not discard it and does not keep crafting.
Its `progress` remains at `GOAL` so it resumes on the very next tick space clears, with zero
restart penalty. Restart penalties would make backpressure lossy and break §3.4's LP equivalence.

`Starved` / `Blocked` are the two states the whole UX of debugging a factory rests on (§7.2).

### 4.3 Backpressure propagation

Backpressure is **not** a separate system. It is the emergent consequence of:
finite buffers → `Blocked` machines → belts stop consuming → belt runs compress → upstream
inserters find no free slot → upstream machines `Blocked`.

The propagation is one tick per hop, and that latency is *correct* — real factories have exactly
this lag. Do not add a global "is downstream full" query; it destroys locality (axiom 3), it is
O(graph) per tick, and it removes the ripple the player learns to read.

### 4.4 Steady-state and buffer sizing (Little's Law)

For a line at steady state with throughput `λ` items/s and buffer occupancy `L`:

```
L = λ · W                       W = mean residence time
```

Practical consequence: a buffer of `L` items absorbs a producer outage of

```
t_absorb = L / λ_downstream     seconds
```

Buffers **do not increase steady-state throughput** — a claim players relentlessly get wrong, and
the game should let them be wrong and then discover it. They only buy variance absorption. The
correct buffer size is set by the largest expected supply gap (train interval, ore-patch swap):

```
L* = λ · t_gap_max · safety      safety = 1.25   [CAL]
```

Oversized buffers are actively bad: they cost `t_fill = L/λ` seconds of startup before the line
reaches steady state, and they hide the starvation signal the player needs to diagnose §7.2. The
engine should never auto-size a buffer.

### 4.5 The bottleneck equation

For a chain of stages `k = 1..n`, each with capacity `C_k` (machines, belt, or inserter limited):

```
Θ_chain = min_k C_k
```

and stage `k` runs at utilization `u_k = Θ_chain / C_k`. The UI surfaces `u_k`; `argmin_k C_k` is
the bottleneck. This is trivial math — the engineering work is *measuring* `C_k` honestly, per
stage, including the inserter and belt terms that players forget.

---

## 5. Progression curve math

### 5.1 Tech gating

```rust
struct Tech {
    cost:        SmallVec<[(ItemId, u32); 8]>,   // packs required
    time:        u32,                            // ticks per unit at speed 1.0
    prereqs:     SmallVec<[TechId; 4]>,
    unlocks:     SmallVec<[RecipeId; 8]>,
    infinite:    Option<InfiniteSpec>,
}
```

Tech DAG. A tech consuming pack types `P` at lab count `n_lab` completes `count` units in:

```
T_research = count · max_{p ∈ P} ( 1 / Θ_p )        seconds
```

The `max` is the point: research is gated by the **scarcest pack**, so adding a new pack type to a
tier is a hard step change in required factory breadth, not a smooth cost bump. Pack tiers are the
game's chapter breaks.

### 5.2 Cost scaling and the pacing invariant

This is the single most important equation in the progression design.

Let tech tier `t`. Cost grows geometrically, and the player's pack throughput also grows
geometrically as they build out:

```
C(t) = C₀ · α^t          α = 1.6      [CAL]  cost multiplier per tier
P(t) = P₀ · β^t          β = 1.5      [CAL]  realized throughput growth per tier
```

Then time-per-tier:

```
T(t) = C(t) / P(t) = (C₀/P₀) · (α/β)^t
```

**Everything about pacing is the ratio `α/β`:**

| Regime | Feel |
|---|---|
| `α/β < 1` | Accelerating. Late game collapses; tiers fall in seconds. Trivializing. |
| `α/β = 1` | Flat. Every tier costs the same wall-clock. Safe, and a bit lifeless. |
| `α/β > 1` | Each tier takes longer. Pressure builds. **Grind if the player can't respond.** |

Target `α/β ≈ 1.067` (`1.6/1.5`) `[CAL]`. Each tier is ~7% longer *if the player builds nothing
new*. The gap is deliberately payable by **optimization, not patience**: a player who re-ratios a
line, adds a beacon row, or switches to a better recipe recovers far more than 7%. That is the
hardcore bargain — the curve pulls ahead of you at a rate that engineering, and only engineering,
closes. Idling is a slow loss; thinking is a fast win.

`β` is not a free parameter — it is *measured*, not decreed. Instrument playtests for realized
`P(t)` and fit `β`. If measured `β` drifts below design `β`, the curve is a grind and `α` must come
down. **`α/β` is the number to put on the dashboard and watch every build.**

### 5.3 Infinite techs

Post-DAG, infinite techs absorb unbounded production:

```
C_∞(k) = C₁ · γ^k       γ = 2.0    [CAL]   k = level, 1-indexed
```

with a **linear** effect (e.g. `+10% mining productivity` per level `[CAL]`). Exponential cost,
linear benefit ⇒ level `k` takes `∝ γ^k` and returns `∝ 1`. The marginal return per second
decays as `γ^-k`, so there is no wall and no completion — the factory can always grow, and the
player chooses when the return stops being worth it. `Σ_k 1/C_∞(k)` converges, so total achievable
benefit per unit of lifetime production is bounded: no runaway.

### 5.4 Recipe cost curves

Within a tier, keep raw-cost (§2.3) growth *sub*-exponential:

```
cost_raw(item at depth k) ≈ cost₀ · k^1.8       [CAL]
```

Polynomial-in-depth, not exponential. Exponential depth cost forces exponential factory size for
linear tech progress, and the factory stops fitting on the map/UPS budget before it stops being
interesting. Depth should cost *complexity* (more distinct intermediates, more logistics) rather
than raw *tonnage*. Tonnage is a UPS bill; complexity is gameplay.

### 5.5 Power

```
P_demand(tick) = Σ_machines draw_i           // machines Crafting, plus idle draw
P_supply(tick) = Σ_generators cap_j
satisfaction   = min(1, P_supply / P_demand)         // Fx32
```

Every machine's `σ` scales by `satisfaction` (§3.1). This is a **global** coupling and the only one
in the spec — it deliberately violates axiom 3's locality, which is exactly why brownouts feel
systemic and terrifying: one under-built power block slows the entire base, including the miners
feeding the boilers. That death spiral (low power → less coal → less power) is a real, correct
consequence of the equation, it is recoverable by design (accumulators, manual coal), and it is
one of the best teaching moments the genre has. Do not damp it.

Compute `satisfaction` in `phase_power` from **last tick's** demand to avoid a circular dependency
within the tick. The one-tick lag is imperceptible (16ms) and keeps the phase barrier clean.

---

## 6. Fluids

Fluids need their own model; treating them as items with a decimal count is the classic mistake and
produces either free teleportation or unexplainable throughput cliffs.

Pipes form segments; a segment is a connected run of pipes solved as one unit.

```
Segment { volume: Fx64, capacity: Fx64, fluid: Option<FluidId>, temp: Fx32 }
```

Flow between adjacent segments `A → B` per tick:

```
Δ = k_flow · (fill(A) − fill(B)) · capacity_link         fill(X) = X.volume / X.capacity
k_flow = 0.4        [CAL]
Δ = clamp(Δ, −Θ_pipe_max, +Θ_pipe_max)
```

This is a discretized diffusion; it is **stable iff `k_flow < 0.5`** for the two-segment case, and
the stability bound must be re-derived (and asserted at load) if segment fan-out exceeds 2 — an
unstable solve oscillates and looks like a haunted pipe.

Consequences, all intentional:
- Throughput **falls with segment length** (`Θ ≈ Θ_max / (1 + L/L₀)`, `L₀ = 17` `[CAL]`) because
  the gradient flattens as it spreads. Long pipe runs are bad. Pumps re-establish the gradient.
- Fluid transport is therefore a genuine engineering constraint with a real design language:
  pumps, parallel runs, or convert to a solid/barrel and belt it.
- Temperature mixes by mass: `T' = (V_A·T_A + V_B·T_B)/(V_A+V_B)`. Heat is not free.

---

## 7. What "hardcore" means, mechanically

"Hardcore" is not damage numbers or scarce ammo. It is a set of falsifiable commitments about the
mechanics. Each of the following is a *constraint on the engine*, and each is testable.

### 7.1 The ratios are not integers

`R_machine = 60σ/d`. Choose `d` and `σ` `[CAL]` so that most production ratios are irrational-ish
in practice — 4.7 smelters, not 5. The player must then choose, every time:

- **round up** → capital cost, idle machines, over-built power;
- **round down** → an under-fed downstream line and a slack you must *decide* is acceptable;
- **re-architect** → beacons/modules to shift `σ` until the ratio lands clean.

This trilemma, repeated at every node of §2.2's graph, is the primary decision loop of the game. If
ratios came out integral, there would be one right answer and no game. **This is the single
highest-leverage `[CAL]` decision in the spec** — tune `d` values against the ratio table, not for
narrative tidiness.

### 7.2 No hand-holding, but total observability

Anti-goals — the engine must never do these:
- auto-balance a belt, auto-size a buffer, auto-insert items;
- suggest a build, mark a "correct" ratio in the recipe UI, or highlight the answer;
- quest markers, forced tutorials, or objective arrows.

Hard requirements — the engine must always do these:
- expose exact `Starved(item)` / `Blocked(item)` / `NoPower` state per machine (§4.2);
- expose measured throughput per belt segment, inserter, and machine over a rolling window;
- expose the recipe graph and let the player query it;
- never round or prettify a displayed number without saying so.

The distinction is deliberate and it is the whole philosophy: **the game gives you perfect
instruments and zero answers.** Difficulty must come from the problem being genuinely hard, never
from the game hiding information. Hidden information produces guessing, and guessing is not
engineering. A player who reads the instruments correctly should be able to derive the bottleneck
with certainty — and should have to.

### 7.3 Real bottleneck math

Every stage's capacity `C_k` is a real, computed number (§4.5), including the terms players want to
ignore: inserter swing time, belt compression, pipe length, power satisfaction, and `ceil` slack. No
fudge factor makes a nearly-right factory work. A line at 97% is at 97%, it will back up, and the
game will show you exactly where.

### 7.4 Logistics as a first-class cost

Because scarcity is local (axiom 3), moving an item always costs something:

| Mode | Throughput | Latency | Cost |
|---|---|---|---|
| Belt | `Θ_belt` (§3.3), distance-independent | `L / v` | Land, and it *blocks*. Belts are walls. |
| Inserter | `Θ_ins`, position-dependent | swing | Power, and it is usually the real cap |
| Bot | `n_bots · payload / round_trip` | distance | Power ∝ flight; charging is a contended resource |
| Train | `cars · 40 stacks / cycle` | huge | Signalling, deadlock risk, station real estate |
| Pipe | falls with `L` (§6) | pressure-limited | Pumps, or barrel it |

There is no mode with high throughput, low latency, and low cost. **Every logistics decision is a
real trade-off** with no dominant strategy, and the crossover points between modes are computable
from this table — which is exactly the calculation a hardcore player should be doing.

### 7.5 Feedback loops with teeth

- **Pollution**: `pollution ∝ Σ machine draw`, diffuses on the chunk grid, and provokes attacks
  scaled to absorbed pollution. Growth is thus *self-punishing*; efficiency is a survival strategy,
  not a score.
- **Ore depletion**: patches are finite. Every build has a half-life and the base must migrate.
  Permanence is not on offer.
- **UPS**: the sim slows rather than diverges (§1.4). At megabase scale UPS becomes the binding
  constraint and the player is now optimizing the *simulation*, not just the factory. Exposing UPS
  and per-system cost turns the engine's own performance into the final tier of gameplay. Lean into
  it: it is the genre's true endgame, and it is only legible because §1 refused to cheat.

---

## 8. Open questions for Phase 1

1. **`α`, `β`, `γ` need playtest fits, not armchair values.** `β` especially — it is measured. Until
   there is a real fit, §5.2 is a shape, not a calibration.
2. Rounding mode for Fx32 multiply: truncate-toward-zero vs round-half-even. Truncate is faster and
   simpler; round-half-even has less systematic bias in long accumulator chains. Needs a numeric
   error budget over a 10⁷-tick run before it is pinned.
3. Fluid model: is the §6 diffusion solve good enough at 10k segments, or does it need segment
   merging / a sparse linear solve? Profile before deciding — this is the likeliest UPS cliff.
4. Is the LP planner (§3.4) an in-game tool, an out-of-game tool, or a tech-gated unlock? Shipping
   it in-game from tick 0 arguably violates §7.2's "zero answers."
5. Multiplayer: lockstep (cheap, needs §1's determinism, which we have) vs authoritative server
   (costly, tolerates desync). Determinism is the enabler either way — decision deferred, cost
   already paid.
6. Does `σ_min` (§3.1) need to exist at all, or should efficiency modules be allowed to stall a
   machine to zero?

---

## Appendix A: constants to calibrate

| Symbol | Meaning | v0 default | Confidence |
|---|---|---|---|
| `TICK_HZ` | sim rate | 60 | High — genre standard |
| `s_item` | belt item spacing | 0.25 tiles | High |
| `Θ_belt` tiers | belt throughput | 15 / 30 / 45 /s | Med |
| `α` | tech cost growth | 1.6 | **Low — needs fit** |
| `β` | throughput growth | 1.5 | **Low — must be measured** |
| `γ` | infinite tech growth | 2.0 | Med |
| `k_flow` | pipe diffusion rate | 0.4 | Med — stability-bounded < 0.5 |
| `L₀` | pipe length constant | 17 tiles | Low |
| `σ_min` | min speed multiplier | 0.2·base | Low |
| depth exponent | raw cost vs depth | 1.8 | Low |
