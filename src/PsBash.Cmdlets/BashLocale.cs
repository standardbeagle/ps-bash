using System.Collections;
using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The shell's character model for string operations: in the C / POSIX locale a string is BYTES (<c>${#s}</c> counts
/// bytes, <c>?</c> in <c>${s/_?_/x}</c> matches one byte); otherwise it is characters (code points: a supplementary
/// character is one, a byte that is not valid UTF-8 is one). The locale is bash's: the first non-empty of
/// <c>LC_ALL</c>, <c>LC_CTYPE</c>, <c>LANG</c>, read when the operation runs (an assignment changes it at once, like
/// bash). Only an explicit <c>C</c> / <c>POSIX</c> selects bytes; nothing set means the UTF-8 the host runs in.
/// </summary>
public static class BashLocale
{
    /// <summary>True when the shell's character locale is C / POSIX (strings are bytes).</summary>
    public static bool IsByteLocale()
    {
        string? v = First("LC_ALL") ?? First("LC_CTYPE") ?? First("LANG");
        return v is "C" or "POSIX";

        static string? First(string name) => BashVariableStore.Get(name) is { Length: > 0 } s ? s : null;
    }

    /// <summary>
    /// <c>${#VAR}</c>: the length of a value in characters, or in bytes in the C locale. A list (what the PowerShell
    /// <c>.Length</c> this replaces counted) is its element count; no value is 0.
    /// </summary>
    public static int Length(object? value)
    {
        if (value is PSObject pso) value = pso.BaseObject;
        return value switch
        {
            null => 0,
            string s => TextLength(s),
            ICollection c => c.Count,
            _ => TextLength(value.ToString() ?? ""),
        };
    }

    /// <summary>Length of <paramref name="s"/> in the current locale: bytes in C, else code points.</summary>
    public static int TextLength(string s)
    {
        if (s.Length == 0) return 0;
        if (IsByteLocale()) return RawBytes.GetByteCount(s);
        int n = s.Length;
        for (int i = 1; i < s.Length; i++)
            if (char.IsLowSurrogate(s[i]) && char.IsHighSurrogate(s[i - 1])) n--;   // a surrogate pair is one character
        return n;
    }
}
