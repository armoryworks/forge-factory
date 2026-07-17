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

    // B54/D22: belts are now modelled, so beltDeltas must be POPULATED, never null. null is
    // reserved for "this build does not model belts" (D21) and would now be a lie.
    Check(ticks.All(t => t.GetProperty("beltDeltas").ValueKind == JsonValueKind.Array),
        "beltDeltas is an array on every emit (belts are modelled; null would claim they are not)");

    if (first.GetProperty("beltDeltas").ValueKind == JsonValueKind.Array)
    {
        var bd = first.GetProperty("beltDeltas").EnumerateArray().ToList();
        Check(bd.Count > 0, $"beltDeltas has an entry per lane (got {bd.Count})");
        if (bd.Count > 0)
        {
            foreach (var f in new[] { "belt", "lane", "spacing", "length", "runs" })
                Check(bd[0].TryGetProperty(f, out _), $"beltDeltas entry has '{f}'");
            Check(bd[0].GetProperty("runs").ValueKind == JsonValueKind.Array, "beltDeltas.runs is an array");
        }

        // D22: ABSOLUTE state, not deltas. The discriminator is that a saturated lane keeps
        // reporting its ~80 items on every emit; a delta stream would report ~0 once steady.
        // Sum item counts across all lanes on the LAST emit, by which point ore is flowing.
        var lastBd = ticks[^1].GetProperty("beltDeltas").EnumerateArray().ToList();
        var items = lastBd.Sum(e => e.GetProperty("runs").EnumerateArray().Sum(r => r.GetProperty("len").GetInt32()));
        Check(items > 0, $"beltDeltas reports absolute belt contents, not per-tick deltas (last emit carries {items} items)");

        // Runs must be front-to-back with no overlap: head strictly decreasing by at least
        // len*spacing. A client expanding runs into sprites depends on this ordering.
        foreach (var e in lastBd)
        {
            var runs = e.GetProperty("runs").EnumerateArray().ToList();
            var spacing = e.GetProperty("spacing").GetInt32();
            var okOrder = true;
            for (int i = 1; i < runs.Count; i++)
            {
                var aheadTail = runs[i - 1].GetProperty("head").GetInt32()
                              - (runs[i - 1].GetProperty("len").GetInt32() - 1) * spacing;
                if (aheadTail - runs[i].GetProperty("head").GetInt32() < spacing) okOrder = false;
            }
            Check(okOrder, $"belt {e.GetProperty("belt").GetInt32()} lane {e.GetProperty("lane").GetInt32()}: runs are front-to-back and non-overlapping ({runs.Count} runs)");
        }
    }

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
    // B54: the adapter now hosts a TRANSPORT world by default. Replaying a beltless world would
    // fail parity for a reason that is not a bug, so reconstruct the world the adapter actually has.
    var transport = state.TryGetProperty("transport", out var tp) && tp.GetBoolean();

    var contentPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../data/recipes-v0.toml"));
    Check(File.Exists(contentPath), $"found content at {contentPath}");

    var world = new Forge.Sim.World(Forge.Sim.Content.Load(contentPath), gearCap, transport: transport);
    while ((long)world.Tick < liveTick) world.Step();

    Console.WriteLine($"  adapter  tick={liveTick} hash={liveHash} (transport={transport})");
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
