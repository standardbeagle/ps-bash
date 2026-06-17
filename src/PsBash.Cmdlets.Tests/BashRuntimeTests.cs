using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Parity tests for the REFACTOR-2 Phase 2 shared C# helpers
/// (<see cref="BashRuntime"/>), extracted from the psm1 functions every leaf
/// Invoke-Bash* function depends on.
///
/// Oracle: the original psm1 helper implementations. These are pure transforms
/// (arg parsing, escape expansion, BashObject construction, text extraction)
/// with no pipeline / file / signal surface, so the applicable failure-surface
/// axes are empty input, unicode, CRLF-in-text, and quoting (a flag bundle with
/// an unknown char must not be silently swallowed). Large-input / broken-pipe /
/// signal axes do not apply: these helpers operate on fixed in-memory argv and
/// strings.
/// </summary>
public class BashRuntimeTests
{
    // ---- NormalizeBashText ----

    [Theory]
    [InlineData("hello\n", "hello")]
    [InlineData("hello", "hello")]
    [InlineData("", "")]
    [InlineData("\n", "")]
    [InlineData("a\nb\n", "a\nb")]      // only ONE trailing \n stripped
    [InlineData("a\n\n", "a\n")]
    public void NormalizeBashText_StripsSingleTrailingNewline(string input, string expected)
    {
        Assert.Equal(expected, BashRuntime.NormalizeBashText(input));
    }

    // ---- NewBashObject ----

    [Fact]
    public void NewBashObject_TextOutputFastPath_ReturnsBareString()
    {
        var result = BashRuntime.NewBashObject("hello\n");
        Assert.IsType<string>(result);
        Assert.Equal("hello", result);  // trailing \n normalized off
    }

    [Fact]
    public void NewBashObject_TypedPath_ReturnsPSObjectWithTypeNameAndBashText()
    {
        var result = BashRuntime.NewBashObject("data\n", "PsBash.CatLine");
        var pso = Assert.IsType<PSObject>(result);
        Assert.Contains("PsBash.CatLine", pso.TypeNames);
        Assert.Equal("data", pso.Properties["BashText"].Value);
        Assert.Null(pso.Properties["NoTrailingNewline"]);
    }

    [Fact]
    public void NewBashObject_NoTrailingNewline_UsesSlowPathWithMarker()
    {
        var result = BashRuntime.NewBashObject("frag", noTrailingNewline: true);
        var pso = Assert.IsType<PSObject>(result);
        Assert.Equal("frag", pso.Properties["BashText"].Value);
        Assert.Equal(true, pso.Properties["NoTrailingNewline"].Value);
    }

    [Fact]
    public void NewBashObject_NoTrailingNewline_PreservesTrailingNewlineVerbatim()
    {
        // Regression (parity-followups-2026-06-17): a noTrailingNewline object
        // means "emit these bytes EXACTLY"; the old unconditional NormalizeBashText
        // stripped the trailing \n, so `printf '%s\n' b` → "b" not "b\n", and the
        // newline vanished whenever a following frame got concatenated.
        var result = BashRuntime.NewBashObject("b\n", noTrailingNewline: true);
        var pso = Assert.IsType<PSObject>(result);
        Assert.Equal("b\n", pso.Properties["BashText"].Value);
        Assert.Equal(true, pso.Properties["NoTrailingNewline"].Value);
    }

    [Fact]
    public void NewBashObject_CommandProperty_AttachedWhenProvided()
    {
        var result = BashRuntime.NewBashObject("x", "PsBash.WcResult", command: "wc");
        var pso = Assert.IsType<PSObject>(result);
        Assert.Equal("wc", pso.Properties["Command"].Value);
    }

    // ---- EmitBashLines ----

    [Fact]
    public void EmitBashLines_SplitsOnNewline_OneObjectPerLine()
    {
        var lines = BashRuntime.EmitBashLines("line1\nline2\n").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("line1", lines[0]);
        Assert.Equal("line2", lines[1]);
    }

    [Fact]
    public void EmitBashLines_NoTrailingNewline_LastLineMarked()
    {
        // "a\nb" — last line "b" had no trailing newline, so it is the
        // NoTrailingNewline slow-path PSObject; "a" is the fast-path string.
        var lines = BashRuntime.EmitBashLines("a\nb").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("a", lines[0]);
        var last = Assert.IsType<PSObject>(lines[1]);
        Assert.Equal("b", last.Properties["BashText"].Value);
        Assert.Equal(true, last.Properties["NoTrailingNewline"].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmitBashLines_EmptyOrNull_YieldsNothing(string? input)
    {
        Assert.Empty(BashRuntime.EmitBashLines(input));
    }

    [Fact]
    public void EmitBashLines_UnicodeContent_Preserved()
    {
        var lines = BashRuntime.EmitBashLines("café\n😀\n").ToList();
        Assert.Equal(new object[] { "café", "😀" }, lines);
    }

    // ---- GetBashText ----

    [Fact]
    public void GetBashText_Null_ReturnsEmpty()
    {
        Assert.Equal("", BashRuntime.GetBashText(null));
    }

    [Fact]
    public void GetBashText_String_ReturnsItself()
    {
        Assert.Equal("plain", BashRuntime.GetBashText("plain"));
    }

    [Fact]
    public void GetBashText_ObjectWithBashTextProperty_ReturnsThatValue()
    {
        var obj = BashRuntime.NewBashObject("payload", "PsBash.CatLine");
        Assert.Equal("payload", BashRuntime.GetBashText(obj));
    }

    [Fact]
    public void GetBashText_ArbitraryObject_Stringifies()
    {
        Assert.Equal("42", BashRuntime.GetBashText(42));
    }

    // ---- NewFlagDefs ----

    [Fact]
    public void NewFlagDefs_BuildsOrdinalDictionary()
    {
        var defs = BashRuntime.NewFlagDefs(new[] { "-a", "desc a", "-b", "desc b" });
        Assert.Equal("desc a", defs["-a"]);
        Assert.Equal("desc b", defs["-b"]);
        Assert.False(defs.ContainsKey("-A"));  // case-sensitive (ordinal)
    }

    [Fact]
    public void NewFlagDefs_OddEntryCount_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => BashRuntime.NewFlagDefs(new[] { "-a", "desc a", "-b" }));
    }

    [Fact]
    public void NewFlagDefs_Empty_ReturnsEmptyDictionary()
    {
        Assert.Empty(BashRuntime.NewFlagDefs(Array.Empty<string>()));
    }

    // ---- ConvertFromBashArgs ----

    private static readonly Dictionary<string, string> SampleDefs = new(StringComparer.Ordinal)
    {
        ["-a"] = "flag a",
        ["-b"] = "flag b",
        ["--long"] = "long flag",
    };

    [Fact]
    public void ConvertFromBashArgs_SeparatesFlagsAndOperands()
    {
        var r = BashRuntime.ConvertFromBashArgs(new[] { "-a", "file1", "file2" }, SampleDefs);
        Assert.True(r.Flags["-a"]);
        Assert.False(r.Flags["-b"]);
        Assert.Equal(new[] { "file1", "file2" }, r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_BundledShortFlags_AllSet()
    {
        var r = BashRuntime.ConvertFromBashArgs(new[] { "-ab" }, SampleDefs);
        Assert.True(r.Flags["-a"]);
        Assert.True(r.Flags["-b"]);
        Assert.Empty(r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_UnknownBundleChar_WholeTokenIsOperand()
    {
        // -aZ : 'a' is known, 'Z' is not — psm1 parity is "whole token becomes
        // an operand and scanning of that token stops".
        var r = BashRuntime.ConvertFromBashArgs(new[] { "-aZ" }, SampleDefs);
        Assert.Contains("-aZ", r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_DoubleDash_EndsFlagParsing()
    {
        var r = BashRuntime.ConvertFromBashArgs(new[] { "--", "-a", "-b" }, SampleDefs);
        Assert.False(r.Flags["-a"]);
        Assert.Equal(new[] { "-a", "-b" }, r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_LongFlag_Recognized()
    {
        var r = BashRuntime.ConvertFromBashArgs(new[] { "--long", "x" }, SampleDefs);
        Assert.True(r.Flags["--long"]);
        Assert.Equal(new[] { "x" }, r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_UnknownLongFlag_IsOperand()
    {
        var r = BashRuntime.ConvertFromBashArgs(new[] { "--nope" }, SampleDefs);
        Assert.Contains("--nope", r.Operands);
    }

    [Theory]
    [InlineData("--long=auto")]
    [InlineData("--long=always")]
    [InlineData("--long=never")]
    public void ConvertFromBashArgs_RegisteredLongFlagWithEqValue_SetsFlag_NotOperand(string token)
    {
        // Regression: the near-universal `alias ls='ls --color=auto'` passes a
        // registered boolean long flag in `--flag=value` form. The base name
        // (before '=') must match the FlagDef; otherwise it falls into operands
        // and the cmdlet treats it as a file → "No such file or directory".
        // Boolean FlagDef → value ignored (the =never wart is documented).
        var r = BashRuntime.ConvertFromBashArgs(new[] { token }, SampleDefs);
        Assert.True(r.Flags["--long"]);
        Assert.Empty(r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_UnregisteredLongFlagWithEqValue_StillOperand()
    {
        // The =value handling only applies to registered flags; an unknown
        // --flag=value must remain an operand (no silent swallow).
        var r = BashRuntime.ConvertFromBashArgs(new[] { "--nope=auto" }, SampleDefs);
        Assert.Contains("--nope=auto", r.Operands);
        Assert.False(r.Flags["--long"]);
    }

    [Fact]
    public void ConvertFromBashArgs_Empty_AllFlagsFalseNoOperands()
    {
        var r = BashRuntime.ConvertFromBashArgs(Array.Empty<string>(), SampleDefs);
        Assert.All(r.Flags.Values, v => Assert.False(v));
        Assert.Empty(r.Operands);
    }

    // ---- ExpandEscapeSequences ----

    [Theory]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"a\rb", "a\rb")]
    [InlineData("", "")]
    [InlineData("no escapes", "no escapes")]
    public void ExpandEscapeSequences_BasicEscapes(string input, string expected)
    {
        Assert.Equal(expected, BashRuntime.ExpandEscapeSequences(input));
    }

    [Fact]
    public void ExpandEscapeSequences_DoubleBackslash_StaysLiteralBackslashN()
    {
        // \\n must produce a literal backslash + 'n', NOT a newline — this is
        // the whole point of the sentinel two-pass scheme.
        Assert.Equal(@"\n", BashRuntime.ExpandEscapeSequences(@"\\n"));
    }

    [Fact]
    public void ExpandEscapeSequences_AllControlEscapes()
    {
        Assert.Equal("\a\b\f\v", BashRuntime.ExpandEscapeSequences(@"\a\b\f\v"));
    }

    // ---- FormatBashError ----

    [Fact]
    public void FormatBashError_PrefixesCommandName()
    {
        Assert.Equal("grep: no such file", BashRuntime.FormatBashError("grep", "no such file"));
    }

    // ---- RunChildProcess (safe-spawn helper) ----
    //
    // Failure-surface axes that DO apply here (qa-rubric Directive 3): missing
    // target (a hung child), large input (>64KB stderr → pipe-buffer deadlock if
    // a single stream is drained), and exit-code propagation. These spawn real OS
    // commands (cmd.exe / /bin/sh + ping|sleep|echo) so they run on every
    // platform without a Skip; the child binaries are always present.

    [Fact]
    public void RunChildProcess_QuickCommand_CapturesStdoutAndExitsZero()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "echo hello-child" })
            : ("/bin/echo", new[] { "hello-child" });

        var result = BashRuntime.RunChildProcess(file, args, TimeSpan.FromSeconds(10));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-child", result.Stdout);
    }

    [Fact]
    public void RunChildProcess_HangingCommand_TimesOutBoundedAndReports124()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 > NUL" })
            : ("/bin/sh", new[] { "-c", "sleep 30" });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = BashRuntime.RunChildProcess(file, args, TimeSpan.FromSeconds(1));
        sw.Stop();

        Assert.True(result.TimedOut, "a child exceeding the budget must report TimedOut");
        Assert.Equal(124, result.ExitCode);
        // ~1s budget + ~2s kill-grace + overhead. 8s proves we did NOT wait the
        // child's full 30s — the wait was bounded and the tree was killed.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8),
            $"per-child wait must be bounded; took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void RunChildProcess_LargeStderr_DrainsConcurrentlyWithoutDeadlock()
    {
        // ~300KB to stderr, far beyond the OS pipe buffer (~64KB). If RunChildProcess
        // drained stdout only (the bug this helper exists to kill), the child would
        // block writing stderr and our bounded wait would TIME OUT. Concurrent drain
        // => the child finishes and the full stderr is captured.
        const string pad = "yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy"; // 49 chars
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", $"for /L %i in (1,1,6000) do @echo {pad} 1>&2" })
            : ("/bin/sh", new[] { "-c", $"i=0; while [ $i -lt 6000 ]; do echo {pad} 1>&2; i=$((i+1)); done" });

        var result = BashRuntime.RunChildProcess(file, args, TimeSpan.FromSeconds(30));

        Assert.False(result.TimedOut, "concurrent stderr drain must prevent a pipe-buffer deadlock");
        Assert.True(result.Stderr.Length > 70_000,
            $"expected large stderr captured, got {result.Stderr.Length} chars");
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void RunChildProcess_OutputAboveCaptureLimit_IsTruncatedButStillDrained()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_CHILD_CAPTURE_MAX_CHARS");
        Environment.SetEnvironmentVariable("PSBASH_CHILD_CAPTURE_MAX_CHARS", "4096");
        try
        {
            const string pad = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
            var (file, args) = OperatingSystem.IsWindows()
                ? ("cmd.exe", new[] { "/c", $"for /L %i in (1,1,6000) do @echo {pad}" })
                : ("/bin/sh", new[] { "-c", $"i=0; while [ $i -lt 6000 ]; do echo {pad}; i=$((i+1)); done" });

            var result = BashRuntime.RunChildProcess(file, args, TimeSpan.FromSeconds(30));

            Assert.False(result.TimedOut, "draining beyond the capture cap must still let the child exit");
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.StdoutTruncated, "stdout should report truncation at the configured cap");
            Assert.False(result.StderrTruncated);
            Assert.Equal(4096, result.Stdout.Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_CHILD_CAPTURE_MAX_CHARS", prior);
        }
    }

    [Fact]
    public void RunChildProcess_ChildReadsStdin_GetsEofAndDoesNotHang()
    {
        // The child blocks trying to read a line of stdin. RunChildProcess must
        // hand it an EOF-closed stdin so it completes instead of hanging until the
        // wait budget elapses. A generous budget proves the EOF (not the timeout)
        // is what unblocks it.
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "set /p x= & echo done-eof" })
            : ("/bin/sh", new[] { "-c", "read x; echo done-eof" });

        var result = BashRuntime.RunChildProcess(file, args, TimeSpan.FromSeconds(10));

        Assert.False(result.TimedOut, "closed stdin must give the child EOF so it does not hang");
        Assert.Contains("done-eof", result.Stdout);
    }
}
