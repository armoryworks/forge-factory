using Forge.Sim;
using Xunit;

namespace Forge.Sim.Tests;

/// <summary>
/// B56/D23: belt placement is the sim's FIRST external input. Until it existed, World was a closed
/// system -- a pure function of (state, tick) -- which is why simprobe can replay from content alone
/// and match the adapter bit-for-bit. These pin the properties that keep that true.
/// </summary>
public class PlacementTests
{
    private static Content C() => Content.Load(GoldenTestsPaths.Recipes);
    private static BeltPlacement P(int x, int y, int dir) => new(x, y, dir);

    /// <summary>
    /// THE determinism property. Arrival order is wall-clock dependent: two hosts receiving the same
    /// POSTs in different network order must still produce identical worlds, or lockstep is dead.
    /// </summary>
    [Fact]
    public void ApplyOrderIsIndependentOfArrivalOrder()
    {
        var batch = new[] { P(52, 50, 1), P(50, 50, 1), P(51, 50, 1) };

        var a = new World(C(), 1_000_000, transport: true);
        a.ApplyBeltBatch(C(), batch);

        var b = new World(C(), 1_000_000, transport: true);
        b.ApplyBeltBatch(C(), batch.Reverse());   // same placements, opposite arrival order

        Assert.Equal(a.HashHex(), b.HashHex());
        Assert.Equal(a.Belts.Count, b.Belts.Count);
    }

    /// <summary>A contiguous posted run becomes ONE lane of that many tiles, not N one-tile belts.</summary>
    [Fact]
    public void ContiguousCellsChainIntoOneLane()
    {
        var w = new World(C(), 1_000_000);
        var r = w.ApplyBeltBatch(C(), [P(50, 50, 1), P(51, 50, 1), P(52, 50, 1)]);

        Assert.All(r, x => Assert.Equal(PlacementRejection.None, x.reason));
        Assert.Single(w.Belts);
        Assert.Equal(3 * Fx32.One, w.Belts[0].Lanes[0].Length);
    }

    /// <summary>Two disjoint runs are two belts -- chains must not be merged across a gap.</summary>
    [Fact]
    public void DisjointRunsBecomeSeparateBelts()
    {
        var w = new World(C(), 1_000_000);
        w.ApplyBeltBatch(C(), [P(10, 10, 1), P(11, 10, 1), P(80, 80, 1)]);
        Assert.Equal(2, w.Belts.Count);
    }

    /// <summary>Refusal is a NORMAL event and must be reported, not thrown.</summary>
    [Fact]
    public void RejectsOffMapBadDirOccupiedAndDuplicates()
    {
        var w = new World(C(), 1_000_000);
        w.ApplyBeltBatch(C(), [P(5, 5, 1)]);

        var r = w.ApplyBeltBatch(C(), [
            P(-1, 0, 1),            // off-map
            P(World.MapSize, 0, 1), // off-map
            P(9, 9, 7),             // bad dir
            P(5, 5, 1),             // occupied by the earlier batch
            P(20, 20, 1), P(20, 20, 2), // duplicate cell within one batch
        ]);

        PlacementRejection Why(int x, int y, int dir) =>
            r.First(e => e.placement == P(x, y, dir)).reason;

        Assert.Equal(PlacementRejection.OffMap, Why(-1, 0, 1));
        Assert.Equal(PlacementRejection.OffMap, Why(World.MapSize, 0, 1));
        Assert.Equal(PlacementRejection.BadDir, Why(9, 9, 7));
        Assert.Equal(PlacementRejection.Occupied, Why(5, 5, 1));
        // (20,20,1) sorts before (20,20,2), so the first wins and the second is the duplicate.
        Assert.Equal(PlacementRejection.None, Why(20, 20, 1));
        Assert.Equal(PlacementRejection.DuplicateInBatch, Why(20, 20, 2));
    }

    /// <summary>
    /// §1.3: replays store (seed, input_stream). Placements MUST be recorded and tick-stamped, or a
    /// replay cannot reproduce the world and the parity check dies the first time anyone posts.
    /// </summary>
    [Fact]
    public void AcceptedPlacementsAreRecordedAsTickStampedInputs()
    {
        var w = new World(C(), 1_000_000);
        for (int i = 0; i < 10; i++) w.Step();
        w.ApplyBeltBatch(C(), [P(1, 1, 1), P(-5, -5, 1)]);   // one accepted, one off-map

        Assert.Single(w.Inputs);                              // rejected inputs are not recorded
        Assert.Equal(10UL, w.Inputs[0].tick);
        Assert.Equal(P(1, 1, 1), w.Inputs[0].placement);
    }

    /// <summary>
    /// Replaying the recorded input stream at the same ticks reproduces the world exactly. This is
    /// the property simprobe's hash parity now depends on, so it is pinned here rather than only
    /// observed live.
    /// </summary>
    [Fact]
    public void ReplayingTheInputStreamReproducesTheWorld()
    {
        var live = new World(C(), 1_000_000, transport: true);
        for (int i = 0; i < 30; i++) live.Step();
        live.ApplyBeltBatch(C(), [P(60, 60, 1), P(61, 60, 1)]);
        for (int i = 0; i < 120; i++) live.Step();

        // Rebuild from content + the recorded stream alone.
        var replay = new World(C(), 1_000_000, transport: true);
        foreach (var group in live.Inputs.GroupBy(i => i.tick).OrderBy(g => g.Key))
        {
            while (replay.Tick < group.Key) replay.Step();
            replay.ApplyBeltBatch(C(), group.Select(g => g.placement));
        }
        while (replay.Tick < live.Tick) replay.Step();

        Assert.Equal(live.HashHex(), replay.HashHex());
    }

    /// <summary>
    /// D23's hash consequence: belts are DYNAMIC now, so the belt count is hashed (transport-v0.md
    /// §7.1 as amended). Two worlds differing only in belt count must not collide.
    /// </summary>
    [Fact]
    public void BeltCountContributesToTheHash()
    {
        var a = new World(C(), 1_000_000);
        var b = new World(C(), 1_000_000);
        Assert.Equal(a.HashHex(), b.HashHex());

        b.ApplyBeltBatch(C(), [P(30, 30, 1)]);
        Assert.NotEqual(a.HashHex(), b.HashHex());
    }
}
