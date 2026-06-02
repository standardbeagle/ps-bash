using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime.Compaction;

/// <summary>
/// Generic, command-agnostic output digest: stream-prefixed lines, run-collapsing,
/// and failure-aware head/tail selection capped at <c>maxLines</c>. This is the
/// fallback the <see cref="FilterEngine"/> uses when no command-aware filter matches,
/// so its output must stay byte-stable.
/// </summary>
public static class OutputCompactor
{
    private const int DefaultMaxLines = 120;

    public static string CompactCommandOutput(
        string command,
        int exitCode,
        bool timedOut,
        IReadOnlyList<OutputFrame> frames,
        int maxLines = DefaultMaxLines)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(frames);

        var lines = CompactionText.SplitFrames(frames);
        var stdoutCount = lines.Count(l => l.Stream == StreamTag.Stdout);
        var stderrCount = lines.Count(l => l.Stream == StreamTag.Stderr);
        var compacted = CompactionText.CollapseRuns(lines);
        var selected = SelectLines(compacted, exitCode != 0 || timedOut, maxLines);

        var sb = new StringBuilder();
        CompactionText.AppendHeader(sb, command, exitCode, timedOut, stdoutCount, stderrCount);

        foreach (var line in selected)
        {
            sb.Append(line.Stream == StreamTag.Stderr ? "[err] " : "[out] ")
              .AppendLine(line.Text);
        }

        return sb.ToString();
    }

    private static List<CompactionText.Line> SelectLines(List<CompactionText.Line> lines, bool failure, int maxLines)
    {
        if (lines.Count <= maxLines) return lines;

        var selected = new List<CompactionText.Line>();
        if (failure)
        {
            selected.AddRange(lines.Where(IsImportant).Take(maxLines / 2));
            AddTail(selected, lines, maxLines - selected.Count);
        }
        else
        {
            var head = Math.Max(8, maxLines / 4);
            selected.AddRange(lines.Take(head));
            selected.Add(new CompactionText.Line(StreamTag.Stdout, $"... omitted {Math.Max(0, lines.Count - maxLines)} compacted lines ..."));
            AddTail(selected, lines, maxLines - selected.Count);
        }

        return selected.Count <= maxLines ? selected : selected.Take(maxLines).ToList();
    }

    private static void AddTail(List<CompactionText.Line> selected, List<CompactionText.Line> source, int budget)
    {
        if (budget <= 0) return;
        foreach (var line in source.TakeLast(budget))
        {
            if (!selected.Contains(line)) selected.Add(line);
        }
    }

    private static bool IsImportant(CompactionText.Line line)
    {
        if (line.Stream == StreamTag.Stderr) return true;
        return Regex.IsMatch(
            line.Text,
            @"(?i)\b(error|failed|failure|exception|timeout|denied|fatal|warning)\b|:\d+(:\d+)?\b|line \d+| at .+\(.+:\d+\)");
    }
}
