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

    private readonly World _world = new(content, config.GetValue<int?>("Sim:GearBufferCap") ?? 1_000_000);

    /// <summary>
    /// Emit every Nth tick. The contract (§3) says sim.tick is "render-rate push (throttled from
    /// sim-rate)" without naming a rate, and factory-math-v0.md does not name one either -- see
    /// B-item `contract-tick-payload-underspecified`. 3 ticks at 60 Hz = 20 Hz, which is a sane
    /// render cadence and keeps the hub off the sim's hot path.
    /// </summary>
    private readonly int _emitEveryNTicks = config.GetValue<int?>("Sim:EmitEveryNTicks") ?? 3;

    // Last emitted buffer levels, for stockDelta. Adapter-side view state, never hashed.
    private int _lastOre, _lastPlate, _lastGear;
    private bool _primed;

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

    private readonly record struct TickPayload(long Tick, object[] BeltDeltas, object MachineState, object StockDelta);

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private TickPayload BuildTickPayload() =>
        new((long)_world.Tick, BeltDeltas(), MachineState(), StockDelta());

    private async Task EmitTickAsync(TickPayload p, CancellationToken ct)
    {
        try
        {
            await hub.SimTickAsync(
                tick: p.Tick,
                beltDeltas: p.BeltDeltas,
                machineState: p.MachineState,
                stockDelta: p.StockDelta,
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
    /// §3's sim.tick carries beltDeltas, but the v0 sim HAS NO BELTS -- golden-v0.md §3 is explicit
    /// that machines emit into shared buffers and transport is not modelled. Emitting an empty array
    /// is the honest reading: the field exists per contract, and there is genuinely nothing to
    /// report. Inventing plausible belt data would be worse than an empty list -- it would make an
    /// unimplemented subsystem look implemented. See `contract-tick-payload-underspecified`.
    /// </summary>
    private static object[] BeltDeltas() => [];

    private object MachineState() => new
    {
        miners = World.Tally(_world.Miners),
        furnaces = World.Tally(_world.Furnaces),
        assemblers = World.Tally(_world.Assemblers),
    };

    /// <summary>
    /// Change in buffer levels since the previous emitted tick. The first emit primes the baseline
    /// and reports zeros rather than reporting the whole world as a delta.
    /// </summary>
    private object StockDelta()
    {
        int ore = _world.Ore.Count, plate = _world.Plate.Count, gear = _world.Gear.Count;
        object delta = _primed
            ? new { ironOre = ore - _lastOre, ironPlate = plate - _lastPlate, ironGear = gear - _lastGear }
            : new { ironOre = 0, ironPlate = 0, ironGear = 0 };
        (_lastOre, _lastPlate, _lastGear, _primed) = (ore, plate, gear, true);
        return delta;
    }

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
