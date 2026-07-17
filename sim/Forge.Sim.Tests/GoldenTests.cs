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

    public static TheoryData<string> Scenarios() => new() { "steady", "backpressure" };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void ReproducesGoldenVector(string scenario)
    {
        var content = LoadContent();
        var sc = Golden().GetProperty("scenarios").GetProperty(scenario);
        var gearCap = sc.GetProperty("gear_buffer_cap").GetInt32();
        var world = new World(content, gearCap);

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
        var a = new World(LoadContent(), gearCap);
        var b = new World(LoadContent(), gearCap);

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
            Build = c.Build,
        };
        var ex = Assert.Throws<ContentValidationException>(() => Content.Validate(broken));
        Assert.Contains("R1", ex.Message);
    }

    /// <summary>Real content must pass, or the negative tests above prove nothing.</summary>
    [Fact]
    public void RealContentPassesValidation() => Content.Validate(LoadContent());
}
