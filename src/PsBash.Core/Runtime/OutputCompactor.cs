using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime;

public readonly record struct OutputFrame(StreamTag Stream, string Text);

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

        var lines = SplitFrames(frames);
        var stdoutCount = lines.Count(l => l.Stream == StreamTag.Stdout);
        var stderrCount = lines.Count(l => l.Stream == StreamTag.Stderr);
        var compacted = CollapseRuns(lines);
        var selected = SelectLines(compacted, exitCode != 0 || timedOut, maxLines);

        var sb = new StringBuilder();
        sb.Append("ps-bash compact-output: exit=").Append(exitCode);
        if (timedOut) sb.Append(" timeout=true");
        sb.Append(" stdout_lines=").Append(stdoutCount)
          .Append(" stderr_lines=").Append(stderrCount)
          .Append(" command=\"").Append(TruncateOneLine(command, 160)).Append('"')
          .AppendLine();

        foreach (var line in selected)
        {
            sb.Append(line.Stream == StreamTag.Stderr ? "[err] " : "[out] ")
              .AppendLine(line.Text);
        }

        return sb.ToString();
    }

    private static List<Line> SplitFrames(IReadOnlyList<OutputFrame> frames)
    {
        var lines = new List<Line>();
        foreach (var frame in frames)
        {
            var text = frame.Text.Replace("\r\n", "\n").Replace('\r', '\n');
            var parts = text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i == parts.Length - 1 && parts[i].Length == 0) continue;
                lines.Add(new Line(frame.Stream, parts[i]));
            }
        }
        return lines;
    }

    private static List<Line> CollapseRuns(List<Line> lines)
    {
        var result = new List<Line>(lines.Count);
        for (var i = 0; i < lines.Count;)
        {
            var cur = lines[i];
            var repeat = 1;
            while (i + repeat < lines.Count
                   && lines[i + repeat].Stream == cur.Stream
                   && lines[i + repeat].Text == cur.Text)
            {
                repeat++;
            }

            result.Add(cur);
            if (repeat > 1)
                result.Add(new Line(cur.Stream, $"... repeated {repeat - 1} more times: {TruncateOneLine(cur.Text, 120)}"));
            i += repeat;
        }
        return result;
    }

    private static List<Line> SelectLines(List<Line> lines, bool failure, int maxLines)
    {
        if (lines.Count <= maxLines) return lines;

        var selected = new List<Line>();
        if (failure)
        {
            selected.AddRange(lines.Where(IsImportant).Take(maxLines / 2));
            AddTail(selected, lines, maxLines - selected.Count);
        }
        else
        {
            var head = Math.Max(8, maxLines / 4);
            selected.AddRange(lines.Take(head));
            selected.Add(new Line(StreamTag.Stdout, $"... omitted {Math.Max(0, lines.Count - maxLines)} compacted lines ..."));
            AddTail(selected, lines, maxLines - selected.Count);
        }

        return selected.Count <= maxLines ? selected : selected.Take(maxLines).ToList();
    }

    private static void AddTail(List<Line> selected, List<Line> source, int budget)
    {
        if (budget <= 0) return;
        foreach (var line in source.TakeLast(budget))
        {
            if (!selected.Contains(line)) selected.Add(line);
        }
    }

    private static bool IsImportant(Line line)
    {
        if (line.Stream == StreamTag.Stderr) return true;
        return Regex.IsMatch(
            line.Text,
            @"(?i)\b(error|failed|failure|exception|timeout|denied|fatal|warning)\b|:\d+(:\d+)?\b|line \d+| at .+\(.+:\d+\)");
    }

    private static string TruncateOneLine(string value, int max)
    {
        var oneLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..Math.Max(0, max - 1)] + "...";
    }

    private readonly record struct Line(StreamTag Stream, string Text);
}
