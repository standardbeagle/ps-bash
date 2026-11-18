using System.Globalization;

namespace PsBash.Core.Parser;

/// <summary>
/// The index a positional-parameter name denotes (<c>$1</c>, <c>${10}</c>, <c>${99999999999999999999}</c>).
/// <para>
/// <b>Invariant</b>: the name is script text, so its digits may exceed any integer. Such a positional is unset (bash
/// expands it to nothing), so it maps to <see cref="int.MaxValue"/>, an index no parameter list reaches. A digit string
/// from the script is never <c>int.Parse</c>d: the <see cref="OverflowException"/> escaped the emitter
/// (docs/specs/host-lifecycle-contract.md, "No input may take the host down"). Guard: <c>Core.Tests/PositionalIndexTests</c>.
/// </para>
/// </summary>
public static class PositionalIndex
{
    /// <summary>True when <paramref name="name"/> is all ASCII digits; <paramref name="index"/> is its value, saturated at
    /// <see cref="int.MaxValue"/>. <c>"0"</c> is 0 (<c>$0</c>).</summary>
    public static bool TryParse(string? name, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(name)) return false;
        foreach (char c in name)
            if (!char.IsAsciiDigit(c)) return false;
        if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out index)) index = int.MaxValue;
        return true;
    }
}
