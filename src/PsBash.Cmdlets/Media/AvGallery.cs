using System.Globalization;
using System.Management.Automation;

namespace PsBash.Cmdlets.Media;

/// <summary>
/// The decision logic behind <c>avtui</c>, the interactive media gallery: which key means what, and
/// what each action actually does to the focused row. Kept separate from the Strata render loop (and
/// free of any Strata reference) so the keymap and the produce-an-artifact actions are unit-testable
/// without a terminal — the same split <c>psgit</c> / <c>gtui</c> uses.
/// </summary>
internal static class AvGallery
{
    /// <summary>An action the gallery takes for a keypress.</summary>
    public enum AvTuiAction
    {
        None,
        Up,
        Down,
        ToggleExpand,
        Screenshot,
        Gif,
        Clip,
        Refresh,
        Quit,
    }

    /// <summary>Map a keystroke to a gallery action (pure — unit-tested; the loop wiring is not).</summary>
    public static AvTuiAction Decide(ConsoleKey key, char ch) => (key, ch) switch
    {
        (ConsoleKey.Q, _) or (ConsoleKey.Escape, _) => AvTuiAction.Quit,
        (ConsoleKey.DownArrow, _) or (_, 'j') => AvTuiAction.Down,
        (ConsoleKey.UpArrow, _) or (_, 'k') => AvTuiAction.Up,
        (ConsoleKey.Enter, _) or (ConsoleKey.Spacebar, _) => AvTuiAction.ToggleExpand,
        (_, 's') => AvTuiAction.Screenshot,
        (_, 'g') => AvTuiAction.Gif,
        (_, 'c') => AvTuiAction.Clip,
        (_, 'r') => AvTuiAction.Refresh,
        _ => AvTuiAction.None,
    };

    /// <summary>The outcome of a gallery action, as the footer line reports it.</summary>
    public readonly record struct ActionResult(bool Ok, string Message);

    /// <summary>
    /// Run one produce-an-artifact action against the focused media row. Defaults are chosen so a
    /// single keystroke yields something immediately useful: a still from the middle of the clip, a
    /// 5-second GIF / 10-second clip from the start.
    /// </summary>
    public static ActionResult Act(AvTuiAction action, PSObject? row, string? workingDir)
    {
        var path = row?.Properties["Path"]?.Value?.ToString();
        if (string.IsNullOrEmpty(path))
        {
            return new ActionResult(false, "no file selected");
        }

        var kind = row?.Properties["class"]?.Value?.ToString() ?? "unknown";
        if (kind is "audio" or "unknown")
        {
            return new ActionResult(false, $"{Path.GetFileName(path)} has no video track");
        }

        var duration = Duration(row);
        var plan = action switch
        {
            AvTuiAction.Screenshot => FfmpegPlan.Shot(
                path,
                duration > 0 ? duration / 2 : 0,
                FfmpegPlan.DefaultOutput(path, "shot", ".png", duration > 0 ? duration / 2 : 0)),
            AvTuiAction.Gif => FfmpegPlan.Gif(
                path, from: 0, duration: Window(duration, 5), fps: 12, width: 800,
                output: FfmpegPlan.DefaultOutput(path, "demo", ".gif")),
            AvTuiAction.Clip => FfmpegPlan.Clip(
                path, from: 0, duration: Window(duration, 10), width: 0, crf: 22, mute: false,
                output: FfmpegPlan.DefaultOutput(path, "demo", ".mp4")),
            _ => null,
        };

        if (plan is null)
        {
            return new ActionResult(false, "nothing to do");
        }

        try
        {
            var result = AvRunner.RunPlan(plan, workingDir);
            if (result.TimedOut)
            {
                return new ActionResult(false, $"{plan.Kind}: timed out");
            }

            if (result.ExitCode != 0)
            {
                var line = (result.Stderr ?? string.Empty).Replace("\r\n", "\n").Split('\n')
                    .FirstOrDefault(l => l.Length > 0) ?? $"exit {result.ExitCode}";
                return new ActionResult(false, $"{plan.Kind}: {line}");
            }

            return new ActionResult(true, $"wrote {Path.GetFileName(plan.OutputPath)}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ActionResult(false, "ffmpeg is not on PATH");
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            return new ActionResult(false, (ex.InnerException ?? ex).Message);
        }
    }

    /// <summary>
    /// Probe every media file under <paramref name="target"/> into <c>PsBash.MediaInfo</c> rows — what
    /// the gallery lists, and what a rescan (<c>r</c>) re-runs. A file ffprobe cannot read is skipped
    /// rather than shown as an empty row.
    /// </summary>
    public static List<PSObject> FetchMedia(string target, string? workingDir)
    {
        var rows = new List<PSObject>();
        foreach (var path in AvRunner.ScanMedia(target))
        {
            var summary = AvRunner.Probe(path, workingDir);
            if (summary is not null)
            {
                rows.Add(InvokeBashFfmpegCommand.ToMediaInfo(summary));
            }
        }

        return rows;
    }

    /// <summary>A clip window that never exceeds what the source actually has.</summary>
    internal static double Window(double duration, double requested)
        => duration > 0 ? Math.Min(duration, requested) : requested;

    private static double Duration(PSObject? row)
    {
        var raw = row?.Properties["Duration"]?.Value;
        return raw switch
        {
            double d => d,
            int i => i,
            long l => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => 0,
        };
    }
}
