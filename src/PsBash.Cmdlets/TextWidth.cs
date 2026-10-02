using System.Buffers;
using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Terminal display width of text, the way <c>column</c> and <c>ls</c> lay out columns: wide East-Asian and
/// emoji code points take 2 cells, combining / format / control characters 0, everything else 1. A lone
/// surrogate (an escaped-byte marker for an invalid byte) counts as 1.
/// </summary>
internal static class TextWidth
{
    public static int Of(string s)
    {
        bool ascii = true;
        foreach (char c in s)
        {
            if (c >= 0x7F || c < 0x20) { ascii = false; break; }
        }
        if (ascii) return s.Length;

        int w = 0;
        ReadOnlySpan<char> span = s;
        int i = 0;
        while (i < span.Length)
        {
            if (Rune.DecodeFromUtf16(span[i..], out var rune, out int consumed) == OperationStatus.Done)
            {
                w += RuneWidth(rune.Value);
                i += consumed;
            }
            else
            {
                w += 1;
                i += 1;
            }
        }
        return w;
    }

    private static int RuneWidth(int cp)
    {
        if (cp == 0) return 0;
        switch (CharUnicodeInfo.GetUnicodeCategory(cp))
        {
            case UnicodeCategory.Control:
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.Format:
                return 0;
        }
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) ||
        (cp >= 0x2E80 && cp <= 0x303E) ||
        (cp >= 0x3041 && cp <= 0x33FF) ||
        (cp >= 0x3400 && cp <= 0x4DBF) ||
        (cp >= 0x4E00 && cp <= 0x9FFF) ||
        (cp >= 0xA000 && cp <= 0xA4CF) ||
        (cp >= 0xAC00 && cp <= 0xD7A3) ||
        (cp >= 0xF900 && cp <= 0xFAFF) ||
        (cp >= 0xFE10 && cp <= 0xFE19) ||
        (cp >= 0xFE30 && cp <= 0xFE6F) ||
        (cp >= 0xFF00 && cp <= 0xFF60) ||
        (cp >= 0xFFE0 && cp <= 0xFFE6) ||
        (cp >= 0x1F300 && cp <= 0x1FAFF) ||
        (cp >= 0x20000 && cp <= 0x3FFFD);

    /// <summary>
    /// The width a column layout wraps at when stdout is not a terminal: the positive <c>COLUMNS</c>
    /// environment variable, else 80 (what <c>ls</c> and util-linux <c>column</c> do without a tty).
    /// </summary>
    public static int DefaultTerminalColumns()
    {
        string? env = Environment.GetEnvironmentVariable("COLUMNS");
        return int.TryParse(env, NumberStyles.None, CultureInfo.InvariantCulture, out int c) && c > 0 ? c : 80;
    }
}
