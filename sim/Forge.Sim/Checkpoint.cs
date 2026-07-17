using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Sim;

/// <summary>
/// A full snapshot of a World, sufficient to reconstruct it exactly. B68.
///
/// WHY A SNAPSHOT AND NOT A REPLAY. D23 established that (content, input stream) reproduces the
/// world, and simprobe's parity check relies on that. But replay is O(ticks): restoring a world at
/// tick 10^7 would mean re-simulating 10^7 ticks. A checkpoint is O(state). Both are valid and they
/// verify each other -- the replay path proves the snapshot is not hiding state, and the snapshot
/// makes restore practical.
///
/// The acceptance test is exact: export -> restore -> hash MUST equal the pre-export hash. That is
/// stronger than "looks right", and it is the only check that catches a field quietly omitted here.
/// </summary>
public sealed class Checkpoint
{
    /// <summary>Bumped whenever this shape changes. A stale blob must fail loudly, not half-load.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // --- topology: how the World was CONSTRUCTED --------------------------------------------
    // Not decoration. The constructor builds the machine<->port wiring from these, and that wiring
    // is object references (LaneSink/LaneSource hold Lane instances). It cannot be serialised, so
    // restore re-runs the constructor with these flags and then overwrites the mutable state.
    public bool Transport { get; set; }
    public bool Splitter { get; set; }
    public bool Inserter { get; set; }
    public int GearCap { get; set; }

    public ulong Tick { get; set; }

    public int Ore { get; set; }
    public int Plate { get; set; }
    public int Gear { get; set; }
    public int Feed { get; set; }

    public List<MachineSnap> Miners { get; set; } = [];
    public List<MachineSnap> Furnaces { get; set; } = [];
    public List<MachineSnap> Assemblers { get; set; } = [];
    public List<BeltSnap> Belts { get; set; } = [];
    public List<SplitterSnap> Splitters { get; set; } = [];
    public List<InserterSnap> Inserters { get; set; } = [];

    /// <summary>D23's input stream. Carried so a restored world can still be replay-verified.</summary>
    public List<InputSnap> Inputs { get; set; } = [];

    public sealed class MachineSnap
    {
        public int Progress { get; set; }
        public byte State { get; set; }
    }

    public sealed class RunSnap
    {
        public int Head { get; set; }
        public int Len { get; set; }
        public int Item { get; set; }
    }

    public sealed class LaneSnap
    {
        /// <summary>Not hashed, but behaviour-relevant: a lane restored to the wrong length moves items wrong.</summary>
        public int Length { get; set; }
        public List<RunSnap> Runs { get; set; } = [];
    }

    public sealed class BeltSnap
    {
        public int Speed { get; set; }
        public int Spacing { get; set; }
        public List<LaneSnap> Lanes { get; set; } = [];

        /// <summary>B66 geometry: the belt's declared cells, tail-first. Empty for a belt with none.</summary>
        public List<CellSnap> Cells { get; set; } = [];
    }

    public sealed class CellSnap
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Dir { get; set; }
    }

    public sealed class SplitterSnap
    {
        public byte InNext { get; set; }
        public byte OutNext { get; set; }
    }

    public sealed class InserterSnap
    {
        public int Progress { get; set; }
        public byte State { get; set; }
        public bool Holding { get; set; }
    }

    public sealed class InputSnap
    {
        public ulong Tick { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Dir { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static Checkpoint FromBytes(ReadOnlySpan<byte> bytes)
    {
        var cp = JsonSerializer.Deserialize<Checkpoint>(bytes, Json)
                 ?? throw new CheckpointException("checkpoint blob deserialised to null");
        if (cp.Version != CurrentVersion)
            throw new CheckpointException(
                $"checkpoint version {cp.Version} != {CurrentVersion}. Refusing to load: a shape change " +
                "means fields are missing or misread, and a half-restored world is worse than none.");
        return cp;
    }
}

public sealed class CheckpointException(string message) : Exception(message);

/// <summary>
/// The storage seam (B68/B70). The sim and adapter know only bytes; forge-expert's blob storage
/// wires in behind this. Deliberately narrow -- a checkpoint is one opaque blob, so the interface
/// has nothing to disagree about across the seam.
/// </summary>
public interface ICheckpointStore
{
    /// <summary>The latest blob, or null if none exists. Null is a normal cold start, not an error.</summary>
    byte[]? Load();

    void Save(byte[] blob);
}

/// <summary>Local-file store. The default until B70's blob storage lands behind the same seam.</summary>
public sealed class FileCheckpointStore(string path) : ICheckpointStore
{
    public string Path { get; } = path;

    public byte[]? Load() => File.Exists(Path) ? File.ReadAllBytes(Path) : null;

    public void Save(byte[] blob)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write-then-move: a crash mid-write must not leave a truncated blob that restores a
        // corrupt world. Torn state is worse than no state -- it fails silently instead of loudly.
        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, Path, overwrite: true);
    }
}
