using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashColumn</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashColumnCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashColumn</c> function — passthrough by
/// default; <c>-t</c> enables column alignment with whitespace (or
/// <c>-s SEP</c>) as the input separator. Output column separator is
/// hard-coded to two spaces.
///
/// Failure-surface axes covered (per Directive 3): empty input, missing
/// file, unicode input, CRLF input, pipeline mode, file mode, custom
/// separator (separated + joined form), <c>--help</c>, alias resolution,
/// quoting/injection probe (Directive 12).
/// </summary>
public class InvokeBashColumnCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashColumnCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-col-{Guid.NewGuid():N}".Substring(0, 22));
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
    public void Column_PlainMode_PassesLinesThrough()
    {
        var f = WriteFile("a.txt", "one\ntwo\nthree\n");
        var lines = RunLines($"Invoke-BashColumn '{Q(f)}'");
        Assert.Equal(new[] { "one", "two", "three" }, lines);
    }

    [Fact]
    public void Column_TableMode_AlignsColumnsByMaxWidth()
    {
        // Three rows, three cols. Per-col max widths:
        //   col0 = max("a","longest","x") = 7
        //   col1 = max("b","mid","y")     = 3
        //   col2 (last; not padded)
        // Output: col0.PadRight(7) + "  " + col1.PadRight(3) + "  " + col2
        var f = WriteFile("t.txt", "a b c\nlongest mid end\nx y z\n");
        var lines = RunLines($"Invoke-BashColumn -t '{Q(f)}'");
        Assert.Equal(new[]
        {
            "a        b    c",
            "longest  mid  end",
            "x        y    z",
        }, lines);
    }

    [Fact]
    public void Column_TableMode_CustomInputSeparator_Separated()
    {
        // -t -s ',' — split each line on comma instead of whitespace.
        // Rows: ("a","beta"), ("ccc","d")
        // Widths: col0=3, col1=4 (col1 is last → not padded).
        var f = WriteFile("t.csv", "a,beta\nccc,d\n");
        var lines = RunLines($"Invoke-BashColumn -t -s ',' '{Q(f)}'");
        Assert.Equal(new[]
        {
            "a    beta",
            "ccc  d",
        }, lines);
    }

    [Fact]
    public void Column_TableMode_CustomInputSeparator_Joined()
    {
        // -s, joined form (oracle: ^-s(.)$).
        var f = WriteFile("t.csv", "a,beta\nccc,d\n");
        // Quote `-s,` so PowerShell does not interpret the comma as an array op.
        var lines = RunLines($"Invoke-BashColumn -t '-s,' '{Q(f)}'");
        Assert.Equal(new[]
        {
            "a    beta",
            "ccc  d",
        }, lines);
    }

    [Fact]
    public void Column_PipelineMode_PlainPassthrough()
    {
        var lines = RunLines(
            "@('alpha','bravo','charlie') | ForEach-Object { New-BashObject -BashText $_ } | Invoke-BashColumn");
        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, lines);
    }

    [Fact]
    public void Column_PipelineMode_TableMode_AlignsColumns()
    {
        var lines = RunLines(
            "@('a b','longer c') | ForEach-Object { New-BashObject -BashText $_ } | Invoke-BashColumn -t");
        // col0 max = "longer" (6), col1 = last (no pad).
        Assert.Equal(new[]
        {
            "a       b",
            "longer  c",
        }, lines);
    }

    [Fact]
    public void Column_EmptyPipeline_NoOutput()
    {
        var lines = RunLines("@() | Invoke-BashColumn -t");
        Assert.Empty(lines);
    }

    [Fact]
    public void Column_EmptyFile_NoOutput()
    {
        var f = WriteFile("empty.txt", "");
        var lines = RunLines($"Invoke-BashColumn -t '{Q(f)}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Column_MissingFile_Error_NoOutputForThatFile()
    {
        var missing = Path.Combine(_tmpDir, "no-such-file.txt");
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript($"Invoke-BashColumn '{Q(missing)}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Column_CrlfNormalized()
    {
        var f = WriteFile("t.txt", "a b\r\nlong c\r\n");
        var lines = RunLines($"Invoke-BashColumn -t '{Q(f)}'");
        Assert.Equal(new[]
        {
            "a     b",
            "long  c",
        }, lines);
    }

    [Fact]
    public void Column_Unicode_NonAscii()
    {
        // "héllo" is 5 .NET chars. The unicode row is the longest of col0.
        var f = WriteFile("t.txt", "a b\nhéllo world\n");
        var lines = RunLines($"Invoke-BashColumn -t '{Q(f)}'");
        Assert.Equal(new[]
        {
            "a      b",
            "héllo  world",
        }, lines);
    }

    [Fact]
    public void Column_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashColumn --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("column", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Column_AliasResolution_ColumnWorks()
    {
        var f = WriteFile("t.txt", "a b\nccc d\n");
        var lines = RunLines($"column -t '{Q(f)}'");
        Assert.Equal(new[]
        {
            "a    b",
            "ccc  d",
        }, lines);
    }

    [Fact]
    public void Column_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. The operand flows
        // through SessionState.Path, never via string-concat into a script
        // body. A non-existent file whose name contains injection chars must
        // hit the bash-style "no such file" path with no script side effect.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript($"Invoke-BashColumn '{Q(probe)}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Column_TableMode_RagggedRows_LastColumnNotPadded()
    {
        // Row 1 has three fields; row 2 has two. Per-col widths:
        //   col0 = 5 ("hello"), col1 = 5 ("there"), col2 last (no pad).
        // For row 2, "x" is in col0; "y" is col1 (last column for that row → no pad).
        var f = WriteFile("t.txt", "hello there world\nx y\n");
        var lines = RunLines($"Invoke-BashColumn -t '{Q(f)}'");
        Assert.Equal(new[]
        {
            "hello  there  world",
            "x      y",
        }, lines);
    }
}
