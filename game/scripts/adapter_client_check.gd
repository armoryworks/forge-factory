extends Node

# B30: negative-controlled proof that AdapterClient.parse_cold_load_body()'s
# int()-cast guard is load-bearing, in the filter_check.gd style (headless,
# self-contained, print + exit-code result).
#
# Tries the live adapter first (POST http://127.0.0.1:5299/cold-load); falls
# back to a canned fixture, matching the live shape
# ({"count":3,"recipesCount":2,"gaps":[...]}, verified 2026-07-17 against the
# real adapter+forge-api stack), if the adapter isn't reachable.
const CANNED_FIXTURE := '{"count":3,"recipesCount":2,"gaps":["Part RAW-00002 (id=3) has no BOM revision — no recipe emitted."]}'
const AdapterClientScript = preload("res://scripts/adapter_client.gd")

func _ready() -> void:
	var client: Object = AdapterClientScript.new()
	var raw_body: String = client.fetch_cold_load_raw_body()
	var source: String = "live-adapter"
	if raw_body.is_empty():
		raw_body = CANNED_FIXTURE
		source = "canned-fixture"

	# --- Unguarded path: what you get straight from JSON.parse_string() ---
	var unguarded: Variant = JSON.parse_string(raw_body)
	var unguarded_count_type_is_float: bool = typeof(unguarded["count"]) == TYPE_FLOAT
	var unguarded_count_type_is_int: bool = typeof(unguarded["count"]) == TYPE_INT

	# --- Guarded path: AdapterClient's int()-cast ---
	var guarded: Dictionary = AdapterClientScript.parse_cold_load_body(raw_body)
	var guarded_count_type_is_int: bool = typeof(guarded["count"]) == TYPE_INT

	# The guard matters iff the raw parse actually produced a float (the bug
	# it exists to catch) and the guarded path fixed it to a real int.
	var bug_reproduced: bool = unguarded_count_type_is_float and not unguarded_count_type_is_int
	var guard_fixes_it: bool = guarded_count_type_is_int and guarded["count"] == 3
	var pass_: bool = bug_reproduced and guard_fixes_it

	print("ADAPTER_CLIENT_CHECK_B30 result=%s source=%s unguarded_type=%s guarded_type=%s bug_reproduced=%s guard_fixes_it=%s" % [
		"PASS" if pass_ else "FAIL",
		source,
		"float" if unguarded_count_type_is_float else "int",
		"int" if guarded_count_type_is_int else "float",
		bug_reproduced,
		guard_fixes_it,
	])

	if source == "live-adapter":
		var health: Dictionary = client.check_health()
		print("ADAPTER_CLIENT_HEALTH ok=%s status_text=%s" % [health.get("ok", false), health.get("status_text", "")])

	get_tree().quit(0 if pass_ else 1)
