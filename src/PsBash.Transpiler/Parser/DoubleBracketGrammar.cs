namespace PsBash.Core.Parser;

/// <summary>
/// Structural check of a <c>[[ … ]]</c> condition, so a malformed one (<c>[[ ]]</c>, <c>[[ -z ]]</c>, <c>[[ &amp;&amp; ]]</c>,
/// <c>[[ a b ]]</c>, <c>[[ ( a ]]</c>) is the syntax error (status 2) bash reports instead of silently evaluating to a
/// constant. Pure: each token is its operator text when it was an UNQUOTED literal word, else <c>null</c> (an operand).
/// </summary>
internal static class DoubleBracketGrammar
{
    private static readonly HashSet<string> Binary = new(StringComparer.Ordinal)
        { "==", "!=", "=", "=~", "<", ">", "-eq", "-ne", "-lt", "-gt", "-le", "-ge", "-nt", "-ot", "-ef" };

    private static readonly HashSet<string> Unary = new(StringComparer.Ordinal)
    {
        "-a", "-b", "-c", "-d", "-e", "-f", "-g", "-h", "-k", "-p", "-r", "-s", "-t", "-u", "-w", "-x",
        "-G", "-L", "-N", "-O", "-S", "-o", "-v", "-R", "-z", "-n",
    };

    public static bool IsWellFormed(IReadOnlyList<string?> toks)
    {
        int i = 0;
        return Or(toks, ref i) && i == toks.Count;
    }

    private static bool Structural(string? t) => t is "&&" or "||" or ")";

    private static bool Or(IReadOnlyList<string?> t, ref int i)
    {
        if (!And(t, ref i)) return false;
        while (i < t.Count && t[i] == "||") { i++; if (!And(t, ref i)) return false; }
        return true;
    }

    private static bool And(IReadOnlyList<string?> t, ref int i)
    {
        if (!Not(t, ref i)) return false;
        while (i < t.Count && t[i] == "&&") { i++; if (!Not(t, ref i)) return false; }
        return true;
    }

    private static bool Not(IReadOnlyList<string?> t, ref int i)
    {
        while (i < t.Count && t[i] == "!") i++;
        if (i >= t.Count) return false;
        return Primary(t, ref i);
    }

    private static bool Primary(IReadOnlyList<string?> t, ref int i)
    {
        if (i >= t.Count) return false;
        var cur = t[i];
        if (cur == "(")
        {
            i++;
            if (!Or(t, ref i) || i >= t.Count || t[i] != ")") return false;
            i++;
            return true;
        }
        if (cur is not null && Unary.Contains(cur) && i + 1 < t.Count && !Structural(t[i + 1]))
        {
            i += 2;
            return true;
        }
        // A unary operator with no operand (`[[ -z ]]`) is a syntax error, not a one-word test.
        if (cur is not null && Unary.Contains(cur)) return false;
        // &&, ||, ) and a symbolic binary operator cannot start an operand.
        if (Structural(cur) || cur is "==" or "!=" or "=~" or "<" or ">") return false;
        i++; // operand word
        if (i < t.Count && t[i] is { } op && Binary.Contains(op))
        {
            if (i + 1 >= t.Count || Structural(t[i + 1])) return false;
            i += 2;
        }
        return true;
    }
}
