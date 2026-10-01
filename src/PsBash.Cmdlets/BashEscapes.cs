using System;
using System.Text;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// Which bash builtin's backslash-escape grammar to apply. The four dialects differ in
/// exactly the places bash's own builtins differ (oracle-verified against bash 5.2):
/// <list type="bullet">
/// <item><see cref="Echo"/> (<c>echo -e</c>): <c>\0NNN</c> (0 + up to 3 octal digits); a bare
/// <c>\NNN</c> is NOT octal and stays literal; <c>\c</c> stops all output; <c>\"</c> stays literal.</item>
/// <item><see cref="PrintfB"/> (<c>printf %b</c> argument): like echo, but <c>\NNN</c> (1-3 octal
/// digits) is also octal.</item>
/// <item><see cref="PrintfFormat"/> (the <c>printf</c> format string): <c>\NNN</c> is 1-3 octal digits
/// INCLUDING the first (so <c>\0101</c> is <c>\010</c> then <c>1</c>); <c>\"</c> <c>\'</c> <c>\?</c>
/// are the bare characters; <c>\c</c> is NOT special.</item>
/// </list>
/// <c>tr</c> SETs are NOT a dialect of <see cref="BashEscapes.Expand(string, EscapeDialect)"/>: an
/// escaped <c>-</c> or <c>[</c> must stay a literal through range / class expansion, so they have their
/// own one-pass <see cref="BashEscapes.ExpandTrSet"/>.
/// </summary>
public enum EscapeDialect { Echo, PrintfB, PrintfFormat }

/// <summary>A malformed tr SET (today only a reverse range); the message is GNU's, without the <c>tr: </c> prefix.</summary>
public sealed class TrSetException : Exception
{
    public TrSetException(string message) : base(message) { }
}

/// <summary>
/// The ONE backslash-escape expander (echo -e, printf format, printf %b, tr SETs). An unrecognised
/// escape keeps its backslash, as bash does. <c>\0</c> yields a real NUL char.
/// </summary>
public static class BashEscapes
{
    public static string Expand(string text, EscapeDialect dialect) => Expand(text, dialect, out _);

    /// <param name="stopped">True when a <c>\c</c> (Echo / PrintfB only) ended the output early;
    /// nothing after it is produced.</param>
    public static string Expand(string text, EscapeDialect dialect, out bool stopped)
    {
        stopped = false;
        if (text.IndexOf('\\') < 0) return text;

        // \xHH / \NNN name BYTES: runs of them are UTF-8 decoded (see EscapedTextBuilder).
        var sb = new EscapedTextBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length) { sb.Append(c); i++; continue; }

            char n = text[i + 1];
            switch (n)
            {
                case 'a': sb.Append('\a'); i += 2; continue;
                case 'b': sb.Append('\b'); i += 2; continue;
                case 'f': sb.Append('\f'); i += 2; continue;
                case 'n': sb.Append('\n'); i += 2; continue;
                case 'r': sb.Append('\r'); i += 2; continue;
                case 't': sb.Append('\t'); i += 2; continue;
                case 'v': sb.Append('\v'); i += 2; continue;
                case '\\': sb.Append('\\'); i += 2; continue;
            }

            switch (n)
            {
                case 'e':
                case 'E': sb.Append('\x1b'); i += 2; continue;
                case 'c' when dialect != EscapeDialect.PrintfFormat:
                    stopped = true;
                    return sb.ToString();
                case 'x':
                    if (TryHex(text, i + 2, 2, out int hv, out int hEnd)) { sb.AppendByte(hv); i = hEnd; }
                    else { sb.Append('\\').Append('x'); i += 2; }
                    continue;
                case 'u':
                case 'U':
                    if (TryHex(text, i + 2, n == 'u' ? 4 : 8, out int uv, out int uEnd))
                    {
                        if (uv <= 0x10FFFF && !(uv >= 0xD800 && uv <= 0xDFFF))
                            sb.Append(char.ConvertFromUtf32(uv));
                        i = uEnd;
                    }
                    else { sb.Append('\\').Append(n); i += 2; }
                    continue;
            }
            if (dialect == EscapeDialect.PrintfFormat && n is '"' or '\'' or '?')
            {
                sb.Append(n); i += 2; continue;
            }

            if (n is >= '0' and <= '7')
            {
                // Echo: only \0NNN. PrintfB: \0NNN and \NNN. PrintfFormat: \NNN (1-3 digits total).
                bool zeroPrefixed = dialect is EscapeDialect.Echo or EscapeDialect.PrintfB;
                if (n == '0' && zeroPrefixed)
                    ParseOctal(text, i + 2, 3, sb, out i);
                else if (n != '0' && dialect == EscapeDialect.Echo)
                { sb.Append('\\').Append(n); i += 2; }
                else
                    ParseOctal(text, i + 1, 3, sb, out i);
                continue;
            }

            sb.Append('\\').Append(n);
            i += 2;
        }
        return sb.ToString();
    }

    private static readonly (string Name, string Chars)[] TrClasses =
    {
        ("alnum", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"),
        ("alpha", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"),
        ("digit", "0123456789"),
        ("upper", "ABCDEFGHIJKLMNOPQRSTUVWXYZ"),
        ("lower", "abcdefghijklmnopqrstuvwxyz"),
        ("space", " \t\n\r\f\v"),
        ("punct", "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~"),
    };

    /// <summary>
    /// Expands one <c>tr</c> SET operand (escapes, <c>[:class:]</c>, <c>a-z</c> ranges) in ONE pass
    /// (oracle-checked, GNU tr 9.4). <c>\NNN</c> is 1-3 octal digits and <c>\a \b \f \n \r \t \v \\</c>
    /// are the control characters; every OTHER escape (<c>\q \x \e \c \- \[</c>) drops the backslash and
    /// yields the character itself, which is then a LITERAL — an escaped <c>-</c> is never a range
    /// operator and an escaped <c>[</c> never opens a class. A class's own characters (the <c>-</c> in
    /// <c>[:punct:]</c>) are literals too. <paramref name="trailingBackslash"/> is true when the SET ends in
    /// a lone backslash (kept as a literal backslash; GNU warns on stderr). Throws
    /// <see cref="TrSetException"/> for a reverse range.
    /// </summary>
    public static string ExpandTrSet(string spec, out bool trailingBackslash)
    {
        trailingBackslash = false;
        // lit = produced by an escape or a class: usable as a range endpoint, never as the '-' operator.
        var items = new List<(char Ch, bool Lit)>(spec.Length);
        int i = 0;
        while (i < spec.Length)
        {
            char c = spec[i];
            if (c == '\\')
            {
                if (i + 1 >= spec.Length) { items.Add(('\\', true)); trailingBackslash = true; i++; continue; }
                char n = spec[i + 1];
                switch (n)
                {
                    case 'a': items.Add(('\a', true)); i += 2; continue;
                    case 'b': items.Add(('\b', true)); i += 2; continue;
                    case 'f': items.Add(('\f', true)); i += 2; continue;
                    case 'n': items.Add(('\n', true)); i += 2; continue;
                    case 'r': items.Add(('\r', true)); i += 2; continue;
                    case 't': items.Add(('\t', true)); i += 2; continue;
                    case 'v': items.Add(('\v', true)); i += 2; continue;
                }
                if (n is >= '0' and <= '7')
                {
                    int j = i + 1, val = 0, cnt = 0;
                    while (j < spec.Length && cnt < 3 && spec[j] is >= '0' and <= '7') { val = val * 8 + (spec[j] - '0'); j++; cnt++; }
                    // \NNN names a BYTE: >= 0x80 is the escaped-byte marker (RawBytes), so it matches the
                    // same byte in the (marker-decoded) input stream.
                    int octByte = val & 0xFF;
                    items.Add((octByte >= 0x80 ? RawBytes.ToMarker((byte)octByte) : (char)octByte, true));
                    i = j;
                    continue;
                }
                items.Add((n, true)); // `\\` and every unknown escape: the character, backslash dropped
                i += 2;
                continue;
            }

            if (c == '[' && i + 1 < spec.Length && spec[i + 1] == ':')
            {
                int end = spec.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    string name = spec.Substring(i + 2, end - i - 2);
                    string? chars = null;
                    foreach (var cls in TrClasses)
                    {
                        if (cls.Name == name) { chars = cls.Chars; break; }
                    }
                    if (chars is not null)
                    {
                        foreach (char ch in chars) items.Add((ch, true));
                        i = end + 2;
                        continue;
                    }
                }
            }

            items.Add((c, false));
            i++;
        }

        var sb = new StringBuilder(items.Count);
        for (int k = 0; k < items.Count;)
        {
            if (k + 2 < items.Count && items[k + 1] is { Ch: '-', Lit: false })
            {
                int start = items[k].Ch, end = items[k + 2].Ch;
                if (start > end)
                    throw new TrSetException(
                        $"range-endpoints of '{(char)start}-{(char)end}' are in reverse collating sequence order");
                for (int ch = start; ch <= end; ch++) sb.Append((char)ch);
                k += 3;
            }
            else
            {
                sb.Append(items[k].Ch);
                k++;
            }
        }
        return sb.ToString();
    }

    private static void ParseOctal(string s, int start, int maxDigits, EscapedTextBuilder sb, out int next)
    {
        int j = start, val = 0, cnt = 0;
        while (j < s.Length && cnt < maxDigits && s[j] is >= '0' and <= '7')
        {
            val = val * 8 + (s[j] - '0');
            j++; cnt++;
        }
        sb.AppendByte(val);
        next = j;
    }

    private static bool TryHex(string s, int start, int maxDigits, out int value, out int next)
    {
        int j = start, cnt = 0; long v = 0;
        while (j < s.Length && cnt < maxDigits && Uri.IsHexDigit(s[j]))
        {
            v = v * 16 + Convert.ToInt32(s[j].ToString(), 16);
            j++; cnt++;
        }
        value = (int)Math.Min(v, int.MaxValue);
        next = j;
        return cnt > 0;
    }
}
