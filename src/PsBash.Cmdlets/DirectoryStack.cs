using System.Globalization;

namespace PsBash.Cmdlets;

/// <summary>
/// bash's directory stack arithmetic (<c>pushd.def</c>), pure. The stack is the list <c>dirs</c> prints: the current
/// directory first (position 0), then the pushed directories, top first; its <c>offset</c> is the number of pushed ones
/// (<c>Count - 1</c>). <c>+N</c> counts from the left of that list, <c>-N</c> from the right, and a position outside
/// <c>0..offset</c> is an error the builtin reports with status 1. The stack itself lives in PowerShell's location stack
/// (<c>InvokeBashDirsCommand.Read</c> / <c>BashShellState.SetDirectoryStack</c>), shared with subshell save and restore.
/// <para>REGRESSION: <c>dirs +5</c>, <c>pushd +5</c>, <c>popd +5</c> never looked at the index's range: dirs printed the
/// whole stack, pushd and popd did nothing, all with status 0 (bash: "directory stack empty" or "5: directory stack index
/// out of range", status 1). <c>popd -N</c> popped the top, <c>pushd +N</c> did not rotate.</para>
/// </summary>
internal static class DirectoryStack
{
    /// <summary>
    /// A <c>+N</c> / <c>-N</c> word: whether it counts from the left (<c>+</c>) and N. False when the text after the sign is
    /// not a number (bash <c>legal_number</c>: blanks around it and a sign of its own are allowed), which the builtins
    /// report as "invalid number" with their usage, status 2.
    /// </summary>
    internal static bool TryParseIndex(string word, out bool fromLeft, out long n)
    {
        fromLeft = word.Length > 0 && word[0] == '+';
        n = 0;
        if (word.Length < 2 || word[0] is not ('+' or '-')) return false;
        return long.TryParse(word.AsSpan(1),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.InvariantCulture, out n);
    }

    /// <summary>The list position <c>+N</c> / <c>-N</c> names, or -1 when it is outside <c>0..offset</c>.</summary>
    internal static int Position(bool fromLeft, long n, int offset)
    {
        long p = fromLeft ? n : offset - n;
        return p < 0 || p > offset ? -1 : (int)p;
    }

    /// <summary>bash's <c>pushd_error</c> text (without the builtin's name): an empty stack, or the index as typed.</summary>
    internal static string RangeError(int offset, string word)
        => offset == 0 ? "directory stack empty" : word + ": directory stack index out of range";

    /// <summary><c>pushd +N</c>: the list rotated so position <paramref name="p"/> comes first (bash's order is kept around it).</summary>
    internal static List<string> Rotate(IReadOnlyList<string> list, int p)
    {
        var rotated = new List<string>(list.Count);
        for (int i = 0; i < list.Count; i++) rotated.Add(list[(p + i) % list.Count]);
        return rotated;
    }

    /// <summary>
    /// <c>pushd -n +N</c>: the rotation without the directory change. bash rotates and then does not move, so the entry
    /// that would have become the current directory drops out: <c>[c b a B]</c> with <c>-n +1</c> is <c>[c a B c]</c>.
    /// </summary>
    internal static List<string> RotateInPlace(IReadOnlyList<string> list, int p)
    {
        var rotated = Rotate(list, p);
        rotated[0] = list[0];
        return rotated;
    }

    /// <summary><c>pushd</c> with no operand: the first two swap (the caller has checked there are two).</summary>
    internal static List<string> Swap(IReadOnlyList<string> list)
    {
        var swapped = new List<string>(list);
        (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
        return swapped;
    }

    /// <summary><c>pushd -n DIR</c>: DIR goes on top of the stack, the current directory stays.</summary>
    internal static List<string> InsertBelowCurrent(IReadOnlyList<string> list, string dir)
    {
        var inserted = new List<string>(list);
        inserted.Insert(1, dir);
        return inserted;
    }

    /// <summary>
    /// <c>popd</c> at position <paramref name="p"/> (from <see cref="Position"/>; the caller has checked the stack is not
    /// empty). Position 0 removes the top of the stack and, unless <paramref name="noCd"/>, makes it the current directory:
    /// <c>[c d a]</c> becomes <c>[d a]</c>, or <c>[c a]</c> with <c>-n</c>. Any other position just leaves the list.
    /// </summary>
    internal static List<string> Pop(IReadOnlyList<string> list, int p, bool noCd)
    {
        var popped = new List<string>(list);
        if (p == 0 && !noCd) popped.RemoveAt(0);
        else popped.RemoveAt(p == 0 ? 1 : p);
        return popped;
    }
}
