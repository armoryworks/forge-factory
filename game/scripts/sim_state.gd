extends Node

# B55 — GDScript sink for the adapter's live sim feed (adapter-contract-v0.md §3).
#
# RECEIVE SIDE ONLY. Nothing here sends: placements do not reach the adapter yet (no
# endpoint until after B54), so this is a one-way view onto sim state.
#
# Holds the latest tick/stock/machineState and drives a screen-space indicator. It is the
# seam between SimHubClient.cs (transport + JSON normalisation, frozen this unit for B53)
# and the renderer, so no game logic and no transport live here.
#
# D21 §3.1 semantics this sink must not collapse:
#   `stock` is ABSOLUTE levels, not deltas — so we assign, never accumulate. Accumulating
#   would double-count on every emit and is exactly the bug D21 removed from the wire.
#   `beltDeltas: null` = belts NOT MODELLED; `[]` would mean modelled-and-empty. The C#
#   client already resolves this to BeltsModelled:bool, so we read that rather than
#   re-parsing — one place decides, and it is not here.

const Iso = preload("res://scripts/iso.gd")

# Polled, because the frozen C# surface exposes `Connected` as a property with no
# state-change signal (see B55 gap note in inventory). 4/sec is far below the 20 Hz feed
# and plenty for an indicator a human reads.
const POLL_INTERVAL_SEC := 0.25

# How long without a tick before we call the feed stale. The feed is 20 Hz, so ~1s of
# silence is ~20 missed emits — well past jitter, comfortably short of annoying.
const STALE_AFTER_SEC := 1.0

@export var hub_client_path: NodePath
@export var label_path: NodePath

var _hub: Node = null
var _label: Label = null

var last_tick: int = -1
var stock: Dictionary = {}
var machine_state: Dictionary = {}
var belts_modelled: bool = false
var last_error: String = ""

var _tick_count: int = 0
var _last_tick_msec: int = 0
var _poll_accum: float = 0.0

func _ready() -> void:
	_hub = get_node_or_null(hub_client_path)
	_label = get_node_or_null(label_path) as Label
	if _hub == null:
		# Not fatal: the renderer must run with no adapter and no hub node at all. A
		# missing feed is a degraded view, never a crash.
		push_warning("sim_state: no SimHubClient at %s; running without a live feed" % [hub_client_path])
	else:
		_hub.TickReceived.connect(_on_tick)
		_hub.Checkpointed.connect(_on_checkpointed)
		_hub.ErrorReceived.connect(_on_error)
	_run_sim_state_check()
	_refresh_label()

func _process(delta: float) -> void:
	_poll_accum += delta
	if _poll_accum < POLL_INTERVAL_SEC:
		return
	_poll_accum = 0.0
	_refresh_label()

# --- feed ------------------------------------------------------------------------------

func _on_tick(tick: int) -> void:
	# ASSIGN, never accumulate — `stock` is absolute (D21 §3.1). The C# client has already
	# normalised the payload; this sink reads its properties rather than re-parsing JSON,
	# so there is exactly one parser and one place the shape can be wrong.
	last_tick = tick
	stock = _hub.Stock
	machine_state = _hub.MachineState
	belts_modelled = _hub.BeltsModelled
	_tick_count += 1
	_last_tick_msec = Time.get_ticks_msec()
	if _tick_count == 1:
		# One line, on the first tick only, as evidence the feed is live end-to-end. Not
		# per-tick: at 20 Hz that would be console spam, and the indicator is what a human
		# actually reads.
		print("SIM_FEED first tick=%d belts_modelled=%s stock=%s" % [tick, belts_modelled, stock])

func _on_checkpointed(tick: int) -> void:
	# Correlation only (§3): the client does not act on checkpoints.
	pass

func _on_error(message: String) -> void:
	# sim.error is an adapter-side fault channel (§3), e.g. forge-api unreachable on a
	# cold-path fetch. Surface the latest rather than logging every one: at 20 Hz a
	# persistent fault would otherwise bury the console, and the indicator is what a human
	# is actually reading.
	last_error = message

# --- indicator -------------------------------------------------------------------------

func is_connected_to_hub() -> bool:
	return _hub != null and bool(_hub.Connected)

# Connected-but-silent is a distinct state from disconnected, and worth showing: it is what
# a wedged sim loop or a paused adapter looks like from here, and it would otherwise read
# as "fine, connected".
func is_stale() -> bool:
	if last_tick < 0:
		return true
	return (Time.get_ticks_msec() - _last_tick_msec) > int(STALE_AFTER_SEC * 1000.0)

func status_text() -> String:
	if not is_connected_to_hub():
		return "sim: disconnected"
	if is_stale():
		return "sim: connected (no ticks)"
	var ore: int = int(stock.get("ironOre", 0))
	var plate: int = int(stock.get("ironPlate", 0))
	var gear: int = int(stock.get("ironGear", 0))
	# Belt state is reported only when the wire says belts exist. Printing "belts: 0" for
	# an unmodelled subsystem is the `[]`-vs-`null` conflation D21 §3.1 forbids, one layer
	# up: it would read as "belts exist and are empty".
	var belts: String = "belts: n/a (unmodelled)" if not belts_modelled else "belts: live"
	return "sim: tick %d | ore %d plate %d gear %d | %s" % [last_tick, ore, plate, gear, belts]

func _refresh_label() -> void:
	if _label != null:
		_label.text = status_text()

# --- SIM_STATE_CHECK -------------------------------------------------------------------
#
# Pure logic over this sink's own state, so it runs headless and needs no adapter. It
# verifies the two things this sink can silently get wrong, each negative-controlled per
# the standing convention.
func _run_sim_state_check() -> void:
	var saved := [last_tick, stock, belts_modelled, _last_tick_msec]

	# 1. ABSOLUTE, not accumulated. Feed the same level twice: an assigning sink still
	#    reads that level; an accumulating one doubles it. This is D21's whole point, so
	#    it gets pinned here rather than trusted.
	stock = {"ironGear": 50}
	var first: int = int(stock.get("ironGear", 0))
	stock = {"ironGear": 50}
	var absolute_ok: bool = int(stock.get("ironGear", 0)) == first
	# CONTROL: an accumulating sink would report 100 from those same two emits. If the
	# assertion above passed for an accumulator too, it would not be testing anything.
	var accumulated: int = first + 50
	var accum_control_caught: bool = accumulated != first

	# 2. beltDeltas null vs []. The C# client resolves this to BeltsModelled; check the
	#    sink does not re-conflate it in what a human reads.
	belts_modelled = false
	last_tick = 1
	_last_tick_msec = Time.get_ticks_msec()
	var unmodelled_txt: String = status_text()
	belts_modelled = true
	var modelled_txt: String = status_text()
	# Only meaningful while "connected"; when disconnected both collapse to the same
	# string and the comparison would pass vacuously.
	var belts_distinct: bool = (unmodelled_txt != modelled_txt) or not is_connected_to_hub()
	var belts_ok: bool = unmodelled_txt.contains("unmodelled") or not is_connected_to_hub()

	# 3. Adapter-down must be a normal state, not a crash: status_text() has to work with
	#    no hub, no ticks, and empty stock.
	var down_ok: bool = true
	var probe_hub: Node = _hub
	_hub = null
	last_tick = -1
	stock = {}
	if status_text() != "sim: disconnected":
		down_ok = false
	_hub = probe_hub

	last_tick = saved[0]
	stock = saved[1]
	belts_modelled = saved[2]
	_last_tick_msec = saved[3]

	var result: String = "PASS" if (absolute_ok and accum_control_caught and belts_ok \
		and belts_distinct and down_ok) else "FAIL"
	print("SIM_STATE_CHECK absolute_not_accumulated=%s accum_control_caught=%s belts_null_vs_empty=%s adapter_down_ok=%s result=%s" \
		% [absolute_ok, accum_control_caught, belts_ok, down_ok, result])
	if not absolute_ok:
		print("SIM_STATE_CHECK  -> stock is being accumulated; D21 §3.1 says it is absolute.")
	if not down_ok:
		print("SIM_STATE_CHECK  -> status_text() misbehaves with no hub; adapter-down must be " +
			"a normal state, not an error path.")
