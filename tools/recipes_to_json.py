#!/usr/bin/env python3
"""D13 build step: transform the authored TOML content into JSON the sim core can load.

Why this exists (B20): TOML is the authored source of truth because the derivations have to live
beside the numbers as comments, and JSON cannot hold comments. But the engine is Godot (D4), which
ships no TOML parser. So humans edit TOML, this step emits JSON, and Godot reads the JSON natively.
Neither side compromises.

If B7 fails and the Bevy fallback fires, a Rust host parses TOML directly and this step becomes
moot. It is cheap insurance either way.

Usage:
    python3 tools/recipes_to_json.py            # write data/recipes-v0.json
    python3 tools/recipes_to_json.py --check    # exit 1 if the JSON is stale (for CI)

The output is deterministic: same TOML in, byte-identical JSON out.

DO NOT hand-edit the generated JSON. Edit data/recipes-v0.toml and re-run this.
"""

import argparse
import json
import os
import sys
import tomllib

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(HERE, "data", "recipes-v0.toml")
DST = os.path.join(HERE, "data", "recipes-v0.json")

BANNER = ("GENERATED from recipes-v0.toml by tools/recipes_to_json.py (D13). Do not hand-edit -- "
          "edit the TOML and re-run. Spec: docs/factory-math-v0.md; notes: docs/recipes-v0.md.")

# Verified empirically against tools/godot4 (Godot 4.7, headless): JSON.parse_string returns every
# number as float -- speed_base arrives as 36044.0, typeof() == TYPE_FLOAT. int() recovers the
# exact value here only because all magnitudes are far below 2^53 (the PREC check in validate()
# enforces that). A loader that feeds the parsed float straight into Fx32 arithmetic puts a float
# in the sim, violating axiom 1 and reintroducing exactly the cross-platform divergence §1.2 exists
# to prevent. Hence this contract travels with the data.
LOADER_CONTRACT = ("Godot's JSON.parse_string returns ALL numbers as float (verified: speed_base "
                   "parses as 36044.0, TYPE_FLOAT). The loader MUST int() every numeric field "
                   "before use -- never pass a parsed value into Fx32 arithmetic directly. Exact "
                   "for these magnitudes (all << 2^53); validate() enforces that bound.")


class ValidationError(Exception):
    pass


def validate(c):
    """Spec §2.4 load-time rules. The build step is the earliest place these can fail, so fail
    here rather than shipping content the engine will reject at runtime."""
    errs = []

    items = {i["id"]: i for i in c["items"]}
    recipes = {r["id"]: r for r in c["recipes"]}

    # Rule 1: every input/output references a live item id.
    for r in c["recipes"]:
        for s in r["inputs"] + r["outputs"]:
            if s["item"] not in items:
                errs.append("R1 recipe '%s' references unknown item id %r"
                            % (r["name"], s["item"]))

    # Rule 2: duration >= 1.
    for r in c["recipes"]:
        if r["duration"] < 1:
            errs.append("R2 recipe '%s' has duration %d, must be >= 1" % (r["name"], r["duration"]))

    # Rule 3: every non-raw item is reachable from Raw.
    reach = {i["id"] for i in c["items"] if i.get("raw")}
    changed = True
    while changed:
        changed = False
        for r in c["recipes"]:
            if all(s["item"] in reach for s in r["inputs"]):
                for s in r["outputs"]:
                    if s["item"] not in reach:
                        reach.add(s["item"])
                        changed = True
    for i in c["items"]:
        if i["id"] not in reach:
            errs.append("R3 item '%s' is unreachable from Raw (orphan intermediate)" % i["name"])

    # Rule 4: every cycle must not manufacture matter from nothing.
    # v0 content is a DAG, so this passes vacuously and the check is UNTESTED by real content.
    # Tracked as `cycle-validator-untested`. Detect cycles so the gap is loud if one ever lands.
    producers = {}
    for r in c["recipes"]:
        for s in r["outputs"]:
            producers.setdefault(s["item"], []).append(r)

    WHITE, GREY, BLACK = 0, 1, 2
    color = {i: WHITE for i in items}

    def visit(item, stack):
        # Unknown ids are already reported by R1; skip them rather than dying with a KeyError and
        # burying the real, actionable error under a traceback.
        if item not in color:
            return
        if color[item] == GREY:
            errs.append("R4 recipe cycle through item '%s' (%s) -- the spectral-radius test is "
                        "NOT implemented; v0 content was a DAG. Implement it before shipping "
                        "cyclic content, or it could manufacture matter from nothing."
                        % (items[item]["name"], " -> ".join(stack)))
            return
        if color[item] == BLACK:
            return
        color[item] = GREY
        for r in producers.get(item, []):
            for s in r["inputs"]:
                visit(s["item"], stack + [r["name"]])
        color[item] = BLACK

    for i in items:
        visit(i, [])

    # Rule 5: stack sizes non-zero.
    for i in c["items"]:
        if i["stack_size"] < 1:
            errs.append("R5 item '%s' has stack_size %d, must be >= 1"
                        % (i["name"], i["stack_size"]))

    # Rule 6: every recipe is unlockable from the start tech set.
    unlocked = {u for t in c["techs"] for u in t["unlocks"]}
    for r in c["recipes"]:
        if r["id"] not in unlocked:
            errs.append("R6 recipe '%s' is not unlocked by any tech (dead content)" % r["name"])

    # Not a §2.4 rule, but a Godot-loader precondition: every number must survive the JSON
    # round-trip exactly. Godot's JSON parser reads numbers as doubles, which are exact for
    # integers up to 2^53. Assert we are nowhere near that, so int() at load is lossless.
    for m in c["machines"]:
        if abs(m["speed_base"]) >= (1 << 53):
            errs.append("PREC machine '%s' speed_base %d exceeds exact double range"
                        % (m["name"], m["speed_base"]))

    if errs:
        raise ValidationError("\n".join("  - " + e for e in errs))


def build(c):
    out = {"_comment": BANNER,
           "_generated_from": "recipes-v0.toml",
           "_loader_contract": LOADER_CONTRACT}
    out.update(c)
    return json.dumps(out, indent=2, sort_keys=False) + "\n"


def main():
    ap = argparse.ArgumentParser(description="D13: recipes-v0.toml -> recipes-v0.json")
    ap.add_argument("--check", action="store_true",
                    help="verify the committed JSON matches the TOML; exit 1 if stale")
    args = ap.parse_args()

    with open(SRC, "rb") as f:
        content = tomllib.load(f)

    try:
        validate(content)
    except ValidationError as e:
        print("FAIL %s does not pass spec §2.4 validation:\n%s" % (SRC, e), file=sys.stderr)
        return 1

    text = build(content)

    if args.check:
        if not os.path.exists(DST):
            print("FAIL %s does not exist; run without --check" % DST, file=sys.stderr)
            return 1
        with open(DST) as f:
            current = f.read()
        if current != text:
            print("FAIL %s is stale -- regenerate with: python3 tools/recipes_to_json.py"
                  % DST, file=sys.stderr)
            return 1
        print("OK %s is in sync with %s" % (os.path.basename(DST), os.path.basename(SRC)))
        return 0

    with open(DST, "w") as f:
        f.write(text)
    print("OK validated §2.4 and wrote %s (%d recipes, %d items)"
          % (DST, len(content["recipes"]), len(content["items"])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
