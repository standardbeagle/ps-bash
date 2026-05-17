using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashJoin</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashJoinCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashJoin</c> function — relational join of
/// two files on a common key column, matching GNU coreutils <c>join</c>.
/// Flags: <c>-t SEP</c>, <c>-1 N</c>, <c>-2 N</c>, <c>--help</c>.
///
/// Failure-surface axes covered (per Directive 3): empty input,
/// missing operand, missing-file error, custom delimiter, custom key
/// column (file 1 / file 2), no-match no-output, <c>--help</c>, alias
/// resolution, and a Directive-12 injection probe.
/// </summary>
public class InvokeBashJoinCommandTests : IDisposable
{
    private readonly string _tmpDir;

    public InvokeBashJoinCommandTests()
    {
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-join-{Guid.NewGuid():N}".Substring(0, 22));
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

    private static string Q(string s) => s.Replace("'", "''");

    [Fact]
    public void Join_SimpleTwoFile_DefaultKeyColumn_JoinsByFirstField()
    {
        // File 1: "1 a", "2 b"
        // File 2: "1 x", "2 y"
        // Expected: "1 a x", "2 b y"
        var f1 = Path.Combine(_tmpDir, "f1.txt");
        var f2 = Path.Combine(_tmpDir, "f2.txt");
        File.WriteAllText(f1, "1 a\n2 b\n");
        File.WriteAllText(f2, "1 x\n2 y\n");

        var lines = RunLines($"Invoke-BashJoin '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "1 a x", "2 b y" }, lines);
    }

    [Fact]
    public void Join_Dash1_KeyInColumn2OfFile1()
    {
        // File 1: "a 1", "b 2"  (key in col 2)
        // File 2: "1 x", "2 y"  (key in col 1)
        // Expected: "1 a x", "2 b y"
        var f1 = Path.Combine(_tmpDir, "f1.txt");
        var f2 = Path.Combine(_tmpDir, "f2.txt");
        File.WriteAllText(f1, "a 1\nb 2\n");
        File.WriteAllText(f2, "1 x\n2 y\n");

        var lines = RunLines($"Invoke-BashJoin -1 2 '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "1 a x", "2 b y" }, lines);
    }

    [Fact]
    public void Join_CustomDelimiter_Comma()
    {
        var f1 = Path.Combine(_tmpDir, "f1.csv");
        var f2 = Path.Combine(_tmpDir, "f2.csv");
        File.WriteAllText(f1, "1,a\n2,b\n");
        File.WriteAllText(f2, "1,x\n2,y\n");

        var lines = RunLines($"Invoke-BashJoin -t ',' '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "1,a,x", "2,b,y" }, lines);
    }

    [Fact]
    public void Join_NoMatch_NoOutput()
    {
        var f1 = Path.Combine(_tmpDir, "f1.txt");
        var f2 = Path.Combine(_tmpDir, "f2.txt");
        File.WriteAllText(f1, "1 a\n2 b\n");
        File.WriteAllText(f2, "9 x\n8 y\n");

        var lines = RunLines($"Invoke-BashJoin '{Q(f1)}' '{Q(f2)}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Join_MissingFirstFile_ErrorAndNoOutput()
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "nope.txt");
        var f2 = Path.Combine(_tmpDir, "f2.txt");
        File.WriteAllText(f2, "1 x\n");
        var result = pwsh.AddScript(
            $"Invoke-BashJoin '{Q(missing)}' '{Q(f2)}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Join_MissingOperand_OneFile_NoOutput()
    {
        var f1 = Path.Combine(_tmpDir, "f1.txt");
        File.WriteAllText(f1, "1 a\n");
        var lines = RunLines($"Invoke-BashJoin '{Q(f1)}' 2>$null");
        Assert.Empty(lines);
    }

    [Fact]
    public void Join_EmptyFiles_NoOutput()
    {
        var f1 = Path.Combine(_tmpDir, "f1.txt");
        var f2 = Path.Combine(_tmpDir, "f2.txt");
        File.WriteAllText(f1, "");
        File.WriteAllText(f2, "");
        var lines = RunLines($"Invoke-BashJoin '{Q(f1)}' '{Q(f2)}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Join_CrlfFiles_Normalized()
    {
        var f1 = Path.Combine(_tmpDir, "f1.txt");
        var f2 = Path.Combine(_tmpDir, "f2.txt");
        File.WriteAllText(f1, "1 a\r\n2 b\r\n");
        File.WriteAllText(f2, "1 x\r\n2 y\r\n");
        var lines = RunLines($"Invoke-BashJoin '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "1 a x", "2 b y" }, lines);
    }

    [Fact]
    public void Join_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashJoin --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("join", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Join_AliasResolution_JoinWorks()
    {
        // The psm1 module registers `Set-Alias join Invoke-BashJoin`. Because
        // the binary cmdlet loads before psm1 runs, the alias resolves to the
        // cmdlet.
        var f1 = Path.Combine(_tmpDir, "f1.txt");
        var f2 = Path.Combine(_tmpDir, "f2.txt");
        File.WriteAllText(f1, "1 a\n");
        File.WriteAllText(f2, "1 x\n");
        var lines = RunLines($"join '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "1 a x" }, lines);
    }

    [Fact]
    public void Join_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. File operands
        // route through SessionState.Path; no string-concat into a script
        // body. The probe lands as a literal file path that doesn't exist
        // and reaches the missing-file error sink — no script evaluation.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var f2 = Path.Combine(_tmpDir, "f2.txt");
        File.WriteAllText(f2, "1 x\n");
        var lines = RunLines(
            $"Invoke-BashJoin '{Q(probe)}' '{Q(f2)}' 2>$null");
        Assert.Empty(lines);
    }
}
