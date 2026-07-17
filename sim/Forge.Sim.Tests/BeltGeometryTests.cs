using Forge.Sim;
using Xunit;

namespace Forge.Sim.Tests;

/// <summary>
/// B66: every belt must be able to say WHERE it is, so a client can draw the items it is told about.
/// Before this, `beltDeltas.belt` was an opaque index and fixture belts had no cells at all.
/// </summary>
public class BeltGeometryTests
{
    private static Content C() => Content.Load(GoldenTestsPaths.Recipes);
    private static BeltPlacement P(int x, int y, int dir) => new(x, y, dir);

    /// <summary>
    /// THE constraint that was easiest to miss: seeded belts, not just client-posted ones. The
    /// transport fixture's belt:0 is exactly the belt iso could not render.
    /// </summary>
    [Fact]
    public void SeededBeltsHaveCells()
    {
        var w = new World(C(), 1_000_000, transport: true);
        var cells = w.BeltCells(0);

        Assert.Equal(World.TransportBeltTiles, cells.Count);
        Assert.Equal(P(World.FixtureBeltOriginX, World.FixtureBeltOriginY, 1), cells[0]);
        // Travel order, tail-first, contiguous: each cell points at the next.
        for (int i = 0; i < cells.Count - 1; i++)
            Assert.Equal((cells[i + 1].X, cells[i + 1].Y), cells[i].Ahead);
    }

    /// <summary>The splitter fixture's two belts must not share a cell.</summary>
    [Fact]
    public void SeededSplitterBeltsDoNotOverlap()
    {
        var w = new World(C(), 1_000_000, splitter: true);
        Assert.Equal(2, w.Belts.Count);

        var a = w.BeltCells(0).Select(p => (p.X, p.Y)).ToHashSet();
        var b = w.BeltCells(1).Select(p => (p.X, p.Y)).ToHashSet();
        Assert.NotEmpty(a);
        Assert.NotEmpty(b);
        Assert.Empty(a.Intersect(b));
    }

    /// <summary>Placed belts report the cells that were posted, in travel order.</summary>
    [Fact]
    public void PlacedBeltsReportTheirPostedCells()
    {
        var w = new World(C(), 1_000_000);
        w.ApplyBeltBatch(C(), [P(10, 10, 1), P(11, 10, 1), P(12, 10, 1)]);

        var cells = w.BeltCells(0);
        Assert.Equal(3, cells.Count);
        Assert.Equal([P(10, 10, 1), P(11, 10, 1), P(12, 10, 1)], cells);
    }

    /// <summary>Geometry must track a §2.5 join, or it goes stale the moment a belt grows.</summary>
    [Fact]
    public void GeometryFollowsAChainJoin()
    {
        var w = new World(C(), 1_000_000);
        w.ApplyBeltBatch(C(), [P(10, 10, 1), P(11, 10, 1)]);
        w.ApplyBeltBatch(C(), [P(9, 10, 1)]);          // tail-join

        var cells = w.BeltCells(0);
        Assert.Equal(3, cells.Count);
        Assert.Equal(P(9, 10, 1), cells[0]);           // the new cell is now the TAIL
        Assert.Equal(cells.Count * Fx32.One, w.Belts[0].Lanes[0].Length);
    }

    /// <summary>
    /// A belt chain can TURN, which is why geometry is cells[] and not origin+dir+len. A corner is
    /// the case that shape would silently get wrong.
    /// </summary>
    [Fact]
    public void GeometrySurvivesACorner()
    {
        var w = new World(C(), 1_000_000);
        // east, east, then south -- (12,10) points at (12,11).
        w.ApplyBeltBatch(C(), [P(10, 10, 1), P(11, 10, 1), P(12, 10, 2), P(12, 11, 2)]);

        var cells = w.BeltCells(0);
        Assert.Equal(4, cells.Count);
        Assert.Contains(cells, p => p.Dir == 1);
        Assert.Contains(cells, p => p.Dir == 2);       // not describable by a single dir
        for (int i = 0; i < cells.Count - 1; i++)
            Assert.Equal((cells[i + 1].X, cells[i + 1].Y), cells[i].Ahead);
    }

    /// <summary>
    /// Now that seeded belts own cells, the world is COHERENT about them: you cannot build on top of
    /// the seeded belt. That was impossible to express before -- the emission would have claimed a
    /// position the sim did not believe in.
    /// </summary>
    [Fact]
    public void SeededBeltCellsAreOccupied()
    {
        var w = new World(C(), 1_000_000, transport: true);
        var seeded = w.BeltCells(0)[5];

        var r = w.ApplyBeltBatch(C(), [P(seeded.X, seeded.Y, 1)]);

        Assert.Equal(PlacementRejection.Occupied, r[0].reason);
    }
}
