using System.Globalization;
using System.Runtime.InteropServices;

namespace PsBash.Cmdlets.Media;

/// <summary>
/// One planned child-process invocation: the tool, its exact argv, what it will produce, and how long
/// it may take. <c>psav</c> builds a plan first and runs it second, so <c>--dry-run</c> prints the
/// identical command that would have executed and every argv is assertable in a unit test without
/// ffmpeg installed.
/// </summary>
internal sealed record AvPlan(
    string Tool,
    IReadOnlyList<string> Arguments,
    string Kind,
    string? OutputPath = null,
    double? At = null,
    TimeSpan? Timeout = null)
{
    private static readonly char[] NeedsQuote = { ' ', '"', '\'', ';', '|', '&' };

    /// <summary>A copy-pasteable rendering of the plan (shell-quoted where a token needs it).</summary>
    public string CommandLine
        => Tool + " " + string.Join(" ", Arguments.Select(Quote));

    private static string Quote(string arg)
        => arg.Length > 0 && arg.IndexOfAny(NeedsQuote) < 0
            ? arg
            : "\"" + arg.Replace("\"", "\\\"") + "\"";
}

/// <summary>
/// The argv builders behind every <c>psav</c> subcommand. Pure functions: options in, an
/// <see cref="AvPlan"/> out. No process is spawned and no file is touched here — that lives in
/// <see cref="InvokeBashFfmpegCommand"/> — which is what makes the whole ffmpeg surface (filter
/// graphs, seek placement, palette generation, per-OS screen grabbers) testable on a machine with no
/// ffmpeg at all.
/// </summary>
internal static class FfmpegPlan
{
    /// <summary>Quiet, non-interactive, overwrite — the prefix every produce-a-file plan shares.</summary>
    private static readonly string[] Common = { "-hide_banner", "-loglevel", "error", "-y", "-nostdin" };

    /// <summary>ffprobe: the whole container + stream description as JSON on stdout.</summary>
    public static AvPlan Probe(string input) => new(
        "ffprobe",
        new[] { "-hide_banner", "-v", "error", "-print_format", "json", "-show_format", "-show_streams", input },
        Kind: "probe");

    /// <summary>
    /// A single still frame. The seek is placed <b>before</b> <c>-i</c> (input seeking): ffmpeg jumps
    /// to the nearest keyframe instead of decoding the whole file up to that point, which is the
    /// difference between a screenshot in 0.1s and one in 20s on a long recording.
    /// </summary>
    public static AvPlan Shot(string input, double at, string output, int width = 0)
    {
        var args = new List<string>(Common)
        {
            "-ss", AvTime.Format(at),
            "-i", input,
        };
        AddVideoFilter(args, ScaleFilter(width, evenHeight: false));
        args.Add("-frames:v");
        args.Add("1");
        args.Add(output);
        return new AvPlan("ffmpeg", args, "screenshot", output, at);
    }

    /// <summary>
    /// Stills sampled at <paramref name="fps"/> frames per second. <paramref name="pattern"/> must
    /// contain a printf slot (<c>frame-%04d.png</c>) — ffmpeg fills it.
    /// </summary>
    public static AvPlan Frames(string input, double fps, string pattern, int width = 0, double? from = null, double? duration = null)
    {
        var args = new List<string>(Common);
        AddSeek(args, from, duration);
        args.Add("-i");
        args.Add(input);
        AddVideoFilter(args, Chain("fps=" + Num(fps), ScaleFilter(width, evenHeight: false)));
        args.Add(pattern);
        return new AvPlan("ffmpeg", args, "frames", pattern, from);
    }

    /// <summary>
    /// A contact sheet: <paramref name="columns"/>×<paramref name="rows"/> evenly-spaced stills tiled
    /// into one image. The sampling rate is derived from the clip's duration so the tiles span the
    /// whole thing rather than the first few seconds.
    /// </summary>
    public static AvPlan Sheet(string input, int columns, int rows, double durationSeconds, string output, int width = 0)
    {
        var tiles = Math.Max(1, columns * rows);
        var span = durationSeconds > 0.1 ? durationSeconds : tiles;
        var fps = tiles / span;
        var args = new List<string>(Common) { "-i", input };
        AddVideoFilter(args, Chain(
            "fps=" + Num(fps),
            ScaleFilter(width == 0 ? 320 : width, evenHeight: false),
            $"tile={columns}x{rows}"));
        args.Add("-frames:v");
        args.Add("1");
        args.Add(output);
        return new AvPlan("ffmpeg", args, "sheet", output);
    }

    /// <summary>
    /// An animated GIF via the <b>palette</b> route: <c>palettegen</c> derives an optimal 256-colour
    /// table for this clip and <c>paletteuse</c> maps frames onto it, in one filter graph via
    /// <c>split</c>. Straight GIF encoding uses a fixed web palette and turns UI gradients into mud —
    /// this is the difference between a demo GIF that looks like your terminal and one that does not.
    /// </summary>
    public static AvPlan Gif(string input, double? from, double? duration, double fps, int width, string output, bool loop = true)
    {
        var args = new List<string>(Common);
        AddSeek(args, from, duration);
        args.Add("-i");
        args.Add(input);
        var graph = Chain("fps=" + Num(fps), ScaleFilter(width, evenHeight: false, flags: "lanczos")) +
                    ",split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5";
        args.Add("-filter_complex");
        args.Add(graph);
        args.Add("-loop");
        args.Add(loop ? "0" : "-1");
        args.Add(output);
        return new AvPlan("ffmpeg", args, "gif", output, from);
    }

    /// <summary>
    /// A trimmed / scaled H.264 demo clip. <c>yuv420p</c> and <c>+faststart</c> are what make the file
    /// play in a browser, in Slack, and in a GitHub PR body rather than only in VLC.
    /// </summary>
    public static AvPlan Clip(string input, double? from, double? duration, int width, int crf, bool mute, string output)
    {
        var args = new List<string>(Common);
        AddSeek(args, from, duration);
        args.Add("-i");
        args.Add(input);
        AddVideoFilter(args, ScaleFilter(width, evenHeight: true));
        args.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", "veryfast",
            "-crf", crf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
        });

        if (mute)
        {
            args.Add("-an");
        }
        else
        {
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });
        }

        args.AddRange(new[] { "-movflags", "+faststart", output });
        return new AvPlan("ffmpeg", args, "clip", output, from);
    }

    /// <summary>Re-encode to whatever container/codec the output extension implies, optionally rescaled.</summary>
    public static AvPlan Convert(string input, string output, int width = 0)
    {
        var args = new List<string>(Common) { "-i", input };
        AddVideoFilter(args, ScaleFilter(width, evenHeight: true));
        args.Add(output);
        return new AvPlan("ffmpeg", args, "convert", output);
    }

    /// <summary>
    /// Record the screen for <paramref name="seconds"/>. The capture device is per-OS
    /// (<see cref="ScreenCaptureInput"/>); the encode settings are the same browser-safe H.264 the
    /// <see cref="Clip"/> path uses.
    /// </summary>
    public static AvPlan Record(
        OSPlatform platform,
        double seconds,
        double fps,
        string output,
        string? window = null,
        string? display = null,
        int crf = 20)
    {
        var args = new List<string>(Common);
        args.AddRange(ScreenCaptureInput(platform, fps, window, display));
        args.Add("-t");
        args.Add(AvTime.Format(seconds));
        args.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", "veryfast",
            "-crf", crf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p", "-movflags", "+faststart", output,
        });

        // A screen grab runs for its full wall-clock duration; give the spawn budget real headroom
        // on top of it or the kill-tree timeout truncates the recording.
        return new AvPlan("ffmpeg", args, "recording", output, Timeout: TimeSpan.FromSeconds(seconds + 60));
    }

    /// <summary>
    /// The per-OS screen-capture input: <c>gdigrab</c> on Windows, <c>x11grab</c> on Linux,
    /// <c>avfoundation</c> on macOS. Takes the platform explicitly so both branches are testable from
    /// either host.
    /// </summary>
    public static IReadOnlyList<string> ScreenCaptureInput(OSPlatform platform, double fps, string? window = null, string? display = null)
    {
        var rate = Num(fps);
        if (platform == OSPlatform.Windows)
        {
            return new[] { "-f", "gdigrab", "-framerate", rate, "-i", string.IsNullOrEmpty(window) ? "desktop" : "title=" + window };
        }

        if (platform == OSPlatform.OSX)
        {
            // avfoundation addresses devices by index: "<video>:<audio>"; none = no audio track.
            return new[] { "-f", "avfoundation", "-framerate", rate, "-i", (string.IsNullOrEmpty(display) ? "1" : display) + ":none" };
        }

        // Linux / anything else: X11. DISPLAY is the natural default.
        var screen = display;
        if (string.IsNullOrEmpty(screen))
        {
            // DISPLAY is a bash variable the script may have exported, so it comes from the
            // variable store, not the process environment (BashVariableStoreGuardTests).
            screen = BashVariableStore.Get("DISPLAY");
        }

        if (string.IsNullOrEmpty(screen))
        {
            screen = ":0.0";
        }

        return new[] { "-f", "x11grab", "-framerate", rate, "-i", screen };
    }

    /// <summary>
    /// Parse a tile spec (<c>4x3</c>, <c>3X2</c>) into columns × rows. Falls back to 4×3 — the shape
    /// that reads well at a normal image width — for anything unparseable.
    /// </summary>
    public static (int Columns, int Rows) ParseTiles(string? spec)
    {
        if (!string.IsNullOrWhiteSpace(spec))
        {
            var parts = spec.Split(new[] { 'x', 'X' }, 2);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var c) && c > 0 &&
                int.TryParse(parts[1], out var r) && r > 0)
            {
                return (c, r);
            }
        }

        return (4, 3);
    }

    /// <summary>
    /// The scale filter for a target <paramref name="width"/>, or null when no rescale was asked for.
    /// Height is <c>-2</c> (not <c>-1</c>) whenever the output will be H.264: libx264 rejects odd
    /// dimensions, and <c>-2</c> rounds the auto-computed height to an even number.
    /// </summary>
    public static string? ScaleFilter(int width, bool evenHeight, string? flags = null)
    {
        if (width <= 0)
        {
            return null;
        }

        var filter = $"scale={width}:{(evenHeight ? "-2" : "-1")}";
        return flags is null ? filter : filter + ":flags=" + flags;
    }

    /// <summary>
    /// The default output path for a produced artifact, derived from the input's name so a session
    /// never silently overwrites the source: <c>demo.mp4</c> → <c>demo-screenshot-00-00-05.000.png</c>.
    /// </summary>
    /// <param name="index">
    /// 1-based position within a multi-artifact run. Supplied whenever one command produces several
    /// files, because two sample points that round to the same timecode would otherwise generate the
    /// same filename and the run would silently overwrite its own output.
    /// </param>
    public static string DefaultOutput(string input, string kind, string extension, double? at = null, string? directory = null, int? index = null)
    {
        var stem = Path.GetFileNameWithoutExtension(input);
        if (string.IsNullOrEmpty(stem))
        {
            stem = "capture";
        }

        var ordinal = index.HasValue ? "-" + index.Value.ToString("00", CultureInfo.InvariantCulture) : string.Empty;
        var suffix = at.HasValue ? $"-{kind}{ordinal}-{AvTime.FileStamp(at.Value)}" : $"-{kind}{ordinal}";
        var dir = directory ?? Path.GetDirectoryName(input);
        var name = stem + suffix + extension;
        return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
    }

    /// <summary>Evenly spaced sample points across a clip, biased inside the ends (never frame 0 or EOF).</summary>
    public static IReadOnlyList<double> SamplePoints(double duration, int count)
    {
        if (count <= 0)
        {
            return Array.Empty<double>();
        }

        if (duration <= 0)
        {
            return Enumerable.Repeat(0d, count).ToArray();
        }

        if (count == 1)
        {
            return new[] { duration / 2 };
        }

        var step = duration / (count + 1);
        return Enumerable.Range(1, count).Select(i => step * i).ToArray();
    }

    private static void AddSeek(List<string> args, double? from, double? duration)
    {
        if (from is > 0)
        {
            args.Add("-ss");
            args.Add(AvTime.Format(from.Value));
        }

        if (duration is > 0)
        {
            args.Add("-t");
            args.Add(AvTime.Format(duration.Value));
        }
    }

    private static void AddVideoFilter(List<string> args, string? filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return;
        }

        args.Add("-vf");
        args.Add(filter);
    }

    private static string Chain(params string?[] parts)
        => string.Join(",", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string Num(double value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);
}
