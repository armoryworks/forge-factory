using Microsoft.AspNetCore.SignalR;

namespace Forge.Factory.Adapter;

// Live-delta hub per adapter-contract-v0.md §3. Push-only, adapter -> Godot client.
// Distinct from forge-api's BoardHub/etc (those are kanban/notification concerns).
// The client never sends sim-state mutations here (§3).
//
// No longer a stub: SimTickService hosts the sim core in this process and drives all three
// events from real World state. (The earlier comment here assumed D5's "sim lives in Godot";
// D14/D15 moved the sim core into C#, so it is hosted here and there is no ingress to build.)
public sealed class SimHub : Hub
{
    public override Task OnConnectedAsync()
    {
        return base.OnConnectedAsync();
    }
}

// Typed broadcaster so callers don't touch IHubContext directly. One method per §3 event.
// Payload shapes are the contract's; the values come from SimTickService.
public sealed class SimHubBroadcaster(IHubContext<SimHub> hub)
{
    // `stock` (absolute levels), not `stockDelta` — D21/B46. The anonymous object's property names
    // ARE the wire shape, so renaming the parameter renames the JSON field; contract §3.1 is the
    // authority on both.
    // `inserters` added ADDITIVELY (B64). §3.1 is not re-cut: existing fields keep their names and
    // shapes, so a client built before B64 ignores the new key and keeps working. The ideal shape
    // (and the beltDeltas -> belts rename) is parked for the eventual §3.1 breaking re-cut.
    public Task SimTickAsync(long tick, object? beltDeltas = null, object? machineState = null, object? stock = null, object? inserters = null, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("sim.tick", new { tick, beltDeltas, machineState, stock, inserters }, ct);

    public Task SimCheckpointedAsync(long tick, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("sim.checkpointed", new { tick }, ct);

    public Task SimErrorAsync(string message, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("sim.error", new { message }, ct);
}
