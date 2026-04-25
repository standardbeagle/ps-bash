using System.Text.RegularExpressions;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Normalizes interpreter output for byte-level comparison.
///
/// Rules (per qa-rubric.md Directive 1):
///   1. Strip ANSI escape sequences.
///   2. Normalize CRLF to LF.
///   3. Strip trailing whitespace per line.
///   4. Preserve trailing newline — it matters.
///   5. Do NOT normalize case or path separators.
/// </summary>
public static class Canonicalizer
{
    // Matches ANSI CSI escape sequences: ESC [ ... final-byte (0x40-0x7E)
    private static readonly Regex AnsiEscape = new(
        @"\x1B\[[0-9;]*[A-Za-z]",
        RegexOptions.Compiled);

    // Matches OSC sequences: ESC ] ... ST (ESC\ or BEL)
    private static readonly Regex OscEscape = new(
        @"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)",
        RegexOptions.Compiled);

    public static string Canonicalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // 1. Strip ANSI escapes
        text = AnsiEscape.Replace(text, string.Empty);
        text = OscEscape.Replace(text, string.Empty);

        // 2. Normalize CRLF -> LF
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        // 3. Strip trailing whitespace per line, preserve trailing newline
        bool trailingNewline = text.EndsWith('\n');
        var lines = text.TrimEnd('\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd();

        var result = string.Join('\n', lines);
        if (trailingNewline)
            result += '\n';

        return result;
    }
}
