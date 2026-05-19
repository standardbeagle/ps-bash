using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashDiff</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashDiffCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashDiff</c> function — LCS-table line diff
/// emitting normal / unified / context format output.
///
/// Failure-surface axes covered (per Directive 3): empty input,
/// missing-operand error, missing-file error, unicode, CRLF input, alias
/// resolution, <c>--help</c>, plus a quoting/injection probe per Directive 12.
/// </summary>
public class InvokeBashDiffCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashDiffCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-diff-{Guid.NewGuid():N}".Substring(0, 23));
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

    private int RunExitCode(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script + " | Out-Null").Invoke();
        pwsh.Commands.Clear();
        var r = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        return (r.FirstOrDefault()?.BaseObject is int i) ? i : 0;
    }

    private string WriteFile(string name, string content)
    {
        var p = Path.Combine(_tmpDir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static string Q(string p) => p.Replace("'", "''");

    [Fact]
    public void Diff_IdenticalFiles_NoOutput()
    {
        var f1 = WriteFile("a.txt", "a\nb\nc\n");
        var f2 = WriteFile("b.txt", "a\nb\nc\n");
        var lines = RunLines($"Invoke-BashDiff '{Q(f1)}' '{Q(f2)}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Diff_IdenticalFiles_ExitCodeZero()
    {
        var f1 = WriteFile("a.txt", "a\nb\n");
        var f2 = WriteFile("b.txt", "a\nb\n");
        int rc = RunExitCode($"Invoke-BashDiff '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(0, rc);
    }

    [Fact]
    public void Diff_OneLineChange_NormalFormat()
    {
        var f1 = WriteFile("a.txt", "a\nb\nc\n");
        var f2 = WriteFile("b.txt", "a\nB\nc\n");
        var lines = RunLines($"Invoke-BashDiff '{Q(f1)}' '{Q(f2)}'");
        // Line 2 changed: "2c2", "< b", "---", "> B"
        Assert.Contains("2c2", lines);
        Assert.Contains("< b", lines);
        Assert.Contains("---", lines);
        Assert.Contains("> B", lines);
    }

    [Fact]
    public void Diff_DifferingFiles_ExitCodeOne()
    {
        var f1 = WriteFile("a.txt", "a\nb\n");
        var f2 = WriteFile("b.txt", "a\nc\n");
        int rc = RunExitCode($"Invoke-BashDiff '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(1, rc);
    }

    [Fact]
    public void Diff_MultiHunk_NormalFormat()
    {
        // Two separated changes
        var f1 = WriteFile("a.txt", "a\nb\nc\nd\ne\n");
        var f2 = WriteFile("b.txt", "A\nb\nc\nd\nE\n");
        var lines = RunLines($"Invoke-BashDiff '{Q(f1)}' '{Q(f2)}'");
        // Expect 1c1 and 5c5 markers
        Assert.Contains("1c1", lines);
        Assert.Contains("5c5", lines);
    }

    [Fact]
    public void Diff_UnifiedFormat_HasHeadersAndHunk()
    {
        var f1 = WriteFile("a.txt", "a\nb\nc\n");
        var f2 = WriteFile("b.txt", "a\nB\nc\n");
        var lines = RunLines($"Invoke-BashDiff -u '{Q(f1)}' '{Q(f2)}'");
        Assert.Contains(lines, l => l.StartsWith("--- "));
        Assert.Contains(lines, l => l.StartsWith("+++ "));
        Assert.Contains(lines, l => l.StartsWith("@@ "));
        Assert.Contains("-b", lines);
        Assert.Contains("+B", lines);
    }

    [Fact]
    public void Diff_IgnoreCase_TreatsLowerAndUpperAsEqual()
    {
        var f1 = WriteFile("a.txt", "Hello\nWorld\n");
        var f2 = WriteFile("b.txt", "hello\nworld\n");
        // Without -i: differs. With -i: identical -> no output.
        var withCase = RunLines($"Invoke-BashDiff '{Q(f1)}' '{Q(f2)}'");
        Assert.NotEmpty(withCase);
        var noCase = RunLines($"Invoke-BashDiff -i '{Q(f1)}' '{Q(f2)}'");
        Assert.Empty(noCase);
    }

    [Fact]
    public void Diff_IgnoreAllSpace_TreatsWhitespaceAsEqual()
    {
        var f1 = WriteFile("a.txt", "hello world\n");
        var f2 = WriteFile("b.txt", "helloworld\n");
        var withSpace = RunLines($"Invoke-BashDiff '{Q(f1)}' '{Q(f2)}'");
        Assert.NotEmpty(withSpace);
        var noSpace = RunLines($"Invoke-BashDiff -w '{Q(f1)}' '{Q(f2)}'");
        Assert.Empty(noSpace);
    }

    [Fact]
    public void Diff_MissingFile_NoOutput()
    {
        var f1 = WriteFile("a.txt", "a\n");
        var missing = Path.Combine(_tmpDir, "no-such-file.txt");
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript($"Invoke-BashDiff '{Q(f1)}' '{Q(missing)}' 2>$null").Invoke();
        Assert.Empty(result);
    }

    [Fact]
    public void Diff_MissingOperand_NoOutput()
    {
        var f1 = WriteFile("a.txt", "a\n");
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript($"Invoke-BashDiff '{Q(f1)}' 2>$null").Invoke();
        Assert.Empty(result);
    }

    [Fact]
    public void Diff_CrlfNormalized()
    {
        var f1 = WriteFile("a.txt", "a\r\nb\r\n");
        var f2 = WriteFile("b.txt", "a\r\nb\r\n");
        var lines = RunLines($"Invoke-BashDiff '{Q(f1)}' '{Q(f2)}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Diff_Unicode_DiffersByCodePoint()
    {
        var f1 = WriteFile("a.txt", "café\n");
        var f2 = WriteFile("b.txt", "cafe\n");
        var lines = RunLines($"Invoke-BashDiff '{Q(f1)}' '{Q(f2)}'");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("café"));
        Assert.Contains(lines, l => l.Contains("cafe"));
    }

    [Fact]
    public void Diff_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashDiff --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("diff", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Diff_AliasResolution_DiffWorks()
    {
        var f1 = WriteFile("a.txt", "x\n");
        var f2 = WriteFile("b.txt", "y\n");
        var lines = RunLines($"diff '{Q(f1)}' '{Q(f2)}'");
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void Diff_BriefMode_ReportsFilesDiffer()
    {
        var f1 = WriteFile("a.txt", "a\n");
        var f2 = WriteFile("b.txt", "b\n");
        var lines = RunLines($"Invoke-BashDiff -q '{Q(f1)}' '{Q(f2)}'");
        Assert.Single(lines);
        Assert.Contains("differ", lines[0]);
    }

    [Fact]
    public void Diff_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. A non-existent
        // file whose name contains injection chars hits the bash-style
        // "no such file" path with no script side effect.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var f1 = WriteFile("a.txt", "a\n");
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript($"Invoke-BashDiff '{Q(f1)}' '{Q(probe)}' 2>$null").Invoke();
        Assert.Empty(result);
    }
}
