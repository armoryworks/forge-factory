#!/usr/bin/env python3
"""Reference implementation of the factory sim core, v0 — generates golden-v0.json.

This is NOT the sim core. It is an executable transcription of factory-math-v0.md §1.2 (fixed
point), §1.5 (hash encoding), §3.1 (machine rate + remainder carry) and §4.2/§4.3 (reservation
and backpressure), used to produce a golden test vector that the real sim core must reproduce
regardless of which host language B7 picks.

Python is chosen deliberately: it is not a candidate host language, so the vector cannot
accidentally encode a host-specific behaviour. Integer arithmetic here is arbitrary-precision and
every width is masked explicitly, matching the fixed widths §1.5 pins.

Run:  python3 tools/refsim_v0.py  > /dev/null   # writes data/golden-v0.json
"""

import json
import os
import tomllib

# --- spec §1.2: fixed point, truncate-toward-zero -------------------------------------------

FX_ONE = 65536


def fx_mul(a: int, b: int) -> int:
    """§1.2 fx_mul. int() truncates toward zero, matching the pinned mode on negatives."""
    return int((a * b) / FX_ONE) if (a * b) < 0 else (a * b) // FX_ONE


# --- spec §1.5: canonical hash encoding ------------------------------------------------------

FNV_OFFSET = 14695981039346656037
FNV_PRIME = 1099511628211
MASK64 = (1 << 64) - 1


class Hasher:
    def __init__(self):
        self.h = FNV_OFFSET

    def _bytes(self, bs: bytes):
        for b in bs:
            self.h = ((self.h ^ b) * FNV_PRIME) & MASK64

    def u8(self, v):
        self._bytes(int(v).to_bytes(1, "little"))

    def u16(self, v):
        self._bytes(int(v).to_bytes(2, "little"))

    def u32(self, v):
        self._bytes(int(v).to_bytes(4, "little"))

    def u64(self, v):
        self._bytes(int(v).to_bytes(8, "little"))


# --- machine states (§4.2); discriminants pinned in §1.5 -------------------------------------

IDLE, CRAFTING, STARVED, BLOCKED = 0, 1, 2, 3
STATE_NAME = {IDLE: "Idle", CRAFTING: "Crafting", STARVED: "Starved", BLOCKED: "Blocked"}


class Buffer:
    """§4.1: every buffer is finite. Reservation debits immediately (§4.2).

    Implements the port protocol (can_take/take/can_put/put) that Machine drives, so a machine
    cannot tell whether it is wired to a buffer or a belt lane. That is the whole point: §3.1's
    rate math must not change because transport appeared underneath it.
    """

    def __init__(self, cap):
        self.count = 0
        self.cap = cap

    def can_take(self, n):
        return self.count >= n

    def can_put(self, n):
        return self.count + n <= self.cap

    def take(self, n):
        self.count -= n

    def put(self, n):
        self.count += n


# --- transport-v0.md: belts -------------------------------------------------------------------

S_ITEM = 16384          # 0.25 tiles in Q16.16 — minimum centre-to-centre spacing [CAL]

# Belt length for the `transport` golden scenario, in tiles. [CAL] Chosen long enough that transit
# latency is visible at the tick-600 checkpoint (20 tiles at tier I = 640 ticks to traverse), so the
# vector pins latency and throughput as separate properties rather than conflating them.
TRANSPORT_BELT_TILES = 20


class Run:
    """A maximally compressed, single-typed block. transport-v0.md §1."""

    __slots__ = ("head", "len", "item")

    def __init__(self, head, length, item):
        self.head = head        # Fx32 position of the FRONTMOST item
        self.len = length
        self.item = item

    def tail(self):
        return self.head - (self.len - 1) * S_ITEM


class Lane:
    """One lane of a belt. transport-v0.md §§1-2."""

    def __init__(self, length_tiles, speed):
        self.length = length_tiles * 65536      # Fx32
        self.speed = speed                      # Fx32 tiles/tick
        self.runs = []                          # front (index 0) to back

    # §2.1 advance, front to back — order is spec, not style.
    def advance(self):
        for i, r in enumerate(self.runs):
            limit = self.length if i == 0 else (self.runs[i - 1].tail() - S_ITEM)
            step = limit - r.head
            assert step >= 0, "invariant 1/2 broken: run overlaps the one ahead"
            if step > self.speed:
                step = self.speed
            if step > 0:
                r.head += step

    # §2.2 merge, back to front. Exact equality: positions are integers (§1.2).
    def merge(self):
        for i in range(len(self.runs) - 1, 0, -1):
            ahead, back = self.runs[i - 1], self.runs[i]
            if ahead.item == back.item and ahead.tail() - back.head == S_ITEM:
                ahead.len += back.len
                del self.runs[i]

    # §2.3 insertion at the tail.
    def can_insert(self):
        return not self.runs or self.runs[-1].tail() >= S_ITEM

    def insert(self, item):
        back = self.runs[-1] if self.runs else None
        if back is not None and back.item == item and back.tail() == S_ITEM:
            back.len += 1
        else:
            self.runs.append(Run(0, 1, item))

    # §2.4 removal at the head.
    def can_take(self):
        return bool(self.runs) and self.runs[0].head == self.length

    def take(self):
        front = self.runs[0]
        item = front.item
        front.head -= S_ITEM
        front.len -= 1
        if front.len == 0:
            del self.runs[0]
        return item

    def item_count(self):
        return sum(r.len for r in self.runs)


class Belt:
    """Two independent lanes. transport-v0.md §1."""

    def __init__(self, length_tiles, speed):
        self.lanes = [Lane(length_tiles, speed), Lane(length_tiles, speed)]

    def step(self):
        for lane in self.lanes:          # lane 0 before lane 1, always (§8)
            lane.advance()
            lane.merge()


class LaneSink:
    """Machine output -> belt tail. Port protocol over Lane (§2.3)."""

    def __init__(self, lane, item):
        self.lane, self.item = lane, item

    def can_put(self, n):
        assert n == 1, "v0 transport moves one item at a time"
        return self.lane.can_insert()

    def put(self, n):
        self.lane.insert(self.item)


class LaneSource:
    """Belt head -> machine input. Port protocol over Lane (§2.4)."""

    def __init__(self, lane, item):
        self.lane, self.item = lane, item

    def can_take(self, n):
        assert n == 1, "v0 transport moves one item at a time"
        return self.lane.can_take() and self.lane.runs[0].item == self.item

    def take(self, n):
        self.lane.take()


class Machine:
    __slots__ = ("sigma", "goal", "inputs", "outputs", "progress", "state")

    def __init__(self, sigma, duration, inputs, outputs):
        self.sigma = sigma
        self.goal = duration << 16          # §3.1 GOAL = d << 16
        self.inputs = inputs                # [(Buffer, count)]
        self.outputs = outputs              # [(Buffer, count)]
        self.progress = 0
        self.state = IDLE

    def _reserve(self):
        if all(p.can_take(n) for p, n in self.inputs):
            for p, n in self.inputs:
                p.take(n)
            return True
        return False

    def _emit(self):
        if all(p.can_put(n) for p, n in self.outputs):
            for p, n in self.outputs:
                p.put(n)
            return True
        return False

    def tick(self, satisfaction):
        # §4.2: a Blocked machine holds completed output at progress >= GOAL, no restart penalty.
        if self.state == BLOCKED:
            if not self._emit():
                return
            self.progress -= self.goal
            self.state = IDLE

        if self.state in (IDLE, STARVED):
            self.state = CRAFTING if self._reserve() else STARVED
            if self.state == STARVED:
                return

        if self.state == CRAFTING:
            self.progress += fx_mul(self.sigma, satisfaction)   # §3.1
            if self.progress >= self.goal:
                if self._emit():
                    self.progress -= self.goal                  # §3.1: carry the remainder
                    self.state = CRAFTING if self._reserve() else STARVED
                else:
                    self.progress = self.goal                   # §4.2: clamp and hold
                    self.state = BLOCKED


class World:
    """v0 golden fixture. See golden-v0.md §2 for what this deliberately does NOT model."""

    def __init__(self, content, gear_cap, transport=False):
        rec = {r["name"]: r for r in content["recipes"]}
        mach = {m["name"]: m for m in content["machines"]}
        belt_def = content["belts"][0]
        rb = content["reference_build"]

        self.tick_no = 0
        self.ore = Buffer(950)
        self.plate = Buffer(200)
        self.gear = Buffer(gear_cap)

        # `transport`: route ore over a belt instead of a shared buffer. Everything downstream of
        # the belt is unchanged, so any difference between this and `steady` is attributable to
        # transport alone -- that is what makes the scenario diagnostic rather than merely different.
        self.belts = []
        if transport:
            belt = Belt(TRANSPORT_BELT_TILES, belt_def["speed"])
            self.belts.append(belt)
            lane = belt.lanes[0]
            ore_out = LaneSink(lane, 0)          # miners -> belt tail
            ore_in = LaneSource(lane, 0)         # belt head -> furnaces
        else:
            ore_out = self.ore
            ore_in = self.ore

        self.miners = [
            Machine(mach["burner-miner"]["speed_base"], rec["mine-iron-ore"]["duration"],
                    [], [(ore_out, 1)])
            for _ in range(rb["miners"])
        ]
        self.furnaces = [
            Machine(mach["stone-furnace"]["speed_base"], rec["smelt-iron-plate"]["duration"],
                    [(ore_in, 1)], [(self.plate, 1)])
            for _ in range(rb["furnaces"])
        ]
        self.assemblers = [
            Machine(mach["assembler-1"]["speed_base"], rec["craft-iron-gear"]["duration"],
                    [(self.plate, 2)], [(self.gear, 1)])
            for _ in range(rb["assemblers"])
        ]

    def step(self):
        # §1.3 phase order. Power is fully satisfied in this fixture (see golden-v0.md §2).
        self.tick_no += 1
        satisfaction = FX_ONE
        for m in self.miners:
            m.tick(satisfaction)
        for m in self.furnaces:
            m.tick(satisfaction)
        for m in self.assemblers:
            m.tick(satisfaction)
        for belt in self.belts:          # phase_belts, after machines (§1.3 / transport §8)
            belt.step()

    def hash(self):
        """§1.5 canonical encoding. Archetype order: buses, miners, furnaces, assemblers."""
        h = Hasher()
        h.u64(self.tick_no)
        # count AND cap: capacity is simulation state (it changes how the world evolves), so a
        # divergence in cap must be a hash mismatch, not a silent one.
        for b in (self.ore, self.plate, self.gear):
            h.u32(b.count)
            h.u32(b.cap)
        for arch in (self.miners, self.furnaces, self.assemblers):
            for m in arch:
                h.u32(m.progress)
                h.u8(m.state)
        # transport-v0.md §7, appended after the machine archetypes. A world with no belts appends
        # NOTHING (the belt list is fixed-topology, so it carries no count prefix), which is why the
        # steady/backpressure hashes are unchanged by transport existing. Asserted by regeneration.
        for belt in self.belts:
            for lane in belt.lanes:
                h.u16(len(lane.runs))            # §7.1: run lists are dynamic -> counted
                for r in lane.runs:
                    h.u32(r.head & 0xFFFFFFFF)   # Fx32 bit pattern, reinterpreted unsigned
                    h.u16(r.len)
                    h.u16(r.item)
        return h.h

    def snapshot(self):
        def tally(arch):
            out = {}
            for m in arch:
                out[STATE_NAME[m.state]] = out.get(STATE_NAME[m.state], 0) + 1
            return out

        return {
            "tick": self.tick_no,
            "hash": "0x%016x" % self.hash(),
            "buffers": {"iron_ore": self.ore.count,
                        "iron_plate": self.plate.count,
                        "iron_gear": self.gear.count},
            "machine_states": {"miners": tally(self.miners),
                               "furnaces": tally(self.furnaces),
                               "assemblers": tally(self.assemblers)},
            "miner_0_progress": self.miners[0].progress,
            "belts": [
                {
                    "lane_items": [lane.item_count() for lane in b.lanes],
                    "lane_runs": [len(lane.runs) for lane in b.lanes],
                    "lane0_front_head": b.lanes[0].runs[0].head if b.lanes[0].runs else None,
                }
                for b in self.belts
            ],
        }


def run(content, gear_cap, checkpoints, transport=False):
    w = World(content, gear_cap, transport=transport)
    out = []
    if 0 in checkpoints:
        out.append(w.snapshot())
    for t in range(1, max(checkpoints) + 1):
        w.step()
        if t in checkpoints:
            out.append(w.snapshot())
    return out


def main():
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    with open(os.path.join(here, "data", "recipes-v0.toml"), "rb") as f:
        content = tomllib.load(f)

    # 12000 exists because at 6000 the backpressure ripple has NOT yet reached the miners
    # (ore buffer still filling). Without it the vector would not cover a fully-propagated stall.
    checkpoints = [0, 600, 6000, 12000]
    golden = {
        "_comment": "GENERATED by tools/refsim_v0.py from data/recipes-v0.toml. Do not hand-edit. "
                    "Spec: docs/factory-math-v0.md §1.5. Notes: docs/golden-v0.md.",
        "version": 0,
        "content_file": "recipes-v0.toml",
        "hash": {
            "algorithm": "FNV-1a-64",
            "offset": "0xcbf29ce484222325",
            "prime": "0x100000001b3",
            "encoding": "little-endian, declared widths, no padding. Order: tick(u64); then for "
                        "each buffer in (ore, plate, gear): count(u32), cap(u32); then for each "
                        "archetype in (miners, furnaces, assemblers), ascending index: "
                        "progress(u32), state(u8).",
        },
        "state_enum": {"Idle": 0, "Crafting": 1, "Starved": 2, "Blocked": 3},
        "scenarios": {
            "steady": {
                "description": "gear buffer effectively unbounded; tests §3.1 rate math, "
                               "remainder carry, and the 4.30% ore slack accumulating as surplus.",
                "gear_buffer_cap": 1_000_000,
                "checkpoints": run(content, 1_000_000, checkpoints),
            },
            "backpressure": {
                "description": "gear buffer capped at 50; fills, then the Blocked ripple "
                               "propagates upstream one hop per tick (§4.3).",
                "gear_buffer_cap": 50,
                "checkpoints": run(content, 50, checkpoints),
            },
            "transport": {
                "description": "ore travels a 20-tile tier-I belt lane instead of a shared buffer "
                               "(transport-v0.md). Closes the transport half of B24. The lane caps "
                               "at 7.5 ore/s, below the miners' 10.449768/s, so the belt -- not the "
                               "machines -- is the bottleneck: gears settle at exactly 3.75/s. "
                               "Everything downstream of the belt is identical to `steady`, so any "
                               "difference is attributable to transport alone.",
                "gear_buffer_cap": 1_000_000,
                "belt": {"tiles": 20, "tier": 1, "lane": 0},
                "checkpoints": run(content, 1_000_000, checkpoints, transport=True),
            },
        },
    }

    path = os.path.join(here, "data", "golden-v0.json")
    with open(path, "w") as f:
        json.dump(golden, f, indent=2)
        f.write("\n")

    for name, sc in golden["scenarios"].items():
        print("=== %s ===" % name)
        for cp in sc["checkpoints"]:
            print(" t=%-5d %s ore=%-4d plate=%-4d gear=%-5d %s" % (
                cp["tick"], cp["hash"], cp["buffers"]["iron_ore"], cp["buffers"]["iron_plate"],
                cp["buffers"]["iron_gear"], cp["machine_states"]))
    print("\nwrote", path)


if __name__ == "__main__":
    main()
