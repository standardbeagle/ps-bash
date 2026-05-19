using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashTac</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashTacCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashTac</c> function — reverses the list of
/// input lines. Flags: <c>-s SEP</c> / <c>--separator=SEP</c> (custom record
/// separator) plus <c>--help</c>.
///
/// Failure-surface axes covered (per Directive 3): empty input,
/// unicode, file mode + pipeline mode + alias resolution, missing-file
/// error continuation, custom separator (<c>-s</c> and <c>--separator=</c>),
/// <c>--help</c>, and a quoting/injection probe per Directive 12.
/// </summary>
public class InvokeBashTacCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashTacCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-tac-{Guid.NewGuid():N}".Substring(0, 22));
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
    public void Tac_EmptyPipeline_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashTac");
        Assert.Empty(lines);
    }

    [Fact]
    public void Tac_PipelineMultipleItems_ReverseOrder()
    {
        var lines = RunLines("'one','two','three' | Invoke-BashTac");
        Assert.Equal(new[] { "three", "two", "one" }, lines);
    }

    [Fact]
    public void Tac_PipelineMultilineItem_SplitsThenReverses()
    {
        // A single multi-line BashText item is split into its lines, then the
        // line list is reversed — matching the psm1 oracle.
        var lines = RunLines("\"a`nb`nc\" | Invoke-BashTac");
        Assert.Equal(new[] { "c", "b", "a" }, lines);
    }

    [Fact]
    public void Tac_FileMode_ThreeLines_Reversed()
    {
        var file = Path.Combine(_tmpDir, "three.txt");
        File.WriteAllText(file, "line1\nline2\nline3\n");
        var lines = RunLines($"Invoke-BashTac '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "line3", "line2", "line1" }, lines);
    }

    [Fact]
    public void Tac_FileMode_CrlfNormalized()
    {
        var file = Path.Combine(_tmpDir, "crlf.txt");
        File.WriteAllText(file, "one\r\ntwo\r\n");
        var lines = RunLines($"Invoke-BashTac '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "two", "one" }, lines);
    }

    [Fact]
    public void Tac_FileMode_Unicode_NonAscii()
    {
        var file = Path.Combine(_tmpDir, "uni.txt");
        File.WriteAllText(file, "héllo\nwörld\n", new System.Text.UTF8Encoding(false));
        var lines = RunLines($"Invoke-BashTac '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "wörld", "héllo" }, lines);
    }

    [Fact]
    public void Tac_FileMode_EmptyFile_EmitsNothing()
    {
        var file = Path.Combine(_tmpDir, "empty.txt");
        File.WriteAllText(file, "");
        var lines = RunLines($"Invoke-BashTac '{file.Replace("'", "''")}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Tac_SeparatorFlag_DashS_ReversesChunks()
    {
        // Oracle: $lines joined by `n then split on separator, chunks
        // reversed. For pipeline input "A:B:C", joined is "A:B:C", split on
        // ":" → ["A","B","C"], reversed → ["C","B","A"].
        var lines = RunLines("'A:B:C' | Invoke-BashTac -s ':'");
        Assert.Equal(new[] { "C", "B", "A" }, lines);
    }

    [Fact]
    public void Tac_SeparatorFlag_LongForm_ReversesChunks()
    {
        var lines = RunLines("'X|Y|Z' | Invoke-BashTac --separator=`|");
        Assert.Equal(new[] { "Z", "Y", "X" }, lines);
    }

    [Fact]
    public void Tac_FileMode_MissingFile_DoesNotThrow_NoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashTac '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Tac_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashTac --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("tac", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tac_AliasResolution_TacWorks()
    {
        // The psm1 module registers `Set-Alias tac Invoke-BashTac`. Because
        // the binary cmdlet loads before psm1 runs, the alias resolves to the
        // cmdlet.
        var lines = RunLines("'a','b','c' | tac");
        Assert.Equal(new[] { "c", "b", "a" }, lines);
    }

    [Fact]
    public void Tac_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. File operands
        // route through SessionState.Path, never via string-concat into a
        // script body.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var lines = RunLines(
            $"Invoke-BashTac '{probe.Replace("'", "''")}' 2>$null");
        // No output. If the probe had been evaluated as script, the test
        // would have thrown or 'pwned' would appear in the output.
        Assert.Empty(lines);
    }
}
