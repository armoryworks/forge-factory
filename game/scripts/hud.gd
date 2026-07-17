extends Node

# B69 — sim HUD. A DETERMINISM WITNESS, not decoration.
#
# tick + hash visible at all times is how a player (and we) spot desync at a glance: two
# clients, or a run and its replay, agreeing on tick but not on hash is a desync, and that
# is the one bug class D2/D5 exist to prevent. So the HUD's job is to make that pair
# readable and honest, and to be LOUD when the feed cannot be trusted.
#
# --- why hash is a "witness pair" and not next to the live tick -------------------------
#
# The hub's sim.tick payload carries NO hash (contract §3.1: tick/beltDeltas/machineState/
# stock/inserters). Hash exists only on GET /sim/state, which returns {tick, hash}
# TOGETHER — a coherent pair. SimHubClient fetches /sim/state for its baseline but does not
# expose the hash, and that file is not ours to change.
#
# So this polls /sim/state at ~1 Hz and reports the pair AS A PAIR, labelled with the
# witness's OWN tick — never the live one. Pairing a ~1 Hz hash with the 20 Hz live tick
# would render "tick 1305 | hash <from tick 1290>", which is a lie of exactly the kind a
# determinism witness must not tell: nobody could compare it against another client's
# reading, and a frozen-looking hash beside a running tick reads as a false all-clear.
# Lagging-but-coherent is useful; live-but-mismatched is worse than nothing.

const POLL_INTERVAL_SEC := 1.0
const HASH_PREFIX_CHARS := 10   # "0x" + 8 hex

@export var sim_state_path: NodePath
@export var belt_sync_path: NodePath
@export var hub_client_path: NodePath
@export var label_path: NodePath
@export var state_url: String = "http://127.0.0.1:5299/sim/state"

var _sim_state: Node = null
var _belt_sync: Node = null
var _hub: Node = null
var _label: Label = null
var _http: HTTPRequest = null

var witness_tick: int = -1
var witness_hash: String = ""
var total_accepted: int = 0
var total_rejected: int = 0

var _poll_accum: float = 0.0
var _in_flight: bool = false
var _reported: bool = false

func _ready() -> void:
	_sim_state = get_node_or_null(sim_state_path)
	_belt_sync = get_node_or_null(belt_sync_path)
	_hub = get_node_or_null(hub_client_path)
	_label = get_node_or_null(label_path) as Label

	if _belt_sync != null and _belt_sync.has_signal("posted"):
		_belt_sync.posted.connect(_on_posted)

	# Own HTTPRequest rather than reaching into adapter_client.gd: that is another role's
	# file, and this needs one endpoint it does not have.
	_http = HTTPRequest.new()
	add_child(_http)
	_http.request_completed.connect(_on_state_fetched)

	_run_hud_check()
	_refresh()

func _process(delta: float) -> void:
	_poll_accum += delta
	if _poll_accum < POLL_INTERVAL_SEC:
		return
	_poll_accum = 0.0
	_poll_state()
	_refresh()

# ~1 Hz, off the tick path. The witness lags by design; see the header.
func _poll_state() -> void:
	if _http == null or _in_flight:
		return
	_in_flight = true
	if _http.request(state_url) != OK:
		_in_flight = false

func _on_state_fetched(_result: int, code: int, _headers: PackedStringArray, body: PackedByteArray) -> void:
	_in_flight = false
	if code != 200:
		return
	var parsed = JSON.parse_string(body.get_string_from_utf8())
	if not (parsed is Dictionary):
		return
	# Take tick and hash from the SAME response, always. Reading them from two fetches
	# would reintroduce the incoherence this design exists to avoid.
	witness_tick = int(parsed.get("tick", -1))
	witness_hash = String(parsed.get("hash", ""))
	if not _reported:
		_reported = true
		# One line, first witness only, as live evidence the readout is real. Not per-poll:
		# the Label is what a human reads, and this is for the log.
		print("HUD_LIVE %s" % [hud_text()])
	_refresh()

func _on_posted(_ok: bool, accepted: int, rejected: int) -> void:
	total_accepted += accepted
	total_rejected += rejected
	_refresh()

# --- readout ---------------------------------------------------------------------------

# connecting / live / reconnecting / offline, from what SimHubClient actually exposes
# (Connected, ConnectAttempts, HasBaseline). It has no HubConnectionState getter, so
# "reconnecting" is inferred from having had a baseline and losing the connection — which
# is exactly the distinction a player needs: a first connect that never lands looks
# identical to a dropped link otherwise.
func connection_state() -> String:
	if _hub == null:
		return "offline"
	if bool(_hub.Connected):
		return "live"
	if bool(_hub.HasBaseline):
		return "reconnecting"
	if int(_hub.ConnectAttempts) > 0:
		return "connecting"
	return "offline"

# Problems get a text flag, never colour alone (§6.2 — non-colour redundancy is
# non-negotiable, and a HUD is exactly where a colour-blind player would lose it).
func flags() -> Array[String]:
	var out: Array[String] = []
	var conn: String = connection_state()
	if conn != "live":
		out.append("!" + conn.to_upper())
	elif _sim_state != null and _sim_state.is_stale():
		# Connected but silent is its own fault, and the one a naive HUD misses: it renders
		# a happy "live" beside a tick that stopped moving.
		out.append("!STALE")
	if _hub != null and bool(_hub.HasGap):
		out.append("!GAP")
	return out

func hud_text() -> String:
	var tick: int = -1
	if _sim_state != null:
		tick = int(_sim_state.last_tick)
	var witness: String = "witness: --"
	if witness_tick >= 0 and witness_hash != "":
		# Labelled with the WITNESS's tick, not the live one. That labelling is the whole
		# honesty of this readout.
		witness = "witness @%d %s" % [witness_tick, witness_hash.substr(0, HASH_PREFIX_CHARS)]
	var txt: String = "sim %s | tick %d | %s | belts +%d/-%d" \
		% [connection_state(), tick, witness, total_accepted, total_rejected]
	var f: Array[String] = flags()
	if not f.is_empty():
		txt += "  " + " ".join(f)
	return txt

func _refresh() -> void:
	if _label != null:
		_label.text = hud_text()

# --- HUD_CHECK -------------------------------------------------------------------------
#
# Pure logic over this node's own state, headless, no adapter. Per B57 the point is to
# prove the HUD is LOUD when things are wrong — a HUD that only renders correctly when
# happy is the failure mode, because that is precisely when nobody is reading it.
func _run_hud_check() -> void:
	var saved_hub: Node = _hub
	var saved_state: Node = _sim_state

	# 1. Disconnected must be FLAGGED, not rendered as a calm readout.
	_hub = null
	var down_txt: String = hud_text()
	var down_flagged: bool = down_txt.contains("!OFFLINE") and flags().size() > 0
	# CONTROL: a HUD that only renders when happy produces no flag at all. If the flagged
	# and unflagged texts were equal, this assertion would be testing nothing.
	var unflagged: String = "sim offline | tick -1 | witness: -- | belts +0/-0"
	var down_control_caught: bool = down_txt != unflagged

	# 2. The witness pair carries the WITNESS's tick, never the live one. This is the
	#    design's central claim, so it gets an assertion rather than a comment.
	_hub = saved_hub
	_sim_state = saved_state
	var saved_wt: int = witness_tick
	var saved_wh: String = witness_hash
	witness_tick = 1290
	witness_hash = "0x0383c629cd7f5df7"
	var w_txt: String = hud_text()
	var pair_ok: bool = w_txt.contains("witness @1290 0x0383c629")
	# CONTROL: the misleading version — hash pinned to the LIVE tick. If a live tick of
	# 1305 could also render "@1290", the label would not be proving anything.
	var live_paired: String = "witness @1305 0x0383c629"
	var pair_control_caught: bool = not w_txt.contains(live_paired)

	# 3. Hash is truncated to a readable prefix, not the full 64-bit word.
	var prefix_ok: bool = w_txt.contains("0x0383c629") and not w_txt.contains("cd7f5df7")

	witness_tick = saved_wt
	witness_hash = saved_wh

	var result: String = "PASS" if (down_flagged and down_control_caught and pair_ok \
		and pair_control_caught and prefix_ok) else "FAIL"
	print("HUD_CHECK disconnect_flagged=%s flag_control_caught=%s witness_pair_own_tick=%s pair_control_caught=%s hash_prefix=%s result=%s" \
		% [down_flagged, down_control_caught, pair_ok, pair_control_caught, prefix_ok, result])
	if not down_flagged:
		print("HUD_CHECK  -> a dead feed rendered without a flag; the HUD is only honest " +
			"when things are fine, which is when nobody needs it.")
	if not pair_ok:
		print("HUD_CHECK  -> the witness is not labelled with its own tick; tick+hash must " +
			"be a coherent pair or it cannot witness anything.")
