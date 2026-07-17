using Microsoft.AspNetCore.SignalR;

namespace Forge.Factory.Adapter;

// Live-delta hub per adapter-contract-v0.md §3. Push-only, adapter -> Godot client.
// Distinct from forge-api's BoardHub/etc (those are kanban/notification concerns).
// The client never sends sim-state mutations here (§3) — this is a stub: the hub
// and its 3 events exist and are wired, but nothing drives them from a real sim
// yet (there is no sim tick loop on the adapter side to begin with — the sim
// lives in Godot, per D5/D10, and pushes deltas to the adapter over an as-yet
// unbuilt ingress; that ingress is out of scope for this skeleton).
public sealed class SimHub : Hub
{
    public override Task OnConnectedAsync()
    {
        return base.OnConnectedAsync();
    }
}

// Typed broadcaster so callers (e.g. the /checkpoint endpoint) don't touch
// IHubContext directly. One method per §3 event; all three are stubs — real
// payloads (beltDeltas, machineState, stockDelta) arrive once the sim side
// exists to produce them.
public sealed class SimHubBroadcaster(IHubContext<SimHub> hub)
{
    public Task SimTickAsync(long tick, object? beltDeltas = null, object? machineState = null, object? stockDelta = null, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("sim.tick", new { tick, beltDeltas, machineState, stockDelta }, ct);

    public Task SimCheckpointedAsync(long tick, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("sim.checkpointed", new { tick }, ct);

    public Task SimErrorAsync(string message, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("sim.error", new { message }, ct);
}
