using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashFold</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashFoldCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashFold</c> function — wraps each input line at
/// a given column width. Default width 80; supports <c>-w N</c>, <c>-s</c>
/// (soft wrap on spaces), <c>-b</c> (bytes, no-op for ASCII).
///
/// Failure-surface axes covered (per Directive 3): empty input, exact-width
/// boundary, longer-than-width hard wrap, narrow <c>-w</c>, soft wrap with
/// space, soft wrap hard-fallback (no space in window), pipeline mode, file
/// mode, multi-line pipeline split, <c>--help</c>, alias resolution, and a
/// Directive-12 injection probe.
/// </summary>
public class InvokeBashFoldCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmpDir;
    private readonly SharedPwshFixture _fixture;

    public InvokeBashFoldCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-fold-{Guid.NewGuid():N}".Substring(0, 23));
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

    [Fact]
    public void Fold_EmptyPipeline_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashFold");
        Assert.Empty(lines);
    }

    [Fact]
    public void Fold_ShortLine_DefaultWidth_PassesThrough()
    {
        var lines = RunLines("'hello' | Invoke-BashFold");
        Assert.Single(lines);
        Assert.Equal("hello", lines[0]);
    }

    [Fact]
    public void Fold_ExactWidthLine_DoesNotWrap()
    {
        // A line of exactly N chars at -w N must emit unchanged (the oracle's
        // `if ($line.Length -le $width) { emit; continue }` branch).
        var lines = RunLines("'abcd' | Invoke-BashFold -w 4");
        Assert.Single(lines);
        Assert.Equal("abcd", lines[0]);
    }

    [Fact]
    public void Fold_LongerThanWidth_HardWrapsIntoSegments()
    {
        // 10 chars, width 4 → ceil(10/4) = 3 segments: 4 + 4 + 2.
        var lines = RunLines("'abcdefghij' | Invoke-BashFold -w 4");
        Assert.Equal(new[] { "abcd", "efgh", "ij" }, lines);
    }

    [Fact]
    public void Fold_NarrowWidth_JoinedForm_Works()
    {
        // Verify the -wN joined form.
        var lines = RunLines("'abcdef' | Invoke-BashFold -w3");
        Assert.Equal(new[] { "abc", "def" }, lines);
    }

    [Fact]
    public void Fold_LongWidthLongFormEquals_Works()
    {
        // Verify --width=N long form.
        var lines = RunLines("'abcdef' | Invoke-BashFold --width=3");
        Assert.Equal(new[] { "abc", "def" }, lines);
    }

    [Fact]
    public void Fold_SoftWrap_BreaksAtSpace()
    {
        // "hello world!" with width 8, -s:
        //   chunkEnd = 8 → space at index 5 → emit "hello " (chars 0..5
        //   inclusive, length 6), then continue from pos 6 → "world!" fits.
        var lines = RunLines("'hello world!' | Invoke-BashFold -w 8 -s");
        Assert.Equal(new[] { "hello ", "world!" }, lines);
    }

    [Fact]
    public void Fold_SoftWrap_NoSpaceInWindow_HardBreaksAtWidth()
    {
        // "abcdefghij" with -w 4 -s and no space → hard-break at width.
        var lines = RunLines("'abcdefghij' | Invoke-BashFold -w 4 -s");
        Assert.Equal(new[] { "abcd", "efgh", "ij" }, lines);
    }

    [Fact]
    public void Fold_FileMode_AsciiContent_Wraps()
    {
        var file = Path.Combine(_tmpDir, "ascii.txt");
        File.WriteAllText(file, "abcdefghij\ntiny\n");
        var lines = RunLines(
            $"Invoke-BashFold -w 4 '{file.Replace("'", "''")}'");
        // 10 chars wraps to 4+4+2; second line "tiny" is exactly 4 chars
        // so it passes through unchanged.
        Assert.Equal(new[] { "abcd", "efgh", "ij", "tiny" }, lines);
    }

    [Fact]
    public void Fold_FileMode_CrlfNormalized()
    {
        var file = Path.Combine(_tmpDir, "crlf.txt");
        File.WriteAllText(file, "abcdef\r\nghij\r\n");
        var lines = RunLines(
            $"Invoke-BashFold -w 3 '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "abc", "def", "ghi", "j" }, lines);
    }

    [Fact]
    public void Fold_FileMode_MissingFile_DoesNotThrow_NoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt")
            .Replace("'", "''");
        var result = pwsh
            .AddScript($"Invoke-BashFold '{missing}' 2>$null")
            .Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Fold_MultiLinePipelineItem_SplitsAndWraps()
    {
        var lines = RunLines(
            "\"abcdef`nghijkl\" | Invoke-BashFold -w 3");
        Assert.Equal(new[] { "abc", "def", "ghi", "jkl" }, lines);
    }

    [Fact]
    public void Fold_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashFold --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines,
            l => l.Contains("fold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Fold_AliasResolution_FoldWorks()
    {
        // The psm1 module registers `Set-Alias fold Invoke-BashFold`. Because
        // the binary cmdlet loads before psm1 runs, the alias resolves to the
        // cmdlet.
        var lines = RunLines("'abcdefgh' | fold -w 4");
        Assert.Equal(new[] { "abcd", "efgh" }, lines);
    }

    [Fact]
    public void Fold_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. The cmdlet routes
        // file operands through SessionState.Path, never via string-concat
        // into a script body.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var lines = RunLines(
            $"Invoke-BashFold '{probe.Replace("'", "''")}' 2>$null");
        // No output. If the probe had been evaluated as script, the test
        // would have thrown or 'pwned' would appear in the output.
        Assert.Empty(lines);
    }

    [Fact]
    public void Fold_BytesFlag_IsAcceptedAsNoOp()
    {
        // -b is accepted for arg compat and should not consume the next
        // operand or affect the ASCII wrap result.
        var lines = RunLines("'abcdef' | Invoke-BashFold -b -w 3");
        Assert.Equal(new[] { "abc", "def" }, lines);
    }

    [Fact]
    public void Fold_UnrecognizedLongOption_ExitCode2_NoOutput()
    {
        // Unrecognized options (garbage) → bash-parity error on stderr,
        // LASTEXITCODE=2, no stdout.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            "Invoke-BashFold --bogus 2>$null; $LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        // Only the exit-code integer should be in the output stream.
        Assert.Single(result);
        Assert.Equal(2, (int)result[0].BaseObject);
    }

    [Fact]
    public void Fold_UnrecognizedOption_NotTreatedAsFilename()
    {
        // Verify that an unknown flag does NOT fall through to the file-read
        // path (which would try to open "--bogus" as a file and emit a
        // "no such file" error with exit 1). The classifier must intercept it
        // first and emit exit 2.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            "Invoke-BashFold --bogus 2>$null; $LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        Assert.Single(result);
        Assert.Equal(2, (int)result[0].BaseObject);
    }
}
