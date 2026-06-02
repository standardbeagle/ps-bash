using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime.Compaction;

/// <summary>
/// What to do when no command-aware <see cref="FilterSpec"/> matches.
/// </summary>
public enum GenericFallback
{
    /// <summary>
    /// Always emit the plain <see cref="OutputCompactor"/> digest. This is the default and
    /// keeps <see cref="FilterEngine.Apply"/> byte-identical to the pre-filter behavior
    /// (the P0 regression guard relies on it).
    /// </summary>
    None,

    /// <summary>
    /// On a failing command (exit != 0 or timeout), keep only error / warning / test-failure
    /// / summary lines (plus all of stderr) and drop the rest — tokf's <c>err</c>/<c>test</c>
    /// catch-all for commands with no dedicated filter. Successful commands are unaffected.
    /// </summary>
    ErrorExtract,
}

/// <summary>
/// Command-agnostic reductions used when no named filter matches but the caller still
/// wants more than the plain digest (see <see cref="GenericFallback.ErrorExtract"/>).
/// </summary>
internal static class GenericFallbacks
{
    // Lines worth surfacing from a failed command across common toolchains.
    private static readonly Regex Important = new(
        @"(?i)\b(error|errors|failed|failure|exception|panic|traceback|fatal|denied|timeout|warning|warnings|assert\w*)\b" +
        @"|npm ERR!|---\s*FAIL|^\s*at\s|:\d+(:\d+)?\b|\berror\[[A-Z]?\d+\]|\bCS\d{3,}\b|✕|✗",
        RegexOptions.Compiled);

    // Result/summary lines that give the failure its headline count.
    private static readonly Regex Summary = new(
        @"(?i)(\btest result:|\btests?:\s|\d+ (passed|failed|errors?|warnings?)\b|build (succeeded|failed)|\bFAILED\b|short test summary)",
        RegexOptions.Compiled);

    public static string Apply(
        string command, int exitCode, bool timedOut, IReadOnlyList<OutputFrame> frames, int maxLines)
    {
        // Success: nothing to extract — defer to the plain digest unchanged.
        if (exitCode == 0 && !timedOut)
            return OutputCompactor.CompactCommandOutput(command, exitCode, timedOut, frames, maxLines);

        var lines = CompactionText.SplitFrames(frames);
        var stdoutCount = lines.Count(l => l.Stream == StreamTag.Stdout);
        var stderrCount = lines.Count(l => l.Stream == StreamTag.Stderr);
        var collapsed = CompactionText.CollapseRuns(lines);

        var kept = collapsed
            .Where(l => l.Stream == StreamTag.Stderr || Important.IsMatch(l.Text) || Summary.IsMatch(l.Text))
            .ToList();

        // An opaque failure with no recognizable signal must NOT be emptied — fall back to
        // the plain digest so the user still sees the tail of what happened.
        if (kept.Count == 0)
            return OutputCompactor.CompactCommandOutput(command, exitCode, timedOut, frames, maxLines);

        if (kept.Count > maxLines) kept = kept.Take(maxLines).ToList();

        var sb = new StringBuilder();
        CompactionText.AppendHeader(sb, command, exitCode, timedOut, stdoutCount, stderrCount);
        foreach (var line in kept)
            sb.Append(line.Stream == StreamTag.Stderr ? "[err] " : "[out] ").AppendLine(line.Text);
        return sb.ToString();
    }
}
