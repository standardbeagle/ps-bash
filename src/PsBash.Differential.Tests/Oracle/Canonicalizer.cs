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
///   6. RC-8b: Normalize process-substitution filenames. Bash uses
///      <c>/dev/fd/N</c> (a transient fd path); ps-bash's temp-file route
///      uses <c>{TMPDIR}/ps-bash/proc-sub/{random}</c>. Both are intrinsically
///      non-reproducible across runs/platforms but semantically equivalent —
///      collapse them to <c>&lt;PROC_SUB&gt;</c> so consumers that echo the
///      operand path (e.g. <c>wc -l &lt;(cmd)</c>) can byte-diff cleanly.
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

    // Bash process-substitution fd path: /dev/fd/<digits>
    private static readonly Regex BashProcSubPath = new(
        @"/dev/fd/\d+",
        RegexOptions.Compiled);

    // ps-bash temp-file process-substitution path:
    //   <tmpdir>/ps-bash/proc-sub/<random-token>
    // The random token from Path.GetRandomFileName is 12 chars + dot + 3 chars.
    // We match a permissive [^\s]+ tail so platform tmpdir variations all collapse.
    private static readonly Regex PsBashProcSubPath = new(
        @"[^\s]*[/\\]ps-bash[/\\]proc-sub[/\\][^\s]+",
        RegexOptions.Compiled);

    public static string Canonicalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // 1. Strip ANSI escapes
        text = AnsiEscape.Replace(text, string.Empty);
        text = OscEscape.Replace(text, string.Empty);

        // 1b. RC-8b: collapse process-substitution operand paths to a placeholder
        // so byte-diff is stable across the bash /dev/fd/N vs ps-bash temp-file
        // representations. Order matters — match the more specific ps-bash
        // temp-file path first to avoid the /dev/fd substring fallback eating
        // it (defensive; the patterns don't actually overlap today).
        text = PsBashProcSubPath.Replace(text, "<PROC_SUB>");
        text = BashProcSubPath.Replace(text, "<PROC_SUB>");

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
