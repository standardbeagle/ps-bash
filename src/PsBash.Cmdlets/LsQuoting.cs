using System.Globalization;
using System.Text;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>GNU <c>ls</c> file-name quoting styles (<c>--quoting-style</c>, <c>-Q</c>, <c>-b</c>, <c>-N</c>).</summary>
internal enum LsQuoteStyle { Literal, Shell, ShellAlways, ShellEscape, ShellEscapeAlways, C, CMaybe, Escape, Locale, CLocale }

/// <summary>
/// Pure implementation of GNU <c>ls</c> file-name quoting (coreutils 9.4 <c>quotearg</c>; oracle-checked in the C
/// locale, and applied here with a UTF-8 locale's notion of "printable": a non-ASCII letter is ordinary text, a
/// control character or an escaped invalid byte is not). Also the display width used to lay out columns.
/// </summary>
internal static class LsQuoting
{
    internal static readonly (string Name, LsQuoteStyle Style)[] StyleTable =
    {
        ("literal", LsQuoteStyle.Literal), ("shell", LsQuoteStyle.Shell), ("shell-always", LsQuoteStyle.ShellAlways),
        ("shell-escape", LsQuoteStyle.ShellEscape), ("shell-escape-always", LsQuoteStyle.ShellEscapeAlways),
        ("c", LsQuoteStyle.C), ("c-maybe", LsQuoteStyle.CMaybe), ("escape", LsQuoteStyle.Escape),
        ("locale", LsQuoteStyle.Locale), ("clocale", LsQuoteStyle.CLocale),
    };

    internal const string StyleValidBlock =
        "  - 'literal'\n  - 'shell'\n  - 'shell-always'\n  - 'shell-escape'\n  - 'shell-escape-always'\n"
        + "  - 'c'\n  - 'c-maybe'\n  - 'escape'\n  - 'locale'\n  - 'clocale'";

    /// <summary>True for a character GNU treats as non-printable: C0/C1 controls, DEL and escaped invalid bytes.</summary>
    internal static bool IsNonPrintable(char c, bool afterHighSurrogate = false)
        => char.IsControl(c) || (RawBytes.IsMarkerChar(c) && !afterHighSurrogate);

    /// <summary>The bytes of one non-printable character (a control character is its own byte, a marker the raw byte).</summary>
    private static IEnumerable<byte> BytesOf(char c)
    {
        if (RawBytes.IsMarkerChar(c)) { yield return RawBytes.FromMarker(c); yield break; }
        foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) yield return b;
    }

    private static string Octal(char c)
    {
        var sb = new StringBuilder();
        foreach (var b in BytesOf(c)) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
        return sb.ToString();
    }

    /// <summary>The C-style backslash letter for a control character, or null when it needs an octal escape.</summary>
    private static string? Letter(char c) => c switch
    {
        '\a' => "\\a", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\v' => "\\v",
        _ => null,
    };

    public static string Quote(string name, LsQuoteStyle style, bool hideControl)
    {
        switch (style)
        {
            case LsQuoteStyle.Literal:
                return hideControl ? ReplaceNonPrintable(name) : name;
            case LsQuoteStyle.C:
            case LsQuoteStyle.CLocale:
                return CStyle(name, '"');
            case LsQuoteStyle.CMaybe:
                return NeedsCEscape(name) ? CStyle(name, '"') : name;
            case LsQuoteStyle.Locale:
                return CStyle(name, '\'');
            case LsQuoteStyle.Escape:
                return EscapeStyle(name);
            default:
                return ShellStyle(name, style);
        }
    }

    private static string ReplaceNonPrintable(string name)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < name.Length; i++)
        {
            bool bad = IsNonPrintable(name[i], i > 0 && char.IsHighSurrogate(name[i - 1]));
            if (bad && sb is null) { sb = new StringBuilder(name, 0, i, name.Length); }
            sb?.Append(bad ? '?' : name[i]);
        }
        return sb?.ToString() ?? name;
    }

    private static bool NeedsCEscape(string name)
    {
        foreach (char c in name)
            if (c is '"' or '\\' || IsNonPrintable(c)) return true;
        return false;
    }

    /// <summary><c>"..."</c> / <c>'...'</c> with backslash escapes; only the active quote character is escaped.</summary>
    private static string CStyle(string name, char quote)
    {
        var sb = new StringBuilder(name.Length + 2).Append(quote);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == quote || c == '\\') sb.Append('\\').Append(c);
            else if (IsNonPrintable(c, i > 0 && char.IsHighSurrogate(name[i - 1])))
                sb.Append(Letter(c) ?? Octal(c));
            else sb.Append(c);
        }
        return sb.Append(quote).ToString();
    }

    /// <summary><c>-b</c>: no quotes; backslash, control characters and spaces are escaped.</summary>
    private static string EscapeStyle(string name)
    {
        var sb = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '\\') sb.Append("\\\\");
            else if (c == ' ') sb.Append("\\ ");
            else if (IsNonPrintable(c, i > 0 && char.IsHighSurrogate(name[i - 1])))
                sb.Append(Letter(c) ?? Octal(c));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private const string ShellSpecial = " !\"$&'()*;<=>?[\\^`|";

    private static string ShellStyle(string name, LsQuoteStyle style)
    {
        bool always = style is LsQuoteStyle.ShellAlways or LsQuoteStyle.ShellEscapeAlways;
        bool escape = style is LsQuoteStyle.ShellEscape or LsQuoteStyle.ShellEscapeAlways;

        bool needs = always || name.Length == 0 || name[0] is '#' or '~';
        bool anyControl = false, hasSingle = false, hasDoubleSpecial = false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (IsNonPrintable(c, i > 0 && char.IsHighSurrogate(name[i - 1]))) { anyControl = true; needs = true; }
            else if (ShellSpecial.IndexOf(c) >= 0) needs = true;
            if (c == '\'') hasSingle = true;
            if (c is '"' or '$' or '`' or '\\') hasDoubleSpecial = true;
        }
        if (!needs) return name;

        // A name with a single quote and nothing a double-quoted string would expand or escape: "it's".
        if (hasSingle && !hasDoubleSpecial && !(escape && anyControl))
            return "\"" + name + "\"";

        var sb = new StringBuilder(name.Length + 4).Append('\'');
        bool open = true;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (escape && IsNonPrintable(c, i > 0 && char.IsHighSurrogate(name[i - 1])))
            {
                if (open) { sb.Append('\''); open = false; }
                // Consecutive non-printables share one $'...' group (é as \303\251).
                int j = i;
                var group = new StringBuilder();
                while (j < name.Length && IsNonPrintable(name[j], j > 0 && char.IsHighSurrogate(name[j - 1])))
                {
                    group.Append(Letter(name[j]) ?? Octal(name[j]));
                    j++;
                }
                sb.Append("$'").Append(group).Append('\'');
                i = j - 1;
                continue;
            }
            if (!open) { sb.Append('\''); open = true; }
            if (c == '\'') sb.Append("'\\''"); else sb.Append(c);
        }
        if (open) sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>
    /// Terminal columns a string occupies: control characters and combining marks take none, East Asian wide
    /// characters two (GNU measures with <c>mbswidth</c>; tab in a name is width 0, oracle-checked).
    /// </summary>
    public static int Width(string s)
    {
        int w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsControl(c) || (RawBytes.IsMarkerChar(c) && !(i > 0 && char.IsHighSurrogate(s[i - 1])))) continue;
            if (char.IsLowSurrogate(c)) continue; // counted with its high surrogate
            int cp = c;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                cp = char.ConvertToUtf32(c, s[i + 1]);
            var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            w += IsWide(cp) ? 2 : 1;
        }
        return w;
    }

    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) || (cp >= 0x2E80 && cp <= 0xA4CF) || (cp >= 0xAC00 && cp <= 0xD7A3)
        || (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0xFE30 && cp <= 0xFE6F) || (cp >= 0xFF00 && cp <= 0xFF60)
        || (cp >= 0xFFE0 && cp <= 0xFFE6) || (cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x20000 && cp <= 0x3FFFD);
}
