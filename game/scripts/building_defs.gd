extends RefCounted

# Buildable types for the vertical slice, mirroring data/recipes-v0.toml's machine list
# (burner-miner / stone-furnace / assembler-1) plus belt-1.
#
# Consume via `const BuildingDefs = preload("res://scripts/building_defs.gd")` — not
# class_name, which needs the editor-generated class cache and parse-errors a fresh
# headless/CI checkout (pinned convention).
#
# FOOTPRINT AND HEIGHT ARE RENDER-SIDE, NOT SIM DATA, and deliberately not in
# recipes-v0.toml: how many tiles a furnace covers is a view/authoring concern, while the
# TOML is the sim's stoichiometry contract. `name` is the join key back to it — if these
# names drift from the TOML's, the link is silently lost, so keep them verbatim.
#
# Heights obey §3's art rule: no entity's visual height exceeds footprint + 1.5 tiles.
# That rule is what keeps tall machines from occluding the factory behind them, and it is
# a constraint on the art bible, not something the renderer can fix later.
#
# Hues are §5's category families: smelting orange, assembly blue, logistics yellow.
# Mining has no assigned family in §5 — slate is chosen here to stay clear of all three,
# and it is a placeholder decision an artist may overrule.

const DEFS: Array[Dictionary] = [
	# DIRECTIONAL. transport-v0.md §1: a belt is one-way — items move in the `+` direction
	# only — and "reversal is a rebuild, not a runtime state". So `dir` is fixed at
	# placement and is NOT a toggle on a placed belt: re-facing one means remove + place.
	# The rotate key therefore steers the GHOST, never an existing entity.
	{
		"name": "belt-1", "category": "logistics", "directional": true,
		"w": 1, "h": 1, "height": 0.15, "hue": Color(0.90, 0.70, 0.20),
	},
	{
		"name": "burner-miner", "category": "mining",
		"w": 2, "h": 2, "height": 1.0, "hue": Color(0.45, 0.48, 0.55),
	},
	{
		"name": "stone-furnace", "category": "smelting",
		"w": 2, "h": 2, "height": 1.0, "hue": Color(0.88, 0.45, 0.18),
	},
	{
		"name": "assembler-1", "category": "crafting",
		"w": 3, "h": 3, "height": 1.5, "hue": Color(0.30, 0.48, 0.85),
	},
]

static func count() -> int:
	return DEFS.size()

static func get_def(index: int) -> Dictionary:
	return DEFS[clampi(index, 0, DEFS.size() - 1)]

# Non-directional types ignore `dir` entirely rather than storing a meaningless 0 — a
# furnace has no facing in v0, and pretending it does would invite a consumer to read one.
static func is_directional(def: Dictionary) -> bool:
	return def.get("directional", false)

# Look up by the recipes-v0.toml join key. Returns {} if absent — callers that hand-roll a
# fixture dict instead of using this drift from the real defs and miss fields (height/hue),
# so prefer this in checks too: a check should exercise the data we actually ship.
static func find(name: String) -> Dictionary:
	for d in DEFS:
		if d.name == name:
			return d
	return {}
