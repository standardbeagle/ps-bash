using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashExpand</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashExpandCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashExpand</c> function — converts tabs to
/// spaces with uniform tab stops every <c>-t N</c> columns (default 8).
/// Per Directive 1, the oracle is the psm1 function; the oracle does NOT
/// support multi-stop tab lists (<c>-t 4,8,12</c>) — that form would throw
/// in both the psm1 oracle (via <c>[int]"4,8,12"</c>) and in this cmdlet.
///
/// Failure-surface axes covered (per Directive 3): empty pipeline, default
/// 8-column tab stops, <c>-t N</c> uniform tab stops, joined <c>-tN</c>
/// form, <c>--tabs=N</c> long form, pipeline mode, file mode (CRLF
/// normalized), no-tabs passthrough, unicode (non-ASCII chars), missing
/// file (Directive 7 negative), <c>--help</c>, alias resolution, and a
/// quoting/injection probe per Directive 12.
/// </summary>
public class InvokeBashExpandCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashExpandCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-expand-{Guid.NewGuid():N}".Substring(0, 25));
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
    public void Expand_EmptyPipeline_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashExpand");
        Assert.Empty(lines);
    }

    [Fact]
    public void Expand_DefaultTabWidth_ReplacesTabWithEightSpaces()
    {
        // A single tab at col 0 -> 8 spaces (next stop at 8).
        var lines = RunLines("\"`thello\" | Invoke-BashExpand");
        Assert.Single(lines);
        Assert.Equal(new string(' ', 8) + "hello", lines[0]);
    }

    [Fact]
    public void Expand_DefaultTabWidth_PartialColumnAdvances()
    {
        // "ab\tc" -> "ab" (col 2), tab fills to next stop (col 8) -> 6 spaces.
        var lines = RunLines("\"ab`tc\" | Invoke-BashExpand");
        Assert.Single(lines);
        Assert.Equal("ab" + new string(' ', 6) + "c", lines[0]);
    }

    [Fact]
    public void Expand_TabWidthFour_SeparateFlag()
    {
        // "a\tb" with -t 4 -> "a" + 3 spaces + "b"
        var lines = RunLines("\"a`tb\" | Invoke-BashExpand -t 4");
        Assert.Single(lines);
        Assert.Equal("a   b", lines[0]);
    }

    [Fact]
    public void Expand_TabWidthFour_JoinedFlag()
    {
        // -t4 joined form
        var lines = RunLines("\"a`tb\" | Invoke-BashExpand -t4");
        Assert.Single(lines);
        Assert.Equal("a   b", lines[0]);
    }

    [Fact]
    public void Expand_LongTabsFlag()
    {
        // --tabs=4 long form
        var lines = RunLines("\"a`tb\" | Invoke-BashExpand --tabs=4");
        Assert.Single(lines);
        Assert.Equal("a   b", lines[0]);
    }

    [Fact]
    public void Expand_MultipleTabs_TrackColumnAcrossStops()
    {
        // "\t\t" with -t 4 -> 4 + 4 = 8 spaces
        var lines = RunLines("\"`t`t\" | Invoke-BashExpand -t 4");
        Assert.Single(lines);
        Assert.Equal(new string(' ', 8), lines[0]);
    }

    [Fact]
    public void Expand_NoTabs_LinePassesThroughUnchanged()
    {
        var lines = RunLines("'plain text' | Invoke-BashExpand");
        Assert.Single(lines);
        Assert.Equal("plain text", lines[0]);
    }

    [Fact]
    public void Expand_MultiLinePipelineItem_SplitsAndExpands()
    {
        // Multi-line BashText item is split into its lines; each is expanded.
        var lines = RunLines("\"a`tb`nc`td\" | Invoke-BashExpand -t 4");
        Assert.Equal(new[] { "a   b", "c   d" }, lines);
    }

    [Fact]
    public void Expand_FileMode_AsciiContent_ExpandsEachLine()
    {
        var file = Path.Combine(_tmpDir, "ascii.txt");
        File.WriteAllText(file, "a\tb\nc\td\n");
        var lines = RunLines($"Invoke-BashExpand -t 4 '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "a   b", "c   d" }, lines);
    }

    [Fact]
    public void Expand_FileMode_CrlfNormalized()
    {
        var file = Path.Combine(_tmpDir, "crlf.txt");
        File.WriteAllText(file, "a\tb\r\nc\td\r\n");
        var lines = RunLines($"Invoke-BashExpand -t 4 '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "a   b", "c   d" }, lines);
    }

    [Fact]
    public void Expand_FileMode_Unicode_NonAscii()
    {
        // Each char advances col by 1 (matches psm1 oracle's .ToCharArray
        // loop). "hé\tllo" -> col 2 after "hé", tab to col 8 (with default 8),
        // 6 spaces between "hé" and "llo".
        var file = Path.Combine(_tmpDir, "uni.txt");
        File.WriteAllText(file, "hé\tllo\n", new System.Text.UTF8Encoding(false));
        var lines = RunLines($"Invoke-BashExpand '{file.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.Equal("hé" + new string(' ', 6) + "llo", lines[0]);
    }

    [Fact]
    public void Expand_FileMode_MissingFile_NoThrow_NoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashExpand '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Expand_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashExpand --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("expand", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Expand_AliasResolution_ExpandWorks()
    {
        // The psm1 module registers `Set-Alias expand Invoke-BashExpand`.
        var lines = RunLines("\"a`tb\" | expand -t 4");
        Assert.Single(lines);
        Assert.Equal("a   b", lines[0]);
    }

    [Fact]
    public void Expand_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. The cmdlet routes
        // file operands through SessionState.Path; an operand whose name
        // contains `; $(throw 'x')` is treated as a literal path -> "no such
        // file" -> no script side effect.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var lines = RunLines(
            $"Invoke-BashExpand '{probe.Replace("'", "''")}' 2>$null");
        Assert.Empty(lines);
    }

    [Fact]
    public void Expand_ValidButUnsupported_FirstOnly_ExitCode2()
    {
        // --first-only is a recognized GNU expand option but not implemented
        // by ps-bash → "option recognized but not supported", exit 2.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            "Invoke-BashExpand --first-only 2>$null; $LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        Assert.Single(result);
        Assert.Equal(2, (int)result[0].BaseObject);
    }

    [Fact]
    public void Expand_UnrecognizedLongOption_ExitCode2()
    {
        // Completely unknown option → bash-parity "unrecognized option" error,
        // LASTEXITCODE=2, no stdout.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            "Invoke-BashExpand --bogus 2>$null; $LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        Assert.Single(result);
        Assert.Equal(2, (int)result[0].BaseObject);
    }
}
