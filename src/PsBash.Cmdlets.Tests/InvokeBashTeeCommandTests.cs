using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashTee</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashTeeCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashTee</c> function — copies pipeline input to
/// stdout and to every named file operand (default: overwrite; <c>-a</c>:
/// append).
///
/// Failure-surface axes covered (per Directive 3): empty pipeline,
/// missing parent directory (Directive 14), unicode content, multi-file
/// emission, pipeline pass-through preservation, <c>--help</c>, alias
/// resolution, and a Directive-12 injection probe via a filename containing
/// PowerShell scriptblock chars.
/// </summary>
public class InvokeBashTeeCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashTeeCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-tee-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    private static string Q(string s) => s.Replace("'", "''");

    [Fact]
    public void Tee_SingleFile_OverwritesContent()
    {
        string target = Path.Combine(_tmpDir, "out.txt");
        // Pre-populate so we can verify overwrite-not-append.
        File.WriteAllText(target, "OLD\n");

        var lines = RunLines(
            $"'one','two','three' | Invoke-BashTee '{Q(target)}'");

        Assert.Equal(new[] { "one", "two", "three" }, lines);
        // Items did not end with \n → oracle joins with \n + trailing \n.
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(target));
    }

    [Fact]
    public void Tee_AppendFlag_DashA_AppendsRatherThanOverwrites()
    {
        string target = Path.Combine(_tmpDir, "out.txt");
        File.WriteAllText(target, "PREEXISTING\n");

        var lines = RunLines(
            $"'one','two' | Invoke-BashTee -a '{Q(target)}'");

        Assert.Equal(new[] { "one", "two" }, lines);
        Assert.Equal("PREEXISTING\none\ntwo\n", File.ReadAllText(target));
    }

    [Fact]
    public void Tee_MultipleFiles_AllReceiveSameContent()
    {
        string a = Path.Combine(_tmpDir, "a.txt");
        string b = Path.Combine(_tmpDir, "b.txt");
        string c = Path.Combine(_tmpDir, "c.txt");

        var lines = RunLines(
            $"'hello' | Invoke-BashTee '{Q(a)}' '{Q(b)}' '{Q(c)}'");

        Assert.Equal(new[] { "hello" }, lines);
        Assert.Equal("hello\n", File.ReadAllText(a));
        Assert.Equal("hello\n", File.ReadAllText(b));
        Assert.Equal("hello\n", File.ReadAllText(c));
    }

    [Fact]
    public void Tee_PipelinePassThrough_DownstreamConsumerSeesItems()
    {
        string target = Path.Combine(_tmpDir, "out.txt");
        // Compose with Measure-Object — proves objects flow downstream after
        // the file write (the "tee" of the name).
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(
            $"('a','b','c','d') | Invoke-BashTee '{Q(target)}' | Measure-Object")
            .Invoke();
        pwsh.Commands.Clear();

        int count = (int)(result[0].Properties["Count"].Value);
        Assert.Equal(4, count);
        Assert.Equal("a\nb\nc\nd\n", File.ReadAllText(target));
    }

    [Fact]
    public void Tee_EmptyPipeline_CreatesEmptyFile()
    {
        // Oracle: $textParts.Count == 0 → textContent stays "" → empty file is
        // written and the pipeline emits nothing. Default mode = overwrite.
        string target = Path.Combine(_tmpDir, "empty.txt");
        File.WriteAllText(target, "WILL BE WIPED");

        var lines = RunLines(
            $"@() | Invoke-BashTee '{Q(target)}'");

        Assert.Empty(lines);
        Assert.Equal(string.Empty, File.ReadAllText(target));
    }

    [Fact]
    public void Tee_MissingParentDirectory_EmitsBashStyleError_NoFileWritten()
    {
        // Oracle: parent-dir Test-Path fails → Write-BashError + continue.
        string target = Path.Combine(_tmpDir, "no-such-dir", "out.txt");
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"'data' | Invoke-BashTee '{Q(target)}' 2>$null").Invoke();
        pwsh.Commands.Clear();

        // Pipeline pass-through still happens after the per-operand error.
        var bashTexts = result.Select(o =>
            o?.Properties["BashText"]?.Value as string ?? o?.ToString()).ToArray();
        Assert.Contains("data", bashTexts);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Tee_TrailingNewlineHeuristic_ItemsAlreadyEndWithNewline_ConcatDirectly()
    {
        // When the first item's BashText already ends with \n (echo/printf
        // shape), the oracle concatenates directly without re-joining. Two
        // items "a\n" + "b\n" should produce "a\nb\n", not "a\n\nb\n\n".
        string target = Path.Combine(_tmpDir, "out.txt");
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(
            $@"
$a = [PSCustomObject]@{{ BashText = ""a`n"" }}
$b = [PSCustomObject]@{{ BashText = ""b`n"" }}
@($a, $b) | Invoke-BashTee '{Q(target)}' | Out-Null
").Invoke();
        pwsh.Commands.Clear();
        Assert.Equal("a\nb\n", File.ReadAllText(target));
    }

    [Fact]
    public void Tee_UnicodeContent_RoundTripsCorrectly()
    {
        string target = Path.Combine(_tmpDir, "uni.txt");
        var lines = RunLines(
            $"'héllo','wörld','café' | Invoke-BashTee '{Q(target)}'");

        Assert.Equal(new[] { "héllo", "wörld", "café" }, lines);
        Assert.Equal("héllo\nwörld\ncafé\n", File.ReadAllText(target));
    }

    [Fact]
    public void Tee_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashTee --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("tee", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tee_AliasResolution_TeeWorks()
    {
        // psm1 registers `Set-Alias tee Invoke-BashTee`; the binary cmdlet
        // loads before psm1 runs, so the alias resolves to the cmdlet.
        string target = Path.Combine(_tmpDir, "alias.txt");
        var lines = RunLines($"'hi' | tee '{Q(target)}'");

        Assert.Equal(new[] { "hi" }, lines);
        Assert.Equal("hi\n", File.ReadAllText(target));
    }

    [Fact]
    public void Tee_InjectionProbe_OperandWithDollarParen_TreatedAsLiteralPath()
    {
        // Directive 12: an operand containing PowerShell injection chars
        // ($(throw 'pwn'), ;, scriptblock chars) must be a literal path.
        // The probe targets a path under our temp dir so it resolves to a
        // missing parent (the unique injection-marker subdir does not exist),
        // not the worktree root.
        string injection = Path.Combine(
            _tmpDir, "$(throw 'INJECTED')", "out.txt");
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"'data' | Invoke-BashTee '{Q(injection)}' 2>$null").Invoke();
        pwsh.Commands.Clear();

        // No throw, pipeline still passed through, no file written. If the
        // probe had been re-parsed as PowerShell, the call would have thrown.
        Assert.False(File.Exists(injection));
        // Pipeline still flows.
        var bashTexts = result.Select(o =>
            o?.Properties["BashText"]?.Value as string ?? o?.ToString()).ToArray();
        Assert.Contains("data", bashTexts);
    }

    [Fact]
    public void Tee_DoubleDash_TerminatesFlagParsing_FilenameStartingWithDashA()
    {
        // After `--`, every remaining arg is an operand — including a literal
        // file name that happens to start with "-a". Oracle handles via the
        // $pastDoubleDash flag.
        string target = Path.Combine(_tmpDir, "-a-literal.txt");
        var lines = RunLines(
            $"'x' | Invoke-BashTee -- '{Q(target)}'");

        Assert.Equal(new[] { "x" }, lines);
        Assert.Equal("x\n", File.ReadAllText(target));
        // Confirm it was a write (not an append) — pre-existing content
        // would have been preserved if -a leaked through.
    }
}
