using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// How GNU coreutils names a file in a diagnostic (gnulib <c>quotearg</c>, shell-escape styles): the name is quoted so
/// it can be pasted back into a shell. Two forms, by what the coreutils source calls:
/// <list type="bullet">
/// <item><see cref="IfNeeded"/> = <c>quotef</c> (<c>cat: nosuch: ...</c>, <c>cat: 'a b': ...</c>, <c>wc: "it's": ...</c>,
/// readlink, realpath): bare unless a character needs quoting.</item>
/// <item><see cref="Always"/> = <c>quoteaf</c> (<c>ls: cannot access 'x'</c>, <c>rm: cannot remove 'x'</c>,
/// <c>head: cannot open 'x' for reading</c>): always quoted.</item>
/// </list>
/// Quoting: single quotes, a <c>'</c> inside as <c>'\''</c>, a control character as a <c>$'\t'</c> / <c>$'\ooo'</c>
/// segment between quoted runs (<c>'a'$'\t''b'</c>). A name whose only awkward character is <c>'</c> (none of
/// <c>$ ` " \ !</c>, no control character) is double-quoted instead (<c>"it's"</c>). Needs quoting: the empty name, a
/// shell special (<c>space ! " $ &amp; ' ( ) * ; &lt; = &gt; ? [ \ ^ ` |</c>), <c>:</c> (the colon style both forms
/// use), a control character, and <c>#</c> / <c>~</c> as the first character. Facts from GNU coreutils 9.4,
/// C.UTF-8 (every printable ASCII character probed); a non-ASCII letter (<c>é</c>) needs none.
/// </summary>
public static class GnuQuote
{
    /// <summary><c>quotef</c>: <paramref name="name"/> bare when nothing in it needs quoting, else quoted.</summary>
    public static string IfNeeded(string name) => NeedsQuoting(name) ? Quote(name) : name;

    /// <summary><c>quoteaf</c>: <paramref name="name"/> always quoted.</summary>
    public static string Always(string name) => Quote(name);

    internal static bool NeedsQuoting(string name)
    {
        if (name.Length == 0) return true;
        if (name[0] is '#' or '~') return true;
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            if (ch == ':' && i == 1 && IsDrivePrefix(name)) continue;
            if (IsSpecial(ch) || IsControl(ch)) return true;
        }
        return false;
    }

    /// <summary>
    /// Windows: the <c>:</c> of a drive root (<c>C:/x</c>, <c>C:\x</c>, <c>C:</c>) is the platform's <c>/</c>, not a
    /// character of the name, so it alone does not quote the name: <c>realpath: C:/tmp/f: ...</c> as GNU prints
    /// <c>realpath: /tmp/f: ...</c>.
    /// </summary>
    private static bool IsDrivePrefix(string name)
    {
        if (!OperatingSystem.IsWindows() || name.Length < 2 || !char.IsAsciiLetter(name[0]) || name[1] != ':') return false;
        return name.Length == 2 || name[2] is '/' or '\\';
    }

    private static bool IsSpecial(char ch) => ch is ' ' or '!' or '"' or '$' or '&' or '\'' or '(' or ')' or '*' or ':'
        or ';' or '<' or '=' or '>' or '?' or '[' or '\\' or '^' or '`' or '|';

    private static bool IsControl(char ch) => ch < 0x20 || ch == 0x7f;

    private static string Quote(string name)
    {
        bool hasSingle = false, dqUnsafe = false;
        foreach (char ch in name)
        {
            if (ch == '\'') hasSingle = true;
            else if (ch is '$' or '`' or '"' or '\\' or '!' || IsControl(ch)) dqUnsafe = true;
        }
        if (hasSingle && !dqUnsafe) return "\"" + name + "\"";

        var sb = new StringBuilder(name.Length + 2).Append('\'');
        foreach (char ch in name)
        {
            if (ch == '\'') sb.Append("'\\''");
            else if (IsControl(ch)) sb.Append("'$'").Append(Escape(ch)).Append("''");
            else sb.Append(ch);
        }
        return sb.Append('\'').ToString();
    }

    private static string Escape(char ch) => ch switch
    {
        '\a' => "\\a", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\v' => "\\v",
        _ => "\\" + Convert.ToString(ch, 8).PadLeft(3, '0'),
    };
}
