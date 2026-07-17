using Forge.Sim;
using Xunit;

namespace Forge.Sim.Tests;

/// <summary>
/// transport-v0.md §10 (B62). The `inserter` golden already kills every mutant I tried, so unlike
/// LaneTests these are not filling a hole — they name the properties directly, so a regression
/// reports "contention rule broke" rather than "hash differs at tick 6000".
/// </summary>
public class InserterTests
{
    private static Content C() => Content.Load(GoldenTestsPaths.Recipes);

    /// <summary>§10: Θ_ins = tick_hz / swing_ticks. The whole v0 model in one number.</summary>
    [Fact]
    public void ThroughputIsTickRateOverSwingTicks()
    {
        var src = new Buffer(int.MaxValue) { Count = int.MaxValue };
        var dst = new Buffer(int.MaxValue);
        var ins = new Inserter(src, dst, 0, swingTicks: 20);

        const int ticks = 6000;
        for (int i = 0; i < ticks; i++) ins.Step();

        // 60/20 = 3/s -> 300 items in 6000 ticks, less at most one in-flight swing.
        var perSecond = dst.Count / (ticks / 60.0);
        Assert.InRange(perSecond, 2.99, 3.0);
    }

    /// <summary>
    /// §10.2: a blocked inserter HOLDS at full extension and delivers the instant space appears.
    /// The item is out of the source and not in the dest -- if it were dropped it would cease to
    /// exist. Conservation is the invariant.
    /// </summary>
    [Fact]
    public void BlockedInserterHoldsItsItemAndDeliversWithoutRestarting()
    {
        var src = new Buffer(10) { Count = 1 };
        var dst = new Buffer(0);                      // cannot accept anything
        var ins = new Inserter(src, dst, 0, swingTicks: 5);

        for (int i = 0; i < 50; i++) ins.Step();

        Assert.Equal(InserterState.Blocked, ins.State);
        Assert.True(ins.Holding, "the item is in the claw: not in src, not in dst -- it must exist somewhere");
        Assert.Equal(0, src.Count);
        Assert.Equal(0, dst.Count);
        Assert.Equal(5, ins.Progress);                // parked at full swing, NOT reset

        // Space appears: delivery is immediate, with no restart penalty (§4.2's rule for machines).
        var roomy = new Buffer(10);
        var ins2 = new Inserter(new Buffer(10) { Count = 1 }, roomy, 0, swingTicks: 5);
        for (int i = 0; i < 5; i++) ins2.Step();
        Assert.Equal(1, roomy.Count);
    }

    /// <summary>
    /// §10.4: contention is resolved FIRST-BY-ID, not round-robin. This is the deliberate asymmetry
    /// with §6's splitter: a splitter exists to divide a stream so fairness is its purpose; an
    /// inserter is a dumb arm, and two arms fighting over one source is a LAYOUT problem. Resolving
    /// it "fairly" would hide that from the player. Starving the higher-id arm is information.
    /// </summary>
    [Fact]
    public void ContentionIsFirstByIdNotRoundRobin()
    {
        var src = new Buffer(int.MaxValue) { Count = 1 };   // exactly ONE item to fight over
        var dstA = new Buffer(int.MaxValue);
        var dstB = new Buffer(int.MaxValue);

        // swingTicks: 1 so both are back to Idle every tick. This isolates the contention rule: with
        // a longer swing the high-id arm would legitimately win whenever the low-id one is mid-swing
        // and busy -- which is correct behaviour, not round-robin, and would mask what is under test.
        var first = new Inserter(src, dstA, 0, swingTicks: 1);
        var second = new Inserter(src, dstB, 0, swingTicks: 1);

        // Ticked in ascending id: `first` grabs, `second` finds nothing.
        first.Step();
        second.Step();
        Assert.Equal(1, dstA.Count);
        Assert.Equal(0, dstB.Count);

        // Both idle every tick, one item every tick: `first` wins EVERY time, forever. Round-robin
        // would split these roughly 20/20.
        for (int i = 0; i < 40; i++)
        {
            src.Count = 1;
            first.Step();
            second.Step();
        }
        Assert.Equal(41, dstA.Count);
        Assert.Equal(0, dstB.Count);
    }

    /// <summary>
    /// §10.4 in the real fixture: insB and insC share a lane head, and insB wins. insC starving is
    /// the SPECIFIED outcome, not a bug -- it is the sim telling the player two arms are fighting.
    /// </summary>
    [Fact]
    public void SharedLaneHeadStarvesTheHigherIdInserter()
    {
        var w = new World(C(), 1_000_000, inserter: true);
        for (int i = 0; i < 6000; i++) w.Step();

        Assert.Equal(3, w.Inserters.Count);
        // insA saturates the lane and sits blocked holding (§10.2).
        Assert.Equal(InserterState.Blocked, w.Inserters[0].State);
        Assert.True(w.Inserters[0].Holding);
        // insC (id 2) is the loser of the contention: it is idle while insB works.
        Assert.Equal(InserterState.Idle, w.Inserters[2].State);
    }

    /// <summary>
    /// §10 makes the inserter pair the bottleneck: 2 x (60/20) = 6 ore/s -> 3 gear/s at 2 plate/gear.
    /// This is §3.3's "the usual real bottleneck" made observable -- and it is BELOW the belt's
    /// 7.5/s and the miners' 10.45/s, so nothing else can be blamed for the rate.
    /// </summary>
    [Fact]
    public void InserterPairIsTheBottleneck()
    {
        var w = new World(C(), 1_000_000, inserter: true);
        for (int i = 0; i < 6000; i++) w.Step();
        var at6000 = w.Gear.Count;
        for (int i = 0; i < 6000; i++) w.Step();

        var gearsPerSecond = (w.Gear.Count - at6000) / (6000.0 / w.TickRate);
        Assert.Equal(3.0, gearsPerSecond, precision: 10);
    }
}
