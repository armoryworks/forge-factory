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

// --- sim.tick: {tick, beltDeltas, machineState, stock} --- contract §3.1, ratified by D21 ---
Console.WriteLine("\nsim.tick (contract §3):");
Check(ticks.Count > 0, "received at least one sim.tick");
if (ticks.Count > 0)
{
    var first = ticks[0];
    foreach (var field in new[] { "tick", "beltDeltas", "machineState", "stock" })
        Check(first.TryGetProperty(field, out _), $"payload has '{field}'");

    // D21/§3.1: null = belts not modelled in this build; [] would mean modelled-but-unchanged.
    // The field must still be PRESENT (asserted above) — absent and null are also different facts.
    Check(ticks.All(t => t.GetProperty("beltDeltas").ValueKind == JsonValueKind.Null),
        "beltDeltas is null on every emit (v0 does not model belts; [] would claim it does)");

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

    // stock must be ABSOLUTE LEVELS, not deltas (D21 amendment 1, contract §3.1). These assertions
    // are the exact inverse of the ones they replace, which asserted delta-ness -- so they are also
    // the negative control for each other: a delta-emitting adapter fails these, and an
    // absolute-emitting adapter failed those. The shapes are not confusable by accident.
    // Guard the value assertions behind the field's existence. Reaching straight for
    // GetProperty("stock") on a wrong-shaped payload throws, and an unhandled throw exits 134 --
    // which breaks this probe's own usage contract ("1 = a contract violation") and reads in CI as
    // an infrastructure flake rather than the contract breach it actually is. A wrong shape must
    // FAIL loudly and exit 1.
    if (first.TryGetProperty("stock", out _))
    {
        foreach (var f in new[] { "ironOre", "ironPlate", "ironGear" })
            Check(first.GetProperty("stock").TryGetProperty(f, out _), $"stock has '{f}'");

        var gearLevels = ticks.Select(t => t.GetProperty("stock").GetProperty("ironGear").GetInt32()).ToList();

        // iron-gear is the chain's terminal output: nothing consumes it, so its LEVEL can only
        // rise. A delta stream sits near zero and bounces; a level climbs and never retreats.
        Check(gearLevels.Zip(gearLevels.Skip(1), (a, b) => b >= a).All(ok => ok),
            "stock.ironGear never decreases (it is a level of a terminal output, not a delta)");
        Check(gearLevels[^1] > gearLevels[0],
            $"stock.ironGear advanced ({gearLevels[0]} -> {gearLevels[^1]}) -- production is happening");

        // The discriminator. At ~5 gear/s the absolute level after `seconds` is ~5*seconds; a
        // per-emit delta at 20 Hz would be ~0.25 and round to 0-1. Anything at the old delta's
        // scale means the adapter is still emitting deltas under the new field name.
        Check(gearLevels[^1] >= seconds * 2,
            $"stock.ironGear is a running total, not a per-emit delta (got {gearLevels[^1]} after {seconds}s)");
    }
    else
    {
        Check(false, "stock value assertions skipped -- 'stock' field absent (see above)");
    }

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

// --- Hash parity: the hosted sim must BE the sim core, not merely resemble it ---
//
// Read (tick, hash) from the adapter, then replay a direct Forge.Sim World -- same content file,
// same gear cap -- to that same tick and compare hashes. If they match, the adapter's hosting
// (BackgroundService, wall-clock pacing, catch-up, SignalR) has provably not perturbed the sim:
// the host paces it and nothing more. This is the §1.5 contract applied across the process
// boundary, and it is what makes the golden vector's guarantees transfer to the running service.
var httpBase = new Uri(hubUrl).GetLeftPart(UriPartial.Authority);
Console.WriteLine("\nhash parity vs a direct Forge.Sim run:");
try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var state = JsonDocument.Parse(await http.GetStringAsync($"{httpBase}/sim/state")).RootElement;

    var liveTick = state.GetProperty("tick").GetInt64();
    var liveHash = state.GetProperty("hash").GetString()!;
    var gearCap = state.GetProperty("gearBufferCap").GetInt32();

    var contentPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../data/recipes-v0.toml"));
    Check(File.Exists(contentPath), $"found content at {contentPath}");

    var world = new Forge.Sim.World(Forge.Sim.Content.Load(contentPath), gearCap);
    while ((long)world.Tick < liveTick) world.Step();

    Console.WriteLine($"  adapter  tick={liveTick} hash={liveHash}");
    Console.WriteLine($"  replayed tick={world.Tick} hash={world.HashHex()}");
    Check(world.HashHex() == liveHash,
        $"hosted sim hash == direct Forge.Sim replay at tick {liveTick} -- hosting does not perturb the sim");
}
catch (Exception ex)
{
    Check(false, $"hash parity check threw: {ex.Message}");
}

Check(errors.Count == 0, $"no sim.error emitted ({string.Join("; ", errors.Select(e => e.ToString()))})");

Console.WriteLine(fail.Count == 0
    ? "\nOK -- all contract assertions passed"
    : $"\nFAILED -- {fail.Count} assertion(s): {string.Join("; ", fail)}");
return fail.Count == 0 ? 0 : 1;
