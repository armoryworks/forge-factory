# Transport Math v0 — belts, lanes, splitters

Status: spec, implementation-ready. Expands [`factory-math-v0.md`](./factory-math-v0.md) §3.3 into
the full deterministic model. Where this doc and §3.3 disagree, **this doc wins** — §3.3 now points
here.

Closes the transport half of **B24** (`golden-excludes-transport`): the golden vector covered the
craft chain only, so belt math had no gate. It also gives `beltDeltas` (B45) a real producer.

Everything here obeys the same non-negotiables as the craft core: integer fixed-point only (§1.2,
truncate toward zero), deterministic iteration order (§1.3), and belt state feeds `World::hash()`
(§1.5). If a rule below cannot be stated as an integer equation with a fixed evaluation order, it is
the wrong rule.

---

## 0. Correction to §3.3

§3.3's advance sketch says the front run's gap is `∞`:

```
gap = (next_run.tail_pos - s_item) - run.head_pos     // ∞ for the front run   <-- WRONG
```

That is a bug: with no upper bound the frontmost item advances past the end of the belt forever.
The front run is bounded by the **lane end** `L`. §1 below is the corrected rule. The rest of §3.3
(runs-not-entities, `Θ_lane = v/s`, the tier table) stands.

---

## 1. Geometry and state

A **belt** is a fixed-topology entity with two independent **lanes**. A lane is a 1-D track of
length `L` tiles. Position `0` is the tail (input); position `L` is the head (output). Items move in
the `+` direction only — belts are one-way, and reversal is a rebuild, not a runtime state.

```
tail (input)                                        head (output)
  0 ──────────────────────────────────────────────── L
       item   item   item ───▶ direction of travel
       ├─ s ──┤
```

```
s_item = 0.25 tiles = 16384 in Q16.16   [CAL]   minimum centre-to-centre spacing
v_belt : Fx32, tiles/tick                       from the belt tier (data, not code)
L      : Fx32, tiles                            lane length
```

A lane is a list of **runs**, ordered front (index 0, largest position) to back:

```rust
struct Run {
    head: Fx32,      // position of the FRONTMOST item in this run
    len:  u16,       // item count, >= 1
    item: ItemId,    // a run is single-typed (see §1.1)
}
struct Lane  { runs: Vec<Run>, length: Fx32 }
struct Belt  { lanes: [Lane; 2], speed: Fx32, tier: u8 }
```

Items of a run sit at `head, head - s, head - 2s, …, head - (len-1)·s` — i.e. a run is a maximally
compressed block. Define:

```
tail(run) = run.head - (run.len - 1)·s_item
```

### 1.1 Why a run is single-typed

A run collapses N items into 3 fields, which is the whole performance argument (§4). That only works
if every item in the block is interchangeable, so a run carries one `ItemId`. Two adjacent blocks of
different items stay two runs even when touching at exactly `s_item`. This costs nothing in the
common case — a saturated single-item belt is still one run — and it keeps the representation
honest rather than lossy.

### 1.2 Invariants

Checked by `debug_assert` every tick; each is a real bug if violated:

1. `runs` is strictly ordered: `tail(runs[i]) - runs[i+1].head >= s_item` for all `i`.
2. `0 <= tail(run)` and `run.head <= L` for every run.
3. `run.len >= 1`.
4. No two adjacent runs have the same `item` AND a gap of exactly `s_item` — that pair must have
   been merged (§2.2). This one matters: skip it and runs fragment without bound, the item count
   per run drifts to 1, and the O(runs) performance claim quietly becomes O(items).

---

## 2. The tick update

Per lane, per tick, in two passes. The order is part of the spec, not an implementation detail.

### 2.1 Advance — front to back

Front-to-back is required, not stylistic: the front run must move *before* the run behind it reads
its position, or a compressed block would take one tick per run to expand instead of moving as a
unit, and throughput would depend on how the runs happened to be fragmented — i.e. on history rather
than state.

```
for i in 0 .. runs.len():
    limit = if i == 0 { L }                        // lane end — NOT infinity (§0)
            else      { tail(runs[i-1]) - s_item } // the run ahead
    step  = min(v_belt, limit - runs[i].head)
    if step > 0 { runs[i].head += step }           // clamp: never move backwards
```

`step` is `min` of speed and available gap, so an item never passes another and never leaves the
lane. `limit - head` can be `0` (fully blocked); it must never be negative — that would mean
invariant 1 or 2 was already broken, so it is an assert, not a clamp to be silently tolerated.

### 2.2 Merge — one pass, back to front

After advancing, any run that has closed the gap to the run ahead merges into it:

```
for i in (1 .. runs.len()).rev():
    ahead = runs[i-1]
    if ahead.item == runs[i].item && tail(ahead) - runs[i].head == s_item {
        ahead.len += runs[i].len        // head unchanged: the front item did not move
        remove runs[i]
    }
```

Merging is exact-equality, not a tolerance — positions are integers, so `==` is meaningful here in a
way it would not be with floats. This is one of the places the fixed-point choice (§1.2) pays for
itself directly: a float model would need an epsilon, and the epsilon would be a tuning parameter
that silently changes throughput.

### 2.3 Insertion at the tail

```
can_insert(lane, item) = lane.runs.is_empty() || tail(runs.last()) >= s_item

insert(lane, item):
    back = runs.last()
    if back exists && back.item == item && tail(back) == s_item {
        back.len += 1                  // extend: the new item lands exactly at 0
    } else {
        runs.push(Run { head: 0, len: 1, item })
    }
```

Insertion places an item at position `0` and is refused if that would violate spacing. A refused
insert is what makes a full belt push back into the machine feeding it (§5).

### 2.4 Removal at the head

An item is **arrived** — available to a consumer — iff `runs[0].head == L`. It cannot exceed `L`
(§2.1), so `==` is the correct test.

```
can_take(lane) = !runs.is_empty() && runs[0].head == L

take(lane) -> ItemId:
    front = runs[0]
    item  = front.item
    front.head -= s_item               // the next item becomes the frontmost, at head - s
    front.len  -= 1
    if front.len == 0 { remove runs[0] }
    return item
```

An arrived item that nobody takes just sits at `L`, and the lane compresses behind it. That is
backpressure, and it needs no separate mechanism (§5).

### 2.5 Chain-join (B67)

A placement adjacent to an existing belt **joins** it rather than forming an isolated stub.

```
tail-join: the new chain's last cell points INTO an existing belt's first cell
           -> that belt grows at its TAIL   -> Length += n,  every run head += n·tile
head-join: an existing belt's last cell points into the new chain's first cell
           -> that belt grows at its HEAD   -> Length += n,  run heads UNCHANGED
```

Checked tail-first, then head; each returns the **lowest** matching belt index, so the outcome never
depends on dictionary iteration.

**Why the shifts differ.** Positions are measured from the tail. A head-join extends the far end, so
the origin does not move and nothing shifts — an item parked at the old `L` simply resumes advancing,
which is exactly what building belt in front of a backed-up item should do. A tail-join extends
*behind* the items, so the origin moves back and every item is now that much further along; every
head shifts by the same offset. Because the shift is *equal* for all runs, the §1.2 spacing invariant
is preserved by construction rather than needing re-derivation.

**This corrects B56's deferral, which was reasoned from a false premise.** B56 recorded that joining
would mean "rebuilding a live lane and either discarding the items on it or migrating them, and
neither is specified". There is no rebuild: growth is one integer add on `Length` and, for a
tail-join, one add per *run* (not per item). Items are never destroyed and never re-derived.

**Not modelled: bridging.** A chain that would attach on *both* sides splices two separate belts into
one. That is a genuine lane **merge** — concatenating two item lists and deciding what happens at the
seam — not an offset, and it has no specified rule. v0 joins the downstream side only and leaves the
upstream belt separate. Recorded in B67; silently splicing would move items between lanes under a
rule nobody wrote.

**Hash note.** Chain-join changes no published hash, and that is not luck: it only fires through
`ApplyBeltBatch`, i.e. runtime placement, and no golden scenario places belts — they build fixture
belts at construction. The goldens therefore *cannot* exercise §2.5 and cannot regress from it.
`ChainJoinTests.cs` is the gate; the goldens are not, and saying so beats letting "goldens green"
imply coverage they do not have.


---

## 3. Throughput

For a saturated lane whose consumer never blocks: the front item is taken at `L`, the next is at
`L - s`, and it needs `s / v` ticks to cover the gap. So one item leaves every `s/v` ticks:

```
Θ_lane = v_belt / s_item        items/tick
Θ_belt = 2 · Θ_lane             items/tick   (lanes are independent)
```

Tier I: `v = 2048`, `s = 16384` → `s/v = 8` ticks/item → `60/8 = 7.5 items/s` per lane, `15/s` per
belt. This reproduces §3.3's table exactly, which is the point — the table was the claim, this is
the mechanism.

**Transit latency** is separate from throughput and players conflate the two constantly: an item
takes `L / v` ticks to traverse an empty lane. At tier I over 20 tiles that is `20/0.03125 = 640`
ticks ≈ 10.7 s. Throughput is unaffected by `L`; latency is linear in it. A long belt does not
reduce items/s — it delays the first item and enlarges the in-flight buffer. The belt *is* a buffer
(§4.4's Little's Law applies: `L_items = λ · W`), which is why a long belt masks a starving line for
a while and then stops masking it all at once.

---

## 4. Why runs, not items

A fully compressed tier-I belt of length 20 holds `20/0.25 = 80` items per lane. As entities that is
80 position updates per lane per tick. As runs it is **one** — the saturated case, which is the case
a real factory spends its life in, is the cheap case.

Cost is `O(runs)`, and runs only appear where the belt is *not* compressed — i.e. at gaps, which is
exactly where there is spare capacity and therefore few items. The representation is cheapest
precisely when the factory is biggest. That is the whole reason §7.5's UPS-as-endgame is viable.

---

## 5. Backpressure through transport

No new mechanism. It falls out of §2.3 and §2.4 composing with §4.2's machine states:

```
consumer stops taking
  -> runs[0].head stays at L
  -> runs behind it advance until limit = tail(ahead) - s, then step = 0
  -> the lane compresses front-to-back into one saturated run
  -> tail(runs.last()) drops below s_item
  -> can_insert == false
  -> the feeding machine's TryEmit fails -> Blocked (§4.2), holding its output
```

The propagation delay is real and correct: a belt of length `L` takes roughly `L/v` ticks to
transmit a stall from head to tail, because the stall travels backwards at the same speed items
travel forwards. A player watching a line back up sees the wave move, and that wave is honest —
it is not an animation, it is the sim.

---

## 6. Splitters — merge and split priority

A splitter has up to 2 inputs and 2 outputs. Its entire behaviour is a fair alternation rule, and
its entire risk is that the alternation state is invisible.

```rust
struct Splitter {
    in_ports:  [Option<PortId>; 2],
    out_ports: [Option<PortId>; 2],
    in_next:   u8,   // 0 or 1 — which input to prefer next
    out_next:  u8,   // 0 or 1 — which output to prefer next
}
```

### 6.1 The rule

Per tick, at most one item moves per splitter:

```
1. Choose input:  try in_next; if it has no arrived item, try the other.
                  If neither, done.
2. Choose output: try out_next; if it cannot accept, try the other.
                  If neither, done — leave the item where it is.
3. Move the item.
4. in_next  = 1 - (input actually used)
5. out_next = 1 - (output actually used)
```

### 6.2 Why "flip on the side actually used"

The alternation pointer is set from the side that *was* used, not the side that was *preferred*.
This makes the rule do the right thing in both regimes without a special case:

- **Both sides free:** strict alternation — A, B, A, B. Fair by construction.
- **One side blocked:** the free side keeps taking, and the pointer stays parked on the blocked
  side. The instant it frees, it gets the very next item. So a splitter recovers to fairness
  immediately rather than after a drift-out period.

There is no RNG and no "whichever was checked first" — given the same state, the same side wins,
forever. **Ties are broken by `in_next`/`out_next` alone.**

### 6.3 Splitter state MUST be hashed

`in_next` and `out_next` are one byte each and are the most desync-prone state in the whole
transport model. They are invisible in the UI, they change every item, and they change future
evolution — two worlds identical except for `out_next` diverge on the very next item and then
forever. This is exactly the class of bug §1.5's hash exists to catch, and exactly the class a
"hash the obvious stuff" encoding would miss. They are hashed (§7).

---

## 7. Hash encoding extension (§1.5)

Appended to the §1.5 encoding, after the machine archetypes, in this order:

```
for each belt in id order:
    for each lane in (0, 1):
        u16  run_count                       <-- length prefix, see below
        for each run, front to back:
            u32 head    (the Fx32 bit pattern, reinterpreted unsigned)
            u16 len
            u16 item
for each splitter in id order:
    u8 in_next
    u8 out_next
```

### 7.1 Amendment to §1.5's "no length prefixes"

§1.5 says *"No padding, no alignment, no length prefixes."* That rule was written for
**fixed-topology** collections — the machine arrays, whose counts are set at construction and never
change. It is correct there and stays.

Run lists are **dynamic** — runs merge and split every tick. Without a count, the encoding is
ambiguous: a lane with 2 runs followed by a lane with 0 hashes identically to 0 followed by 2, and
two genuinely different worlds collide. So:

> **§1.5, amended:** fixed-topology collections (machines, splitters) carry no count prefix;
> **variable-length collections (belts, belt runs) are prefixed with a `u16` count.**

**AMENDED AGAIN by D23 (B56): belts moved from fixed-topology to dynamic.** The exemption above
originally covered belts, justified by their being "set at construction and never change". Runtime
belt placement (`POST /sim/belts`) falsified that: belts are now created mid-run, which is exactly
the condition this rule says requires a prefix. Keeping the exemption would leave the encoding's own
stated rationale false and rest correctness on "the `len >= 1` invariant probably prevents a
collision" — the reasoning this section rejects for runs. So `Belts.Count` is now hashed as a `u16`
before the belt blocks.

**This changed all 16 previously published hashes**, which is a consequence of the topology model
changing, not a regression. The evidence it is only an *encoding* change: regenerating the vector
leaves every non-hash field — buffers, machine states, belt contents, splitter pointers — bit-for-bit
identical across all four scenarios. The simulation did not move; the ruler did.

**This preserves every existing golden hash.** Belts and splitters are appended *after* the existing
fields, and a world with no belts and no splitters appends nothing at all — no zero count, because
the belt list itself is fixed-topology and unprefixed. The `steady` and `backpressure` scenarios are
byte-identical under the amended encoding. That is asserted, not assumed: `refsim_v0.py` regenerates
them and the C# `GoldenTests` still compare against the same published hashes.

---

## 8. Evaluation order within a tick

`phase_belts` (§1.3, phase 4) runs after machines and inserters. Within it:

```
for each belt in id order:
    for lane in (0, 1):        # lane 0 before lane 1, always
        advance(lane)          # §2.1
        merge(lane)            # §2.2
for each splitter in id order:
    step(splitter)             # §6.1
```

Belt id order and lane order are fixed. Splitters run after all belts so that an item arriving at a
lane head this tick is visible to the splitter this tick rather than next — otherwise splitter
latency would depend on the id order of the belts feeding it, which is topology-dependent and
therefore a desync waiting to happen.

---

---

## 10. Inserters (B62)

Closes B24's largest residual. §3.3 calls the inserter *"the usual real bottleneck, and the one
players most often mis-model"* and gives:

```
Θ_ins = swing_capacity / (t_swing + t_pickup + t_drop)      items/s
```

**v0 implements a deliberate simplification of that formula**, and the gap is stated up front so a
green inserter golden is not misread as gating §3.3:

| §3.3 claims | v0 implements |
|---|---|
| `swing_capacity` = stack size, tech-boosted | **1 item per swing.** No stack bonus. |
| `t_swing` depends on arc length; `Θ_ins` is position-dependent (chest→chest ≠ belt→machine) | **One fixed `swing_ticks` per inserter**, from content. Position-independent. |
| `t_pickup` + `t_drop` distinct from `t_swing` | **Folded into `swing_ticks`** — one number for the whole cycle. |

So v0's throughput is simply:

```
Θ_ins = tick_hz / swing_ticks        items/s
```

Tier-0 inserter at `swing_ticks = 20` → `60/20 = 3 items/s`. **Position-dependence and stack
bonuses remain unimplemented and ungated** — B24 keeps that residual.

### 10.1 State

```rust
enum InserterState { Idle = 0, Swinging = 1, Blocked = 2 }

struct Inserter {
    source: PortId,       // where it takes from
    dest:   PortId,       // where it puts
    item:   ItemId,       // what it moves (v0: filtered, one type)
    swing_ticks: u32,     // whole cycle, from content
    progress: u32,        // ticks elapsed this swing
    state: InserterState,
    holding: bool,        // true while an item is in the claw
}
```

`holding` is not redundant with `state`: an inserter that has grabbed an item and is `Blocked` at
the destination is still **holding** it. That item is out of the source and not yet in the
destination — it exists nowhere else, so if `holding` were dropped from the model the item would be
destroyed. Conservation is the invariant, and it is one field.

### 10.2 The rule

```
tick(ins):
    if state == Idle:
        if !source.can_take(item): return          // starved; no progress
        source.take(item); holding = true
        progress = 0; state = Swinging

    if state == Swinging:
        progress += 1
        if progress < swing_ticks: return
        state = Blocked                            // fall through: the swing is complete

    if state == Blocked:                           // swing done, trying to deliver
        if !dest.can_put(item): return             // HOLD. progress stays at swing_ticks.
        dest.put(item); holding = false
        progress = 0; state = Idle
```

A blocked inserter **holds its item at full extension** and delivers the instant space appears — the
same no-restart-penalty rule as §4.2's blocked machine, for the same reason: a restart penalty would
make backpressure lossy and the measured `Θ_ins` would depend on stall history rather than state.

### 10.3 Ordering — why `phase_inserters` sits between machines and belts

`factory-math-v0.md` §1.3 already fixes the phase order:

```
phase_machines   ->   phase_inserters   ->   phase_belts
```

That is not incidental, and it produces a **deliberate asymmetry** that must be specified rather
than discovered:

- **Pickup from a lane head reads the belt as it was at the END of last tick.** An item that will
  arrive at the head during *this* tick's `phase_belts` is not visible yet, so it is picked up next
  tick. Inserter pickup therefore carries a **one-tick latency** against belt arrival.
- **Drop onto a lane tail is followed by this tick's `phase_belts`.** An item inserted at position 0
  advances immediately, on the same tick it was dropped. No latency.

Pickup lags, drop does not. Both are consequences of one phase order, and the alternative
(inserters after belts) merely moves the latency to the other end — it does not remove it. What
matters is that the choice is *stated*, so the number a player measures is derivable rather than
mysterious.

### 10.4 Contention — first by id, NOT round-robin

Two inserters may share a source. They are ticked **in ascending inserter id**, and the first one
able to take wins. There is no alternation.

This is a real asymmetry with §6's splitter, and it is intentional:

- A **splitter** exists to divide a stream; fairness is its entire purpose, so it alternates (§6.2).
- An **inserter** is a dumb arm. Two arms reaching for the same item is a *layout* problem, and the
  engine resolving it "fairly" would hide that from the player. First-by-id is deterministic, and
  the resulting starvation of the higher-id arm is **information**: it tells the player their two
  inserters are fighting over one source.

Determinism is what matters, and id order supplies it. Fairness is not owed here.

### 10.5 Hash encoding

Appended after the splitter block (§7):

```
for each inserter in id order:
    u32 progress
    u8  state
    u8  holding
```

**No count prefix — inserters are fixed-topology in v0** (constructed at world build; there is no
placement path for them). This follows §7.1's amended rule exactly as written: prefix iff dynamic.
Machines and splitters are likewise unprefixed, and worlds with no inserters append nothing, so
**every previously published hash is preserved**.

**Trigger for a future prefix, recorded so it is not rediscovered the hard way:** the moment
inserters become placeable at runtime — the way belts did in B56 — they become dynamic and §7.1
forces a `u16` count prefix, which will change every published hash exactly as D23 did. That is not
a reason to prefix now: paying it now would break 16 hashes for a feature that does not exist. It is
a reason to know the bill in advance.

### 10.6 What v0 inserters do NOT model

- Stack bonuses / `swing_capacity > 1` (§3.3).
- Position-dependent swing time (§3.3) — the reason `Θ_ins` differs chest→chest vs belt→machine.
- Lane-specific pickup (near vs far lane).
- Filters beyond the single `item` an inserter is built with.


## 9. What v0 transport does NOT model

Stated so a green transport golden is not read as more than it is:

- **Inserters.** §3.3's `Θ_ins` (swing time, stack bonus, position-dependence) is specified but not
  implemented. In the fixture, machines insert onto and take from belts directly, so the *usual real
  bottleneck* is still absent. This is the largest remaining hole in B24 after this pass.
- **Underground belts and undergrounds' length limits.**
- **Sideloading** (an item entering a lane from a perpendicular belt mid-run). Only tail insertion
  and head removal exist.
- **Curves.** A lane is straight; length is scalar. Curves change effective spacing on the inner
  lane in a real factory; here they do not exist.
- **Priority splitters / filters.** §6 is unfiltered round-robin only.
- **Fluids** (§6 of factory-math-v0.md) — unchanged, still unimplemented.
