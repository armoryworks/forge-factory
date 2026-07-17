extends Node

# B61 — sends player belt placements to the adapter and reconciles the response.
#
# Sits between entity_layer.gd (which owns belts and must not know the sim exists) and
# SimHubClient.cs's B60 bridge (which owns transport and knows nothing about placement).
# Neither of those should grow a dependency on the other; this node is the seam.
#
# WHEN IT SENDS: on placement commit — entity_layer.belts_placed, emitted once per
# single-click and once per drag-run release. Not per belt, not on a timer. A placement
# commit is the player's unit of intent, so it is the unit of transport.
#
# WHAT IT SENDS: only pending (never-sent) belts, via entity_layer.pending_belts(). NOT
# belts_for_adapter(), which is the whole board — posting that on every commit would make
# the adapter re-apply belts it already holds, and grow every request without bound as the
# factory grows.
#
# V0 LIMITATION, respected not fixed: later batches do not join existing belt chains. The
# UI therefore does not imply they do — there is no "connect to that lane" affordance, and
# nothing here merges a new batch into a previously-sent run. Placing a belt adjacent to an
# earlier one gives you two independent lanes, which is what the sim will actually build.

const EntityLayer = preload("res://scripts/entity_layer.gd")

@export var entity_layer_path: NodePath
@export var hub_client_path: NodePath
@export var label_path: NodePath

var _entity_layer: Node = null
var _hub: Node = null
var _label: Label = null

# Ids of the batch currently in flight. Kept here rather than inferred from send_state so
# the response can be reconciled against exactly what was sent, even if the player places
# more belts while the POST is resolving.
var _in_flight: Array = []

var last_applied_tick: int = -1
var last_accepted: int = 0
var last_rejected: int = 0
var last_error: String = ""
var posts_attempted: int = 0
var posts_failed: int = 0

func _ready() -> void:
	_entity_layer = get_node_or_null(entity_layer_path)
	_label = get_node_or_null(label_path) as Label
	_hub = get_node_or_null(hub_client_path)

	if _entity_layer != null:
		_entity_layer.belts_placed.connect(_on_belts_placed)
	if _hub != null and _hub.has_signal("BeltsPosted"):
		_hub.BeltsPosted.connect(_on_belts_posted)
	# Flush on connect as well as on commit. Belts placed while the adapter was down stay
	# LOCAL by design; without this they would sit unsent until the player happened to place
	# again, which makes "the adapter came up" silently not mean "the board is synced".
	# B58's retry-on-initial-connect makes this reachable rather than theoretical.
	if _hub != null and _hub.has_signal("BaselineEstablished"):
		_hub.BaselineEstablished.connect(_on_baseline)
	_run_belt_sync_check()
	_refresh_label()

# --- send ------------------------------------------------------------------------------

func _on_belts_placed() -> void:
	flush()

func _on_baseline(_tick: int) -> void:
	flush()

func flush() -> void:
	if _entity_layer == null:
		return
	var pending: Array[Dictionary] = _entity_layer.pending_belts()
	if pending.is_empty():
		return

	# Adapter unreachable is a NORMAL state, not an error path — but it must not be a
	# silent one. Belts stay LOCAL (they will go on the next commit that finds a live hub)
	# and the indicator says so. The failure mode this avoids: marking them sent anyway, so
	# the board looks synced while the adapter has never heard of them.
	if not _can_send():
		last_error = "adapter unreachable; %d belt(s) unsent" % [pending.size()]
		_refresh_label()
		return

	var ids: Array = []
	var payload := []
	for b in pending:
		ids.append(b.id)
		# The wire shape is exactly {cell, dir} — the id is ours and stays here.
		payload.append({"cell": b.cell, "dir": b.dir})

	_entity_layer.set_send_state(ids, EntityLayer.SEND_SENDING)
	_in_flight = ids
	posts_attempted += 1
	last_error = ""
	_hub.SendBeltsFromGodot(payload)
	_refresh_label()

func _can_send() -> bool:
	return _hub != null and _hub.has_method("SendBeltsFromGodot") and bool(_hub.Connected)

# --- response --------------------------------------------------------------------------

# ok=false means the REQUEST failed (unreachable / malformed) — the belts' fate is unknown,
# so they go back to LOCAL and will be retried on the next commit. ok=true with a non-empty
# rejected[] is a different fact: the adapter answered and refused those cells. B60 keeps
# those two apart deliberately; collapsing them here would throw that away.
func _on_belts_posted(ok: bool, applied_at_tick: int, accepted: int, rejected: Array) -> void:
	var batch: Array = _in_flight
	_in_flight = []

	if not ok:
		posts_failed += 1
		_entity_layer.set_send_state(batch, EntityLayer.SEND_LOCAL)
		last_error = "POST failed; %d belt(s) returned to unsent" % [batch.size()]
		_refresh_label()
		return

	last_applied_tick = applied_at_tick
	last_accepted = accepted
	last_rejected = rejected.size()
	last_error = ""

	# Rejected belts must LEAVE the board. Off-map is currently the only cause, and a belt
	# drawn where the sim has none is the ghost lying: the player would build onto a lane
	# that does not exist. Removing also frees the cell, so they can place again.
	for r in rejected:
		var cell: Vector2i = r.get("cell", Vector2i.ZERO)
		var id: int = _entity_layer.belt_id_at(cell)
		if id != -1:
			_entity_layer.remove_belt(id)
			batch.erase(id)
		last_error = "rejected %s (%s)" % [cell, r.get("reason", "?")]

	# Whatever survived rejection is genuinely on the adapter.
	_entity_layer.set_send_state(batch, EntityLayer.SEND_SENT)
	_refresh_label()

# --- indicator -------------------------------------------------------------------------

func status_text() -> String:
	if _entity_layer == null:
		return "belts: no layer"
	var unsent: int = _entity_layer.belts_in_state(EntityLayer.SEND_LOCAL)
	var sending: int = _entity_layer.belts_in_state(EntityLayer.SEND_SENDING)
	var sent: int = _entity_layer.belts_in_state(EntityLayer.SEND_SENT)
	var txt: String = "belts: %d sent, %d unsent, %d in-flight" % [sent, unsent, sending]
	if last_applied_tick >= 0:
		txt += " | applied@%d acc %d rej %d" % [last_applied_tick, last_accepted, last_rejected]
	if last_error != "":
		txt += " | " + last_error
	return txt

func _refresh_label() -> void:
	if _label != null:
		_label.text = status_text()

# --- BELT_SYNC_CHECK -------------------------------------------------------------------
#
# Pure logic over a scratch layer, so it runs headless with no adapter. Covers what the
# send path can silently get wrong, each negative-controlled per the standing convention.
func _run_belt_sync_check() -> void:
	var belt: Dictionary = load("res://scripts/building_defs.gd").find("belt-1")
	var scratch := EntityLayer.new()

	# 1. Only unsent belts are sent. Place 2, mark them sent, place 1 more: pending must be
	#    1, not 3. CONTROL: belts_for_adapter() (the whole board) returns 3 for the same
	#    state — that is what re-posting the board looks like, and it is what this must not
	#    do. If the two agreed, the assertion would not be testing incrementality at all.
	scratch.place(belt, Vector2i(0, 0), 1)
	scratch.place(belt, Vector2i(1, 0), 1)
	var first_batch: Array = []
	for b in scratch.pending_belts():
		first_batch.append(b.id)
	scratch.set_send_state(first_batch, EntityLayer.SEND_SENT)
	scratch.place(belt, Vector2i(2, 0), 1)
	var pending_after: int = scratch.pending_belts().size()
	var whole_board: int = scratch.belts_for_adapter().size()
	var incremental_ok: bool = pending_after == 1
	var board_control_caught: bool = whole_board == 3 and whole_board != pending_after

	# 2. A rejected cell leaves the board AND frees its cell. A belt the sim refused but the
	#    client still draws is the ghost lying.
	var rid: int = scratch.belt_id_at(Vector2i(2, 0))
	var removed: bool = scratch.remove_belt(rid)
	var gone: bool = scratch.belt_id_at(Vector2i(2, 0)) == -1
	var replaceable: bool = scratch.can_place(belt, Vector2i(2, 0))
	var reject_ok: bool = removed and gone and replaceable

	# 3. Adapter-down must degrade EXPLICITLY, not silently. With no hub, a flush must leave
	#    belts unsent and say so — not mark them sent.
	var saved_layer: Node = _entity_layer
	var saved_hub: Node = _hub
	var saved_err: String = last_error
	_entity_layer = scratch
	_hub = null
	scratch.place(belt, Vector2i(5, 5), 1)
	var before_unsent: int = scratch.belts_in_state(EntityLayer.SEND_LOCAL)
	flush()
	var after_unsent: int = scratch.belts_in_state(EntityLayer.SEND_LOCAL)
	var down_keeps_pending: bool = after_unsent == before_unsent and before_unsent > 0
	var down_is_loud: bool = last_error.contains("unreachable")
	# CONTROL: a sink that silently marked them sent would report 0 unsent here. That is the
	# bug this leg exists to catch, and it is only caught because we assert the count is
	# UNCHANGED rather than merely "no crash".
	var silent_control_caught: bool = after_unsent != 0
	var down_ok: bool = down_keeps_pending and down_is_loud and silent_control_caught

	_entity_layer = saved_layer
	_hub = saved_hub
	last_error = saved_err
	scratch.free()

	var result: String = "PASS" if (incremental_ok and board_control_caught and reject_ok \
		and down_ok) else "FAIL"
	print("BELT_SYNC_CHECK incremental=%s board_control_caught=%s reject_removes=%s adapter_down_loud=%s silent_control_caught=%s result=%s" \
		% [incremental_ok, board_control_caught, reject_ok, down_is_loud, silent_control_caught, result])
	if not incremental_ok:
		print("BELT_SYNC_CHECK  -> pending is %d, want 1: already-sent belts are being re-posted." \
			% [pending_after])
	if not reject_ok:
		print("BELT_SYNC_CHECK  -> a rejected belt survived locally or left its cell claimed; " +
			"the board would show a belt the sim does not have.")
	if not down_ok:
		print("BELT_SYNC_CHECK  -> adapter-down did not degrade explicitly (unsent %d->%d, err '%s'); " \
			% [before_unsent, after_unsent, last_error] + "belts must stay unsent and say so.")
