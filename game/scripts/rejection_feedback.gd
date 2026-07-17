extends Node2D

# B71 — show the player that a placement bounced, and why.
#
# belt_sync.gd frees a rejected cell correctly but SILENTLY: the belt simply vanishes. From
# the player's chair that is indistinguishable from a misclick, a render glitch, or the
# tool being broken — and a hardcore player will not tolerate a build action that fails
# without saying so. Two things are needed and they are different questions:
#
#   WHERE it bounced -> a marker on the cell, in world space (this node's _draw).
#   WHY  it bounced -> the reason string, on screen (this node's label line).
#
# A HUD line alone cannot answer "which cell"; a flash alone cannot answer "why". So both,
# from one node, so they can never disagree about what happened.
#
# --- the reason is OPAQUE (contract §3.4) ----------------------------------------------
#
# "Reasons are advisory strings — clients should not bind to their text." As of B67 the set
# is off-map / occupied / duplicate-in-batch, and it is explicitly ADDITIVE: it will grow.
#
# So this NEVER branches on the text. It displays whatever arrives, verbatim. A client that
# match/cased known reasons would render a future reason as "unknown" or drop it entirely —
# i.e. it would go quiet exactly when the sim started refusing placements for a NEW reason,
# which is precisely when the player most needs telling. REJECTION_CHECK's main leg pins
# this with a reason string that does not exist yet.

const Iso = preload("res://scripts/iso.gd")

# How long a marker stays up. Long enough to notice a bounce you weren't watching for,
# short enough not to litter the board while you keep building.
const MARKER_TTL_SEC := 3.0

const MARKER_LINE := Color(1.0, 0.35, 0.35, 0.95)
const MARKER_WIDTH := 2.5

@export var belt_sync_path: NodePath
@export var label_path: NodePath

var _belt_sync: Node = null
var _label: Label = null

# [{cell, reason, expires_msec}] — recent bounces, newest last.
var _markers: Array[Dictionary] = []
var total_rejected: int = 0
var last_reasons: String = ""

func _ready() -> void:
	_belt_sync = get_node_or_null(belt_sync_path)
	if _belt_sync != null and _belt_sync.has_signal("belts_rejected"):
		_belt_sync.belts_rejected.connect(_on_rejected)
	_run_rejection_check()
	_refresh_label()

func _process(_delta: float) -> void:
	if _markers.is_empty():
		return
	var now: int = Time.get_ticks_msec()
	var before: int = _markers.size()
	_markers = _markers.filter(func(m): return int(m.expires_msec) > now)
	if _markers.size() != before:
		_refresh_label()
		queue_redraw()

func _on_rejected(entries: Array) -> void:
	var now: int = Time.get_ticks_msec()
	var reasons: Array[String] = []
	for e in entries:
		# Verbatim. No lookup table, no default-casing, no "friendly" rewrite.
		_markers.append({
			"cell": e.get("cell", Vector2i.ZERO),
			"reason": String(e.get("reason", "")),
			"expires_msec": now + int(MARKER_TTL_SEC * 1000.0),
		})
		reasons.append("%s (%s)" % [e.get("cell", Vector2i.ZERO), String(e.get("reason", ""))])
	total_rejected += entries.size()
	last_reasons = ", ".join(reasons)
	print("REJECTION %d: %s" % [entries.size(), last_reasons])
	_refresh_label()
	queue_redraw()

# --- readout ---------------------------------------------------------------------------

func active_markers() -> int:
	return _markers.size()

func status_text() -> String:
	if _markers.is_empty():
		return ""
	# Text, not colour alone (§6.2). The marker below is also a SHAPE (a cross), so the
	# feedback survives a player who cannot separate the red from the ghost's white.
	return "REJECTED %d: %s" % [_markers.size(), last_reasons]

func _refresh_label() -> void:
	if _label != null:
		_label.text = status_text()

func _draw() -> void:
	for m in _markers:
		var c: Vector2i = m.cell
		var x: float = float(c.x)
		var y: float = float(c.y)
		var n: Vector2 = Iso.world_to_screen(x, y)
		var e: Vector2 = Iso.world_to_screen(x + 1.0, y)
		var s: Vector2 = Iso.world_to_screen(x + 1.0, y + 1.0)
		var w: Vector2 = Iso.world_to_screen(x, y + 1.0)
		# Outline the cell, then cross it out. The cross is the non-colour half of the cue:
		# a struck-through cell reads as "refused" without relying on hue at all.
		draw_polyline(PackedVector2Array([n, e, s, w, n]), MARKER_LINE, MARKER_WIDTH)
		draw_line(n, s, MARKER_LINE, MARKER_WIDTH)
		draw_line(e, w, MARKER_LINE, MARKER_WIDTH)

# --- REJECTION_CHECK -------------------------------------------------------------------
#
# Pure logic over this node's own state, headless, no adapter. Negative-controlled per the
# standing convention.
func _run_rejection_check() -> void:
	var saved_markers: Array[Dictionary] = _markers.duplicate()
	var saved_total: int = total_rejected
	var saved_reasons: String = last_reasons
	_markers = []

	# 1. THE MAIN LEG: an unknown reason survives verbatim. §3.4 says the set is additive, so
	#    the reason that matters most is the one nobody has written a case for yet.
	var future: String = "some-reason-invented-after-this-code"
	_on_rejected([{"cell": Vector2i(3, 4), "reason": future}])
	var verbatim_ok: bool = status_text().contains(future)
	# CONTROL: a client that branched on known reasons. Given a string outside its table it
	# renders a placeholder — so the text would NOT contain the raw reason. If the branching
	# version's output equalled ours, this leg would be proving nothing.
	var branching: String = "REJECTED 1: (3, 4) (unknown)"
	var branch_control_caught: bool = status_text() != branching

	# 2. EVERY rejection is surfaced, not just the last. This is a real bug this unit fixed:
	#    belt_sync overwrote last_error inside its loop, so a 3-cell bounce reported one.
	_markers = []
	_on_rejected([
		{"cell": Vector2i(0, 0), "reason": "off-map"},
		{"cell": Vector2i(1, 0), "reason": "occupied"},
		{"cell": Vector2i(2, 0), "reason": "duplicate-in-batch"},
	])
	var all_ok: bool = active_markers() == 3 \
		and status_text().contains("off-map") \
		and status_text().contains("occupied") \
		and status_text().contains("duplicate-in-batch")
	# CONTROL: a last-only implementation shows exactly one. If 3 and 1 were indistinguishable
	# here the assertion would be vacuous.
	var last_only_control_caught: bool = active_markers() != 1

	# 3. Silence when nothing bounced — the feedback must not be permanent furniture, or it
	#    stops meaning "something just failed".
	_markers = []
	var quiet_ok: bool = status_text() == "" and active_markers() == 0

	# 4. Markers expire. An already-expired entry must be gone on the next _process.
	_markers = [{"cell": Vector2i(9, 9), "reason": "off-map",
		"expires_msec": Time.get_ticks_msec() - 1}]
	_process(0.0)
	var expiry_ok: bool = active_markers() == 0

	_markers = saved_markers
	total_rejected = saved_total
	last_reasons = saved_reasons
	_refresh_label()

	var result: String = "PASS" if (verbatim_ok and branch_control_caught and all_ok \
		and last_only_control_caught and quiet_ok and expiry_ok) else "FAIL"
	print("REJECTION_CHECK verbatim_unknown_reason=%s branch_control_caught=%s all_surfaced=%s last_only_control_caught=%s quiet_when_none=%s expires=%s result=%s" \
		% [verbatim_ok, branch_control_caught, all_ok, last_only_control_caught, quiet_ok, expiry_ok, result])
	if not verbatim_ok:
		print("REJECTION_CHECK  -> an unknown reason did not survive to the player; the " +
			"client is binding to reason text, which §3.4 forbids and B67 already broke once.")
	if not all_ok:
		print("REJECTION_CHECK  -> only some rejections surfaced; a player would see one cell " +
			"bounce and never learn the rest did.")
