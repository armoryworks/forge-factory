using Forge.Sim;
using Xunit;

namespace Forge.Sim.Tests;

/// <summary>
/// B68: checkpoint restore. The acceptance contract is exact -- export -> restore -> hash MUST equal
/// the pre-export hash. That is the only check that catches a field quietly left out of Export(),
/// which is the failure mode a snapshot invites: everything looks right until the one field nobody
/// serialised drifts.
/// </summary>
public class CheckpointTests
{
    private static Content C() => Content.Load(GoldenTestsPaths.Recipes);
    private static BeltPlacement P(int x, int y, int dir) => new(x, y, dir);

    private static World Round(World w) => World.Restore(C(), Checkpoint.FromBytes(w.Export().ToBytes()));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ExportRestoreReproducesTheHashExactly(bool transport, bool splitter, bool inserter)
    {
        var w = new World(C(), 1_000_000, transport: transport, splitter: splitter, inserter: inserter);
        for (int i = 0; i < 2000; i++) w.Step();

        var restored = Round(w);

        Assert.Equal(w.Tick, restored.Tick);
        Assert.Equal(w.HashHex(), restored.HashHex());
    }

    /// <summary>
    /// A restored world must keep SIMULATING identically, not merely look identical. A snapshot that
    /// restores the hash but breaks the machine<->lane wiring passes the hash check at tick N and
    /// diverges at N+1 -- which is exactly the bug the object-reference wiring invites.
    /// </summary>
    [Fact]
    public void ARestoredWorldKeepsTickingIdentically()
    {
        var w = new World(C(), 1_000_000, transport: true);
        for (int i = 0; i < 1500; i++) w.Step();

        var restored = Round(w);
        for (int i = 0; i < 500; i++) { w.Step(); restored.Step(); }

        Assert.Equal(w.HashHex(), restored.HashHex());
        Assert.Equal(w.Gear.Count, restored.Gear.Count);
        Assert.True(w.Gear.Count > 0, "the chain should be producing, or this proves nothing");
    }

    /// <summary>Placed belts are created by inputs, not the constructor -- restore must rebuild them.</summary>
    [Fact]
    public void PlacedBeltsSurviveRestore()
    {
        var w = new World(C(), 1_000_000, transport: true);
        for (int i = 0; i < 100; i++) w.Step();
        w.ApplyBeltBatch(C(), [P(50, 50, 1), P(51, 50, 1), P(52, 50, 1)]);
        for (int i = 0; i < 100; i++) w.Step();

        var restored = Round(w);

        Assert.Equal(w.Belts.Count, restored.Belts.Count);
        Assert.Equal(2, restored.Belts.Count);                       // fixture + placed
        Assert.Equal(3 * Fx32.One, restored.Belts[1].Lanes[0].Length);
        Assert.Equal(w.HashHex(), restored.HashHex());
    }

    /// <summary>
    /// B66 geometry must survive, for BOTH seeded and placed belts. A world restored hash-equal but
    /// geometrically blank would pass the headline test and still be unrenderable -- and every cell
    /// would silently be free to build on again.
    /// </summary>
    [Fact]
    public void GeometrySurvivesRestoreForSeededAndPlacedBelts()
    {
        var w = new World(C(), 1_000_000, transport: true);
        w.ApplyBeltBatch(C(), [P(50, 50, 1), P(51, 50, 1)]);

        var restored = Round(w);

        Assert.Equal(w.BeltCells(0), restored.BeltCells(0));         // seeded
        Assert.Equal(World.TransportBeltTiles, restored.BeltCells(0).Count);
        Assert.Equal(w.BeltCells(1), restored.BeltCells(1));         // placed
        Assert.Equal(2, restored.BeltCells(1).Count);

        // The occupancy index must be rebuilt too, or a restored world lets you build on its belts.
        //
        // Probe a PLACED cell, not just a seeded one. A seeded cell is re-registered by the
        // constructor during Restore, so asserting only on it passes even when the restore loop
        // rebuilds nothing -- mutation testing caught exactly that. Placed cells are the ones that
        // depend on the restore path.
        var onPlaced = restored.ApplyBeltBatch(C(), [P(50, 50, 1)]);
        Assert.Equal(PlacementRejection.Occupied, onPlaced[0].reason);

        var onSeeded = restored.ApplyBeltBatch(C(), [P(5, 0, 1)]);
        Assert.Equal(PlacementRejection.Occupied, onSeeded[0].reason);
    }

    /// <summary>D23's input stream must survive, or a restored world can no longer be replay-verified.</summary>
    [Fact]
    public void InputStreamSurvivesRestore()
    {
        var w = new World(C(), 1_000_000, transport: true);
        for (int i = 0; i < 50; i++) w.Step();
        w.ApplyBeltBatch(C(), [P(60, 60, 1)]);

        var restored = Round(w);

        Assert.Equal(w.Inputs.Count, restored.Inputs.Count);
        Assert.Equal(w.Inputs[0], restored.Inputs[0]);
    }

    /// <summary>Inserter `holding` is one bool and the conservation witness (D24) -- it must round-trip.</summary>
    [Fact]
    public void InserterHoldingSurvivesRestore()
    {
        var w = new World(C(), 1_000_000, inserter: true);
        for (int i = 0; i < 2000; i++) w.Step();
        Assert.Contains(w.Inserters, i => i.Holding);                // or the test proves nothing

        var restored = Round(w);

        for (int i = 0; i < w.Inserters.Count; i++)
        {
            Assert.Equal(w.Inserters[i].Holding, restored.Inserters[i].Holding);
            Assert.Equal(w.Inserters[i].State, restored.Inserters[i].State);
            Assert.Equal(w.Inserters[i].Progress, restored.Inserters[i].Progress);
        }
    }

    /// <summary>A version bump must fail LOUDLY. A half-read blob is worse than no blob.</summary>
    [Fact]
    public void AStaleVersionIsRefused()
    {
        var cp = new World(C(), 1_000_000).Export();
        cp.Version = Checkpoint.CurrentVersion + 1;
        var bytes = cp.ToBytes();

        var ex = Assert.Throws<CheckpointException>(() => Checkpoint.FromBytes(bytes));
        Assert.Contains("version", ex.Message);
    }

    /// <summary>The store seam: absent blob is a normal cold start, not an error.</summary>
    [Fact]
    public void FileStoreRoundTripsAndTreatsAbsentAsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"forge-cp-{Guid.NewGuid():N}.json");
        var store = new FileCheckpointStore(path);
        try
        {
            Assert.Null(store.Load());                               // cold start

            var w = new World(C(), 1_000_000, transport: true);
            for (int i = 0; i < 300; i++) w.Step();
            store.Save(w.Export().ToBytes());

            var restored = World.Restore(C(), Checkpoint.FromBytes(store.Load()!));
            Assert.Equal(w.HashHex(), restored.HashHex());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
