namespace Forge.Sim;

/// <summary>
/// A belt placement request, in the game's cell coordinates. Mirrors belts_for_adapter()'s
/// {cell, dir} verbatim (B52/B56) -- the sim owns lane-building, so the client sends raw cells and
/// never coalesces.
///
/// Dir is PINNED to iso.gd:103-106: N=0, E=1, S=2, W=3. Pinned here rather than inferred because
/// the sim and the client must agree on travel direction or belts silently run backwards.
/// </summary>
public readonly record struct BeltPlacement(int X, int Y, int Dir)
{
    public static readonly (int dx, int dy)[] DirVectors = [(0, -1), (1, 0), (0, 1), (-1, 0)];

    public (int x, int y) Ahead => (X + DirVectors[Dir].dx, Y + DirVectors[Dir].dy);
}

/// <summary>Why a placement was refused. Refusal is a NORMAL event, not an error (B56).</summary>
public enum PlacementRejection { None = 0, OffMap, BadDir, Occupied, DuplicateInBatch }

/// <summary>Machine states. Discriminants are PINNED by §1.5 -- they are hashed as u8.</summary>
public enum MachineState : byte
{
    Idle = 0,
    Crafting = 1,
    Starved = 2,
    Blocked = 3,
}

/// <summary>§4.1: every buffer is finite. There is no infinite sink in the core rules.</summary>
public sealed class Buffer(int cap) : IPort
{
    public int Count;
    public readonly int Cap = cap;

    public bool CanTake(int n) => Count >= n;
    public bool CanPut(int n) => Count + n <= Cap;
    public void Take(int n) => Count -= n;
    public void Put(int n) => Count += n;
}

/// <summary>A wiring: which port, and how many items per craft. The port may be a buffer or a belt.</summary>
public readonly record struct Port(IPort Target, int Count);

/// <summary>
/// A crafting machine. Spec §3.1 (rate, remainder carry) and §4.2 (reservation, blocking).
/// Reservation debits the input buffer immediately: "reserved" means the items are committed.
/// </summary>
public sealed class Machine(int sigma, int goal, Port[] inputs, Port[] outputs)
{
    public readonly int Sigma = sigma;
    public readonly int Goal = goal;
    private readonly Port[] _inputs = inputs;
    private readonly Port[] _outputs = outputs;

    public int Progress;
    public MachineState State = MachineState.Idle;

    private bool TryReserve()
    {
        foreach (var p in _inputs) if (!p.Target.CanTake(p.Count)) return false;
        foreach (var p in _inputs) p.Target.Take(p.Count);
        return true;
    }

    private bool TryEmit()
    {
        foreach (var p in _outputs) if (!p.Target.CanPut(p.Count)) return false;
        foreach (var p in _outputs) p.Target.Put(p.Count);
        return true;
    }

    public void Tick(int satisfaction)
    {
        // §4.2: a Blocked machine HOLDS its completed output at Progress >= Goal. It does not
        // discard it and does not keep crafting, so it resumes with zero restart penalty the tick
        // space clears. A restart penalty would make backpressure lossy and break the §3.4
        // LP-vs-sim equivalence.
        if (State == MachineState.Blocked)
        {
            if (!TryEmit()) return;
            Progress -= Goal;
            State = MachineState.Idle;
        }

        if (State is MachineState.Idle or MachineState.Starved)
        {
            State = TryReserve() ? MachineState.Crafting : MachineState.Starved;
            if (State == MachineState.Starved) return;
        }

        if (State == MachineState.Crafting)
        {
            Progress += Fx32.Mul(Sigma, satisfaction);
            if (Progress >= Goal)
            {
                if (TryEmit())
                {
                    // §3.1: carry the remainder. Resetting to zero loses up to one tick per craft
                    // -- 0.82% on the v0 miner -- and makes the sim disagree with the player's own
                    // arithmetic, which axiom 4 forbids.
                    Progress -= Goal;
                    State = TryReserve() ? MachineState.Crafting : MachineState.Starved;
                }
                else
                {
                    Progress = Goal; // clamp and hold
                    State = MachineState.Blocked;
                }
            }
        }
    }
}

/// <summary>
/// Sim core v0 -- the §3.2 reference build (ore -> plate -> gear).
///
/// SCOPE: this models §3.1 machine rates and §4 buffers/backpressure only. Belts, inserters,
/// fluids, pollution and tech are NOT here, and machines emit directly into shared buffers rather
/// than through transport. The shared buffers knowingly violate axiom 3 (scarcity is local); they
/// stand in for transport so the golden vector can isolate the rate and backpressure math. See
/// docs/golden-v0.md §3 -- a green test here must not be read as more coverage than that.
/// </summary>
public sealed class World
{
    /// <summary>
    /// Ticks per second, taken from content ([meta] tick_hz), never hardcoded. §1.1: the sim is an
    /// integer tick counter and there is no delta-time anywhere in simulation code -- this value
    /// exists only to convert ticks to seconds for humans and for rate assertions.
    /// </summary>
    public int TickRate { get; }

    // Fixture buffer capacities, matching tools/refsim_v0.py. Not content: they are properties of
    // the golden fixture, so they live with the fixture rather than in recipes-v0.toml.
    public const int OreCap = 950;
    public const int PlateCap = 200;

    public ulong Tick { get; private set; }

    public readonly Buffer Ore;
    public readonly Buffer Plate;
    public readonly Buffer Gear;

    public readonly Machine[] Miners;
    public readonly Machine[] Furnaces;
    public readonly Machine[] Assemblers;

    /// <summary>Belts, in id order. Empty unless the world was built with transport (§7).</summary>
    public readonly List<Belt> Belts = [];

    /// <summary>
    /// Belt length for the `transport` scenario, in tiles. [CAL] Matches TRANSPORT_BELT_TILES in
    /// tools/refsim_v0.py; the two must agree or the golden cannot be reproduced.
    /// </summary>
    public const int TransportBeltTiles = 20;

    /// <summary>Splitters, in id order. Empty unless the world was built with one (§6).</summary>
    public readonly List<Splitter> Splitters = [];

    /// <summary>
    /// Square map bound. Cells outside [0, MapSize) are rejected OffMap. A bound must exist: without
    /// one a typo'd coordinate allocates an arbitrarily long lane.
    /// </summary>
    public const int MapSize = 256;

    /// <summary>Occupied belt cells -> the belt they belong to. Not hashed: derived from Placed.</summary>
    private readonly Dictionary<(int x, int y), int> _beltCells = [];

    /// <summary>
    /// The ordered cells of a PLACED belt, tail-first, keyed by its index in <see cref="Belts"/>.
    ///
    /// A Dictionary and not a parallel List: scenario belts (transport/splitter/inserter) are
    /// constructed directly into Belts and have no cells at all, so a parallel list would drift out
    /// of alignment and a join would silently extend the WRONG belt. Absent key = not placeable =
    /// never a join target, which is also the correct semantics for a fixture belt.
    /// Not hashed -- derived from the input stream.
    /// </summary>
    private readonly Dictionary<int, List<BeltPlacement>> _beltChains = [];

    /// <summary>
    /// Every placement this world has accepted, in application order. THIS IS THE INPUT STREAM
    /// (§1.3): World stopped being a closed system the moment placement existed, so state is no
    /// longer a function of content alone -- it is a function of (content, input stream). A replay
    /// that does not feed these back cannot reproduce the world, and hash parity would fail for a
    /// reason that is not a bug. See D23.
    /// </summary>
    public IReadOnlyList<(ulong tick, BeltPlacement placement)> Inputs => _inputs;
    private readonly List<(ulong tick, BeltPlacement placement)> _inputs = [];

    /// <summary>Inserters, in id order. §10.4: ticked ascending; first able to take WINS.</summary>
    public readonly List<Inserter> Inserters = [];

    /// <summary>Feed buffer for the `inserter` scenario. [CAL] matches refsim INSERTER_FEED_CAP.</summary>
    public const int InserterFeedCap = 16;
    public const int InserterFastSwing = 4;
    public const int InserterSlowSwing = 20;

    /// <summary>Non-null only in the `inserter` scenario.</summary>
    public Buffer? Feed { get; private set; }

    public World(Content c, int gearCap, bool transport = false, bool splitter = false, bool inserter = false)
    {
        TickRate = c.TickHz;
        Ore = new Buffer(OreCap);
        Plate = new Buffer(PlateCap);
        Gear = new Buffer(gearCap);

        var mine = c.Recipe("mine-iron-ore");
        var smelt = c.Recipe("smelt-iron-plate");
        var craft = c.Recipe("craft-iron-gear");

        // `transport`: route ore over a belt instead of a shared buffer. Everything downstream is
        // unchanged, so any difference from `steady` is attributable to transport alone -- that is
        // what makes the scenario diagnostic rather than merely different.
        IPort oreOut, oreIn;
        Lane[]? splitOut = null;
        if (inserter)
        {
            // §10 scenario: miners -> ore -> insA -> beltIn.lane0 -> {insB,insC} -> feed -> furnaces.
            var def = c.Belts[0];
            var belt = new Belt(TransportBeltTiles, def.Speed, def.ItemSpacing, def.Lanes);
            Belts.Add(belt);
            Feed = new Buffer(InserterFeedCap);
            Inserters.Add(new Inserter(Ore, new LaneSink(belt.Lanes[0], 0), 0, InserterFastSwing));
            Inserters.Add(new Inserter(new LaneSource(belt.Lanes[0], 0), Feed, 0, InserterSlowSwing));
            Inserters.Add(new Inserter(new LaneSource(belt.Lanes[0], 0), Feed, 0, InserterSlowSwing));
            oreOut = Ore;
            oreIn = Feed;
        }
        else if (splitter)
        {
            // miners -> beltIn.lane0 -> SPLITTER -> beltOut.lane0 + beltOut.lane1 -> furnaces (8/8).
            var def = c.Belts[0];
            var beltIn = new Belt(TransportBeltTiles, def.Speed, def.ItemSpacing, def.Lanes);
            var beltOut = new Belt(TransportBeltTiles, def.Speed, def.ItemSpacing, def.Lanes);
            Belts.Add(beltIn);
            Belts.Add(beltOut);
            Splitters.Add(new Splitter([beltIn.Lanes[0]], [beltOut.Lanes[0], beltOut.Lanes[1]]));
            oreOut = new LaneSink(beltIn.Lanes[0], 0);
            oreIn = Ore;                 // unused: furnaces are wired per-lane below
            splitOut = [beltOut.Lanes[0], beltOut.Lanes[1]];
        }
        else if (transport)
        {
            var def = c.Belts[0];
            var belt = new Belt(TransportBeltTiles, def.Speed, def.ItemSpacing, def.Lanes);
            Belts.Add(belt);
            oreOut = new LaneSink(belt.Lanes[0], 0);     // miners -> belt tail
            oreIn = new LaneSource(belt.Lanes[0], 0);    // belt head -> furnaces
        }
        else
        {
            oreOut = Ore;
            oreIn = Ore;
        }

        Miners = Build(c.Build.Miners, c.Machine("burner-miner").SpeedBase, mine.Goal,
            [], [new Port(oreOut, 1)]);
        if (splitOut is not null)
        {
            // Half the furnaces drink from each output lane. An even split means a FAIR splitter
            // keeps both groups equally fed -- and an unfair one shows up as a lane imbalance.
            var n = c.Build.Furnaces;
            Furnaces = new Machine[n];
            for (int i = 0; i < n; i++)
                Furnaces[i] = new Machine(c.Machine("stone-furnace").SpeedBase, smelt.Goal,
                    [new Port(new LaneSource(splitOut[i < n / 2 ? 0 : 1], 0), 1)],
                    [new Port(Plate, 1)]);
        }
        else
        {
            Furnaces = Build(c.Build.Furnaces, c.Machine("stone-furnace").SpeedBase, smelt.Goal,
                [new Port(oreIn, 1)], [new Port(Plate, 1)]);
        }
        Assemblers = Build(c.Build.Assemblers, c.Machine("assembler-1").SpeedBase, craft.Goal,
            [new Port(Plate, 2)], [new Port(Gear, 1)]);
    }

    /// <summary>
    /// Apply a batch of placements AT A TICK BOUNDARY. D23.
    ///
    /// The caller (the adapter's tick loop) must invoke this between ticks, holding its lock, and
    /// must NOT pre-sort: this sorts by (Y, X, Dir) itself, because arrival order is wall-clock
    /// dependent and two hosts receiving the same POSTs in different network order would diverge.
    /// Sorting here rather than trusting the caller keeps the determinism guarantee inside the sim,
    /// where it can be tested, rather than in a host that can quietly get it wrong.
    ///
    /// Contiguous accepted cells (each pointing at the next) are chained into ONE lane, so a posted
    /// run of N cells becomes an N-tile belt. Chains are NOT joined to belts from earlier batches:
    /// that would mean rebuilding a live lane and either discarding the items on it or migrating
    /// them, and neither is specified. A cell adjacent to an existing belt therefore starts a
    /// separate belt -- a real v0 limitation, recorded in B56 rather than silently approximated.
    /// </summary>
    public IReadOnlyList<(BeltPlacement placement, PlacementRejection reason)> ApplyBeltBatch(
        Content c, IEnumerable<BeltPlacement> batch)
    {
        var (results, accepted) = Judge(batch);

        if (accepted.Count > 0)
        {
            var def = c.Belts[0];
            var byCell = accepted.ToDictionary(p => (p.X, p.Y));
            var consumed = new HashSet<(int, int)>();

            // A chain STARTS at a cell nothing else points into: walking from heads keeps chain
            // identity independent of iteration order.
            var pointedInto = accepted.Select(p => p.Ahead).ToHashSet();

            foreach (var start in accepted)
            {
                var cell = (start.X, start.Y);
                if (consumed.Contains(cell) || pointedInto.Contains(cell)) continue;
                consumed.UnionWith(EmitChain(c, def, byCell, cell));
            }

            // Any cell still unconsumed sits on a cycle (a closed loop of belts). Walk them too,
            // from an arbitrary but DETERMINISTIC start -- accepted is already sorted, so the first
            // remaining cell in that order is stable across hosts.
            foreach (var p in accepted)
            {
                var cell = (p.X, p.Y);
                if (!consumed.Contains(cell)) consumed.UnionWith(EmitChain(c, def, byCell, cell));
            }

            foreach (var p in accepted) _inputs.Add((Tick, p));
        }

        return results;
    }

    /// <summary>
    /// Walk a chain from `start`, then either JOIN it to an adjacent existing belt (§2.5, B67) or
    /// allocate a new one. Maps its cells either way.
    /// </summary>
    private HashSet<(int, int)> EmitChain(
        Content c, BeltDef def, Dictionary<(int, int), BeltPlacement> byCell, (int x, int y) start)
    {
        var chain = new List<BeltPlacement>();
        var visited = new HashSet<(int, int)>();
        var cell = start;

        while (byCell.TryGetValue(cell, out var p) && visited.Add(cell))
        {
            chain.Add(p);
            cell = p.Ahead;
        }

        // §2.5 chain-join. Two ways this chain can attach to an existing belt:
        //
        //  TAIL-JOIN: our last cell points INTO an existing belt's first cell, so we feed it. That
        //            belt grows at its tail and its items shift forward by our length.
        //  HEAD-JOIN: an existing belt's last cell points into OUR first cell, so it feeds us. That
        //            belt grows at its head and its items do not move.
        //
        // Checked in that fixed order, and each returns the LOWEST matching belt index, so the
        // outcome cannot depend on dictionary iteration.
        var tailJoin = FindBeltWhoseFirstCellIs(chain[^1].Ahead);
        var headJoin = FindBeltWhoseLastCellPointsAt((chain[0].X, chain[0].Y));

        // A chain that would attach on BOTH sides bridges two separate belts into one. That is a
        // genuine lane MERGE -- concatenating two item lists, not offsetting one -- and it is not
        // modelled in v0. We join the downstream side only and leave the upstream belt separate.
        // Recorded in B67 rather than approximated: silently splicing them would move items between
        // lanes with no specified rule for what happens at the seam.
        if (tailJoin >= 0)
        {
            Belts[tailJoin].Extend(chain.Count, atHead: false);
            AbsorbChain(chain, tailJoin, prepend: true);
            return visited;
        }
        if (headJoin >= 0)
        {
            Belts[headJoin].Extend(chain.Count, atHead: true);
            AbsorbChain(chain, headJoin, prepend: false);
            return visited;
        }

        var beltIndex = Belts.Count;
        Belts.Add(new Belt(chain.Count, def.Speed, def.ItemSpacing, def.Lanes));
        _beltChains[beltIndex] = chain;
        foreach (var p in chain) _beltCells[(p.X, p.Y)] = beltIndex;
        return visited;
    }

    /// <summary>
    /// Predict how a batch WOULD be judged against the world as it stands, without applying it.
    /// B67 part 2: lets the POST response name real reasons instead of only `off-map`.
    ///
    /// Shares its rules and its ordering with <see cref="ApplyBeltBatch"/> BY CONSTRUCTION -- both
    /// call <see cref="Judge"/> -- so the prediction cannot drift from the decision by someone
    /// editing one and forgetting the other. That shared path is the only reason a prediction is
    /// safe to put on the wire at all.
    /// </summary>
    public IReadOnlyList<(BeltPlacement placement, PlacementRejection reason)> PredictBeltBatch(
        IEnumerable<BeltPlacement> batch) => Judge(batch).results;

    /// <summary>
    /// The single source of truth for placement judgement: sort deterministically, then rule on each
    /// cell. Used by both the predictor and the applier.
    /// </summary>
    private (List<(BeltPlacement placement, PlacementRejection reason)> results, List<BeltPlacement> accepted) Judge(
        IEnumerable<BeltPlacement> batch)
    {
        var results = new List<(BeltPlacement placement, PlacementRejection reason)>();
        var accepted = new List<BeltPlacement>();
        var seen = new HashSet<(int, int)>();

        // Deterministic order: (Y, X, Dir). Never arrival order (D23).
        foreach (var p in batch.OrderBy(p => p.Y).ThenBy(p => p.X).ThenBy(p => p.Dir))
        {
            PlacementRejection why =
                p.Dir is < 0 or > 3 ? PlacementRejection.BadDir
                : p.X < 0 || p.Y < 0 || p.X >= MapSize || p.Y >= MapSize ? PlacementRejection.OffMap
                : _beltCells.ContainsKey((p.X, p.Y)) ? PlacementRejection.Occupied
                : !seen.Add((p.X, p.Y)) ? PlacementRejection.DuplicateInBatch
                : PlacementRejection.None;

            results.Add((p, why));
            if (why == PlacementRejection.None) accepted.Add(p);
        }
        return (results, accepted);
    }

    /// <summary>
    /// Lowest belt index whose FIRST (tail) cell is `cell`, or -1. Scans ascending and returns the
    /// lowest match, so the result cannot depend on Dictionary iteration order.
    /// </summary>
    private int FindBeltWhoseFirstCellIs((int x, int y) cell)
    {
        for (int i = 0; i < Belts.Count; i++)
            if (_beltChains.TryGetValue(i, out var ch) && (ch[0].X, ch[0].Y) == cell) return i;
        return -1;
    }

    /// <summary>Lowest belt index whose LAST (head) cell points at `cell`, or -1.</summary>
    private int FindBeltWhoseLastCellPointsAt((int x, int y) cell)
    {
        for (int i = 0; i < Belts.Count; i++)
            if (_beltChains.TryGetValue(i, out var ch) && ch[^1].Ahead == cell) return i;
        return -1;
    }

    private void AbsorbChain(List<BeltPlacement> chain, int beltIndex, bool prepend)
    {
        if (prepend) _beltChains[beltIndex].InsertRange(0, chain);
        else _beltChains[beltIndex].AddRange(chain);
        foreach (var p in chain) _beltCells[(p.X, p.Y)] = beltIndex;
    }

    private static Machine[] Build(int n, int sigma, int goal, Port[] ins, Port[] outs)
    {
        var a = new Machine[n];
        for (int i = 0; i < n; i++) a[i] = new Machine(sigma, goal, ins, outs);
        return a;
    }

    /// <summary>
    /// One tick. §1.3 phase order, restricted to the phases v0 implements. Power is fully
    /// satisfied in this fixture, but the Fx32.Mul(sigma, satisfaction) path still executes so
    /// the helper is covered.
    /// </summary>
    public void Step()
    {
        Tick++;
        const int satisfaction = Fx32.One;
        foreach (var m in Miners) m.Tick(satisfaction);
        foreach (var m in Furnaces) m.Tick(satisfaction);
        foreach (var m in Assemblers) m.Tick(satisfaction);
        // §1.3/§10.3: phase_inserters runs AFTER machines and BEFORE belts. That order produces a
        // deliberate asymmetry -- pickup from a lane head reads the belt as it was at the END of
        // last tick (one-tick latency), while a drop onto a tail is followed by THIS tick's advance
        // (no latency). Specified, not incidental.
        foreach (var ins in Inserters) ins.Step();
        foreach (var b in Belts) b.Step();   // phase_belts, after machines (§1.3 / transport §8)
        // §8: splitters AFTER all belts, so an item arriving at a lane head this tick is visible to
        // the splitter this tick. Otherwise splitter latency would depend on the id order of the
        // belts feeding it -- topology-dependent, i.e. a desync waiting to happen.
        foreach (var sp in Splitters) sp.Step();
    }

    /// <summary>
    /// §1.5 canonical hash. Order: tick; each buffer's count then cap; then miners, furnaces,
    /// assemblers in ascending index, each contributing progress then state.
    ///
    /// Cap is hashed alongside count because capacity is simulation state -- it changes how the
    /// world evolves, so a cap divergence must be a mismatch rather than a silent difference.
    /// </summary>
    public ulong Hash()
    {
        var h = new StateHasher();
        h.U64(Tick);
        foreach (var b in new[] { Ore, Plate, Gear })
        {
            h.U32((uint)b.Count);
            h.U32((uint)b.Cap);
        }
        foreach (var arch in new[] { Miners, Furnaces, Assemblers })
            foreach (var m in arch)
            {
                h.U32((uint)m.Progress);
                h.U8((byte)m.State);
            }

        // transport-v0.md §7, appended AFTER the machine archetypes.
        //
        // BELT COUNT PREFIX (D23). §7.1 originally exempted belts from a count prefix because they
        // were "fixed-topology -- set at construction and never change". B56's runtime placement
        // destroyed that justification: belts are now DYNAMIC, which is precisely the condition
        // §7.1 says requires a prefix. Without it the encoding's own stated rationale is false, and
        // relying on "the len>=1 invariant probably prevents a collision" is exactly the reasoning
        // §7.1 rejected for runs. This changes every previously published hash -- a legitimate
        // consequence of the topology model changing, not a regression.
        h.U16((ushort)Belts.Count);
        foreach (var belt in Belts)
            foreach (var lane in belt.Lanes)
            {
                // §7.1: run lists are DYNAMIC, so they are counted. Without this, a lane with 2 runs
                // followed by one with 0 hashes identically to 0 followed by 2 -- two genuinely
                // different worlds colliding. This is the one place §1.5's "no length prefixes"
                // is deliberately amended.
                h.U16((ushort)lane.Runs.Count);
                foreach (var r in lane.Runs)
                {
                    h.U32((uint)r.Head);     // Fx32 bit pattern, reinterpreted unsigned
                    h.U16((ushort)r.Len);
                    h.U16((ushort)r.Item);
                }
            }

        // §6.3/§7: the alternation pointers ARE simulation state. Unhashed, a splitter desync stays
        // invisible until the two worlds have already diverged past recovery.
        foreach (var sp in Splitters)
        {
            h.U8(sp.InNext);
            h.U8(sp.OutNext);
        }

        // §10.5: inserters, NO count prefix -- they are fixed-topology in v0 (no placement path),
        // which is exactly what §7.1's amended rule requires: prefix iff dynamic. Worlds with no
        // inserters append nothing, so every previously published hash is preserved. TRIGGER: if
        // inserters ever become placeable the way belts did in B56, they become dynamic and this
        // needs a u16 count, breaking every hash exactly as D23 did.
        foreach (var ins in Inserters)
        {
            h.U32((uint)ins.Progress);
            h.U8((byte)ins.State);
            h.U8(ins.Holding ? (byte)1 : (byte)0);
        }
        return h.Value;
    }

    public string HashHex() => StateHasher.Format(Hash());

    /// <summary>Count of machines in each state, for the golden's machine_states comparison.</summary>
    public static Dictionary<string, int> Tally(Machine[] arch)
    {
        var d = new Dictionary<string, int>();
        foreach (var m in arch)
        {
            var k = m.State.ToString();
            d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;
        }
        return d;
    }
}
