namespace Forge.Sim;

/// <summary>
/// Q16.16 fixed-point arithmetic. Spec: factory-math-v0.md §1.2.
///
/// Rounding is PINNED to truncate-toward-zero. C#'s integer division operator truncates toward
/// zero by definition (ECMA-334), including for negative operands, which is exactly the pinned
/// mode -- so <c>/</c> is used rather than <c>&gt;&gt;</c> (which floors on negatives and would
/// silently disagree).
///
/// These helpers are the ONLY place fixed-point scaling is allowed to appear. A raw shift on a
/// possibly-negative value elsewhere is a bug.
/// </summary>
public static class Fx32
{
    /// <summary>Scale factor: 2^16. <c>One</c> represents 1.0.</summary>
    public const int One = 65536;

    /// <summary>Shift used to build a work goal from a tick duration (§3.1: GOAL = d &lt;&lt; 16).</summary>
    public const int Shift = 16;

    /// <summary>a * b in Q16.16. Truncates toward zero.</summary>
    public static int Mul(int a, int b) => (int)((long)a * b / One);

    /// <summary>a / b in Q16.16. Truncates toward zero. Divide-by-zero is a programmer error.</summary>
    public static int Div(int a, int b)
    {
        if (b == 0) throw new DivideByZeroException("Fx32.Div by zero -- content validation should have caught this at load (§2.4).");
        return (int)(((long)a << Shift) / b);
    }

    /// <summary>Convert a Q16.16 value to a whole number, truncating toward zero.</summary>
    public static int ToInt(int a) => a / One;

    /// <summary>
    /// Convert a decimal literal to Q16.16 at content-load time. Deliberately takes decimal, not
    /// double: this is a load-time boundary helper and must never be called from the tick loop.
    /// Truncates, matching the pinned mode -- e.g. 0.55 becomes 36044, not 36045.
    /// </summary>
    public static int FromDecimal(decimal v) => (int)(v * One);
}
