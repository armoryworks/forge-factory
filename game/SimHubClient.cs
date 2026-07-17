using System;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Microsoft.AspNetCore.SignalR.Client;

namespace Factory;

// B48: game-side client for adapter-contract-v0.md §3's /hubs/sim live-delta hub.
// A thin transport + type-normalization seam, like adapter_client.gd is for the HTTP
// side -- no game logic here.
//
// Payload shapes are POST-D21 §3.1, the sole authority:
//   - `stock` is ABSOLUTE buffer levels, not deltas.
//   - `beltDeltas` is `null` in v0 (belts not modelled) -- NOT `[]` (modelled, empty).
//     These are different facts (§3.1); collapsing them is the bug this client must
//     not have.
//   - No `seq` field. Gap detection is `tick` itself: consecutive emits differ by
//     exactly EmitEveryNTicks (§3.2). This client surfaces `LastTick` for that; it
//     does not implement the full §3.2 resync/baseline contract (GET /sim/state) --
//     that is out of this unit's scope, logged as a B48 residual.
public partial class SimHubClient : Node
{
	[Signal]
	public delegate void TickReceivedEventHandler(long tick);

	[Signal]
	public delegate void CheckpointedEventHandler(long tick);

	[Signal]
	public delegate void ErrorReceivedEventHandler(string message);

	[Export] public string HubUrl { get; set; } = "http://127.0.0.1:5299/hubs/sim";

	public long LastTick { get; private set; } = -1;

	// v0: always false (belts unmodelled, §3.1). A true here, once belts land, means
	// the wire shape changed and this client needs re-cutting -- not silently ignored.
	public bool BeltsModelled { get; private set; }
	public Godot.Collections.Dictionary MachineState { get; private set; } = new();
	public Godot.Collections.Dictionary Stock { get; private set; } = new();
	public bool Connected => _connection?.State == HubConnectionState.Connected;

	private HubConnection _connection;

	public override void _Ready()
	{
		_connection = new HubConnectionBuilder().WithUrl(HubUrl).Build();
		_connection.On<JsonElement>("sim.tick", OnSimTick);
		_connection.On<JsonElement>("sim.checkpointed", OnSimCheckpointed);
		_connection.On<JsonElement>("sim.error", OnSimError);
		_ = ConnectAsync();
	}

	public override void _ExitTree()
	{
		if (_connection != null)
		{
			_ = _connection.DisposeAsync();
		}
	}

	private async Task ConnectAsync()
	{
		try
		{
			await _connection.StartAsync();
			GD.Print($"SimHubClient: connected to {HubUrl}");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SimHubClient: connect failed: {ex.Message}");
		}
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
		LastTick = state.Tick;
		BeltsModelled = state.BeltsModelled;
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

		var machineState = ToGodotDict(payload.GetProperty("machineState"));
		var stock = ToGodotDict(payload.GetProperty("stock"));

		return new TickState(tick, beltsModelled, machineState, stock);
	}

	// machineState is {class: {state: count}}; stock is {item: count}. Both are
	// shallow/flat per §3.1 -- one generic one-level JSON->Dictionary walk covers
	// both without hard-coding the field set.
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
}
