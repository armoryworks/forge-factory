class_name AdapterClient
extends RefCounted

# Thin HTTP wrapper for the live sim-adapter endpoints per
# docs/adapter-contract-v0.md: GET /health, POST /cold-load, POST /checkpoint.
# Does NOT touch the SignalR hub (/hubs/sim) -- that's a separate protocol,
# out of scope for "thin HTTP wrapper".
#
# B30 guard: Godot's JSON.parse_string() returns every number as float, never
# int -- even for fields the C# contract types as int (adapter/Program.cs's
# CheckpointDelta.PartId; the cold-load response's count/recipesCount;
# SimContentModels.cs's SimItem.Id etc). Left unguarded, a caller doing
# `typeof(x) == TYPE_INT`, using the value as a dictionary key alongside real
# ints, or round-tripping it back into a payload the contract types as an
# integer would silently carry a float instead. Every such field is
# explicitly int()-cast on the way out of parse_string() here, in
# parse_cold_load_body() -- see game/scripts/adapter_client_check.gd for the
# negative-controlled proof that this actually matters.

var base_url: String
var host: String
var port: int

func _init(p_base_url: String = "http://127.0.0.1:5299") -> void:
	base_url = p_base_url
	var stripped: String = base_url.trim_prefix("http://").trim_prefix("https://")
	var parts: PackedStringArray = stripped.split(":")
	host = parts[0]
	port = int(parts[1]) if parts.size() > 1 else 80

func check_health() -> Dictionary:
	var res: Dictionary = _request(HTTPClient.METHOD_GET, "/health")
	if not res.ok or res.status != 200:
		return {"ok": false, "raw": res}
	var parsed: Variant = JSON.parse_string(res.body)
	if parsed == null:
		return {"ok": false, "error": "JSON parse failed"}
	return {"ok": true, "status_text": String(parsed.get("status", ""))}

func cold_load() -> Dictionary:
	var res: Dictionary = _request(HTTPClient.METHOD_POST, "/cold-load")
	if not res.ok or res.status != 200:
		return {"ok": false, "raw": res}
	return parse_cold_load_body(res.body)

# Raw body accessor for the negative-control check -- deliberately returns
# text, not a parsed/guarded Dictionary, so the check can demonstrate the
# unguarded float-typed path before calling parse_cold_load_body() below.
func fetch_cold_load_raw_body() -> String:
	var res: Dictionary = _request(HTTPClient.METHOD_POST, "/cold-load")
	if not res.ok or res.status != 200:
		return ""
	return res.body

# Guarded parse: every field the contract types as int is int()-cast here.
static func parse_cold_load_body(body: String) -> Dictionary:
	var parsed: Variant = JSON.parse_string(body)
	if parsed == null:
		return {"ok": false, "error": "JSON parse failed"}
	return {
		"ok": true,
		"count": int(parsed.get("count", 0)), # SimContent.Items.Count : int
		"recipes_count": int(parsed.get("recipesCount", 0)), # SimContent.Recipes.Count : int
		"gaps": parsed.get("gaps", []), # List<string>, no cast needed
	}

func post_checkpoint(part_id: int, delta: float, location_id, reason: String) -> Dictionary:
	var payload: Dictionary = {
		"partId": part_id,
		"delta": delta,
		"locationId": location_id,
		"reason": reason,
	}
	var body: String = JSON.stringify(payload)
	var res: Dictionary = _request(HTTPClient.METHOD_POST, "/checkpoint", body)
	return {"ok": res.ok and res.status == 204, "status": res.get("status", -1)}

func _request(method: HTTPClient.Method, path: String, body: String = "") -> Dictionary:
	var http := HTTPClient.new()
	var err: int = http.connect_to_host(host, port)
	if err != OK:
		return {"ok": false, "error": "connect_to_host failed: %d" % err}

	while http.get_status() == HTTPClient.STATUS_CONNECTING or http.get_status() == HTTPClient.STATUS_RESOLVING:
		http.poll()
		OS.delay_msec(10)

	if http.get_status() != HTTPClient.STATUS_CONNECTED:
		return {"ok": false, "error": "connect failed, status=%d" % http.get_status()}

	var headers: PackedStringArray = ["Content-Type: application/json"]
	err = http.request(method, path, headers, body)
	if err != OK:
		return {"ok": false, "error": "request failed: %d" % err}

	while http.get_status() == HTTPClient.STATUS_REQUESTING:
		http.poll()
		OS.delay_msec(10)

	var status: int = http.get_status()
	if status != HTTPClient.STATUS_BODY and status != HTTPClient.STATUS_CONNECTED:
		return {"ok": false, "error": "bad status after request: %d" % status}

	var response_code: int = http.get_response_code()
	var raw: PackedByteArray = PackedByteArray()
	while http.get_status() == HTTPClient.STATUS_BODY:
		http.poll()
		var chunk: PackedByteArray = http.read_response_body_chunk()
		if chunk.size() > 0:
			raw.append_array(chunk)

	return {"ok": true, "status": response_code, "body": raw.get_string_from_utf8()}
