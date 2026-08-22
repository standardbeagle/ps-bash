using System.Runtime.InteropServices;
using PsBash.Cmdlets.Media;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Tests for <c>psav</c> (<c>Invoke-BashFfmpeg</c>), the ffmpeg wrapper.
/// <para><b>Oracle note (Directive 1):</b> ffmpeg is not a bash builtin and has no bash oracle — this
/// is a ps-bash-specific cmdlet surface, so hand-written asserts apply. The design compensates by
/// keeping every ffmpeg decision in pure argv builders (<see cref="FfmpegPlan"/>), so the filter
/// graphs, seek placement and per-OS screen grabbers are asserted exactly, on any machine, with
/// <b>no ffmpeg installed</b> — which is also how the <c>--dry-run</c> path is verified end to end.
/// The spawn itself is the shared, already-tested <c>BashRuntime.RunChildProcess</c> contract.</para>
/// </summary>
public class InvokeBashFfmpegCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashFfmpegCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    // ── timecodes ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("7", 7)]
    [InlineData("7.5", 7.5)]
    [InlineData("1:02", 62)]
    [InlineData("00:01:02.500", 62.5)]
    [InlineData("90s", 90)]
    [InlineData("1m30s", 90)]
    [InlineData("1h2m3s", 3723)]
    [InlineData("250ms", 0.25)]
    public void Time_ParsesEveryShapeAPersonTypes(string text, double expected)
    {
        Assert.True(AvTime.TryParse(text, out var seconds));
        Assert.Equal(expected, seconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("m30")]
    [InlineData("5q")]
    public void Time_RejectsGarbage(string text)
    {
        Assert.False(AvTime.TryParse(text, out _));
    }

    [Fact]
    public void Time_FormatsCanonicallyForFfmpeg()
    {
        Assert.Equal("00:00:05.000", AvTime.Format(5));
        Assert.Equal("01:02:03.500", AvTime.Format(3723.5));
        // Never emit a negative seek — ffmpeg would reject the argv outright.
        Assert.Equal("00:00:00.000", AvTime.Format(-4));
    }

    [Theory]
    [InlineData(4.2, "4.2s")]
    [InlineData(67, "1:07")]
    [InlineData(3723, "1:02:03")]
    public void Time_HumanizesForSummaryLines(double seconds, string expected)
    {
        Assert.Equal(expected, AvTime.Humanize(seconds));
    }

    // ── option parsing ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Options_SplitSubcommandOperandsAndValues()
    {
        var opts = AvOptions.Parse(new[] { "gif", "demo.mp4", "--from", "5", "--fps=20", "--mute" });

        Assert.Equal("gif", opts.Subcommand);
        Assert.Equal(new[] { "demo.mp4" }, opts.Operands);
        Assert.Equal(5, opts.Time("from"));
        Assert.Equal(20, opts.Int("fps", 0));      // --name=value spelling
        Assert.True(opts.Flag("mute"));            // a switch never eats the next token
        Assert.Null(opts.UnknownOption);
    }

    [Fact]
    public void Options_SwitchDoesNotSwallowTheFollowingOperand()
    {
        var opts = AvOptions.Parse(new[] { "shot", "--dry-run", "clip.mp4" });

        Assert.True(opts.Flag("dry-run"));
        Assert.Equal(new[] { "clip.mp4" }, opts.Operands);
    }

    [Fact]
    public void Options_DoubleDashSendsTheRestToPassthrough()
    {
        var opts = AvOptions.Parse(new[] { "raw", "--", "-i", "in.mp4", "-y", "out.mp4" });

        Assert.Equal("raw", opts.Subcommand);
        Assert.Equal(new[] { "-i", "in.mp4", "-y", "out.mp4" }, opts.Passthrough);
        Assert.Empty(opts.Operands);
    }

    [Fact]
    public void Options_ValueFlagWithNoValueIsReportedNotSilentlyDropped()
    {
        var opts = AvOptions.Parse(new[] { "gif", "demo.mp4", "--fps" });

        Assert.Equal("--fps", opts.UnknownOption);
    }

    // ── argv builders ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shot_SeeksBeforeTheInputSoAGrabIsFastNotAFullDecode()
    {
        var plan = FfmpegPlan.Shot("in.mp4", 5, "out.png");

        var ss = plan.Arguments.ToList().IndexOf("-ss");
        var i = plan.Arguments.ToList().IndexOf("-i");
        Assert.True(ss >= 0 && i > ss, "the seek must precede -i (input seeking)");
        Assert.Equal("00:00:05.000", plan.Arguments[ss + 1]);
        Assert.Equal(new[] { "-frames:v", "1", "out.png" }, plan.Arguments.TakeLast(3));
        Assert.Equal("screenshot", plan.Kind);
    }

    [Fact]
    public void Shot_WidthAddsAScaleFilterWithFreeHeight()
    {
        var plan = FfmpegPlan.Shot("in.mp4", 1, "out.png", width: 640);

        var vf = plan.Arguments.ToList();
        Assert.Contains("-vf", vf);
        Assert.Equal("scale=640:-1", vf[vf.IndexOf("-vf") + 1]);
    }

    [Fact]
    public void Gif_UsesThePaletteRouteNotTheDefaultWebPalette()
    {
        var plan = FfmpegPlan.Gif("in.mp4", from: 2, duration: 5, fps: 12, width: 800, output: "out.gif");

        var graph = plan.Arguments[plan.Arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("fps=12", graph);
        Assert.Contains("scale=800:-1:flags=lanczos", graph);
        Assert.Contains("palettegen", graph);
        Assert.Contains("paletteuse", graph);
        Assert.Contains("-ss", plan.Arguments);
        Assert.Contains("00:00:05.000", plan.Arguments);   // -t window
        Assert.Equal("0", plan.Arguments[plan.Arguments.ToList().IndexOf("-loop") + 1]);
    }

    [Fact]
    public void Clip_ProducesABrowserPlayableMp4()
    {
        var plan = FfmpegPlan.Clip("in.mov", from: null, duration: 10, width: 1280, crf: 22, mute: true, output: "out.mp4");

        Assert.Contains("libx264", plan.Arguments);
        Assert.Contains("yuv420p", plan.Arguments);        // playable in browsers / Slack, not just VLC
        Assert.Contains("+faststart", plan.Arguments);
        Assert.Contains("-an", plan.Arguments);            // --mute drops the audio stream
        Assert.DoesNotContain("aac", plan.Arguments);
        var vf = plan.Arguments.ToList();
        // libx264 rejects an odd height, so an H.264 rescale must use -2, never -1.
        Assert.Equal("scale=1280:-2", vf[vf.IndexOf("-vf") + 1]);
    }

    [Fact]
    public void Clip_WithoutMuteKeepsAnEncodedAudioTrack()
    {
        var plan = FfmpegPlan.Clip("in.mov", null, null, 0, 22, mute: false, output: "out.mp4");

        Assert.Contains("aac", plan.Arguments);
        Assert.DoesNotContain("-an", plan.Arguments);
        Assert.DoesNotContain("-vf", plan.Arguments);      // no width asked for → no filter at all
    }

    [Fact]
    public void Sheet_SamplesAcrossTheWholeClipNotTheFirstSeconds()
    {
        // 12 tiles over a 60s clip → one frame every 5s → fps 0.2.
        var plan = FfmpegPlan.Sheet("in.mp4", 4, 3, durationSeconds: 60, output: "sheet.png");

        var graph = plan.Arguments[plan.Arguments.ToList().IndexOf("-vf") + 1];
        Assert.Contains("fps=0.2", graph);
        Assert.Contains("tile=4x3", graph);
    }

    [Theory]
    [InlineData("4x3", 4, 3)]
    [InlineData("3X2", 3, 2)]
    [InlineData("", 4, 3)]
    [InlineData("garbage", 4, 3)]
    [InlineData("0x5", 4, 3)]
    public void ParseTiles_FallsBackToAReadableDefault(string spec, int columns, int rows)
    {
        Assert.Equal((columns, rows), FfmpegPlan.ParseTiles(spec));
    }

    [Fact]
    public void ScreenCapture_PicksThePlatformGrabber()
    {
        Assert.Equal(
            new[] { "-f", "gdigrab", "-framerate", "30", "-i", "desktop" },
            FfmpegPlan.ScreenCaptureInput(OSPlatform.Windows, 30));

        Assert.Equal(
            new[] { "-f", "gdigrab", "-framerate", "30", "-i", "title=ps-bash" },
            FfmpegPlan.ScreenCaptureInput(OSPlatform.Windows, 30, window: "ps-bash"));

        Assert.Equal(
            new[] { "-f", "avfoundation", "-framerate", "30", "-i", "1:none" },
            FfmpegPlan.ScreenCaptureInput(OSPlatform.OSX, 30));

        Assert.Equal(
            new[] { "-f", "x11grab", "-framerate", "25", "-i", ":0.0" },
            FfmpegPlan.ScreenCaptureInput(OSPlatform.Linux, 25, display: ":0.0"));
    }

    [Fact]
    public void Record_BudgetsAWaitLongerThanTheRecordingItself()
    {
        var plan = FfmpegPlan.Record(OSPlatform.Windows, seconds: 10, fps: 30, output: "demo.mp4");

        // A too-tight spawn budget would kill-tree the encoder and truncate the recording.
        Assert.NotNull(plan.Timeout);
        Assert.True(plan.Timeout!.Value.TotalSeconds > 10);
        Assert.Contains("gdigrab", plan.Arguments);
        Assert.Equal("00:00:10.000", plan.Arguments[plan.Arguments.ToList().IndexOf("-t") + 1]);
    }

    [Fact]
    public void SamplePoints_SpreadsShotsInsideTheClipNeverAtFrameZeroOrEof()
    {
        var points = FfmpegPlan.SamplePoints(duration: 100, count: 4);

        Assert.Equal(new[] { 20d, 40d, 60d, 80d }, points);
        Assert.Equal(new[] { 50d }, FfmpegPlan.SamplePoints(100, 1));
        Assert.Empty(FfmpegPlan.SamplePoints(100, 0));
    }

    [Fact]
    public void DefaultOutput_NeverCollidesWithTheSourceFile()
    {
        var shot = FfmpegPlan.DefaultOutput(Path.Combine("videos", "demo.mp4"), "shot", ".png", at: 5);
        Assert.Equal(Path.Combine("videos", "demo-shot-00-00-05.000.png"), shot);

        var gif = FfmpegPlan.DefaultOutput(Path.Combine("videos", "demo.mp4"), "demo", ".gif");
        Assert.Equal(Path.Combine("videos", "demo-demo.gif"), gif);
    }

    [Fact]
    public void DefaultOutput_MultiShotNamesCannotCollide()
    {
        // Regression: two sample points that round to the same timecode (which is every point when
        // the duration is unknown) produced ONE filename, so an N-shot run silently overwrote its
        // own output N-1 times. The 1-based index makes collision impossible.
        var first = FfmpegPlan.DefaultOutput("demo.mp4", "shot", ".png", at: 0, index: 1);
        var second = FfmpegPlan.DefaultOutput("demo.mp4", "shot", ".png", at: 0, index: 2);

        Assert.NotEqual(first, second);
        Assert.Equal("demo-shot-01-00-00-00.000.png", Path.GetFileName(first));
        Assert.Equal("demo-shot-02-00-00-00.000.png", Path.GetFileName(second));
    }

    // ── ffprobe report ─────────────────────────────────────────────────────────────────────────

    private const string VideoProbeJson = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "r_frame_rate": "30000/1001" },
            { "codec_type": "audio", "codec_name": "aac", "channels": 2, "sample_rate": "48000" }
          ],
          "format": { "filename": "demo.mp4", "format_name": "mov,mp4,m4a", "duration": "12.500000", "size": "1048576", "bit_rate": "671088" }
        }
        """;

    [Fact]
    public void Probe_LiftsTheFieldsWorthPipelining()
    {
        var m = FfprobeReport.Parse("demo.mp4", VideoProbeJson);

        Assert.NotNull(m);
        Assert.Equal("video", m!.Class);
        Assert.Equal(12.5, m.Duration, 3);
        Assert.Equal(1048576, m.Size);
        Assert.Equal("h264", m.VideoCodec);
        Assert.Equal(1920, m.Width);
        Assert.Equal(1080, m.Height);
        Assert.Equal("aac", m.AudioCodec);
        Assert.Equal(2, m.Channels);
        Assert.Equal(48000, m.SampleRate);
        // NTSC 29.97, not the 30 an int parse would report.
        Assert.Equal(29.97, m.Fps, 2);
    }

    [Fact]
    public void Probe_ClassifiesAStillImageAsImageNotAZeroLengthVideo()
    {
        const string json = """
            {
              "streams": [ { "codec_type": "video", "codec_name": "png", "width": 800, "height": 600, "r_frame_rate": "25/1" } ],
              "format": { "format_name": "png_pipe", "size": "4096" }
            }
            """;

        var m = FfprobeReport.Parse("shot.png", json);

        Assert.Equal("image", m!.Class);
        Assert.Equal(0, m.Duration);
    }

    [Fact]
    public void Probe_AudioOnlyFileIsAudio()
    {
        const string json = """
            {
              "streams": [ { "codec_type": "audio", "codec_name": "mp3", "channels": 2, "sample_rate": "44100" } ],
              "format": { "format_name": "mp3", "duration": "180.0", "size": "3000000" }
            }
            """;

        Assert.Equal("audio", FfprobeReport.Parse("song.mp3", json)!.Class);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public void Probe_RefusesToInventARowFromNonMedia(string json)
    {
        Assert.Null(FfprobeReport.Parse("junk.bin", json));
    }

    [Theory]
    [InlineData("30000/1001", 29.97)]
    [InlineData("25/1", 25)]
    [InlineData("0/0", 0)]
    [InlineData(null, 0)]
    [InlineData("24", 24)]
    public void Probe_ParsesRationalFrameRates(string? text, double expected)
    {
        Assert.Equal(expected, FfprobeReport.ParseRational(text), 2);
    }

    [Fact]
    public void Probe_BashTextReadsLikeANativeToolLine()
    {
        var text = FfprobeReport.Parse("demo.mp4", VideoProbeJson)!.Text;

        Assert.Contains("demo.mp4", text);
        Assert.Contains("12.5s", text);
        Assert.Contains("1920x1080", text);
        Assert.Contains("1 MB", text);
    }

    // ── interactive gallery building blocks (headless-testable; the ReadKey loop is not) ────────

    [Theory]
    [InlineData(ConsoleKey.Q, '\0', AvGallery.AvTuiAction.Quit)]
    [InlineData(ConsoleKey.Escape, '\0', AvGallery.AvTuiAction.Quit)]
    [InlineData(ConsoleKey.DownArrow, '\0', AvGallery.AvTuiAction.Down)]
    [InlineData(ConsoleKey.UpArrow, '\0', AvGallery.AvTuiAction.Up)]
    [InlineData(ConsoleKey.J, 'j', AvGallery.AvTuiAction.Down)]
    [InlineData(ConsoleKey.K, 'k', AvGallery.AvTuiAction.Up)]
    [InlineData(ConsoleKey.Enter, '\r', AvGallery.AvTuiAction.ToggleExpand)]
    [InlineData(ConsoleKey.S, 's', AvGallery.AvTuiAction.Screenshot)]
    [InlineData(ConsoleKey.G, 'g', AvGallery.AvTuiAction.Gif)]
    [InlineData(ConsoleKey.C, 'c', AvGallery.AvTuiAction.Clip)]
    [InlineData(ConsoleKey.R, 'r', AvGallery.AvTuiAction.Refresh)]
    [InlineData(ConsoleKey.X, 'x', AvGallery.AvTuiAction.None)]
    internal void Gallery_MapsKeysToActions(ConsoleKey key, char ch, AvGallery.AvTuiAction expected)
    {
        Assert.Equal(expected, AvGallery.Decide(key, ch));
    }

    [Fact]
    public void Gallery_RefusesToCaptureFromAnAudioOnlyRow()
    {
        var row = new System.Management.Automation.PSObject();
        row.Properties.Add(new System.Management.Automation.PSNoteProperty("Path", "song.mp3"));
        row.Properties.Add(new System.Management.Automation.PSNoteProperty("class", "audio"));

        var outcome = AvGallery.Act(AvGallery.AvTuiAction.Screenshot, row, workingDir: null);

        Assert.False(outcome.Ok);
        Assert.Contains("no video track", outcome.Message);
    }

    [Fact]
    public void Gallery_ClipWindowNeverExceedsTheSource()
    {
        Assert.Equal(3, AvGallery.Window(duration: 3, requested: 5));
        Assert.Equal(5, AvGallery.Window(duration: 30, requested: 5));
        Assert.Equal(5, AvGallery.Window(duration: 0, requested: 5));   // unknown duration → ask for it anyway
    }

    // ── the cmdlet, end to end, with no ffmpeg installed (--dry-run) ────────────────────────────

    [Fact]
    public void DryRun_EmitsThePlannedCommandWithoutSpawningAnything()
    {
        var file = NewTempFile("clip.mp4");
        try
        {
            var rows = Run($"Invoke-BashFfmpeg gif '{file}' --from 2 --dur 5 --fps 20 --width 640 --dry-run");

            var row = Assert.Single(rows);
            Assert.Equal("PsBash.MediaArtifact", row.TypeNames[0]);
            Assert.Equal("planned", Prop(row, "Status"));
            Assert.Equal("planned", Prop(row, "class"));
            var command = Prop(row, "Command");
            Assert.StartsWith("ffmpeg ", command);
            Assert.Contains("palettegen", command);
            Assert.Contains("fps=20", command);
            Assert.EndsWith(".gif", command.Trim().TrimEnd('"'));
        }
        finally { Delete(file); }
    }

    [Fact]
    public void DryRun_ShotHonoursAnExplicitTimecode()
    {
        var file = NewTempFile("clip.mp4");
        try
        {
            var rows = Run($"Invoke-BashFfmpeg shot '{file}' --at 1m30s --out '{file}.png' --dry-run");

            var command = Prop(Assert.Single(rows), "Command");
            Assert.Contains("-ss 00:01:30.000", command);
            Assert.Contains("-frames:v 1", command);
        }
        finally { Delete(file); }
    }

    [Fact]
    public void MultiShot_WithoutAReadableDurationIsAnErrorNotNIdenticalFrames()
    {
        // Regression: an unprobeable clip made every sample point 0, so `--count 3` planned three
        // grabs of the same frame at the same path. It now refuses and points at --at instead.
        var file = NewTempFile("clip.mp4");
        try
        {
            var pwsh = _fixture.AcquireFresh();
            var result = pwsh.AddScript($"Invoke-BashFfmpeg shot '{file}' --count 3 --dry-run").Invoke();

            Assert.Empty(result);
            Assert.True(pwsh.HadErrors);
            Assert.Contains("--at", string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        }
        finally { Delete(file); }
    }

    [Fact]
    public void Help_ListsTheSubcommands()
    {
        var text = string.Join("\n", Run("Invoke-BashFfmpeg help").Select(r => r.ToString()));

        foreach (var sub in new[] { "probe", "shot", "frames", "sheet", "gif", "clip", "record", "raw" })
        {
            Assert.Contains(sub, text);
        }
    }

    [Fact]
    public void UnknownSubcommand_IsAnErrorNotASilentNoOp()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript("Invoke-BashFfmpeg frobnicate x.mp4").Invoke();

        Assert.Empty(result);
        Assert.True(pwsh.HadErrors);
    }

    [Fact]
    public void MissingInputFile_IsReportedBashStyle()
    {
        var pwsh = _fixture.AcquireFresh();
        var missing = Path.Combine(Path.GetTempPath(), "psbash-no-such-" + Guid.NewGuid().ToString("N") + ".mp4");
        pwsh.AddScript($"Invoke-BashFfmpeg gif '{missing}'").Invoke();

        Assert.True(pwsh.HadErrors);
        Assert.Contains(
            "No such file or directory",
            string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private List<System.Management.Automation.PSObject> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        return result.ToList();
    }

    private static string Prop(System.Management.Automation.PSObject row, string name)
        => row.Properties[name]?.Value?.ToString() ?? string.Empty;

    private static string NewTempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "psbash-av-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[] { 0, 1, 2, 3 });   // never decoded: --dry-run spawns nothing
        return path;
    }

    private static void Delete(string file)
    {
        try { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); }
        catch { /* best effort */ }
    }
}
