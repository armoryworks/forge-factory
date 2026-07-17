using System.Diagnostics;
using Forge.Sim;

namespace Forge.Factory.Adapter;

/// <summary>
/// Hosts the sim core and owns the authoritative tick. TICK_RATE comes from content
/// ([meta] tick_hz = 60), not from a constant here.
///
/// DETERMINISM BOUNDARY: the wall clock below paces the loop, and that is ALL it does. It never
/// reaches the sim -- <see cref="World.Step"/> takes no arguments and no delta-time, so the sim
/// stays a pure function of (state, tick) per factory-math-v0.md axiom 1. Everything in this file
/// is host concern; nothing in it can change a hash.
///
/// §1.4: if the host cannot keep up, the game SLOWS DOWN. It does not drop ticks and it does not
/// scale dt -- either would make the sim disagree with its own golden vector. The catch-up cap
/// bounds how far we chase a backlog before conceding real time, which is what "slow down" means
/// mechanically and what stops a death-spiral on a stalled host.
/// </summary>
public sealed class SimTickService(
    Content content,
    SimHubBroadcaster hub,
    IConfiguration config,
    ILogger<SimTickService> logger) : BackgroundService
{
    /// <summary>§1.4 MAX_CATCHUP_TICKS.</summary>
    private const int MaxCatchupTicks = 8;

    /// <summary>
    /// Whether the hosted world routes ore over a belt (transport-v0.md) rather than a shared
    /// buffer. Default TRUE: the vertical slice is defined as source -> BELT -> machine -> output,
    /// so a beltless world is not the slice. Configurable because the beltless world is still the
    /// one the `steady`/`backpressure` vectors describe.
    /// </summary>
    private readonly bool _transport = config.GetValue<bool?>("Sim:Transport") ?? true;

    private readonly World _world = new(
        content,
        config.GetValue<int?>("Sim:GearBufferCap") ?? 1_000_000,
        transport: config.GetValue<bool?>("Sim:Transport") ?? true);

    /// <summary>
    /// Emit every Nth tick. The contract (§3) says sim.tick is "render-rate push (throttled from
    /// sim-rate)" without naming a rate, and factory-math-v0.md does not name one either -- see
    /// B-item `contract-tick-payload-underspecified`. 3 ticks at 60 Hz = 20 Hz, which is a sane
    /// render cadence and keeps the hub off the sim's hot path.
    /// </summary>
    private readonly int _emitEveryNTicks = config.GetValue<int?>("Sim:EmitEveryNTicks") ?? 3;

    /// <summary>
    /// Guards the World against concurrent access. The tick loop mutates it on a background thread
    /// while HTTP handlers (/sim/state) read it -- without this, a reader can observe Tick=N next to
    /// the hash of N+1, a torn read that makes the state look non-deterministic when it isn't.
    /// The sim itself is single-threaded and stays that way; this only serialises host access to it.
    /// </summary>
    private readonly Lock _gate = new();

    public World World => _world;
    public int TickRate => _world.TickRate;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var tickMs = 1000.0 / _world.TickRate;
        logger.LogInformation(
            "Sim tick loop starting: {Rate} Hz ({TickMs:F2} ms/tick), emitting every {N} ticks, gear cap {Cap}",
            _world.TickRate, tickMs, _emitEveryNTicks, _world.Gear.Cap);

        var clock = Stopwatch.StartNew();
        var lastMs = 0.0;
        var accumulator = 0.0;

        while (!ct.IsCancellationRequested)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            accumulator += now - lastMs;
            lastMs = now;

            var ran = 0;
            while (accumulator >= tickMs && ran < MaxCatchupTicks)
            {
                // Step and build the payload under the lock so the emitted snapshot is coherent;
                // send outside it, because awaiting while holding a lock would stall the sim on
                // a slow client -- the hub must never be able to backpressure the tick loop.
                TickPayload payload = default;
                var emit = false;
                lock (_gate)
                {
                    _world.Step();
                    if (_world.Tick % (ulong)_emitEveryNTicks == 0)
                    {
                        payload = BuildTickPayload();
                        emit = true;
                    }
                }

                accumulator -= tickMs;
                ran++;

                if (emit) await EmitTickAsync(payload, ct);
            }

            if (ran == MaxCatchupTicks && accumulator >= tickMs)
            {
                // Conceded: we are behind real time and will stay behind. Shed the backlog rather
                // than chase it forever. This is §1.4's "the game slows down" -- correctness is
                // preserved (every tick that ran, ran fully), only wall-clock pacing gives.
                logger.LogWarning("Sim behind real time by {Backlog:F0} ms; shedding backlog (UPS < {Rate})",
                    accumulator, _world.TickRate);
                accumulator = 0;
            }

            try
            {
                await Task.Delay(1, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Sim tick loop stopped at tick {Tick}", _world.Tick);
    }

    private readonly record struct TickPayload(long Tick, object? BeltDeltas, object MachineState, object Stock);

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private TickPayload BuildTickPayload() =>
        new((long)_world.Tick, BeltDeltas(), MachineState(), Stock());

    private async Task EmitTickAsync(TickPayload p, CancellationToken ct)
    {
        try
        {
            await hub.SimTickAsync(
                tick: p.Tick,
                beltDeltas: p.BeltDeltas,
                machineState: p.MachineState,
                stock: p.Stock,
                ct: ct);
        }
        catch (Exception ex)
        {
            // A hub failure must never take down the sim: the tick loop is authoritative and a
            // disconnected client is not a sim fault. Report it on the contract's error channel.
            logger.LogError(ex, "sim.tick emit failed at tick {Tick}", _world.Tick);
            try { await hub.SimErrorAsync($"sim.tick emit failed: {ex.Message}", ct); } catch { /* hub is down; already logged */ }
        }
    }

    /// <summary>
    /// §3's beltDeltas. B54/D22.
    ///
    /// NULL vs ARRAY (D21, contract §3.1): null means "this build does not model belts"; an array
    /// means they are modelled. Those are different facts and the client must be able to separate
    /// them -- so this returns null ONLY when the hosted world genuinely has no belts, which is now
    /// a config choice rather than a fact about the sim.
    ///
    /// ABSOLUTE STATE, NOT DELTAS (D22) -- despite the field's name. This is D21's `stock` ruling
    /// applied to belts, for the identical reason: contract §3.2 says resync is automatic *because*
    /// stock and machineState are absolute, so every emit is a full resync and a dropped message
    /// costs one frame of staleness. True belt deltas would make belts the ONE field that cannot
    /// self-heal -- a late-joining or gap-hit client could never reconstruct belt contents -- which
    /// is precisely the flaw D21 removed from stock. The "snapshot grows without bound" worry does
    /// not apply either: runs are O(runs), not O(items), and a saturated belt is exactly ONE run
    /// (transport-v0.md §4). The full state is smaller than a delta stream's bookkeeping.
    ///
    /// The name is therefore a wart, kept deliberately: iso's SimHubClient is in flight (B52) and
    /// renaming mid-flight breaks it for no functional gain. D21's §3.1 already flags a forced
    /// client re-cut when machineState gains entity identity; the rename to `belts` should ride
    /// with that, not ahead of it.
    /// </summary>
    private object? BeltDeltas()
    {
        if (_world.Belts.Count == 0) return null;    // not modelled -> null, per D21

        var outp = new List<object>();
        for (int b = 0; b < _world.Belts.Count; b++)
        {
            var belt = _world.Belts[b];
            for (int l = 0; l < belt.Lanes.Length; l++)
            {
                var lane = belt.Lanes[l];
                outp.Add(new
                {
                    belt = b,
                    lane = l,
                    // Runs front-to-back, exactly as the sim holds them: head is the Fx32 position
                    // of the frontmost item, and the rest sit at head - n*spacing. The client needs
                    // spacing to expand a run, so it is published here rather than assumed.
                    spacing = lane.Spacing,
                    length = lane.Length,
                    runs = lane.Runs.Select(r => new { head = r.Head, len = r.Len, item = r.Item }).ToArray(),
                });
            }
        }
        return outp;
    }

    private object MachineState() => new
    {
        miners = World.Tally(_world.Miners),
        furnaces = World.Tally(_world.Furnaces),
        assemblers = World.Tally(_world.Assemblers),
    };

    /// <summary>
    /// ABSOLUTE buffer levels, not deltas. D21 (B46), contract §3.1.
    ///
    /// This was deltas-since-emit, justified by "a snapshot would grow without bound" -- true of an
    /// event log, false of a LEVEL, which is O(1) per item type. Three ints, constant forever. So
    /// deltas bought nothing and cost correctness two ways:
    ///
    ///   1. The baseline was per-SERVER (a _primed flag here), not per-client. Any client
    ///      connecting after the first emit received deltas anchored to a baseline it never saw,
    ///      and could not reconstruct absolute stock from the stream at all.
    ///   2. The hub is Clients.All with no replay, so one dropped message meant PERMANENT silent
    ///      divergence -- a client's accumulated total is wrong forever with no way to detect it.
    ///
    /// Absolute levels are strictly more informative at identical cost: a client derives a delta
    /// from two consecutive samples, but cannot derive a level from deltas without a baseline. And
    /// every emit is a full resync, so a gap costs one frame of staleness instead of correctness.
    /// This method holds no state now, which is the point -- there is nothing here to get out of
    /// sync with a client.
    /// </summary>
    private object Stock() => new
    {
        ironOre = _world.Ore.Count,
        ironPlate = _world.Plate.Count,
        ironGear = _world.Gear.Count,
    };

    /// <summary>
    /// Snapshot for the /sim/state probe. Read-only; does not advance anything. Taken under the
    /// lock so (tick, hash) are coherent -- an incoherent pair would look exactly like a
    /// determinism bug, which is the one thing this probe exists to rule out.
    ///
    /// gearBufferCap is exposed so a verifier can reconstruct this exact World and replay to the
    /// same tick. It is the only construction parameter not already in the content file.
    /// </summary>
    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                tick = (long)_world.Tick,
                tickRate = _world.TickRate,
                hash = _world.HashHex(),
                gearBufferCap = _world.Gear.Cap,
                // Exposed so a verifier can reconstruct this exact World: with belts wired, the
                // beltless replay would be a different world and parity would fail for a reason
                // that is not a bug.
                transport = _transport,
                buffers = new
                {
                    ironOre = _world.Ore.Count,
                    ironPlate = _world.Plate.Count,
                    ironGear = _world.Gear.Count,
                },
                machineState = MachineState(),
            };
        }
    }
}
