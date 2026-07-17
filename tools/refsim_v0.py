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

    def u32(self, v):
        self._bytes(int(v).to_bytes(4, "little"))

    def u64(self, v):
        self._bytes(int(v).to_bytes(8, "little"))


# --- machine states (§4.2); discriminants pinned in §1.5 -------------------------------------

IDLE, CRAFTING, STARVED, BLOCKED = 0, 1, 2, 3
STATE_NAME = {IDLE: "Idle", CRAFTING: "Crafting", STARVED: "Starved", BLOCKED: "Blocked"}


class Buffer:
    """§4.1: every buffer is finite. Reservation debits immediately (§4.2)."""

    def __init__(self, cap):
        self.count = 0
        self.cap = cap

    def take(self, n):
        if self.count >= n:
            self.count -= n
            return True
        return False

    def put(self, n):
        if self.count + n <= self.cap:
            self.count += n
            return True
        return False


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
        if all(b.count >= n for b, n in self.inputs):
            for b, n in self.inputs:
                b.take(n)
            return True
        return False

    def _emit(self):
        if all(b.count + n <= b.cap for b, n in self.outputs):
            for b, n in self.outputs:
                b.put(n)
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

    def __init__(self, content, gear_cap):
        rec = {r["name"]: r for r in content["recipes"]}
        mach = {m["name"]: m for m in content["machines"]}
        rb = content["reference_build"]

        self.tick_no = 0
        self.ore = Buffer(950)
        self.plate = Buffer(200)
        self.gear = Buffer(gear_cap)

        self.miners = [
            Machine(mach["burner-miner"]["speed_base"], rec["mine-iron-ore"]["duration"],
                    [], [(self.ore, 1)])
            for _ in range(rb["miners"])
        ]
        self.furnaces = [
            Machine(mach["stone-furnace"]["speed_base"], rec["smelt-iron-plate"]["duration"],
                    [(self.ore, 1)], [(self.plate, 1)])
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
        }


def run(content, gear_cap, checkpoints):
    w = World(content, gear_cap)
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
