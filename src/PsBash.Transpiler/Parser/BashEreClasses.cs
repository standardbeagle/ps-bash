namespace PsBash.Core.Parser;

/// <summary>
/// The character classes and the case map of <see cref="BashEre"/>: glibc's <c>iswctype</c> and <c>towupper</c> in the
/// C.UTF-8 locale, exactly (the tables in <c>BashEreClasses.Table.cs</c> are dumped from the oracle's glibc), so
/// <c>[[:alpha:]]</c>, <c>\w</c> and <c>shopt -s nocasematch</c> agree with bash on every code point, not only ASCII.
/// glibc's own derivations hold in the tables: <c>alnum = alpha | digit</c>, <c>graph = print - space</c>,
/// <c>punct = graph - alnum</c>; <c>digit</c> and <c>xdigit</c> are ASCII only (U+0663 is alpha, not digit).
/// </summary>
internal static partial class BashEreClasses
{
    internal const int Alpha = 1, Upper = 2, Lower = 4, Alnum = 8, Digit = 16, Xdigit = 32, Space = 64, Blank = 128,
        Cntrl = 256, Print = 512, Graph = 1024, Punct = 2048;

    /// <summary>The class bit of a <c>[:name:]</c>, or 0 for a name regcomp rejects (REG_ECTYPE).</summary>
    internal static int ByName(string name) => name switch
    {
        "alpha" => Alpha, "upper" => Upper, "lower" => Lower, "alnum" => Alnum, "digit" => Digit,
        "xdigit" => Xdigit, "space" => Space, "blank" => Blank, "cntrl" => Cntrl, "print" => Print,
        "graph" => Graph, "punct" => Punct,
        _ => 0,
    };

    private static int[]? s_alpha, s_upper, s_lower, s_punct, s_print, s_upperRuns;

    private static int[] AlphaTable => s_alpha ??= ParseRanges(AlphaRanges);
    private static int[] UpperTable => s_upper ??= ParseRanges(UpperRanges);
    private static int[] LowerTable => s_lower ??= ParseRanges(LowerRanges);
    private static int[] PunctTable => s_punct ??= ParseRanges(PunctRanges);
    private static int[] PrintTable => s_print ??= ParseRanges(PrintRanges);
    private static int[] UpperRunTable => s_upperRuns ??= ParseRuns(ToUpperRuns);

    /// <summary>Is <paramref name="cp"/> in every class of <paramref name="mask"/>'s ANY bit (a bracket's classes OR).</summary>
    internal static bool InAny(int mask, int cp)
    {
        for (int bit = 1; bit <= Punct; bit <<= 1)
            if ((mask & bit) != 0 && Is(bit, cp)) return true;
        return false;
    }

    internal static bool Is(int cls, int cp) => cls switch
    {
        Alpha => InRanges(AlphaTable, cp),
        Upper => InRanges(UpperTable, cp),
        Lower => InRanges(LowerTable, cp),
        Alnum => cp is >= '0' and <= '9' || InRanges(AlphaTable, cp),
        Digit => cp is >= '0' and <= '9',
        Xdigit => cp is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F',
        Space => IsSpace(cp),
        Blank => cp is 9 or 32 or 0x1680 or >= 0x2000 and <= 0x2006 or >= 0x2008 and <= 0x200A or 0x205F or 0x3000,
        Cntrl => cp is >= 0 and <= 31 or >= 127 and <= 159 or 0x2028 or 0x2029,
        Print => InRanges(PrintTable, cp),
        Graph => InRanges(PrintTable, cp) && !IsSpace(cp),
        Punct => InRanges(PunctTable, cp),
        _ => false,
    };

    private static bool IsSpace(int cp) =>
        cp is >= 9 and <= 13 or 32 or 0x1680 or >= 0x2000 and <= 0x2006 or >= 0x2008 and <= 0x200A
            or 0x2028 or 0x2029 or 0x205F or 0x3000;

    /// <summary>A word character of <c>\w</c>, <c>\b</c>, <c>\&lt;</c>, <c>\&gt;</c>: alnum or underscore.</summary>
    internal static bool IsWord(int cp) => cp == '_' || Is(Alnum, cp);

    /// <summary>glibc <c>towupper</c> (what REG_ICASE folds both the pattern and the subject with).</summary>
    internal static int ToUpper(int cp)
    {
        if (cp < 0x80) return cp is >= 'a' and <= 'z' ? cp - 32 : cp;
        var runs = UpperRunTable;
        int lo = 0, hi = runs.Length / 4 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int first = runs[mid * 4], last = runs[mid * 4 + 1];
            if (cp < first) hi = mid - 1;
            else if (cp > last) lo = mid + 1;
            else return (cp - first) % runs[mid * 4 + 2] == 0 ? cp + runs[mid * 4 + 3] : cp;
        }
        return cp;
    }

    // Sorted [lo0, hi0, lo1, hi1, ...].
    private static bool InRanges(int[] table, int cp)
    {
        int lo = 0, hi = table.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < table[mid * 2]) hi = mid - 1;
            else if (cp > table[mid * 2 + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }

    private static int[] ParseRanges(string text)
    {
        var items = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[items.Length * 2];
        int prev = -1;
        for (int i = 0; i < items.Length; i++)
        {
            int dot = items[i].IndexOf('.');
            int lo = prev + 1 + Base36(items[i].AsSpan(0, dot));
            int hi = lo + Base36(items[i].AsSpan(dot + 1));
            result[i * 2] = lo;
            result[i * 2 + 1] = hi;
            prev = hi;
        }
        return result;
    }

    // [first, last, step, delta] per run.
    private static int[] ParseRuns(string text)
    {
        var items = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[items.Length * 4];
        int prev = -1;
        for (int i = 0; i < items.Length; i++)
        {
            var f = items[i].Split('.');
            int first = prev + 1 + Base36(f[0]);
            int step = 0; // decimal, from the generated table (not script text)
            foreach (var c in f[2]) step = step * 10 + (c - '0');
            int last = first + Base36(f[1]) * step;
            result[i * 4] = first;
            result[i * 4 + 1] = last;
            result[i * 4 + 2] = step;
            result[i * 4 + 3] = Base36(f[3]);
            prev = last;
        }
        return result;
    }

    private static int Base36(ReadOnlySpan<char> s)
    {
        bool neg = s.Length > 0 && s[0] == '-';
        int n = 0;
        for (int i = neg ? 1 : 0; i < s.Length; i++)
            n = n * 36 + (s[i] <= '9' ? s[i] - '0' : s[i] - 'a' + 10);
        return neg ? -n : n;
    }
}
