using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU sed's character escapes (<c>normalize_text</c>), one decoder for every place sed reads them: a regex, an
/// <c>s</c> replacement and a <c>y</c> string. <c>\a \f \v \r \t</c>, <c>\dNNN</c> (decimal), <c>\oNNN</c> (octal),
/// <c>\xHH</c> (hex) and <c>\cX</c> (control) each stand for one character. In a regex that character is then read as
/// regex text (<c>s/\x2e/X/g</c> replaces every character, as <c>.</c> does); in a replacement or a <c>y</c> string it is
/// itself (<c>s/b/\x26/</c> inserts <c>&amp;</c>). <c>\n</c> is left to each caller (a newline in all three, but a regex
/// keeps it as <c>\n</c> for <see cref="GnuRegex"/>).
/// </summary>
internal static class SedEscape
{
    /// <summary>
    /// Decode the escape whose letter is at <paramref name="at"/> (just after the backslash). True with the character
    /// and the index after the escape; false when it is no character escape (<c>\q</c>, <c>\1</c>, <c>\x</c> with no
    /// hex digit after it).
    /// </summary>
    public static bool TryDecode(string s, int at, out char value, out int next)
    {
        value = '\0';
        next = at + 1;
        if (at >= s.Length) return false;
        switch (s[at])
        {
            case 'a': value = '\a'; return true;
            case 'f': value = '\f'; return true;
            case 'v': value = '\v'; return true;
            case 'r': value = '\r'; return true;
            case 't': value = '\t'; return true;
            case 'd': return Number(s, at + 1, 10, 3, out value, out next);
            case 'o': return Number(s, at + 1, 8, 3, out value, out next);
            case 'x': return Number(s, at + 1, 16, 2, out value, out next);
            case 'c':
                if (at + 1 >= s.Length) return false;
                value = (char)(char.ToUpperInvariant(s[at + 1]) ^ 0x40);
                next = at + 2;
                return true;
            default: return false;
        }
    }

    private static bool Number(string s, int start, int radix, int maxDigits, out char value, out int next)
    {
        int v = 0, n = 0;
        while (n < maxDigits && start + n < s.Length)
        {
            int d = HexDigit(s[start + n]);
            if (d < 0 || d >= radix) break;
            v = v * radix + d;
            n++;
        }
        value = (char)(v & 0xFF);
        next = start + n;
        return n > 0;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>The character an escape stands for in data: a value of 0x80 or more is that one byte (its marker).</summary>
    public static char DataChar(char v) => v >= 0x80 ? PsBash.Core.RawBytes.ToMarker((byte)v) : v;

    /// <summary>A regex with its character escapes replaced by the characters (every other <c>\X</c>, and <c>\\</c>, kept).</summary>
    public static string NormalizeRegex(string pattern)
    {
        if (pattern.IndexOf('\\') < 0) return pattern;
        var sb = new StringBuilder(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c != '\\' || i + 1 >= pattern.Length) { sb.Append(c); continue; }
            if (TryDecode(pattern, i + 1, out char v, out int next))
            {
                sb.Append(DataChar(v));
                i = next - 1;
                continue;
            }
            sb.Append(c).Append(pattern[i + 1]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// A <c>y</c> string as GNU reads it after the field scan: <c>\\</c> is a backslash, <c>\n</c> a newline, a character
    /// escape its character, and a backslash before any other character is dropped (<c>y/\q/Z/</c> maps <c>q</c>).
    /// </summary>
    public static string TransliterationText(string field)
    {
        if (field.IndexOf('\\') < 0) return field;
        var sb = new StringBuilder(field.Length);
        for (int i = 0; i < field.Length; i++)
        {
            char c = field[i];
            if (c != '\\' || i + 1 >= field.Length) { sb.Append(c); continue; }
            if (TryDecode(field, i + 1, out char v, out int next))
            {
                sb.Append(v);
                i = next - 1;
                continue;
            }
            char n = field[++i];
            sb.Append(n == 'n' ? '\n' : n);
        }
        return sb.ToString();
    }
}
