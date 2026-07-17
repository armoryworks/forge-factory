namespace Forge.Sim;

/// <summary>Machine states. Discriminants are PINNED by §1.5 -- they are hashed as u8.</summary>
public enum MachineState : byte
{
    Idle = 0,
    Crafting = 1,
    Starved = 2,
    Blocked = 3,
}

/// <summary>§4.1: every buffer is finite. There is no infinite sink in the core rules.</summary>
public sealed class Buffer(int cap)
{
    public int Count;
    public readonly int Cap = cap;

    public bool CanTake(int n) => Count >= n;
    public bool CanPut(int n) => Count + n <= Cap;
    public void Take(int n) => Count -= n;
    public void Put(int n) => Count += n;
}

public readonly record struct Port(Buffer Buffer, int Count);

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
        foreach (var p in _inputs) if (!p.Buffer.CanTake(p.Count)) return false;
        foreach (var p in _inputs) p.Buffer.Take(p.Count);
        return true;
    }

    private bool TryEmit()
    {
        foreach (var p in _outputs) if (!p.Buffer.CanPut(p.Count)) return false;
        foreach (var p in _outputs) p.Buffer.Put(p.Count);
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

    public World(Content c, int gearCap)
    {
        TickRate = c.TickHz;
        Ore = new Buffer(OreCap);
        Plate = new Buffer(PlateCap);
        Gear = new Buffer(gearCap);

        var mine = c.Recipe("mine-iron-ore");
        var smelt = c.Recipe("smelt-iron-plate");
        var craft = c.Recipe("craft-iron-gear");

        Miners = Build(c.Build.Miners, c.Machine("burner-miner").SpeedBase, mine.Goal,
            [], [new Port(Ore, 1)]);
        Furnaces = Build(c.Build.Furnaces, c.Machine("stone-furnace").SpeedBase, smelt.Goal,
            [new Port(Ore, 1)], [new Port(Plate, 1)]);
        Assemblers = Build(c.Build.Assemblers, c.Machine("assembler-1").SpeedBase, craft.Goal,
            [new Port(Plate, 2)], [new Port(Gear, 1)]);
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
