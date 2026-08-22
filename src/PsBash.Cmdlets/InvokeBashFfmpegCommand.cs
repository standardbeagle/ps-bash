using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Runtime.InteropServices;
using PsBash.Cmdlets.Media;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>psav</c> — ffmpeg as a task-shaped wrapper that emits structured objects. Instead of
/// remembering a filter graph, you ask for the artifact you actually want: a screenshot
/// (<c>shot</c>), a contact sheet (<c>sheet</c>), a demo GIF (<c>gif</c>), a trimmed browser-safe
/// clip (<c>clip</c>), a screen recording (<c>record</c>), or the file's own vitals
/// (<c>probe</c>). Every subcommand returns typed <c>PsBash.Media*</c> objects carrying the exact
/// ffmpeg command that produced them, so results filter, sort, and pipe — and pipe to
/// <c>Show-Styled</c> / <c>Format-Styled av</c> for the colour-by-state view.
/// </summary>
/// <remarks>
/// <para><b>Native ffmpeg is left alone.</b> Like <c>psgit</c>, this is deliberately NOT aliased over
/// <c>ffmpeg</c>: the real binary stays available, fully interactive, for everything this wrapper
/// does not model. <c>psav raw -- &lt;args&gt;</c> reaches it with the arguments untouched.</para>
/// <para><b>Long flags only</b> (<c>--at</c>, <c>--out</c>, <c>--fps</c>): a single-dash token is a
/// PowerShell parameter name and bare <c>-i</c> / <c>-t</c> / <c>-o</c> would be swallowed or crash
/// the binder. See <see cref="AvOptions"/>.</para>
/// <para>Shells out through <see cref="BashRuntime.RunChildProcess(ProcessStartInfo, TimeSpan?)"/>
/// (bounded wait + kill-tree), so a wedged encode can never hang the host runspace.</para>
/// </remarks>
[Cmdlet(VerbsLifecycle.Invoke, "BashFfmpeg")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashFfmpegCommand : PSCmdlet
{
    /// <summary>The subcommand and its long-form options; everything after <c>--</c> is passthrough.</summary>
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private const string Usage = """
        usage: psav <subcommand> [FILE] [--options]

          probe  FILE...                      media vitals as typed objects
          shot   FILE [--at T] [--count N]    still frame(s) -> PNG
          frames FILE [--fps N]               every Nth second as a numbered PNG
          sheet  FILE [--tiles 4x3]           contact sheet of evenly spaced stills
          gif    FILE [--from T] [--dur S]    palette-optimised demo GIF
          clip   FILE [--from T] [--dur S]    trimmed, browser-safe H.264 MP4
          record [--seconds N]                screen recording -> MP4
          to     FILE --out OUT               convert / rescale by output extension
          raw    -- <ffmpeg args>             passthrough to native ffmpeg

        common options:
          --out PATH      --width PX      --fps N        --at TIME
          --from TIME     --to TIME       --dur SECONDS  --count N
          --tiles CxR     --crf N         --mute         --dry-run
          --seconds N     --window TITLE  --display SPEC --quiet

        TIME accepts 7, 7.5, 1:02, 00:01:02.500, 90s, 1m30s, 250ms.
        """;

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "psav", args))
        {
            return;
        }

        var opts = AvOptions.Parse(args);

        if (args.Length == 0 || opts.Flag("help") || opts.Subcommand is "help" or "")
        {
            foreach (var line in Usage.Split('\n'))
            {
                WriteObject(BashRuntime.NewBashObject(line.TrimEnd('\r')));
            }

            return;
        }

        if (opts.UnknownOption is not null)
        {
            Fail($"psav: option requires a value: {opts.UnknownOption}", 2);
            return;
        }

        try
        {
            switch (opts.Subcommand)
            {
                case "probe" or "info": Probe(opts); break;
                case "shot" or "screenshot" or "still": Shot(opts); break;
                case "frames": Frames(opts); break;
                case "sheet" or "contact": Sheet(opts); break;
                case "gif": Gif(opts); break;
                case "clip" or "demo" or "trim": Clip(opts); break;
                case "record" or "capture": Record(opts); break;
                case "to" or "convert": Convert(opts); break;
                case "raw" or "ffmpeg": Raw(opts); break;
                default:
                    Fail($"psav: unknown subcommand '{opts.Subcommand}'. Run `psav help` for the list.", 2);
                    break;
            }
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            Fail("psav: " + (ex.InnerException ?? ex).Message, 1);
        }
    }

    // ── subcommands ────────────────────────────────────────────────────────────────────────────

    private void Probe(AvOptions opts)
    {
        var inputs = ResolveInputs(opts);
        if (inputs.Count == 0)
        {
            Fail("psav probe: no input file", 2);
            return;
        }

        foreach (var input in inputs)
        {
            if (opts.Flag("dry-run"))
            {
                WriteObject(DryRun(FfmpegPlan.Probe(input)));
                continue;
            }

            var summary = ProbeFile(input, out var error);
            if (summary is null)
            {
                Fail($"psav probe: {Path.GetFileName(input)}: {error}", 1);
                continue;
            }

            WriteObject(ToMediaInfo(summary));
        }
    }

    private void Shot(AvOptions opts)
    {
        var input = SingleInput(opts, "shot");
        if (input is null)
        {
            return;
        }

        var width = opts.Int("width", 0);
        var count = Math.Max(1, opts.Int("count", 1));
        var explicitOut = opts.Value("out");

        // --at wins; otherwise sample `count` points evenly across the clip (needs the duration,
        // so this is the one place a produce-a-file path probes first).
        IReadOnlyList<double> points;
        var at = opts.Time("at");
        if (at.HasValue && count == 1)
        {
            points = new[] { at.Value };
        }
        else
        {
            // Spreading N stills across a clip needs its length. Without it every sample point
            // collapses to 0 — N identical grabs — so say so rather than quietly produce one frame
            // N times.
            var duration = DurationOf(input);
            if (duration <= 0)
            {
                Fail($"psav shot: --count {count} needs the clip duration, and ffprobe could not read {Path.GetFileName(input)}. Use --at TIME for a single frame.", 1);
                return;
            }

            points = FfmpegPlan.SamplePoints(duration, count);
            if (at.HasValue)
            {
                points = points.Select(p => at.Value + p).ToArray();
            }
        }

        var multiple = points.Count > 1;
        var index = 0;
        foreach (var point in points)
        {
            index++;
            // With --count > 1 an explicit --out is a directory hint, not a filename: N shots cannot
            // share one path, and silently overwriting N-1 of them would be the worst outcome.
            var output = !multiple && explicitOut is not null
                ? Absolute(explicitOut)!
                : FfmpegPlan.DefaultOutput(
                    input, "shot", ".png", point,
                    OutputDirectory(explicitOut, input, multiple),
                    multiple ? index : null);

            Emit(FfmpegPlan.Shot(input, point, output, width), opts);
        }
    }

    private void Frames(AvOptions opts)
    {
        var input = SingleInput(opts, "frames");
        if (input is null)
        {
            return;
        }

        var dir = OutputDirectory(opts.Value("out"), input, isDirectory: true) ?? ".";
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(input);
        var pattern = Path.Combine(dir, stem + "-%04d.png");

        var plan = FfmpegPlan.Frames(
            input,
            opts.Double("fps", 1),
            pattern,
            opts.Int("width", 0),
            opts.Time("from"),
            DurationWindow(opts));

        // The output is a numbered set, so report what actually landed rather than one path.
        Emit(plan, opts, () => Directory
            .EnumerateFiles(dir, stem + "-*.png")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList());
    }

    private void Sheet(AvOptions opts)
    {
        var input = SingleInput(opts, "sheet");
        if (input is null)
        {
            return;
        }

        var (columns, rows) = FfmpegPlan.ParseTiles(opts.Value("tiles"));
        var output = Absolute(opts.Value("out")) ?? FfmpegPlan.DefaultOutput(input, "sheet", ".png");
        Emit(FfmpegPlan.Sheet(input, columns, rows, DurationOf(input), output, opts.Int("width", 0)), opts);
    }

    private void Gif(AvOptions opts)
    {
        var input = SingleInput(opts, "gif");
        if (input is null)
        {
            return;
        }

        var output = Absolute(opts.Value("out")) ?? FfmpegPlan.DefaultOutput(input, "demo", ".gif");
        Emit(
            FfmpegPlan.Gif(
                input,
                opts.Time("from"),
                DurationWindow(opts),
                opts.Double("fps", 12),
                opts.Int("width", 800),
                output,
                loop: !opts.Flag("no-loop")),
            opts);
    }

    private void Clip(AvOptions opts)
    {
        var input = SingleInput(opts, "clip");
        if (input is null)
        {
            return;
        }

        var output = Absolute(opts.Value("out")) ?? FfmpegPlan.DefaultOutput(input, "demo", ".mp4");
        Emit(
            FfmpegPlan.Clip(
                input,
                opts.Time("from"),
                DurationWindow(opts),
                opts.Int("width", 0),
                opts.Int("crf", 22),
                mute: opts.Flag("mute"),
                output),
            opts);
    }

    private void Record(AvOptions opts)
    {
        var seconds = opts.Double("seconds", 10);
        if (seconds <= 0)
        {
            Fail("psav record: --seconds must be greater than 0", 2);
            return;
        }

        var output = Absolute(opts.Value("out"))
            ?? Absolute("screen-recording-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".mp4")!;

        var plan = FfmpegPlan.Record(
            CurrentPlatform(),
            seconds,
            opts.Double("fps", 30),
            output,
            opts.Value("window"),
            opts.Value("display"),
            opts.Int("crf", 20));

        if (!opts.Flag("quiet") && !opts.Flag("dry-run"))
        {
            WriteWarning($"Recording the screen for {AvTime.Humanize(seconds)} -> {output}");
        }

        Emit(plan, opts);
    }

    private void Convert(AvOptions opts)
    {
        var input = SingleInput(opts, "to");
        if (input is null)
        {
            return;
        }

        var output = Absolute(opts.Value("out"));
        if (output is null)
        {
            Fail("psav to: --out PATH is required (the extension picks the format)", 2);
            return;
        }

        Emit(FfmpegPlan.Convert(input, output, opts.Int("width", 0)), opts);
    }

    private void Raw(AvOptions opts)
    {
        var passthrough = opts.Passthrough.Count > 0 ? opts.Passthrough : opts.Operands;
        if (passthrough.Count == 0)
        {
            Fail("psav raw: no arguments. Use `psav raw -- -i in.mp4 -y out.mp4`.", 2);
            return;
        }

        var plan = new AvPlan("ffmpeg", passthrough.ToList(), "raw");
        if (opts.Flag("dry-run"))
        {
            WriteObject(DryRun(plan));
            return;
        }

        var result = Run(plan, out var missing);
        if (missing)
        {
            return;
        }

        foreach (var line in SplitLines(result.Stdout).Concat(SplitLines(result.Stderr)))
        {
            WriteObject(BashRuntime.NewBashObject(line));
        }

        FileSystemHelpers.SetLastExitCode(this, result.ExitCode);
    }

    // ── execution ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Run a produce-a-file plan (or print it under <c>--dry-run</c>) and emit its artifact row.</summary>
    private void Emit(AvPlan plan, AvOptions opts, Func<IReadOnlyList<string>>? outputs = null)
    {
        if (opts.Flag("dry-run"))
        {
            WriteObject(DryRun(plan));
            return;
        }

        var started = Stopwatch.StartNew();
        var result = Run(plan, out var missing);
        started.Stop();
        if (missing)
        {
            return;
        }

        if (result.ExitCode != 0 || result.TimedOut)
        {
            var reason = result.TimedOut
                ? $"timed out after {plan.Timeout?.TotalSeconds ?? 0:0}s"
                : FirstLine(result.Stderr) ?? $"ffmpeg exited {result.ExitCode}";
            WriteObject(ToArtifact(plan, plan.OutputPath, started.Elapsed, ok: false, detail: reason));
            FileSystemHelpers.WriteBashError(this, $"psav {plan.Kind}: {reason}");
            FileSystemHelpers.SetLastExitCode(this, result.TimedOut ? 124 : result.ExitCode);
            return;
        }

        var produced = outputs?.Invoke() ?? new[] { plan.OutputPath! };
        if (produced.Count == 0)
        {
            WriteObject(ToArtifact(plan, plan.OutputPath, started.Elapsed, ok: false, detail: "ffmpeg wrote no output"));
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        foreach (var path in produced)
        {
            WriteObject(ToArtifact(plan, path, started.Elapsed, ok: true));
        }
    }

    /// <summary>
    /// Spawn a plan. <paramref name="toolMissing"/> is set (and a bash-shaped error written) when the
    /// binary is not on PATH — the one failure worth an install hint rather than a raw exception.
    /// </summary>
    private BashRuntime.ChildProcessResult Run(AvPlan plan, out bool toolMissing)
    {
        toolMissing = false;
        try
        {
            return AvRunner.RunPlan(plan, CurrentDirectory());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            toolMissing = true;
            Fail(
                $"psav: {plan.Tool} is not on PATH. Install it: " +
                "winget install Gyan.FFmpeg  |  brew install ffmpeg  |  apt install ffmpeg",
                127);
            return default;
        }
    }

    /// <summary>Probe one file into a <see cref="MediaSummary"/>, or null with the reason.</summary>
    internal MediaSummary? ProbeFile(string input, out string error)
    {
        error = string.Empty;
        var result = Run(FfmpegPlan.Probe(input), out var missing);
        if (missing)
        {
            error = "ffprobe not found";
            return null;
        }

        if (result.ExitCode != 0)
        {
            error = FirstLine(result.Stderr) ?? $"ffprobe exited {result.ExitCode}";
            return null;
        }

        var summary = FfprobeReport.Parse(input, result.Stdout);
        if (summary is null)
        {
            error = "not a media file ffprobe understands";
        }

        return summary;
    }

    /// <summary>The clip duration in seconds, or 0 when it cannot be probed (callers degrade, never throw).</summary>
    private double DurationOf(string input)
        => ProbeFile(input, out _)?.Duration ?? 0;

    // ── option / path plumbing ─────────────────────────────────────────────────────────────────

    /// <summary><c>--dur S</c>, else <c>--to T</c> minus <c>--from T</c>, else null (to the end).</summary>
    private static double? DurationWindow(AvOptions opts)
    {
        var dur = opts.Time("dur") ?? opts.Time("duration");
        if (dur.HasValue)
        {
            return dur;
        }

        var to = opts.Time("to");
        if (!to.HasValue)
        {
            return null;
        }

        var from = opts.Time("from") ?? 0;
        var window = to.Value - from;
        return window > 0 ? window : null;
    }

    private List<string> ResolveInputs(AvOptions opts)
        => opts.Operands.Select(o => Absolute(o)!).Where(p => p is not null).ToList();

    private string? SingleInput(AvOptions opts, string subcommand)
    {
        var inputs = ResolveInputs(opts);
        if (inputs.Count == 0)
        {
            Fail($"psav {subcommand}: no input file", 2);
            return null;
        }

        if (!File.Exists(inputs[0]))
        {
            Fail($"psav {subcommand}: {opts.Operands[0]}: No such file or directory", 1);
            return null;
        }

        return inputs[0];
    }

    /// <summary>The session's current filesystem location, or null in host states that have none.</summary>
    private string? CurrentDirectory()
    {
        try { return SessionState.Path.CurrentFileSystemLocation.Path; }
        catch { return null; }
    }

    /// <summary>Resolve a user-supplied path against the session's current directory (not the process cwd).</summary>
    private string? Absolute(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(SessionState.Path.CurrentFileSystemLocation.Path, path));
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>Where a multi-artifact subcommand writes: an explicit <c>--out</c> dir, else beside the input.</summary>
    private string? OutputDirectory(string? explicitOut, string input, bool isDirectory)
    {
        if (!isDirectory || string.IsNullOrEmpty(explicitOut))
        {
            return Path.GetDirectoryName(Absolute(input));
        }

        return Absolute(explicitOut);
    }

    // ── object projection ──────────────────────────────────────────────────────────────────────

    /// <summary>A <c>PsBash.MediaInfo</c> row: the probe result as a typed, styleable object.</summary>
    internal static PSObject ToMediaInfo(MediaSummary m)
    {
        var o = new PSObject();
        o.TypeNames.Insert(0, "PsBash.MediaInfo");
        o.Properties.Add(new PSNoteProperty("Path", m.Path));
        o.Properties.Add(new PSNoteProperty("Name", Path.GetFileName(m.Path)));
        o.Properties.Add(new PSNoteProperty("Format", m.Format));
        o.Properties.Add(new PSNoteProperty("Duration", Math.Round(m.Duration, 3)));
        o.Properties.Add(new PSNoteProperty("Length", AvTime.Humanize(m.Duration)));
        o.Properties.Add(new PSNoteProperty("Size", m.Size));
        o.Properties.Add(new PSNoteProperty("SizeText", AvFormat.HumanSize(m.Size)));
        o.Properties.Add(new PSNoteProperty("Bitrate", m.Bitrate));
        o.Properties.Add(new PSNoteProperty("Video", m.VideoCodec));
        o.Properties.Add(new PSNoteProperty("Width", m.Width));
        o.Properties.Add(new PSNoteProperty("Height", m.Height));
        o.Properties.Add(new PSNoteProperty("Fps", m.Fps));
        o.Properties.Add(new PSNoteProperty("Audio", m.AudioCodec));
        o.Properties.Add(new PSNoteProperty("Channels", m.Channels));
        o.Properties.Add(new PSNoteProperty("SampleRate", m.SampleRate));
        o.Properties.Add(new PSNoteProperty("class", m.Class));
        o.Properties.Add(new PSNoteProperty("BashText", m.Text));
        SetColumns(o, "Name", "Length", "Video", "Width", "Height", "Fps", "SizeText");
        return o;
    }

    /// <summary>A <c>PsBash.MediaArtifact</c> row: a file this run produced (or failed to).</summary>
    private static PSObject ToArtifact(AvPlan plan, string? path, TimeSpan elapsed, bool ok, string? detail = null)
    {
        var size = 0L;
        if (ok && path is not null)
        {
            try { size = new FileInfo(path).Length; }
            catch { /* the file vanished under us — report 0 rather than throw */ }
        }

        var o = new PSObject();
        o.TypeNames.Insert(0, "PsBash.MediaArtifact");
        o.Properties.Add(new PSNoteProperty("Path", path));
        o.Properties.Add(new PSNoteProperty("Name", path is null ? plan.Kind : Path.GetFileName(path)));
        o.Properties.Add(new PSNoteProperty("Kind", plan.Kind));
        o.Properties.Add(new PSNoteProperty("At", plan.At.HasValue ? AvTime.Format(plan.At.Value) : null));
        o.Properties.Add(new PSNoteProperty("Size", size));
        o.Properties.Add(new PSNoteProperty("SizeText", AvFormat.HumanSize(size)));
        o.Properties.Add(new PSNoteProperty("Elapsed", Math.Round(elapsed.TotalSeconds, 2)));
        o.Properties.Add(new PSNoteProperty("Status", ok ? "ok" : "failed"));
        o.Properties.Add(new PSNoteProperty("Detail", detail));
        o.Properties.Add(new PSNoteProperty("Command", plan.CommandLine));
        o.Properties.Add(new PSNoteProperty("class", ok ? "ok" : "failed"));
        o.Properties.Add(new PSNoteProperty("BashText", ok
            ? $"{plan.Kind}: {path} ({AvFormat.HumanSize(size)}, {elapsed.TotalSeconds:0.0}s)"
            : $"{plan.Kind}: FAILED — {detail}"));
        SetColumns(o, "Name", "Kind", "At", "SizeText", "Elapsed", "Status");
        return o;
    }

    /// <summary>The <c>--dry-run</c> row: the plan, unexecuted, carrying the command it would run.</summary>
    private static PSObject DryRun(AvPlan plan)
    {
        var o = new PSObject();
        o.TypeNames.Insert(0, "PsBash.MediaArtifact");
        o.Properties.Add(new PSNoteProperty("Path", plan.OutputPath));
        o.Properties.Add(new PSNoteProperty("Name", plan.OutputPath is null ? plan.Kind : Path.GetFileName(plan.OutputPath)));
        o.Properties.Add(new PSNoteProperty("Kind", plan.Kind));
        o.Properties.Add(new PSNoteProperty("At", plan.At.HasValue ? AvTime.Format(plan.At.Value) : null));
        o.Properties.Add(new PSNoteProperty("Size", 0L));
        o.Properties.Add(new PSNoteProperty("SizeText", "-"));
        o.Properties.Add(new PSNoteProperty("Elapsed", 0d));
        o.Properties.Add(new PSNoteProperty("Status", "planned"));
        o.Properties.Add(new PSNoteProperty("Detail", null));
        o.Properties.Add(new PSNoteProperty("Command", plan.CommandLine));
        o.Properties.Add(new PSNoteProperty("class", "planned"));
        o.Properties.Add(new PSNoteProperty("BashText", plan.CommandLine));
        SetColumns(o, "Name", "Kind", "Status", "Command");
        return o;
    }

    /// <summary>Declare the default display columns (honoured by raw output and Format-Styled).</summary>
    private static void SetColumns(PSObject o, params string[] columns)
    {
        o.Members.Add(new PSMemberSet("PSStandardMembers", new List<PSMemberInfo>
        {
            new PSPropertySet("DefaultDisplayPropertySet", columns),
        }));
    }

    // ── small helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>The running platform as an <see cref="OSPlatform"/> (the screen-grabber selector).</summary>
    internal static OSPlatform CurrentPlatform()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
            : OSPlatform.Linux;

    private void Fail(string message, int exitCode)
    {
        FileSystemHelpers.WriteBashError(this, message);
        FileSystemHelpers.SetLastExitCode(this, exitCode);
    }

    private static string? FirstLine(string? text)
        => SplitLines(text).FirstOrDefault();

    private static IEnumerable<string> SplitLines(string? text)
        => string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
