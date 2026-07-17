namespace Forge.Sim;

/// <summary>
/// FNV-1a 64-bit state hash with the canonical encoding pinned in factory-math-v0.md §1.5.
///
/// The encoding is a cross-implementation contract, so nothing here may depend on the host:
/// bytes are emitted little-endian by explicit shifting rather than via BitConverter, whose
/// byte order follows the machine. That is the difference between a hash that is portable and
/// one that merely happens to match on x86.
/// </summary>
public struct StateHasher
{
    private const ulong Offset = 14695981039346656037UL; // 0xcbf29ce484222325
    private const ulong Prime = 1099511628211UL;         // 0x100000001b3

    private ulong _h;

    public StateHasher() => _h = Offset;

    public ulong Value => _h;

    public void U8(byte v)
    {
        // unchecked is the default context, but state it: the multiply is meant to wrap mod 2^64.
        unchecked
        {
            _h ^= v;
            _h *= Prime;
        }
    }

    public void U16(ushort v)
    {
        for (int i = 0; i < 2; i++) U8((byte)(v >> (8 * i)));
    }

    public void U32(uint v)
    {
        for (int i = 0; i < 4; i++) U8((byte)(v >> (8 * i)));
    }

    public void U64(ulong v)
    {
        for (int i = 0; i < 8; i++) U8((byte)(v >> (8 * i)));
    }

    /// <summary>Format as the golden vector writes them: lowercase hex, 0x-prefixed, 16 digits.</summary>
    public static string Format(ulong h) => "0x" + h.ToString("x16");
}
