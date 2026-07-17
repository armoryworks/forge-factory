// Scripted SignalR client that verifies the adapter emits the 3 contract events
// (adapter-contract-v0.md §3) from real sim state. Dev/CI probe, not shipped to players.
//
// Usage:  dotnet run --project tools/simprobe [hubUrl] [seconds]
// Exit 0 = all assertions passed, 1 = a contract violation (message says which).
//
// This exists because a green unit test cannot prove the wire format: it observes the actual
// SignalR payloads a Godot client would receive.

using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

var hubUrl = args.Length > 0 ? args[0] : "http://127.0.0.1:5199/hubs/sim";
var seconds = args.Length > 1 ? int.Parse(args[1]) : 5;

var ticks = new List<JsonElement>();
var checkpoints = new List<JsonElement>();
var errors = new List<JsonElement>();

var conn = new HubConnectionBuilder().WithUrl(hubUrl).Build();
conn.On<JsonElement>("sim.tick", p => { lock (ticks) ticks.Add(p.Clone()); });
conn.On<JsonElement>("sim.checkpointed", p => { lock (checkpoints) checkpoints.Add(p.Clone()); });
conn.On<JsonElement>("sim.error", p => { lock (errors) errors.Add(p.Clone()); });

Console.WriteLine($"connecting to {hubUrl} ...");
await conn.StartAsync();
Console.WriteLine($"connected; observing for {seconds}s");
await Task.Delay(TimeSpan.FromSeconds(seconds));
await conn.StopAsync();

var fail = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) fail.Add(what);
}

Console.WriteLine($"\nobserved: {ticks.Count} sim.tick, {checkpoints.Count} sim.checkpointed, {errors.Count} sim.error");

// --- sim.tick: {tick, beltDeltas:[...], machineState, stockDelta} ---
Console.WriteLine("\nsim.tick (contract §3):");
Check(ticks.Count > 0, "received at least one sim.tick");
if (ticks.Count > 0)
{
    var first = ticks[0];
    foreach (var field in new[] { "tick", "beltDeltas", "machineState", "stockDelta" })
        Check(first.TryGetProperty(field, out _), $"payload has '{field}'");

    Check(first.GetProperty("beltDeltas").ValueKind == JsonValueKind.Array, "beltDeltas is an array");

    var tickValues = ticks.Select(t => t.GetProperty("tick").GetInt64()).ToList();
    Check(tickValues.SequenceEqual(tickValues.OrderBy(x => x)), "tick is monotonically non-decreasing");
    Check(tickValues.Distinct().Count() == tickValues.Count, "no duplicate ticks");
    Check(tickValues[^1] > tickValues[0], $"tick advanced ({tickValues[0]} -> {tickValues[^1]}) -- the sim is really running");

    // Throttle: contract says render-rate, throttled from sim rate. Default 3 ticks @60Hz = 20Hz.
    var gaps = tickValues.Zip(tickValues.Skip(1), (a, b) => b - a).Distinct().ToList();
    Check(gaps.Count == 1, $"emit gap is uniform (gaps seen: {string.Join(",", gaps)})");

    var observedRate = (tickValues[^1] - tickValues[0]) / (double)seconds;
    Check(observedRate is > 45 and < 75, $"sim advanced at ~{observedRate:F1} ticks/s (expect ~60)");

    // machineState must reflect the real reference build: 19 miners, 16 furnaces, 2 assemblers.
    var ms = first.GetProperty("machineState");
    int Total(string arch) => ms.GetProperty(arch).EnumerateObject().Sum(p => p.Value.GetInt32());
    Check(Total("miners") == 19, $"machineState.miners totals 19 (got {Total("miners")})");
    Check(Total("furnaces") == 16, $"machineState.furnaces totals 16 (got {Total("furnaces")})");
    Check(Total("assemblers") == 2, $"machineState.assemblers totals 2 (got {Total("assemblers")})");

    // stockDelta must be a real delta, not a snapshot: gears accrue ~5/s, so over a 20Hz emit
    // window the per-emit delta is small. A snapshot would grow without bound.
    var gearDeltas = ticks.Select(t => t.GetProperty("stockDelta").GetProperty("ironGear").GetInt32()).ToList();
    Check(gearDeltas.All(d => d < 50), "stockDelta.ironGear looks like a delta, not a running total");
    Check(gearDeltas.Any(d => d != 0), "stockDelta.ironGear is non-zero at least once (production is happening)");

    Console.WriteLine("\n  first sim.tick payload:\n    " + JsonSerializer.Serialize(first));
    Console.WriteLine("  last  sim.tick payload:\n    " + JsonSerializer.Serialize(ticks[^1]));
}

// --- sim.checkpointed: {tick} --- only fires if something POSTs /checkpoint while we watch.
if (checkpoints.Count > 0)
{
    Console.WriteLine("\nsim.checkpointed (contract §3):");
    var cp = checkpoints[0];
    Check(cp.TryGetProperty("tick", out _), "payload has 'tick'");
    var cpTick = cp.GetProperty("tick").GetInt64();
    Check(cpTick > 0, $"tick is the real sim tick, not the old stub 0 (got {cpTick})");
    if (ticks.Count > 0)
    {
        var lo = ticks[0].GetProperty("tick").GetInt64();
        var hi = ticks[^1].GetProperty("tick").GetInt64();
        Check(cpTick >= lo && cpTick <= hi, $"tick {cpTick} falls inside the observed run [{lo},{hi}] — it correlates");
    }
    Console.WriteLine("  payload: " + JsonSerializer.Serialize(cp));
}
else
{
    Console.WriteLine("\nsim.checkpointed: not observed (nothing POSTed /checkpoint during the window)");
}

Check(errors.Count == 0, $"no sim.error emitted ({string.Join("; ", errors.Select(e => e.ToString()))})");

Console.WriteLine(fail.Count == 0
    ? "\nOK -- all contract assertions passed"
    : $"\nFAILED -- {fail.Count} assertion(s): {string.Join("; ", fail)}");
return fail.Count == 0 ? 0 : 1;
