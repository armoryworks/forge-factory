# Golden test vector v0 — acceptance contract for the sim core

Data: **[`../data/golden-v0.json`](../data/golden-v0.json)** (generated — do not hand-edit).
Generator: [`../tools/refsim_v0.py`](../tools/refsim_v0.py). Content: `recipes-v0.toml`.
Spec: [`factory-math-v0.md`](./factory-math-v0.md) §1.2, §1.5, §3.1, §4.

**What this is for:** the sim core must reproduce these hashes and state values exactly — in
GDScript today (D4/D5), in C#/GDExtension if B7's fixed-point hot path forces the escape hatch, or
in Bevy if the fallback ever fires. It is host-independent by construction: the reference
implementation is Python precisely *because* Python is not a candidate host, so the vector cannot
smuggle in a host-specific behaviour. Any implementation matching these hashes has fixed-point,
remainder carry, reservation, and backpressure right; any that doesn't has a bug the vector
localises to a tick. That makes it the acceptance test B7 can be *decided* against rather than
argued about.

**Unaffected by B20/D13:** this vector ships as **JSON**, which Godot parses natively. Only the
authored *content* file (`recipes-v0.toml`) needs the D13 build-step transform.

## 1. The vector

Two scenarios over the §3.2 reference build (19 miners / 16 furnaces / 2 assemblers), checkpointed
at ticks **0, 600, 6000, 12000**.

**`steady`** — gear buffer effectively unbounded. Tests rate math and remainder carry.

| tick | hash | ore | plate | gear | states |
|---|---|---|---|---|---|
| 0 | `0x1350a7b63257f785` | 0 | 0 | 0 | all Idle |
| 600 | `0x03d2aeb8532c9f52` | 12 | 1 | 31 | all Crafting |
| 6000 | `0x77e025fb20fdc184` | 41 | 3 | 481 | all Crafting |
| 12000 | `0xebcf45673b3806ab` | 87 | 2 | 981 | all Crafting |

**`backpressure`** — gear buffer capped at 50. Tests the §4.3 ripple.

| tick | hash | ore | plate | gear | states |
|---|---|---|---|---|---|
| 0 | `0x373b942d1c066d2c` | 0 | 0 | 0 | all Idle |
| 600 | `0xdf780706178d4643` | 12 | 1 | 31 | all Crafting |
| 6000 | `0x5f57059e6bd6cb02` | 706 | 200 | 50 | miners Crafting, furnaces+assemblers **Blocked** |
| 12000 | `0x308da25ff5eb64d4` | 950 | 200 | 50 | **all Blocked**, every buffer full |

The 12000 checkpoint exists because at 6000 the ripple has *not* yet reached the miners — the ore
buffer is still filling (706/950). Stopping at 6000 would have left a fully-propagated stall
untested, which is most of the point of the scenario.

## 2. Verification against theory

The vector is checked against the closed-form math, not merely recorded:

- **Gear throughput, 900s steady window (t=6000→60000): measured `5.000000/s`, theory `5.000000/s`,
  error 0.0000%.** This is §3.2's reference build reproducing its own prediction, and it is the
  §3.4 sim-vs-LP equivalence check at its smallest useful size.
- **Remainder carry:** 19 miners produce `10.449768/s`. An implementation that reset `progress` to
  0 instead of carrying would produce `10.363636/s` — **0.82% low**, exactly the loss §3.1 predicts.
  A regression here changes the tick-6000 hash, so the vector catches it.
- **Startup transient:** `steady` reaches 481 gears at t=6000 rather than 500, because the chain
  takes ≈230 ticks to fill (110 ore + 96 plate + 24 gear). `5/s × (100s − 3.83s) = 481`. ✓ The
  deficit is pipeline fill, not a rate error — confirmed by the 6000→12000 window running at
  exactly 5.00/s with no transient.
- **Ore slack made visible:** the `steady` ore buffer grows ~0.45/s. That is the 4.30% `ceil` slack
  from §3.2 accumulating as physical surplus — the ratio trilemma showing up as a number.

## 3. What this fixture deliberately does NOT model

Stated plainly because a green golden must not be read as more coverage than it is:

- **No belts, no inserters.** Machines emit directly into shared buffers. §3.3's belt-run positions
  and inserter swing timing are not pinned tightly enough in v0 to golden; doing so would freeze
  numbers we expect to move. **The `Θ_belt` / `Θ_ins` math is therefore untested by this vector.**
- **Shared buffers are a fixture abstraction**, and they knowingly violate axiom 3 (scarcity is
  local). They stand in for transport so the vector can isolate §3.1/§4. Not a model of the game.
- **Power satisfaction is pinned at 1.0.** The `fx_mul(σ, satisfaction)` path executes, so the
  helper is covered, but brownout behaviour (§5.5) is not.
- **No fluids, no pollution, no tech.** Out of scope for v0.
- Buffer `cap` **is** hashed alongside `count`: capacity is simulation state (it changes how the
  world evolves), so a cap divergence must be a hash mismatch rather than a silent one. This is why
  the two scenarios differ at tick 0 despite identical machine state.

## 4. How to use it

Load `recipes-v0.toml`, build the reference build, run N ticks, hash per §1.5, compare. A mismatch
at the first checkpoint is almost always the hash encoding (field order / endianness / width), not
the sim; a mismatch that appears only at 6000 or 12000 is the sim.

Regenerate with `python3 tools/refsim_v0.py` (deterministic — byte-identical output across runs,
verified). Any `[CAL]` change to `recipes-v0.toml` invalidates every hash here and the vector must
be regenerated in the same commit as the content change.
