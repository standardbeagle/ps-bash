using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashNl</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashNlCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashNl</c> function — numbers non-empty lines
/// by default; <c>-ba</c> numbers all lines (including empty).
///
/// Failure-surface axes covered (per Directive 3): empty input, unicode,
/// CRLF, file mode, pipeline mode, missing file, alias resolution,
/// <c>--help</c>, and an injection probe per Directive 12.
/// </summary>
public class InvokeBashNlCommandTests : IDisposable
{
    private readonly string _tmpDir;

    public InvokeBashNlCommandTests()
    {
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-nl-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string[] RunLines(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    [Fact]
    public void Nl_EmptyPipeline_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashNl");
        Assert.Empty(lines);
    }

    [Fact]
    public void Nl_ThreeLineFile_Default_NumbersAllNonEmptyLines()
    {
        var file = Path.Combine(_tmpDir, "three.txt");
        File.WriteAllText(file, "alpha\nbeta\ngamma\n");
        var lines = RunLines($"Invoke-BashNl '{file.Replace("'", "''")}'");
        // Format: 6-col right-aligned + tab + line.
        Assert.Equal(new[] {
            "     1\talpha",
            "     2\tbeta",
            "     3\tgamma",
        }, lines);
    }

    [Fact]
    public void Nl_EmptyLinesMidFile_Default_SkipsNumberingButEmitsBlank()
    {
        var file = Path.Combine(_tmpDir, "blanks.txt");
        File.WriteAllText(file, "one\n\ntwo\n\nthree\n");
        var lines = RunLines($"Invoke-BashNl '{file.Replace("'", "''")}'");
        // Default: empty lines emit a bare empty line (unnumbered); numbering
        // only advances on non-empty lines.
        Assert.Equal(new[] {
            "     1\tone",
            "",
            "     2\ttwo",
            "",
            "     3\tthree",
        }, lines);
    }

    [Fact]
    public void Nl_BaFlag_NumbersAllLinesIncludingEmpty()
    {
        var file = Path.Combine(_tmpDir, "blanks2.txt");
        File.WriteAllText(file, "one\n\ntwo\n");
        var lines = RunLines($"Invoke-BashNl -ba '{file.Replace("'", "''")}'");
        Assert.Equal(new[] {
            "     1\tone",
            "     2\t",
            "     3\ttwo",
        }, lines);
    }

    [Fact]
    public void Nl_PipelineMode_NumbersInput()
    {
        var lines = RunLines("'foo','bar','baz' | Invoke-BashNl");
        Assert.Equal(new[] {
            "     1\tfoo",
            "     2\tbar",
            "     3\tbaz",
        }, lines);
    }

    [Fact]
    public void Nl_PipelineMode_BaIncludesEmpty()
    {
        var lines = RunLines("'foo','','bar' | Invoke-BashNl -ba");
        Assert.Equal(new[] {
            "     1\tfoo",
            "     2\t",
            "     3\tbar",
        }, lines);
    }

    [Fact]
    public void Nl_FileMode_CrlfNormalized()
    {
        var file = Path.Combine(_tmpDir, "crlf.txt");
        File.WriteAllText(file, "one\r\ntwo\r\n");
        var lines = RunLines($"Invoke-BashNl '{file.Replace("'", "''")}'");
        Assert.Equal(new[] {
            "     1\tone",
            "     2\ttwo",
        }, lines);
    }

    [Fact]
    public void Nl_FileMode_Unicode_Preserved()
    {
        var file = Path.Combine(_tmpDir, "uni.txt");
        File.WriteAllText(file, "héllo\nwörld\n", new System.Text.UTF8Encoding(false));
        var lines = RunLines($"Invoke-BashNl '{file.Replace("'", "''")}'");
        Assert.Equal(new[] {
            "     1\théllo",
            "     2\twörld",
        }, lines);
    }

    [Fact]
    public void Nl_FileMode_MissingFile_DoesNotThrow_NoOutput()
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashNl '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Nl_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashNl --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("nl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Nl_AliasResolution_NlWorks()
    {
        // The psm1 module registers `Set-Alias nl Invoke-BashNl`.
        var lines = RunLines("'hello' | nl");
        Assert.Single(lines);
        Assert.Equal("     1\thello", lines[0]);
    }

    [Fact]
    public void Nl_SplitBaForm_NumbersAllLines()
    {
        // Oracle accepts the split form `-b a` (two consecutive tokens).
        var file = Path.Combine(_tmpDir, "split.txt");
        File.WriteAllText(file, "one\n\ntwo\n");
        var lines = RunLines($"Invoke-BashNl -b a '{file.Replace("'", "''")}'");
        Assert.Equal(new[] {
            "     1\tone",
            "     2\t",
            "     3\ttwo",
        }, lines);
    }

    [Fact]
    public void Nl_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand with PowerShell injection
        // chars must reach the file-resolver as a literal path, never as
        // script. Result: "no such file" → no output, no side effect.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var lines = RunLines(
            $"Invoke-BashNl '{probe.Replace("'", "''")}' 2>$null");
        Assert.Empty(lines);
    }
}
