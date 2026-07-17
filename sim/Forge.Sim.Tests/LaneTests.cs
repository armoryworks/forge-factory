using Forge.Sim;
using Xunit;

namespace Forge.Sim.Tests;

/// <summary>
/// Direct unit tests for transport-v0.md §2, using geometry chosen to EXPOSE the rules.
///
/// WHY THESE EXIST. The `transport` golden scenario does not test most of §2. Mutation testing
/// proved it: breaking the front-run bound, disabling merge entirely, reversing the advance order,
/// and loosening the insert spacing all left the golden green. Instrumenting the reference sim
/// showed why -- across 12000 ticks the fixture's lane never holds more than ONE run and merge()
/// never fires once. A single-run belt cannot exercise multi-run ordering or merging, and the
/// fixture's geometry hides the rest:
///
///   L/v = 1310720/2048 = 640 EXACTLY, and S/v = 8 EXACTLY. Items land precisely on the lane end
///   whether or not the front run is bounded, so `limit = L` and `limit = infinity` are
///   indistinguishable there. And every position is a multiple of v = 2048, so `tail >= S` and
///   `tail >= S-1` can never disagree.
///
/// So the golden pins throughput, transit latency and insert-time compression -- real properties,
/// but not the rules below. Each test here is built to kill a specific mutant the golden survives;
/// the geometry (odd speeds, short lanes, mixed item types) is deliberate, not arbitrary.
/// </summary>
public class LaneTests
{
    private const int S = 16384;        // 0.25 tiles, matching content item_spacing
    private const int V = 2048;         // tier-I speed

    /// <summary>
    /// §2.1/§0: the front run is bounded by the LANE END, not infinity. This is the exact bug that
    /// shipped in factory-math-v0.md §3.3 ("∞ for the front run"), and the golden cannot see it.
    /// </summary>
    [Fact]
    public void FrontRunStopsAtTheLaneEnd()
    {
        var lane = new Lane(lengthTiles: 2, speed: V, sItem: S);
        lane.Insert(item: 0);

        // Far more ticks than needed to traverse: an unbounded front run would sail past the end.
        for (int i = 0; i < 500; i++)
        {
            lane.Advance();
            lane.Merge();
            Assert.True(lane.Runs[0].Head <= lane.Length,
                $"item ran off the end of the belt: head={lane.Runs[0].Head} > L={lane.Length}");
        }

        Assert.Equal(lane.Length, lane.Runs[0].Head);
        Assert.True(lane.CanTake());
    }

    /// <summary>
    /// §2.2: when a trailing run closes the gap to the run ahead, they merge. The fixture never
    /// exercises this -- its lane is one run maintained by insert-time extension, so merge() is
    /// dead code there. Without merging, runs fragment and the O(runs) claim (§4) quietly dies.
    /// </summary>
    [Fact]
    public void RunsMergeWhenTheGapCloses()
    {
        var lane = new Lane(lengthTiles: 2, speed: V, sItem: S);

        lane.Insert(0);
        for (int i = 0; i < 24; i++) lane.Advance();   // pull it well clear of the tail
        lane.Insert(0);                                // gap is > S, so this is a SECOND run
        Assert.Equal(2, lane.Runs.Count);

        // No consumer: the front parks at L, the back closes up behind it, and they become one.
        for (int i = 0; i < 200; i++) { lane.Advance(); lane.Merge(); }

        Assert.Single(lane.Runs);
        Assert.Equal(2, lane.Runs[0].Len);
        Assert.Equal(lane.Length, lane.Runs[0].Head);
        Assert.Equal(lane.Length - S, lane.Runs[0].Tail(S));   // exactly one spacing behind
    }

    /// <summary>
    /// §2.1: advance is front-to-back, and that is spec rather than style. Back-to-front would make
    /// a trailing run read the run ahead's PRE-MOVE position, so a touching block would expand one
    /// run per tick instead of moving as a unit -- throughput would depend on how runs happened to
    /// be fragmented, i.e. on history rather than state.
    ///
    /// Two touching runs of DIFFERENT items (so they cannot merge) make the order observable in a
    /// single tick.
    /// </summary>
    [Fact]
    public void AdvanceIsFrontToBackSoTouchingRunsMoveAsAUnit()
    {
        var lane = new Lane(lengthTiles: 10, speed: V, sItem: S);

        lane.Insert(0);
        for (int i = 0; i < 8; i++) lane.Advance();     // head = 8*V = S exactly
        Assert.Equal(S, lane.Runs[0].Head);

        lane.Insert(1);                                 // different item -> second run at 0, touching
        Assert.Equal(2, lane.Runs.Count);
        Assert.Equal(S, lane.Runs[0].Tail(S) - lane.Runs[1].Head);

        lane.Advance();

        // Front-to-back: the back run sees the front's NEW position and advances a full step.
        // Back-to-front would leave it pinned at 0 this tick.
        Assert.Equal(S + V, lane.Runs[0].Head);
        Assert.Equal(V, lane.Runs[1].Head);
    }

    /// <summary>
    /// §2.3: insertion requires a full spacing, exactly -- `tail >= s`, not `tail > s - 1`.
    ///
    /// Needs speed 1 to be visible at all: at tier-I speed every position is a multiple of 2048, so
    /// tail can never actually BE s-1 and the off-by-one is unobservable. That is precisely why the
    /// golden cannot catch it.
    /// </summary>
    [Fact]
    public void InsertRequiresAFullSpacingNotOneLess()
    {
        var lane = new Lane(lengthTiles: 2, speed: 1, sItem: S);
        lane.Insert(0);

        for (int i = 0; i < S - 1; i++) lane.Advance();
        Assert.Equal(S - 1, lane.Runs[0].Tail(S));
        Assert.False(lane.CanInsert(), "inserting at tail = s-1 would violate the spacing invariant");

        lane.Advance();
        Assert.Equal(S, lane.Runs[0].Tail(S));
        Assert.True(lane.CanInsert(), "tail = s is exactly enough room");

        lane.Insert(0);
        Assert.Single(lane.Runs);                      // same item at exactly s -> extends the block
        Assert.Equal(2, lane.Runs[0].Len);
    }

    /// <summary>§2.4: taking the front item promotes the next one, one spacing back.</summary>
    [Fact]
    public void TakingTheFrontItemPromotesTheNext()
    {
        var lane = new Lane(lengthTiles: 2, speed: V, sItem: S);
        lane.Insert(0);
        for (int i = 0; i < 8; i++) lane.Advance();     // clear one spacing before inserting again
        lane.Insert(0);
        for (int i = 0; i < 200; i++) { lane.Advance(); lane.Merge(); }

        Assert.Single(lane.Runs);
        Assert.Equal(2, lane.Runs[0].Len);
        Assert.True(lane.CanTake());

        Assert.Equal(0, lane.Take());
        Assert.Equal(1, lane.Runs[0].Len);
        Assert.Equal(lane.Length - S, lane.Runs[0].Head);
        Assert.False(lane.CanTake());                   // the promoted item has not arrived yet

        for (int i = 0; i < 8; i++) lane.Advance();     // S/V = 8 ticks to cover one spacing
        Assert.True(lane.CanTake());
        Assert.Equal(0, lane.Take());
        Assert.Empty(lane.Runs);
    }

    /// <summary>
    /// §3: Θ_lane = v/s. Drained as fast as it arrives, a saturated lane delivers one item every
    /// s/v ticks. Asserted directly rather than inferred from the chain's gear rate.
    /// </summary>
    [Fact]
    public void SaturatedLaneDeliversAtThetaLane()
    {
        var lane = new Lane(lengthTiles: 4, speed: V, sItem: S);
        var delivered = 0;
        const int ticks = 6000;

        for (int i = 0; i < ticks; i++)
        {
            if (lane.CanInsert()) lane.Insert(0);       // an infinitely fast producer
            lane.Advance();
            lane.Merge();
            if (lane.CanTake()) { lane.Take(); delivered++; }
        }

        // Θ_lane = V/S = 2048/16384 = 0.125 items/tick = 7.5/s at 60 Hz. Allow the pipeline-fill
        // transient (the first item needs L/v ticks to arrive) but nothing else.
        var perTick = delivered / (double)ticks;
        Assert.InRange(perTick, 0.120, 0.125);
    }

    /// <summary>Lanes are independent: lane 1 must not move because lane 0 did.</summary>
    [Fact]
    public void LanesAreIndependent()
    {
        var belt = new Belt(lengthTiles: 2, speed: V, sItem: S);
        belt.Lanes[0].Insert(0);
        for (int i = 0; i < 50; i++) belt.Step();

        Assert.Single(belt.Lanes[0].Runs);
        Assert.Empty(belt.Lanes[1].Runs);
    }
}
