using Forge.Sim;
using Xunit;

namespace Forge.Sim.Tests;

/// <summary>
/// Direct unit tests for transport-v0.md §6.
///
/// WHY THESE EXIST, given the `splitter` golden already passes. The golden's layout is SYMMETRIC:
/// both output lanes are drained by 8 identical furnaces, so neither ever blocks. In that regime
/// "flip on the side actually used" (§6.2, the spec) and "flip on the side preferred" (the obvious
/// wrong implementation) produce *identical* behaviour -- both alternate A,B,A,B forever. The
/// golden therefore cannot tell them apart, and its balanced [40,40] proves only that the splitter
/// is not grossly broken.
///
/// The rules only become observable when one side is BLOCKED. That is what these construct.
/// This is the same lesson as LaneTests: a scenario that runs the code is not a scenario that
/// tests it (see B49).
/// </summary>
public class SplitterTests
{
    private const int S = 16384;
    private const int V = 2048;

    /// <summary>Build a lane already holding one item parked at its head, ready to be taken.</summary>
    private static Lane FedLane(int tiles = 1)
    {
        var lane = new Lane(tiles, V, S);
        lane.Insert(0);
        for (int i = 0; i < tiles * Fx32.One / V; i++) lane.Advance();
        Assert.True(lane.CanTake());
        return lane;
    }

    /// <summary>A lane packed solid, so it can accept nothing.</summary>
    private static Lane FullLane()
    {
        var lane = new Lane(1, V, S);
        while (lane.CanInsert())
        {
            lane.Insert(0);
            lane.Advance();
        }
        Assert.False(lane.CanInsert());
        return lane;
    }

    /// <summary>§6.2, both outputs free: strict alternation. Fair by construction.</summary>
    [Fact]
    public void AlternatesStrictlyWhenBothOutputsAreFree()
    {
        var input = new Lane(1, V, S);
        var outA = new Lane(40, V, S);
        var outB = new Lane(40, V, S);
        var sp = new Splitter([input], [outA, outB]);

        var landed = new List<int>();
        var beforeA = 0;
        var beforeB = 0;

        for (int i = 0; i < 400; i++)
        {
            if (input.CanInsert()) input.Insert(0);
            input.Advance();
            input.Merge();
            sp.Step();

            if (outA.ItemCount() > beforeA) { landed.Add(0); beforeA = outA.ItemCount(); }
            if (outB.ItemCount() > beforeB) { landed.Add(1); beforeB = outB.ItemCount(); }
            outA.Advance(); outA.Merge();
            outB.Advance(); outB.Merge();
        }

        Assert.True(landed.Count >= 8, $"expected several items to move, got {landed.Count}");
        for (int i = 1; i < landed.Count; i++)
            Assert.True(landed[i] != landed[i - 1],
                $"two consecutive items went to the same output at index {i}: [{string.Join(",", landed)}]");
    }

    /// <summary>
    /// §6.2, one output blocked -- THE test the golden cannot do.
    ///
    /// The free side must keep taking, and the pointer must PARK on the blocked side rather than
    /// advancing past it. "Flip on the side preferred" would advance the pointer on every attempt,
    /// so it would drift and the blocked side would not be first in line on recovery.
    /// </summary>
    [Fact]
    public void ParksThePointerOnABlockedOutputAndRecoversImmediately()
    {
        var input = FedLane();
        var blocked = FullLane();
        var free = new Lane(40, V, S);
        var sp = new Splitter([input], [blocked, free]);

        Assert.Equal(0, sp.OutNext);         // prefers the blocked side

        sp.Step();

        // The item went to the free side (index 1), so the pointer flips to 1-1 = 0: it PARKS on
        // the blocked output, which is now first in line the instant it clears.
        Assert.Equal(1, free.ItemCount());
        Assert.Equal(0, sp.OutNext);

        // Repeat while still blocked: the free side keeps taking and the pointer stays parked.
        // The output lane must be advanced too, or its own tail blocks the next insert and we would
        // be testing a full belt rather than a parked pointer.
        for (int i = 0; i < 3; i++)
        {
            input.Insert(0);
            for (int t = 0; t < Fx32.One / V; t++) { input.Advance(); free.Advance(); free.Merge(); }
            sp.Step();
            Assert.Equal(0, sp.OutNext);
        }
        Assert.Equal(4, free.ItemCount());

        // Now clear the blocked side. Because the pointer parked, the very next item goes there.
        blocked.Take();
        for (int t = 0; t < 8; t++) blocked.Advance();
        input.Insert(0);
        for (int t = 0; t < Fx32.One / V; t++) { input.Advance(); free.Advance(); free.Merge(); }

        var freeBefore = free.ItemCount();
        var blockedBefore = blocked.ItemCount();
        sp.Step();

        Assert.Equal(blockedBefore + 1, blocked.ItemCount());
        Assert.Equal(freeBefore, free.ItemCount());
    }

    /// <summary>§6.1: if BOTH outputs are blocked, the item stays put -- it is never dropped.</summary>
    [Fact]
    public void MovesNothingWhenBothOutputsAreBlocked()
    {
        var input = FedLane();
        var sp = new Splitter([input], [FullLane(), FullLane()]);

        var before = input.ItemCount();
        sp.Step();

        Assert.Equal(before, input.ItemCount());   // conservation: matter is not destroyed
        Assert.Equal(0, sp.OutNext);               // pointer untouched by a failed attempt
    }

    /// <summary>§6.1: nothing to take is not an error, and must not disturb the pointers.</summary>
    [Fact]
    public void DoesNothingWhenTheInputIsEmpty()
    {
        var sp = new Splitter([new Lane(1, V, S)], [new Lane(4, V, S), new Lane(4, V, S)]);
        sp.Step();
        Assert.Equal(0, sp.InNext);
        Assert.Equal(0, sp.OutNext);
    }

    /// <summary>
    /// §6.3: OutNext is hashed. Two worlds identical but for the pointer MUST hash differently --
    /// otherwise a splitter desync stays invisible until the worlds have diverged past recovery.
    /// This is the property that makes the pointer safe to rely on at all.
    /// </summary>
    [Fact]
    public void SplitterPointersContributeToTheHash()
    {
        var content = Content.Load(GoldenTestsPaths.Recipes);
        var a = new World(content, 1_000_000, splitter: true);
        var b = new World(content, 1_000_000, splitter: true);

        Assert.Equal(a.HashHex(), b.HashHex());

        // Perturb ONLY the alternation pointer. Nothing else about the two worlds differs.
        b.Splitters[0].OutNext = 1;

        Assert.NotEqual(a.HashHex(), b.HashHex());
    }
}

/// <summary>Shared path helper so SplitterTests does not duplicate the walk-up logic.</summary>
internal static class GoldenTestsPaths
{
    public static string Recipes => Find("recipes-v0.toml");

    private static string Find(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "data", file);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"could not locate data/{file}");
    }
}
