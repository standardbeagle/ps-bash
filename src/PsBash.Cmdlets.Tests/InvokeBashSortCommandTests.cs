using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashSort</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashSortCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashSort</c> function — GNU coreutils
/// <c>sort</c> with full flag surface (-r -n -u -f -k -t -h -V -M -c -b -d -s).
/// Failure-surface axes (per Directive 3): empty input, unicode, file mode,
/// pipeline mode, alias resolution, missing-file error continuation,
/// value-flags (-k / -t), check-mode exit code, multi-file, and an injection
/// probe per Directive 12 with <c>$(throw 'pwn')</c> as the -k value.
/// </summary>
public class InvokeBashSortCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashSortCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-sort-{Guid.NewGuid():N}".Substring(0, 22));
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
    public void Sort_DefaultLexicographic()
    {
        var lines = RunLines("'banana','apple','cherry' | Invoke-BashSort");
        Assert.Equal(new[] { "apple", "banana", "cherry" }, lines);
    }

    [Fact]
    public void Sort_ReverseFlag_ReversesOrder()
    {
        var lines = RunLines("'a','b','c' | Invoke-BashSort -r");
        Assert.Equal(new[] { "c", "b", "a" }, lines);
    }

    [Fact]
    public void Sort_NumericFlag_OrdersByNumber()
    {
        var lines = RunLines("'10','2','1','20' | Invoke-BashSort -n");
        Assert.Equal(new[] { "1", "2", "10", "20" }, lines);
    }

    [Fact]
    public void Sort_UniqueFlag_DeduplicatesAdjacent()
    {
        var lines = RunLines("'b','a','b','a','c' | Invoke-BashSort -u");
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [Fact]
    public void Sort_FoldCase_IgnoresCase()
    {
        var lines = RunLines("'Banana','apple','Cherry' | Invoke-BashSort -f");
        Assert.Equal(new[] { "apple", "Banana", "Cherry" }, lines);
    }

    [Fact]
    public void Sort_KeyFieldSecond()
    {
        // Sort by second whitespace-separated field.
        var lines = RunLines("'1 c','2 a','3 b' | Invoke-BashSort -k 2");
        Assert.Equal(new[] { "2 a", "3 b", "1 c" }, lines);
    }

    [Fact]
    public void Sort_KeyFieldRangeSingleField()
    {
        // -k 2,2 limits the key to just field 2.
        // Quote the comma value so PowerShell does not array-split it.
        var lines = RunLines("'1 c x','2 a y','3 b z' | Invoke-BashSort -k '2,2'");
        Assert.Equal(new[] { "2 a y", "3 b z", "1 c x" }, lines);
    }

    [Fact]
    public void Sort_FieldSeparatorComma()
    {
        // -t ',' delimiter splits on comma.
        var lines = RunLines("'1,c','2,a','3,b' | Invoke-BashSort -t ',' -k 2");
        Assert.Equal(new[] { "2,a", "3,b", "1,c" }, lines);
    }

    [Fact]
    public void Sort_HumanNumeric_ParsesSuffixes()
    {
        var lines = RunLines("'1K','500','2M','10' | Invoke-BashSort -h");
        Assert.Equal(new[] { "10", "500", "1K", "2M" }, lines);
    }

    [Fact]
    public void Sort_VersionSort_NaturalAlphanumeric()
    {
        var lines = RunLines("'v1.10','v1.2','v1.1','v2.0' | Invoke-BashSort -V");
        Assert.Equal(new[] { "v1.1", "v1.2", "v1.10", "v2.0" }, lines);
    }

    [Fact]
    public void Sort_MonthSort_OrdersMonthNames()
    {
        var lines = RunLines("'Mar','Jan','Feb','Dec' | Invoke-BashSort -M");
        Assert.Equal(new[] { "Jan", "Feb", "Mar", "Dec" }, lines);
    }

    [Fact]
    public void Sort_CheckMode_OrderedReturnsExit0()
    {
        var lines = RunLines("'a','b','c' | Invoke-BashSort -c; $LASTEXITCODE");
        // Only the exit code is emitted (no sort output in -c mode).
        Assert.Single(lines);
        Assert.Equal("0", lines[0]);
    }

    [Fact]
    public void Sort_CheckMode_UnorderedReturnsExit1()
    {
        var lines = RunLines("'b','a','c' | Invoke-BashSort -c; $LASTEXITCODE");
        Assert.Single(lines);
        Assert.Equal("1", lines[0]);
    }

    [Fact]
    public void Sort_PipelineMode_EmptyEmitsNothing()
    {
        var lines = RunLines("Invoke-BashSort");
        Assert.Empty(lines);
    }

    [Fact]
    public void Sort_FileMode_SortsFileContents()
    {
        var path = Path.Combine(_tmpDir, "input.txt").Replace('\\', '/');
        File.WriteAllText(path, "banana\napple\ncherry\n");
        var lines = RunLines($"Invoke-BashSort '{path}'");
        Assert.Equal(new[] { "apple", "banana", "cherry" }, lines);
    }

    [Fact]
    public void Sort_MultiFile_MergesAndSorts()
    {
        var p1 = Path.Combine(_tmpDir, "a.txt").Replace('\\', '/');
        var p2 = Path.Combine(_tmpDir, "b.txt").Replace('\\', '/');
        File.WriteAllText(p1, "x\nm\n");
        File.WriteAllText(p2, "a\nz\n");
        var lines = RunLines($"Invoke-BashSort '{p1}' '{p2}'");
        Assert.Equal(new[] { "a", "m", "x", "z" }, lines);
    }

    [Fact]
    public void Sort_MissingFile_ContinuesWithExitCode1()
    {
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt").Replace('\\', '/');
        var lines = RunLines(
            $"Invoke-BashSort '{missing}' 2>$null; $LASTEXITCODE");
        Assert.Single(lines);
        Assert.Equal("1", lines[0]);
    }

    [Fact]
    public void Sort_Unicode_OrdinalOrdering()
    {
        // CJK / accent characters compare by ordinal codepoint.
        var lines = RunLines("'é','a','z','ñ' | Invoke-BashSort");
        // Ordinal: 'a' < 'z' < 'é' (U+00E9) < 'ñ' (U+00F1) — but ñ=0xF1 > é=0xE9.
        Assert.Equal(new[] { "a", "z", "é", "ñ" }, lines);
    }

    [Fact]
    public void Sort_AliasResolution()
    {
        // The `sort` alias is registered in psm1 and resolves to this cmdlet
        // because the cmdlet class is imported before the psm1 Set-Alias line.
        var lines = RunLines("'b','a','c' | sort");
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [Fact]
    public void Sort_Help_DelegatesToShowBashHelp()
    {
        // --help routes through Show-BashHelp; output is some non-empty text.
        var lines = RunLines("Invoke-BashSort --help");
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void Sort_InjectionProbe_KValueWithDollarThrow_DoesNotEval()
    {
        // Directive 12: a -k value containing $(throw 'pwn') is never re-parsed
        // as PowerShell. The oracle regex (^(\d+)(?:\.(\d+))?([nrRbB]*)?$) does
        // not match this literal string, so the spec falls through to all
        // zeros and the sort proceeds without error or evaluation.
        // Wrapped in a try/catch script so a throw would propagate as a test
        // failure with a non-empty error stream rather than a passing assertion.
        var lines = RunLines(@"
            try {
                $r = 'b','a','c' | Invoke-BashSort -k '$(throw ''pwn'')'
                ($r | ForEach-Object { $_.BashText }) -join '|'
            } catch {
                'CAUGHT:' + $_.Exception.Message
            }
        ");
        Assert.Single(lines);
        // Must NOT start with CAUGHT: (no exception); the literal "pwn" must
        // not appear anywhere — i.e. the throw never fired.
        Assert.DoesNotContain("CAUGHT", lines[0]);
        Assert.DoesNotContain("pwn", lines[0]);
    }
}
