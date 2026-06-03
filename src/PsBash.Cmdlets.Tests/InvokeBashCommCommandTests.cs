using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashComm</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashCommCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashComm</c> function — a two-pointer walk over
/// two sorted files emitting 3-column tab-prefixed output, with <c>-1</c> /
/// <c>-2</c> / <c>-3</c> digit flags suppressing the corresponding columns.
///
/// Failure-surface axes covered (per Directive 3): empty input,
/// missing-operand error, missing-file error, unicode (non-ASCII), CRLF input,
/// digit-bundle flag parsing (-1, -2, -3, -12, -123), alias resolution,
/// <c>--help</c>, and a quoting/injection probe per Directive 12.
/// </summary>
public class InvokeBashCommCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashCommCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-comm-{Guid.NewGuid():N}".Substring(0, 23));
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

    private string WriteFile(string name, string content)
    {
        var p = Path.Combine(_tmpDir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static string Q(string p) => p.Replace("'", "''");

    [Fact]
    public void Comm_DisjointFiles_NoOverlap_EmitsCol1AndCol2()
    {
        // file1: a, c     file2: b, d
        // Expected (default):
        //   a            (col 1, no prefix)
        //   \tb          (col 2, one tab)
        //   c            (col 1)
        //   \td          (col 2)
        var f1 = WriteFile("a.txt", "a\nc\n");
        var f2 = WriteFile("b.txt", "b\nd\n");
        var lines = RunLines($"Invoke-BashComm '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "a", "\tb", "c", "\td" }, lines);
    }

    [Fact]
    public void Comm_OverlappingFiles_EmitsAllThreeColumns()
    {
        // file1: a, b, c, e   file2: b, c, d
        // Walk:
        //  a vs b -> a   (col 1)
        //  b == b -> \t\tb (col 3)
        //  c == c -> \t\tc (col 3)
        //  e vs d -> \td (col 2)
        //  e (tail of file1) -> e (col 1)
        var f1 = WriteFile("a.txt", "a\nb\nc\ne\n");
        var f2 = WriteFile("b.txt", "b\nc\nd\n");
        var lines = RunLines($"Invoke-BashComm '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "a", "\t\tb", "\t\tc", "\td", "e" }, lines);
    }

    [Fact]
    public void Comm_Suppress1_DropsCol1_ShiftsCol2AndCol3()
    {
        // -1 suppresses col 1, also removes its tab from col 2/col 3 prefix.
        var f1 = WriteFile("a.txt", "a\nb\n");
        var f2 = WriteFile("b.txt", "b\nc\n");
        // Walk: a vs b -> col1 (suppressed); b==b -> col3 (one tab now); c -> col2 (no tab now).
        var lines = RunLines($"Invoke-BashComm -1 '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "\tb", "c" }, lines);
    }

    [Fact]
    public void Comm_Suppress2_DropsCol2()
    {
        var f1 = WriteFile("a.txt", "a\nb\n");
        var f2 = WriteFile("b.txt", "b\nc\n");
        // Walk: a -> col1 ("a"); b==b -> col3 (one tab — col 2's tab dropped); c -> col2 (suppressed).
        var lines = RunLines($"Invoke-BashComm -2 '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "a", "\tb" }, lines);
    }

    [Fact]
    public void Comm_Suppress3_DropsCommonLines()
    {
        var f1 = WriteFile("a.txt", "a\nb\n");
        var f2 = WriteFile("b.txt", "b\nc\n");
        // Walk: a (col1); b==b -> col3 suppressed; c -> col2.
        var lines = RunLines($"Invoke-BashComm -3 '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "a", "\tc" }, lines);
    }

    [Fact]
    public void Comm_Suppress12_Bundled_ShowsOnlyCol3()
    {
        var f1 = WriteFile("a.txt", "a\nb\nc\n");
        var f2 = WriteFile("b.txt", "b\nc\nd\n");
        // -12 suppresses col 1 and col 2; col 3 has prefix "" (both tabs removed).
        var lines = RunLines($"Invoke-BashComm -12 '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "b", "c" }, lines);
    }

    [Fact]
    public void Comm_Suppress123_AllColumns_EmitsNothing()
    {
        var f1 = WriteFile("a.txt", "a\nb\n");
        var f2 = WriteFile("b.txt", "b\nc\n");
        var lines = RunLines($"Invoke-BashComm -123 '{Q(f1)}' '{Q(f2)}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Comm_BothFilesEmpty_EmitsNothing()
    {
        var f1 = WriteFile("a.txt", "");
        var f2 = WriteFile("b.txt", "");
        var lines = RunLines($"Invoke-BashComm '{Q(f1)}' '{Q(f2)}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Comm_OneEmpty_OtherHasLines_EmitsCol2()
    {
        var f1 = WriteFile("a.txt", "");
        var f2 = WriteFile("b.txt", "x\ny\n");
        var lines = RunLines($"Invoke-BashComm '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "\tx", "\ty" }, lines);
    }

    [Fact]
    public void Comm_MissingOperand_Error_NoOutput()
    {
        var f1 = WriteFile("a.txt", "a\n");
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript($"Invoke-BashComm '{Q(f1)}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Comm_MissingFile_Error_NoOutput()
    {
        var f1 = WriteFile("a.txt", "a\n");
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt");
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"Invoke-BashComm '{Q(f1)}' '{Q(missing)}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Comm_CrlfNormalized()
    {
        var f1 = WriteFile("a.txt", "a\r\nb\r\n");
        var f2 = WriteFile("b.txt", "a\r\nc\r\n");
        var lines = RunLines($"Invoke-BashComm '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "\t\ta", "b", "\tc" }, lines);
    }

    [Fact]
    public void Comm_Unicode_NonAscii_OrdinalCompare()
    {
        // Ordinal compare on UTF-16: "é" (U+00E9) sorts after ASCII letters.
        var f1 = WriteFile("a.txt", "a\né\n");
        var f2 = WriteFile("b.txt", "b\né\n");
        var lines = RunLines($"Invoke-BashComm '{Q(f1)}' '{Q(f2)}'");
        // a < b < é. Walk:
        //   a vs b -> col1 "a"
        //   é vs b -> col2 "\tb"
        //   é == é -> col3 "\t\té"
        Assert.Equal(new[] { "a", "\tb", "\t\té" }, lines);
    }

    [Fact]
    public void Comm_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashComm --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("comm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Comm_AliasResolution_CommWorks()
    {
        var f1 = WriteFile("a.txt", "x\n");
        var f2 = WriteFile("b.txt", "y\n");
        var lines = RunLines($"comm '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "x", "\ty" }, lines);
    }

    [Fact]
    public void Comm_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. Operands flow
        // through SessionState.Path, never via string-concat into a script
        // body. A non-existent file whose name contains injection chars must
        // hit the bash-style "no such file" path with no script side effect.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var f1 = WriteFile("a.txt", "a\n");
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"Invoke-BashComm '{Q(f1)}' '{Q(probe)}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Comm_IdenticalFiles_AllLinesAreCol3()
    {
        var f1 = WriteFile("a.txt", "a\nb\nc\n");
        var f2 = WriteFile("b.txt", "a\nb\nc\n");
        var lines = RunLines($"Invoke-BashComm '{Q(f1)}' '{Q(f2)}'");
        Assert.Equal(new[] { "\t\ta", "\t\tb", "\t\tc" }, lines);
    }
}
