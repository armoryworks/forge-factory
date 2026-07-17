namespace Forge.Sim;

/// <summary>
/// A port a machine can move items through. Spec: transport-v0.md §2.3/§2.4.
///
/// The point of the abstraction is that <see cref="Machine"/> cannot tell whether it is wired to a
/// buffer or a belt lane: §3.1's rate math must not change because transport appeared underneath
/// it. That the steady/backpressure hashes survive this refactor unchanged is the evidence it did
/// not.
/// </summary>
public interface IPort
{
    bool CanTake(int n);
    void Take(int n);
    bool CanPut(int n);
    void Put(int n);
}

/// <summary>
/// A maximally compressed, single-typed block of items. transport-v0.md §1.
///
/// Mutable class, not a struct: runs are edited in place during advance/merge, and a struct in a
/// List&lt;T&gt; would hand out copies and silently drop the writes.
/// </summary>
public sealed class Run(int head, int len, int item)
{
    /// <summary>Fx32 position of the FRONTMOST item in this run.</summary>
    public int Head = head;
    public int Len = len;
    public readonly int Item = item;

    public int Tail(int sItem) => Head - (Len - 1) * sItem;
}

/// <summary>One lane of a belt. transport-v0.md §§1-2.</summary>
public sealed class Lane(int lengthTiles, int speed, int sItem)
{
    /// <summary>Lane length in Fx32 tiles.</summary>
    public readonly int Length = lengthTiles * Fx32.One;

    /// <summary>Fx32 tiles per tick, from the belt tier (content, not code).</summary>
    public readonly int Speed = speed;

    private readonly int _s = sItem;

    /// <summary>Runs ordered front (index 0, largest position) to back.</summary>
    public readonly List<Run> Runs = [];

    /// <summary>
    /// §2.1 advance, front to back. The order is spec, not style: back-to-front would make a
    /// compressed block expand one run per tick, so throughput would depend on how the runs
    /// happened to be fragmented -- on history rather than on state.
    /// </summary>
    public void Advance()
    {
        for (int i = 0; i < Runs.Count; i++)
        {
            // The front run is bounded by the LANE END, not infinity. factory-math-v0.md §3.3
            // originally said the front gap was infinite; unbounded, the frontmost item advances
            // past the end of the belt forever. See transport-v0.md §0.
            var limit = i == 0 ? Length : Runs[i - 1].Tail(_s) - _s;
            var step = limit - Runs[i].Head;
            if (step < 0)
                throw new InvalidOperationException(
                    $"transport invariant 1/2 broken: run {i} at {Runs[i].Head} overlaps limit {limit}");
            if (step > Speed) step = Speed;
            if (step > 0) Runs[i].Head += step;
        }
    }

    /// <summary>
    /// §2.2 merge, back to front. Exact equality, not a tolerance: positions are integers (§1.2),
    /// so == is meaningful here in a way it would not be with floats -- a float model would need an
    /// epsilon, and that epsilon would be a tuning parameter that silently changes throughput.
    /// </summary>
    public void Merge()
    {
        for (int i = Runs.Count - 1; i >= 1; i--)
        {
            Run ahead = Runs[i - 1], back = Runs[i];
            if (ahead.Item == back.Item && ahead.Tail(_s) - back.Head == _s)
            {
                ahead.Len += back.Len;      // head unchanged: the front item did not move
                Runs.RemoveAt(i);
            }
        }
    }

    /// <summary>§2.3: an item may enter at position 0 only if that respects spacing.</summary>
    public bool CanInsert() => Runs.Count == 0 || Runs[^1].Tail(_s) >= _s;

    public void Insert(int item)
    {
        // CanInsert is the documented precondition (§2.3), and the machine path always honours it
        // via IPort.CanPut. Enforce it anyway: without this, misuse silently creates overlapping
        // runs and the invariant only trips on a LATER Advance, pointing at the wrong code. Fail at
        // the call site instead.
        if (!CanInsert())
            throw new InvalidOperationException(
                "Lane.Insert without CanInsert: no room at the tail (would violate the §1.2 spacing invariant)");

        var back = Runs.Count > 0 ? Runs[^1] : null;
        if (back is not null && back.Item == item && back.Tail(_s) == _s)
            back.Len += 1;                  // the new item lands exactly at 0, extending the block
        else
            Runs.Add(new Run(0, 1, item));
    }

    /// <summary>§2.4: arrived == at the head. Head cannot exceed Length, so == is the right test.</summary>
    public bool CanTake() => Runs.Count > 0 && Runs[0].Head == Length;

    public int Take()
    {
        var front = Runs[0];
        var item = front.Item;
        front.Head -= _s;                   // the next item becomes the frontmost
        front.Len -= 1;
        if (front.Len == 0) Runs.RemoveAt(0);
        return item;
    }

    public int ItemCount()
    {
        var n = 0;
        foreach (var r in Runs) n += r.Len;
        return n;
    }
}

/// <summary>Two independent lanes. transport-v0.md §1.</summary>
public sealed class Belt
{
    public readonly Lane[] Lanes;

    public Belt(int lengthTiles, int speed, int sItem, int lanes = 2)
    {
        Lanes = new Lane[lanes];
        for (int i = 0; i < lanes; i++) Lanes[i] = new Lane(lengthTiles, speed, sItem);
    }

    public void Step()
    {
        // Lane 0 before lane 1, always (transport-v0.md §8).
        foreach (var lane in Lanes)
        {
            lane.Advance();
            lane.Merge();
        }
    }
}

/// <summary>Machine output -> belt tail. transport-v0.md §2.3.</summary>
public sealed class LaneSink(Lane lane, int item) : IPort
{
    public bool CanPut(int n) => n == 1
        ? lane.CanInsert()
        : throw new NotSupportedException("v0 transport moves one item at a time");

    public void Put(int n) => lane.Insert(item);

    public bool CanTake(int n) => throw new NotSupportedException("LaneSink is output-only");
    public void Take(int n) => throw new NotSupportedException("LaneSink is output-only");
}

/// <summary>Belt head -> machine input. transport-v0.md §2.4.</summary>
public sealed class LaneSource(Lane lane, int item) : IPort
{
    public bool CanTake(int n) => n == 1
        ? lane.CanTake() && lane.Runs[0].Item == item
        : throw new NotSupportedException("v0 transport moves one item at a time");

    public void Take(int n) => lane.Take();

    public bool CanPut(int n) => throw new NotSupportedException("LaneSource is input-only");
    public void Put(int n) => throw new NotSupportedException("LaneSource is input-only");
}

/// <summary>
/// transport-v0.md §6. Up to 2 inputs, up to 2 outputs, at most one item per tick.
///
/// The whole behaviour is a fair alternation rule, and its whole risk is that the alternation state
/// is INVISIBLE: <see cref="InNext"/>/<see cref="OutNext"/> never appear in the UI, change on every
/// item, and change future evolution. Two worlds identical but for OutNext diverge on the very next
/// item and then forever. That is why §6.3 requires them hashed -- it is exactly the class of state
/// a "hash the obvious stuff" encoding misses.
/// </summary>
public sealed class Splitter(Lane[] ins, Lane[] outs)
{
    public readonly Lane[] Ins = ins;
    public readonly Lane[] Outs = outs;

    /// <summary>Which input to prefer next. Hashed (§6.3).</summary>
    public byte InNext;

    /// <summary>Which output to prefer next. Hashed (§6.3).</summary>
    public byte OutNext;

    public void Step()
    {
        // 1. Choose input: prefer InNext, else the other.
        int src = -1;
        for (int k = 0; k < Ins.Length; k++)
        {
            var i = (InNext + k) % Ins.Length;
            if (Ins[i].CanTake()) { src = i; break; }
        }
        if (src < 0) return;

        // 2. Choose output: prefer OutNext, else the other. If neither, leave the item where it is.
        int dst = -1;
        for (int k = 0; k < Outs.Length; k++)
        {
            var o = (OutNext + k) % Outs.Length;
            if (Outs[o].CanInsert()) { dst = o; break; }
        }
        if (dst < 0) return;

        // 3. Move exactly one item.
        var item = Ins[src].Take();
        Outs[dst].Insert(item);

        // 4/5. Flip from the side ACTUALLY used, not the side preferred (§6.2). Both regimes then
        // work with no special case: strict alternation while both are free, and when one side is
        // blocked the pointer parks on it so it gets the next item the instant it frees -- the
        // splitter recovers to fairness immediately rather than after a drift-out period.
        InNext = (byte)((src + 1) % Ins.Length);
        OutNext = (byte)((dst + 1) % Outs.Length);
    }
}
