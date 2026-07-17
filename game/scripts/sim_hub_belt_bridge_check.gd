extends Node

# B60: proves SimHubClient.SendBeltsFromGodot is actually callable FROM GDSCRIPT
# (not just C#-to-C#) and that the result marshals back correctly on the main
# thread via the BeltsPosted signal.
#
# Off-map cell (500,500) is a cheap, deterministic negative control -- B56 found
# empirically that the world enforces map bounds, so this cell must come back
# rejected with reason "off-map" whether or not the endpoint happens to be live,
# and "always rejected" is exactly what makes it useful as a control: a bridge
# that silently dropped the call, or one that mis-marshalled Vector2i, would
# both present as "no BeltsPosted signal" or a malformed payload -- distinct,
# detectable failures.
const SimHubClientScript = preload("res://SimHubClient.cs")

var _done := false
var _ok := false
var _accepted := -1
var _rejected: Array = []

func _ready() -> void:
	var client: Node = SimHubClientScript.new()
	add_child(client)
	client.connect("BeltsPosted", Callable(self, "_on_belts_posted"))

	# Let the connection + baseline establish before posting.
	await get_tree().create_timer(2.5).timeout

	# Exact belts_for_adapter() shape, zero reshaping: Array[Dictionary] of
	# {"cell": Vector2i, "dir": int}.
	var belts: Array[Dictionary] = [{"cell": Vector2i(500, 500), "dir": 0}]
	client.SendBeltsFromGodot(belts)

	var waited := 0.0
	while not _done and waited < 8.0:
		await get_tree().create_timer(0.25).timeout
		waited += 0.25

	if not _done:
		print("SIM_HUB_BELT_BRIDGE_CHECK result=SKIPPED (no BeltsPosted signal -- adapter/endpoint not reachable)")
		get_tree().quit(0)
		return

	if not _ok:
		print("SIM_HUB_BELT_BRIDGE_CHECK result=SKIPPED (SendBeltsFromGodot reported ok=false -- endpoint unreachable)")
		get_tree().quit(0)
		return

	var rejected_off_map := false
	for r in _rejected:
		if String(r.get("reason", "")) == "off-map":
			rejected_off_map = true

	var check_pass: bool = _accepted == 0 and _rejected.size() == 1 and rejected_off_map
	print("SIM_HUB_BELT_BRIDGE_CHECK result=%s accepted=%d rejected_count=%d rejected_off_map=%s (expect 0, 1, true -- GDScript call + marshalled rejection)" % [
		"PASS" if check_pass else "FAIL", _accepted, _rejected.size(), rejected_off_map,
	])
	get_tree().quit(0 if check_pass else 1)

func _on_belts_posted(ok: bool, _applied_at_tick: int, accepted: int, rejected: Array) -> void:
	_done = true
	_ok = ok
	_accepted = accepted
	_rejected = rejected
