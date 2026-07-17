using Forge.Sim;
using Xunit;

namespace Forge.Sim.Tests;

/// <summary>
/// transport-v0.md §2.5 / B67: a later batch joins an existing belt instead of forming an isolated
/// stub. B56 deferred this on the grounds that joining meant "rebuilding a live lane and either
/// discarding its items or migrating them, and neither is specified". That framing was wrong:
/// growth is an exact integer offset, and because every run shifts equally the spacing invariant
/// holds by construction. These tests pin that -- especially that items SURVIVE a join, which is
/// the thing the deferral was worried about.
/// </summary>
public class ChainJoinTests
{
    private static Content C() => Content.Load(GoldenTestsPaths.Recipes);
    private static BeltPlacement P(int x, int y, int dir) => new(x, y, dir);

    /// <summary>A later cell feeding INTO an existing belt extends it, rather than making a stub.</summary>
    [Fact]
    public void TailJoinExtendsTheExistingBeltInsteadOfCreatingAStub()
    {
        var w = new World(C(), 1_000_000);
        w.ApplyBeltBatch(C(), [P(10, 10, 1), P(11, 10, 1)]);      // 2-tile belt, cells 10..11
        Assert.Single(w.Belts);
        Assert.Equal(2 * Fx32.One, w.Belts[0].Lanes[0].Length);

        // (9,10) points east into (10,10) -- the existing belt's FIRST cell. It feeds it.
        w.ApplyBeltBatch(C(), [P(9, 10, 1)]);

        Assert.Single(w.Belts);                                    // joined, NOT a second belt
        Assert.Equal(3 * Fx32.One, w.Belts[0].Lanes[0].Length);
    }

    /// <summary>A later cell an existing belt feeds INTO extends it at the head.</summary>
    [Fact]
    public void HeadJoinExtendsTheExistingBelt()
    {
        var w = new World(C(), 1_000_000);
        w.ApplyBeltBatch(C(), [P(10, 10, 1), P(11, 10, 1)]);       // head cell is (11,10), pointing east
        w.ApplyBeltBatch(C(), [P(12, 10, 1)]);                     // (11,10).Ahead == (12,10)

        Assert.Single(w.Belts);
        Assert.Equal(3 * Fx32.One, w.Belts[0].Lanes[0].Length);
    }

    /// <summary>
    /// THE property B56's deferral was actually about: items on a live lane survive the join.
    /// A tail-join moves the origin back behind them, so every head shifts by exactly the added
    /// length -- no item is lost, and no spacing changes.
    /// </summary>
    [Fact]
    public void TailJoinPreservesItemsByShiftingThemNotDiscardingThem()
    {
        var w = new World(C(), 1_000_000);
        w.ApplyBeltBatch(C(), [P(10, 10, 1), P(11, 10, 1)]);
        var lane = w.Belts[0].Lanes[0];

        lane.Insert(0);
        for (int i = 0; i < 8; i++) lane.Advance();
        var headBefore = lane.Runs[0].Head;
        var countBefore = lane.ItemCount();

        w.ApplyBeltBatch(C(), [P(9, 10, 1)]);                      // tail-join: +1 tile behind

        Assert.Equal(countBefore, lane.ItemCount());               // conservation
        Assert.Equal(headBefore + Fx32.One, lane.Runs[0].Head);    // shifted by exactly the growth
        Assert.Equal(3 * Fx32.One, lane.Length);
        lane.Advance();                                            // invariants still hold
    }

    /// <summary>A head-join must NOT move items: the tail they are measured from did not move.</summary>
    [Fact]
    public void HeadJoinLeavesItemPositionsUntouched()
    {
        var w = new World(C(), 1_000_000);
        w.ApplyBeltBatch(C(), [P(10, 10, 1), P(11, 10, 1)]);
        var lane = w.Belts[0].Lanes[0];

        lane.Insert(0);
        // A 2-tile lane is 131072 Fx32; at speed 2048 that is 64 ticks to traverse. Run past it so
        // the item is genuinely parked at the end rather than still in transit.
        for (int i = 0; i < 80; i++) lane.Advance();
        Assert.True(lane.CanTake());                               // parked at the old end
        var headBefore = lane.Runs[0].Head;

        w.ApplyBeltBatch(C(), [P(12, 10, 1)]);                     // head-join: +1 tile in front

        Assert.Equal(headBefore, lane.Runs[0].Head);               // did not move
        Assert.False(lane.CanTake());                              // the end moved away from it
        lane.Advance();
        Assert.Equal(headBefore + lane.Speed, lane.Runs[0].Head);  // it resumes flowing
    }

    /// <summary>Chain-join must not disturb a fixture belt: those have no cells, so they never join.</summary>
    [Fact]
    public void ScenarioBeltsAreNeverJoinTargets()
    {
        var w = new World(C(), 1_000_000, transport: true);
        Assert.Single(w.Belts);
        var fixtureLength = w.Belts[0].Lanes[0].Length;

        w.ApplyBeltBatch(C(), [P(10, 10, 1)]);

        Assert.Equal(2, w.Belts.Count);                            // a NEW belt, not an extension
        Assert.Equal(fixtureLength, w.Belts[0].Lanes[0].Length);   // the fixture belt is untouched
    }

    /// <summary>Join must be order-independent, like every other placement outcome (D23).</summary>
    [Fact]
    public void JoinOutcomeIsIndependentOfArrivalOrder()
    {
        var a = new World(C(), 1_000_000);
        a.ApplyBeltBatch(C(), [P(10, 10, 1)]);
        a.ApplyBeltBatch(C(), [P(11, 10, 1), P(12, 10, 1)]);

        var b = new World(C(), 1_000_000);
        b.ApplyBeltBatch(C(), [P(10, 10, 1)]);
        b.ApplyBeltBatch(C(), [P(12, 10, 1), P(11, 10, 1)]);       // reversed arrival

        Assert.Equal(a.HashHex(), b.HashHex());
        Assert.Equal(a.Belts.Count, b.Belts.Count);
    }
}
