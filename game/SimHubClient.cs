using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Microsoft.AspNetCore.SignalR.Client;

namespace Factory;

// B51/B53: game-side client for adapter-contract-v0.md §3's /hubs/sim live-delta hub.
// A thin transport + type-normalization seam, like adapter_client.gd is for the HTTP
// side -- no game logic here.
//
// Payload shapes are POST-D21 §3.1, the sole authority:
//   - `stock` is ABSOLUTE buffer levels, not deltas.
//   - `beltDeltas` is `null` in v0 (belts not modelled) -- NOT `[]` (modelled, empty).
//     These are different facts (§3.1); collapsing them is the bug this client must
//     not have.
//   - No `seq` field. Gap detection is `tick` itself: consecutive emits differ by
//     exactly EmitEveryNTicks (§3.2).
//
// B53: §3.2 resync/baseline. `GET /sim/state` on connect AND on reconnect (the hub is
// `Clients.All` with no replay -- a disconnect loses messages permanently, so a
// reconnect without a fresh baseline is silent divergence). Contract text says the
// push rate is "published in /sim/state" for gap detection -- MEASURED (read
// adapter/SimTickService.cs's Snapshot(), did not assume): it publishes `tickRate`
// (the sim's 60Hz, not the ~20Hz push cadence) and no `EmitEveryNTicks`/pushRate field
// exists there. So this client does not trust a field the endpoint doesn't actually
// have; it learns the emit cadence empirically from the first observed inter-tick
// delta after baseline, then flags any later delta that disagrees.
public partial class SimHubClient : Node
{
	[Signal]
	public delegate void TickReceivedEventHandler(long tick);

	[Signal]
	public delegate void CheckpointedEventHandler(long tick);

	[Signal]
	public delegate void ErrorReceivedEventHandler(string message);

	[Signal]
	public delegate void BaselineEstablishedEventHandler(long tick);

	[Signal]
	public delegate void GapDetectedEventHandler(long expectedTick, long actualTick);

	[Export] public string HubUrl { get; set; } = "http://127.0.0.1:5299/hubs/sim";

	public long LastTick { get; private set; } = -1;

	// v0: always false (belts unmodelled, §3.1). A true here, once belts land, means
	// the wire shape changed and this client needs re-cutting -- not silently ignored.
	// B54/B56: now true against a transport-hosted adapter. BeltDeltas carries the raw
	// array (per belt/lane: {belt, lane, spacing, length, runs[...]}) so a caller (or
	// the round-trip check) can diff it against a prior sample -- absolute state per
	// D22, not an actual delta despite the field's name.
	public bool BeltsModelled { get; private set; }
	public Godot.Collections.Array BeltDeltas { get; private set; } = new();
	public Godot.Collections.Dictionary MachineState { get; private set; } = new();
	public Godot.Collections.Dictionary Stock { get; private set; } = new();
	public bool Connected => _connection?.State == HubConnectionState.Connected;

	// §3.2 baseline state. HasBaseline is the "did the resync actually happen" flag a
	// caller must check before trusting Stock/MachineState/LastTick -- it stays false
	// on a failed GET /sim/state rather than silently claiming a baseline never taken.
	public bool HasBaseline { get; private set; }
	public long BaselineTick { get; private set; } = -1;
	public bool HasGap { get; private set; }
	public long LastGapExpectedTick { get; private set; } = -1;
	public long LastGapActualTick { get; private set; } = -1;

	private HubConnection _connection;
	private readonly System.Net.Http.HttpClient _http = new();
	private long _expectedStep = -1;
	private bool _skipNextGapCheck;

	public override void _Ready()
	{
		_connection = new HubConnectionBuilder().WithUrl(HubUrl).WithAutomaticReconnect().Build();
		_connection.On<JsonElement>("sim.tick", OnSimTick);
		_connection.On<JsonElement>("sim.checkpointed", OnSimCheckpointed);
		_connection.On<JsonElement>("sim.error", OnSimError);
		// §3.2: a reconnect is a fresh session against a hub with no replay -- re-baseline,
		// don't resume applying ticks against stale state.
		_connection.Reconnected += _ => EstablishBaselineAsync();
		_ = ConnectAsync();
	}

	public override void _ExitTree()
	{
		if (_connection != null)
		{
			_ = _connection.DisposeAsync();
		}
		_http.Dispose();
	}

	private async Task ConnectAsync()
	{
		try
		{
			await _connection.StartAsync();
			GD.Print($"SimHubClient: connected to {HubUrl}");
			await EstablishBaselineAsync();
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SimHubClient: connect failed: {ex.Message}");
		}
	}

	// Public and independently callable (not just from ConnectAsync) so the
	// negative-controlled check can drive it directly -- including against a HubUrl
	// that can never resolve, to prove a failed baseline fetch is detected rather than
	// silently reported as success.
	public async Task<bool> EstablishBaselineAsync()
	{
		try
		{
			string stateUrl = HubUrl.Replace("/hubs/sim", "/sim/state");
			string json = await _http.GetStringAsync(stateUrl);
			var parsed = ParseBaseline(json);
			Callable.From(() => ApplyBaseline(parsed)).CallDeferred();
			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SimHubClient: baseline fetch failed: {ex.Message}");
			return false;
		}
	}

	public readonly record struct BaselineState(
		long Tick,
		Godot.Collections.Dictionary MachineState,
		Godot.Collections.Dictionary Stock);

	// Pure and public for the same reason ParseTick is: testable without a live server.
	public static BaselineState ParseBaseline(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var payload = doc.RootElement;
		long tick = payload.GetProperty("tick").GetInt64();
		var machineState = ToGodotDict(payload.GetProperty("machineState"));
		// /sim/state's own field is "buffers", not "stock" -- naming drift against the
		// hub's "stock" field (both mean the same absolute levels per §3.1/Snapshot()).
		// Normalized here so callers see one name regardless of which endpoint fed it.
		var stock = ToGodotDict(payload.GetProperty("buffers"));
		return new BaselineState(tick, machineState, stock);
	}

	private void ApplyBaseline(BaselineState baseline)
	{
		LastTick = baseline.Tick;
		BaselineTick = baseline.Tick;
		HasBaseline = true;
		HasGap = false;
		_expectedStep = -1; // re-learn cadence from genuine tick-to-tick deltas only
		// The baseline fetch is an HTTP round-trip racing an independent SignalR stream:
		// by the time it lands, `sim.tick` events may already be several emits ahead.
		// That gap is baseline latency, not a missed message -- comparing the first
		// post-baseline tick against BaselineTick would misreport it as one. MEASURED:
		// a same-thread version of this without the skip flagged a false gap
		// (baseline_tick=722, first tick=894) on every live run.
		_skipNextGapCheck = true;
		MachineState = baseline.MachineState;
		Stock = baseline.Stock;
		EmitSignal(SignalName.BaselineEstablished, baseline.Tick);
	}

	// SignalR's `On<T>` handlers run on the connection's own background thread, never
	// the Godot main thread. EmitSignal (and any Node-visible state write) MUST be
	// deferred to the main thread -- calling it directly throws
	// "The caller thread can't call the function emit_signalp() on this node."
	// Measured, not assumed: this exact exception is what a same-thread version of
	// this handler threw against a live hub.
	private void OnSimTick(JsonElement payload)
	{
		var state = ParseTickElement(payload);
		Callable.From(() => ApplyTick(state)).CallDeferred();
	}

	private void ApplyTick(TickState state)
	{
		if (_skipNextGapCheck)
		{
			// First tick after a (re)baseline: record it as the new reference point but
			// don't compute a delta against BaselineTick -- see ApplyBaseline's comment.
			_skipNextGapCheck = false;
		}
		else if (LastTick >= 0)
		{
			long delta = state.Tick - LastTick;
			if (_expectedStep < 0 && delta > 0)
			{
				_expectedStep = delta; // §3.2: learn the emit cadence, don't assume it
			}
			else if (_expectedStep > 0 && delta != _expectedStep)
			{
				HasGap = true;
				LastGapExpectedTick = LastTick + _expectedStep;
				LastGapActualTick = state.Tick;
				EmitSignal(SignalName.GapDetected, LastGapExpectedTick, LastGapActualTick);
			}
		}

		LastTick = state.Tick;
		BeltsModelled = state.BeltsModelled;
		BeltDeltas = state.BeltDeltas;
		MachineState = state.MachineState;
		Stock = state.Stock;
		EmitSignal(SignalName.TickReceived, LastTick);
	}

	private void OnSimCheckpointed(JsonElement payload)
	{
		long tick = payload.GetProperty("tick").GetInt64();
		Callable.From(() => EmitSignal(SignalName.Checkpointed, tick)).CallDeferred();
	}

	private void OnSimError(JsonElement payload)
	{
		string message = payload.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
		Callable.From(() => EmitSignal(SignalName.ErrorReceived, message)).CallDeferred();
	}

	public readonly record struct TickState(
		long Tick,
		bool BeltsModelled,
		Godot.Collections.Array BeltDeltas,
		Godot.Collections.Dictionary MachineState,
		Godot.Collections.Dictionary Stock);

	// Pure and public so the negative-controlled check (SimHubClientCheck.cs) can
	// drive it with good/mangled JSON directly, no live hub required.
	public static TickState ParseTick(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return ParseTickElement(doc.RootElement);
	}

	private static TickState ParseTickElement(JsonElement payload)
	{
		long tick = payload.GetProperty("tick").GetInt64();

		// §3.1: null = not modelled, [] = modelled-and-empty. Different facts.
		bool beltsModelled = payload.TryGetProperty("beltDeltas", out var bd)
			&& bd.ValueKind != JsonValueKind.Null;
		var beltDeltas = beltsModelled ? ToGodotArray(bd) : new Godot.Collections.Array();

		var machineState = ToGodotDict(payload.GetProperty("machineState"));
		var stock = ToGodotDict(payload.GetProperty("stock"));

		return new TickState(tick, beltsModelled, beltDeltas, machineState, stock);
	}

	// Pure, public: exercised directly by the check with synthetic (previous, step,
	// next) triples so gap detection is provably correct without needing a live
	// dropped-message scenario, which isn't reproducible on demand.
	public static bool IsGap(long previousTick, long expectedStep, long newTick) =>
		expectedStep > 0 && (newTick - previousTick) != expectedStep;

	// machineState is {class: {state: count}}; stock/buffers is {item: count}. Both are
	// shallow/flat per §3.1 -- one generic one-level JSON->Dictionary walk covers both
	// without hard-coding the field set.
	private static Godot.Collections.Dictionary ToGodotDict(JsonElement obj)
	{
		var result = new Godot.Collections.Dictionary();
		foreach (var prop in obj.EnumerateObject())
		{
			switch (prop.Value.ValueKind)
			{
				case JsonValueKind.Object:
					result[prop.Name] = ToGodotDict(prop.Value);
					break;
				case JsonValueKind.Array:
					result[prop.Name] = ToGodotArray(prop.Value);
					break;
				case JsonValueKind.Number:
					result[prop.Name] = prop.Value.GetInt64();
					break;
				case JsonValueKind.String:
					result[prop.Name] = prop.Value.GetString();
					break;
			}
		}
		return result;
	}

	// beltDeltas is an array of {belt, lane, spacing, length, runs:[{head,len,item}]} --
	// one level deeper than machineState/stock, so arrays need their own walk too.
	private static Godot.Collections.Array ToGodotArray(JsonElement arr)
	{
		var result = new Godot.Collections.Array();
		foreach (var item in arr.EnumerateArray())
		{
			switch (item.ValueKind)
			{
				case JsonValueKind.Object:
					result.Add(ToGodotDict(item));
					break;
				case JsonValueKind.Array:
					result.Add(ToGodotArray(item));
					break;
				case JsonValueKind.Number:
					result.Add(item.GetInt64());
					break;
				case JsonValueKind.String:
					result.Add(item.GetString());
					break;
			}
		}
		return result;
	}

	// --- B56: belt placement SEND path (POST /sim/belts) ---
	//
	// Contract, agreed live in the belt-send huddle (mathematician builds the adapter
	// side; this is the game-side send capability only -- GDScript/UI hookup is
	// out of scope, iso's tree, its own ID later):
	//   body:  raw array, verbatim belts_for_adapter() shape, no wrapper --
	//          [{"cell":{"x":int,"y":int},"dir":int}, ...]
	//   202 -> {"appliedAtTick":long,"accepted":int,"rejected":[{"cell":{x,y},"reason":string}]}
	//   400 -> malformed body
	// 202, not 204: per D23 the POST enqueues, the tick loop drains it at a tick
	// boundary -- 204 would claim a completion that hasn't happened yet. rejected is a
	// normal outcome (cell can't host a belt), not a transport error -- same shape as
	// entity_layer.gd's place() returning -1 for "nothing happened", not throwing.
	public readonly record struct BeltPlacement(int X, int Y, int Dir);

	public readonly record struct RejectedBelt(int X, int Y, string Reason);

	public readonly record struct BeltPostResult(
		bool Ok,
		long AppliedAtTick,
		int Accepted,
		IReadOnlyList<RejectedBelt> Rejected);

	private static readonly BeltPostResult FailedPost = new(false, -1, 0, Array.Empty<RejectedBelt>());

	public async Task<BeltPostResult> SendBeltsAsync(IEnumerable<BeltPlacement> belts)
	{
		try
		{
			string beltsUrl = HubUrl.Replace("/hubs/sim", "/sim/belts");
			string body = SerializeBelts(belts);
			using var content = new StringContent(body, Encoding.UTF8, "application/json");
			using var response = await _http.PostAsync(beltsUrl, content);
			string responseBody = await response.Content.ReadAsStringAsync();

			if (response.StatusCode != HttpStatusCode.Accepted)
			{
				GD.PrintErr($"SimHubClient: POST /sim/belts returned {(int)response.StatusCode} " +
					$"(expected 202): {responseBody}");
				return FailedPost;
			}
			return ParseBeltPostResponse(responseBody);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SimHubClient: POST /sim/belts failed: {ex.Message}");
			return FailedPost;
		}
	}

	// Pure and public: no wrapper object, no "belts" key -- the sim owns this shape
	// (belts_for_adapter() already emits it correctly), so re-wrapping it here would be
	// a client-side transform with no purchaser.
	public static string SerializeBelts(IEnumerable<BeltPlacement> belts)
	{
		var sb = new StringBuilder("[");
		bool first = true;
		foreach (var b in belts)
		{
			if (!first) sb.Append(',');
			first = false;
			sb.Append($"{{\"cell\":{{\"x\":{b.X},\"y\":{b.Y}}},\"dir\":{b.Dir}}}");
		}
		sb.Append(']');
		return sb.ToString();
	}

	// Pure and public so the negative-controlled check can drive it with a canned 202
	// body -- including one with a non-empty `rejected`, to prove accepted vs rejected
	// stay distinguishable rather than collapsing into one count.
	public static BeltPostResult ParseBeltPostResponse(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		long appliedAtTick = root.GetProperty("appliedAtTick").GetInt64();
		int accepted = root.GetProperty("accepted").GetInt32();

		var rejected = new List<RejectedBelt>();
		if (root.TryGetProperty("rejected", out var rejArr) && rejArr.ValueKind == JsonValueKind.Array)
		{
			foreach (var r in rejArr.EnumerateArray())
			{
				var cell = r.GetProperty("cell");
				int x = cell.GetProperty("x").GetInt32();
				int y = cell.GetProperty("y").GetInt32();
				string reason = r.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "" : "";
				rejected.Add(new RejectedBelt(x, y, reason));
			}
		}

		return new BeltPostResult(true, appliedAtTick, accepted, rejected);
	}
}
