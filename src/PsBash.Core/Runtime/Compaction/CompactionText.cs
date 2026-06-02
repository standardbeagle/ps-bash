using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime.Compaction;

/// <summary>One output line tagged with the stream it came from.</summary>
public readonly record struct OutputFrame(StreamTag Stream, string Text);

/// <summary>
/// Pure text primitives shared by the compaction layer: frame→line splitting,
/// consecutive-run collapsing, one-line truncation, the digest header, and ANSI
/// stripping. Both the generic <see cref="OutputCompactor"/> fallback and the
/// command-aware <see cref="FilterStage"/> pipeline build on these so their
/// line/dedup/header handling can never drift apart.
/// </summary>
internal static class CompactionText
{
    internal readonly record struct Line(StreamTag Stream, string Text);

    // CSI / SGR escape sequences (colour, cursor moves). Bounded, linear — no ReDoS risk.
    private static readonly Regex AnsiPattern =
        new(@"\x1b\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    /// <summary>
    /// Split frames into lines on <c>\n</c> (after CRLF/CR normalization). A single
    /// trailing empty line per frame is dropped — it is the newline terminator, not a
    /// record. Matches bash semantics where <c>\n</c> is the record separator.
    /// </summary>
    internal static List<Line> SplitFrames(IReadOnlyList<OutputFrame> frames)
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

    /// <summary>
    /// Collapse a run of consecutive identical lines (same stream + text) to the first
    /// line plus a <c>... repeated N more times: …</c> marker.
    /// </summary>
    internal static List<Line> CollapseRuns(List<Line> lines)
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

    /// <summary>Append the canonical compact-output digest header line.</summary>
    internal static void AppendHeader(
        StringBuilder sb, string command, int exitCode, bool timedOut, int stdoutCount, int stderrCount)
    {
        sb.Append("ps-bash compact-output: exit=").Append(exitCode);
        if (timedOut) sb.Append(" timeout=true");
        sb.Append(" stdout_lines=").Append(stdoutCount)
          .Append(" stderr_lines=").Append(stderrCount)
          .Append(" command=\"").Append(TruncateOneLine(command, 160)).Append('"')
          .AppendLine();
    }

    internal static string TruncateOneLine(string value, int max)
    {
        var oneLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..Math.Max(0, max - 1)] + "...";
    }

    internal static string StripAnsi(string value) => AnsiPattern.Replace(value, string.Empty);
}
