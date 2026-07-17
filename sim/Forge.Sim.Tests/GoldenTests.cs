using System.Text.Json;
using Forge.Sim;
using Xunit;

namespace Forge.Sim.Tests;

/// <summary>
/// The acceptance gate: the C# sim core must reproduce data/golden-v0.json exactly.
///
/// The expectations are read from the golden JSON rather than hardcoded here, so the vector stays
/// the single source of truth. Hardcoding them would let the test and the vector drift apart --
/// and a test that agrees only with itself proves nothing.
/// </summary>
public class GoldenTests
{
    private static string RepoData(string file)
    {
        // Walk up to the factory/ root so the test works from any working directory.
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "data", file);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"could not locate data/{file} above {dir}");
    }

    private static Content LoadContent() => Content.Load(RepoData("recipes-v0.toml"));

    private static JsonElement Golden()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoData("golden-v0.json")));
        return doc.RootElement.Clone();
    }

    public static TheoryData<string> Scenarios() => new() { "steady", "backpressure", "transport", "splitter" };

    /// <summary>Scenarios that route ore over a belt (transport-v0.md) rather than a buffer.</summary>
    private static bool IsTransport(string scenario) => scenario == "transport";

    /// <summary>Scenarios wired through a splitter (transport-v0.md §6).</summary>
    private static bool IsSplitter(string scenario) => scenario == "splitter";

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void ReproducesGoldenVector(string scenario)
    {
        var content = LoadContent();
        var sc = Golden().GetProperty("scenarios").GetProperty(scenario);
        var gearCap = sc.GetProperty("gear_buffer_cap").GetInt32();
        var world = new World(content, gearCap, transport: IsTransport(scenario), splitter: IsSplitter(scenario));

        var checkpoints = sc.GetProperty("checkpoints").EnumerateArray().ToList();
        Assert.NotEmpty(checkpoints);

        foreach (var cp in checkpoints)
        {
            var tick = cp.GetProperty("tick").GetUInt64();
            while (world.Tick < tick) world.Step();

            var where = $"{scenario} @ tick {tick}";

            // Buffers first: a buffer mismatch localises the bug far better than a hash diff.
            var bufs = cp.GetProperty("buffers");
            Assert.Equal(bufs.GetProperty("iron_ore").GetInt32(), world.Ore.Count);
            Assert.Equal(bufs.GetProperty("iron_plate").GetInt32(), world.Plate.Count);
            Assert.Equal(bufs.GetProperty("iron_gear").GetInt32(), world.Gear.Count);

            // Machine state tallies.
            var states = cp.GetProperty("machine_states");
            AssertTally(states.GetProperty("miners"), world.Miners, $"{where} miners");
            AssertTally(states.GetProperty("furnaces"), world.Furnaces, $"{where} furnaces");
            AssertTally(states.GetProperty("assemblers"), world.Assemblers, $"{where} assemblers");

            Assert.Equal(cp.GetProperty("miner_0_progress").GetInt32(), world.Miners[0].Progress);

            // Belt state, when the vector carries it. Compared BEFORE the hash for the same reason
            // the buffers are: a belt mismatch says which belt property drifted, a hash mismatch
            // only says "something did".
            if (cp.TryGetProperty("belts", out var belts))
            {
                var expected = belts.EnumerateArray().ToList();
                Assert.Equal(expected.Count, world.Belts.Count);
                for (int b = 0; b < expected.Count; b++)
                {
                    var laneItems = expected[b].GetProperty("lane_items").EnumerateArray().Select(x => x.GetInt32()).ToList();
                    var laneRuns = expected[b].GetProperty("lane_runs").EnumerateArray().Select(x => x.GetInt32()).ToList();
                    Assert.Equal(laneItems.Count, world.Belts[b].Lanes.Length);

                    for (int l = 0; l < laneItems.Count; l++)
                    {
                        Assert.Equal(laneItems[l], world.Belts[b].Lanes[l].ItemCount());
                        Assert.Equal(laneRuns[l], world.Belts[b].Lanes[l].Runs.Count);
                    }

                    var head = expected[b].GetProperty("lane0_front_head");
                    var lane0 = world.Belts[b].Lanes[0];
                    if (head.ValueKind == JsonValueKind.Null)
                        Assert.Empty(lane0.Runs);
                    else
                        Assert.Equal(head.GetInt32(), lane0.Runs[0].Head);
                }
            }

            // Splitter alternation pointers, when the vector carries them. These are invisible
            // state (§6.3): a mismatch here is the difference between a splitter that is fair and
            // one that merely looks fair for a while.
            if (cp.TryGetProperty("splitters", out var sps))
            {
                var expected = sps.EnumerateArray().ToList();
                Assert.Equal(expected.Count, world.Splitters.Count);
                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.Equal(expected[i].GetProperty("in_next").GetInt32(), world.Splitters[i].InNext);
                    Assert.Equal(expected[i].GetProperty("out_next").GetInt32(), world.Splitters[i].OutNext);
                }
            }

            // The contract itself.
            Assert.Equal(cp.GetProperty("hash").GetString(), world.HashHex());
        }
    }

    private static void AssertTally(JsonElement expected, Machine[] arch, string where)
    {
        var actual = World.Tally(arch);
        var want = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        Assert.True(want.Count == actual.Count && want.All(kv => actual.TryGetValue(kv.Key, out var v) && v == kv.Value),
            $"{where}: expected {Fmt(want)} but got {Fmt(actual)}");
    }

    private static string Fmt(Dictionary<string, int> d) =>
        "{" + string.Join(", ", d.OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value}")) + "}";

    /// <summary>
    /// §1.1/§1.3: the sim is a pure function of (state, tick). Two independent runs of the same
    /// content must agree at every tick, and -- because there is no delta-time anywhere -- how the
    /// stepping is chunked must not matter either. The golden proves the sim is *correct*; this
    /// proves it is *reproducible*, which is the property replays and lockstep actually rest on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void ReplayIsDeterministic(string scenario)
    {
        var gearCap = Golden().GetProperty("scenarios").GetProperty(scenario)
                              .GetProperty("gear_buffer_cap").GetInt32();

        // Two independently constructed worlds from independently loaded content: nothing shared,
        // so any accidental static, cache, or allocation-order dependence shows up as a divergence.
        var a = new World(LoadContent(), gearCap, transport: IsTransport(scenario), splitter: IsSplitter(scenario));
        var b = new World(LoadContent(), gearCap, transport: IsTransport(scenario), splitter: IsSplitter(scenario));

        // `a` advances one tick at a time; `b` in ragged bursts, clamped to land exactly on each
        // checkpoint so both are compared at the same tick.
        var bursts = new ulong[] { 7, 1, 13, 100, 3 };
        int bi = 0;

        foreach (ulong t in new ulong[] { 500, 1000, 2500, 6000 })
        {
            while (a.Tick < t) a.Step();
            while (b.Tick < t)
            {
                var target = Math.Min(t, b.Tick + bursts[bi++ % bursts.Length]);
                while (b.Tick < target) b.Step();
            }

            Assert.Equal(t, a.Tick);
            Assert.Equal(t, b.Tick);
            Assert.Equal(a.HashHex(), b.HashHex());
        }
    }

    /// <summary>TICK_RATE comes from content ([meta] tick_hz), not a hardcoded constant.</summary>
    [Fact]
    public void TickRateComesFromContent()
    {
        var content = LoadContent();
        Assert.Equal(60, content.TickHz);
        Assert.Equal(content.TickHz, new World(content, 100).TickRate);
    }

    /// <summary>§1.2: 0.55 is unrepresentable in Q16.16 and must truncate, not round.</summary>
    [Fact]
    public void Fx32TruncatesTowardZero()
    {
        Assert.Equal(36044, Fx32.FromDecimal(0.55m));   // 36044.8 -> 36044, not 36045
        Assert.Equal(65536, Fx32.One);

        // Mul takes and returns Q16.16: 4.0 * 0.5 == 2.0, which is 2 << 16.
        Assert.Equal(Fx32.One * 2, Fx32.Mul(Fx32.One * 4, Fx32.One / 2));
        Assert.Equal(Fx32.One * 4, Fx32.Div(Fx32.One * 2, Fx32.One / 2));

        // Truncate toward zero, NOT floor -- this is the §1.2 pin, and it is why Mul divides
        // rather than shifting. A >> 16 would floor here and give -2.0, disagreeing on negatives.
        Assert.Equal(-(Fx32.One * 3 / 2), Fx32.Mul(-3 * Fx32.One / 2, Fx32.One));
        Assert.Equal(0, Fx32.Mul(-1, 1));               // -0.0000...15 truncates to 0, not -1
        Assert.Equal(0, Fx32.Mul(1, 1));
    }

    /// <summary>
    /// §3.1's remainder carry is the claim golden-v0.md §2 says a regression would silently cost
    /// 0.82% on. Assert the realized rate directly, so a carry regression fails with a message
    /// about throughput rather than only as an opaque hash diff.
    /// </summary>
    [Fact]
    public void MinerRateMatchesTheoryBecauseRemainderIsCarried()
    {
        var content = LoadContent();
        var mine = content.Recipe("mine-iron-ore");
        var sigma = content.Machine("burner-miner").SpeedBase;

        // Drive a single miner in isolation. Measuring through the full chain would mean accounting
        // for items reserved inside machines and partial progress in flight -- noise that obscures
        // the one thing under test. One machine, one buffer, exact count.
        var sink = new Buffer(int.MaxValue);
        var miner = new Machine(sigma, mine.Goal, [], [new Port(sink, 1)]);

        const int ticks = 60_000; // 1000 s
        for (int i = 0; i < ticks; i++) miner.Tick(Fx32.One);

        double perSecond = sink.Count / ((double)ticks / content.TickHz);

        // Theory: 60 * 36044 / (60<<16) = 0.549988/s -> 549.988 crafts in 1000 s, so 549 complete
        // (the 550th lands just past the window). Reset-to-zero instead of carrying gives
        // 60/110 = 0.545455/s -> 545 crafts: 0.82% low. The window is sized so the two are
        // unambiguously distinguishable; the bound below excludes 545 (0.545) comfortably.
        Assert.True(sink.Count is 549 or 550,
            $"expected 549-550 crafts (remainder carried), got {sink.Count}; 545 would mean the remainder is being dropped");
        Assert.InRange(perSecond, 0.5489, 0.5501);
    }

    /// <summary>
    /// transport-v0.md's two headline claims, asserted as numbers rather than left implicit in a
    /// hash. A hash mismatch says "something drifted"; these say WHICH property drifted, which is
    /// the difference between a gate that reports a bug and one that reports a mystery.
    /// </summary>
    [Fact]
    public void TransportIsBeltLimitedAndFullyCompressed()
    {
        var content = LoadContent();
        var world = new World(content, gearCap: 1_000_000, transport: true);
        for (int i = 0; i < 6000; i++) world.Step();
        var gearsAt6000 = world.Gear.Count;
        for (int i = 0; i < 6000; i++) world.Step();

        // Belt-limited throughput: the lane caps at Θ_lane = v/s = 2048/16384 = 7.5 ore/s, under the
        // miners' 10.449768/s, so the BELT is the bottleneck -- 7.5 ore/s -> 7.5 plate/s -> exactly
        // 3.75 gear/s at 2 plate/gear. Without a belt (the `steady` scenario) this is 5.00/s.
        var gearsPerSecond = (world.Gear.Count - gearsAt6000) / (6000.0 / content.TickHz);
        Assert.Equal(3.75, gearsPerSecond, precision: 10);

        // Compression: a saturated 20-tile lane at 0.25 spacing holds exactly 80 items, and holds
        // them in exactly ONE run. 80 checks the spacing invariant; 1 checks transport-v0.md §4 --
        // the saturated case, which is where a real factory lives, is the cheap case. If runs
        // fragmented, item count would still read 80 while the O(runs) claim quietly died.
        var lane0 = world.Belts[0].Lanes[0];
        Assert.Equal(80, lane0.ItemCount());
        Assert.Single(lane0.Runs);

        // The head is a revolving door, NOT parked at the lane end: the furnaces are consuming, so
        // each front item is taken at L and the next advances from L - s. The head therefore cycles
        // within one spacing of the end. Asserting head == L would be asserting a STALLED belt --
        // which is what `backpressure` looks like, not a healthy belt-limited line.
        var laneEnd = 20 * Fx32.One;
        var spacing = LoadContent().Belts[0].ItemSpacing;
        Assert.InRange(lane0.Runs[0].Head, laneEnd - spacing, laneEnd);

        // Lane 1 is untouched: the fixture uses lane 0 only, and lanes are independent.
        Assert.Empty(world.Belts[0].Lanes[1].Runs);
    }

    /// <summary>
    /// Transit latency is a property distinct from throughput, and the vector must keep them apart.
    /// An empty 20-tile lane takes L/v = 1310720/2048 = 640 ticks to traverse, so at tick 600 the
    /// belt is full of ore that has not arrived yet and NO gear exists.
    /// </summary>
    [Fact]
    public void TransportTransitLatencyDelaysFirstOutput()
    {
        var world = new World(LoadContent(), gearCap: 1_000_000, transport: true);
        for (int i = 0; i < 600; i++) world.Step();

        Assert.Equal(0, world.Gear.Count);
        Assert.Equal(62, world.Belts[0].Lanes[0].ItemCount());
        Assert.All(world.Furnaces, m => Assert.Equal(MachineState.Starved, m.State));
    }

    /// <summary>
    /// The §1.5 amendment (transport-v0.md §7.1) must be backward compatible: a world with no belts
    /// appends nothing to the hash, so the published pre-transport vectors still hold. This is the
    /// property that let belts land without invalidating the existing gate, so it is worth asserting
    /// directly rather than inferring it from the other tests passing.
    /// </summary>
    [Fact]
    public void NonTransportWorldsHashAsIfBeltsDoNotExist()
    {
        var world = new World(LoadContent(), gearCap: 1_000_000);
        Assert.Empty(world.Belts);
        for (int i = 0; i < 600; i++) world.Step();

        var published = Golden().GetProperty("scenarios").GetProperty("steady")
            .GetProperty("checkpoints").EnumerateArray()
            .First(c => c.GetProperty("tick").GetInt64() == 600).GetProperty("hash").GetString();
        Assert.Equal(published, world.HashHex());
    }

    /// <summary>§2.4 must reject content, not just accept it. A validator never shown a violation
    /// is not a verified validator.</summary>
    [Fact]
    public void ValidationRejectsDeadContent()
    {
        var c = LoadContent();
        var broken = new Content
        {
            TickHz = c.TickHz,
            Items = c.Items,
            Recipes = c.Recipes,
            Machines = c.Machines,
            Techs = [new TechDef(0, "start", [0, 1])], // drops recipe 2 -> dead content
            Belts = c.Belts,
            Build = c.Build,
        };
        var ex = Assert.Throws<ContentValidationException>(() => Content.Validate(broken));
        Assert.Contains("R6", ex.Message);
    }

    [Fact]
    public void ValidationRejectsUnknownItemId()
    {
        var c = LoadContent();
        var bad = new RecipeDef(9, "bogus", "crafting", 10, [new Stack(99, 1)], [new Stack(1, 1)], []);
        var broken = new Content
        {
            TickHz = c.TickHz,
            Items = c.Items,
            Recipes = [.. c.Recipes, bad],
            Machines = c.Machines,
            Techs = [new TechDef(0, "start", [0, 1, 2, 9])],
            Belts = c.Belts,
            Build = c.Build,
        };
        var ex = Assert.Throws<ContentValidationException>(() => Content.Validate(broken));
        Assert.Contains("R1", ex.Message);
    }

    /// <summary>Real content must pass, or the negative tests above prove nothing.</summary>
    [Fact]
    public void RealContentPassesValidation() => Content.Validate(LoadContent());
}
