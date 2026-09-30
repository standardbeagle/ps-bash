using System;
using System.Text;

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
/// <item><see cref="Tr"/> (<c>tr</c> SETs): <c>\NNN</c> 1-3 octal digits and the single-char escapes only —
/// no <c>\x</c>, <c>\u</c>, <c>\e</c>.</item>
/// </list>
/// </summary>
public enum EscapeDialect { Echo, PrintfB, PrintfFormat, Tr }

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

        bool full = dialect != EscapeDialect.Tr; // \e \x \u \U
        var sb = new StringBuilder(text.Length);
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

            if (full)
            {
                switch (n)
                {
                    case 'e':
                    case 'E': sb.Append('\x1b'); i += 2; continue;
                    case 'c' when dialect != EscapeDialect.PrintfFormat:
                        stopped = true;
                        return sb.ToString();
                    case 'x':
                        if (TryHex(text, i + 2, 2, out int hv, out int hEnd)) { sb.Append((char)hv); i = hEnd; }
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
            }

            if (n is >= '0' and <= '7')
            {
                // Echo: only \0NNN. PrintfB: \0NNN and \NNN. PrintfFormat/Tr: \NNN (1-3 digits total).
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

    private static void ParseOctal(string s, int start, int maxDigits, StringBuilder sb, out int next)
    {
        int j = start, val = 0, cnt = 0;
        while (j < s.Length && cnt < maxDigits && s[j] is >= '0' and <= '7')
        {
            val = val * 8 + (s[j] - '0');
            j++; cnt++;
        }
        sb.Append((char)(val & 0xFF));
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
