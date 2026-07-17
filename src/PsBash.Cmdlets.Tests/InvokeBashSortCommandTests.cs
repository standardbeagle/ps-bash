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

    private (string[] outLines, string[] errors) RunWithErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        var outLines = result.Select(o =>
            o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
        return (outLines, errs);
    }

    [Fact]
    public void Sort_LongFormReverse_ReversesOrder()
    {
        // --reverse is now parsed as the long form of -r.
        var lines = RunLines("'a','b','c' | Invoke-BashSort --reverse");
        Assert.Equal(new[] { "c", "b", "a" }, lines);
    }

    [Fact]
    public void Sort_LongFormNumeric_OrdersByNumber()
    {
        var lines = RunLines("'10','2','1','20' | Invoke-BashSort --numeric-sort");
        Assert.Equal(new[] { "1", "2", "10", "20" }, lines);
    }

    [Fact]
    public void Sort_LongFormKeyAndFieldSeparator_SortByField()
    {
        // --key / --field-separator are aliases of -k / -t.
        var lines = RunLines(
            "'c:1','a:3','b:2' | Invoke-BashSort --field-separator=: --key=2");
        Assert.Equal(new[] { "c:1", "b:2", "a:3" }, lines);
    }

    [Fact]
    public void Sort_GeneralNumeric_HandlesScientificNotation()
    {
        // -g parses the whole field as a general float, so 1e3 > 50 (which -n,
        // reading only the leading integer, would get wrong: 1 < 50).
        var lines = RunLines("'1e3','50','2e0' | Invoke-BashSort -g");
        Assert.Equal(new[] { "2e0", "50", "1e3" }, lines);
    }

    [Fact]
    public void Sort_UnrecognizedLongOption_BashParityMessage()
    {
        var (_, errs) = RunWithErrors("'a','b' | Invoke-BashSort --bogus");
        Assert.Contains(errs, m => m.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
                                   && m.Contains("--bogus", StringComparison.Ordinal));
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
    public void Sort_UniqueByKey_CollapsesLinesEqualOnKeyDifferingAfter()
    {
        // GNU `sort -u` dedups by the active comparison key, NOT the whole line:
        // two lines equal on the key but differing after it collapse to the FIRST.
        // Oracle: printf '1 a\n1 b\n' | sort -u -k1,1  =>  "1 a" (one line).
        var lines = RunLines("'1 a','1 b' | Invoke-BashSort -u '-k1,1'");
        Assert.Equal(new[] { "1 a" }, lines);
    }

    [Fact]
    public void Sort_UniqueNumeric_TreatsEqualNumbersAsOne()
    {
        // Under -n, "01" and "1" compare equal, so -u keeps only the first of the
        // sorted run. The whole-line last-resort tiebreak orders "01" before "1".
        // Oracle: printf '01\n1\n' | sort -n -u  =>  "01" (one line).
        var lines = RunLines("'01','1' | Invoke-BashSort -n -u");
        Assert.Equal(new[] { "01" }, lines);
    }

    [Fact]
    public void Sort_UniqueNoKey_StillDedupsByFullLine()
    {
        // Control: with no -k/-n the comparison key IS the full line, so plain -u
        // must still dedup by full line — distinct full lines are all kept, only
        // the exact duplicate collapses.
        // Oracle: printf '1 a\n1 b\n1 a\n' | sort -u  =>  "1 a", "1 b".
        var lines = RunLines("'1 a','1 b','1 a' | Invoke-BashSort -u");
        Assert.Equal(new[] { "1 a", "1 b" }, lines);
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
    public void Sort_VersionSort_StableForEqualKeys()
    {
        var lines = RunLines(@"
            @(
                [pscustomobject]@{ BashText = 'v1.0'; Marker = 'first' },
                [pscustomobject]@{ BashText = 'v1.0'; Marker = 'second' },
                [pscustomobject]@{ BashText = 'v0.9'; Marker = 'third' }
            ) | Invoke-BashSort -V | ForEach-Object { $_.Marker }
        ");
        Assert.Equal(new[] { "third", "first", "second" }, lines);
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
    public void Sort_CheckMode_Unordered_WritesGnuStyleDisorderMessage()
    {
        // GNU: `sort -c` on disorder prints
        // "sort: <file>:<line>: disorder: <line>" to stderr (verified against
        // the WSL oracle: `printf 'b\na\n' | sort -c` -> "sort: -:2: disorder: a").
        // Pipeline input has no filename, so the locator is "-".
        var (outLines, errs) = RunWithErrors("'b','a','c' | Invoke-BashSort -c");
        Assert.Empty(outLines);
        // WriteBashError emits both a WriteError record and a Write-BashError
        // psm1-formatted echo (see FileSystemHelpers.WriteBashError) — same
        // dual-emission pattern as Sort_UnrecognizedLongOption_BashParityMessage.
        Assert.Contains(errs, m => m == "sort: -:2: disorder: a");
    }

    [Fact]
    public void Sort_CheckMode_Ordered_WritesNoDisorderMessage()
    {
        var (outLines, errs) = RunWithErrors("'a','b','c' | Invoke-BashSort -c");
        Assert.Empty(outLines);
        Assert.Empty(errs);
    }

    [Fact]
    public void Sort_CheckMode_FileMode_DisorderMessageIncludesFilePath()
    {
        var file = Path.Combine(_tmpDir, "s.txt");
        File.WriteAllText(file, "b\na\n");
        var normalized = file.Replace('\\', '/');
        var (outLines, errs) = RunWithErrors($"Invoke-BashSort -c '{normalized}'");
        Assert.Empty(outLines);
        Assert.Contains(errs, m => m == $"sort: {normalized}:2: disorder: a");
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

    // ---- GNU field-model parity regressions (oracle: WSL `sort`, GNU coreutils 9.4) ----
    // These cover three divergences that the hand-written cases above never
    // exercised because none used leading whitespace, -V with a key, or
    // equal-key tie ordering. Each expected value is the byte output of the
    // `wsl sort ...` command quoted in the comment (Directive 1: oracle first).

    [Fact]
    public void Sort_KeyField_LeadingSpace_NumericField2_GnuFieldSplit()
    {
        // BUG 1: GNU's default field model attaches a field's leading blanks to
        // that field, so "  leading\t9" splits as ["  leading", "\t9"] and -k2
        // is the numeric 9 — NOT "leading" (the old `\s+` split produced an
        // empty leading field, mis-selecting field 2).
        // Oracle: printf '  leading\t9\nzulu\t5\nalpha\t100\n' | sort -n -k2
        //   -> zulu\t5 / "  leading"\t9 / alpha\t100
        var lines = RunLines(
            "\"  leading`t9\", \"zulu`t5\", \"alpha`t100\" | Invoke-BashSort -n '-k2'");
        Assert.Equal(new[] { "zulu\t5", "  leading\t9", "alpha\t100" }, lines);
    }

    [Fact]
    public void Sort_KeyField_LeadingTab_NumericField2_GnuFieldSplit()
    {
        // BUG 1, leading-tab variant.
        // Oracle: printf '\tindent\t9\nzulu\t5\nalpha\t100\n' | sort -n -k2
        //   -> zulu\t5 / "\tindent"\t9 / alpha\t100
        var lines = RunLines(
            "\"`tindent`t9\", \"zulu`t5\", \"alpha`t100\" | Invoke-BashSort -n '-k2'");
        Assert.Equal(new[] { "zulu\t5", "\tindent\t9", "alpha\t100" }, lines);
    }

    [Fact]
    public void Sort_KeyField_LeadingBlanksAreSignificantInLexicalKey()
    {
        // BUG 1 discriminator (no -b): the leading separator blanks ARE part of
        // the lexical key, so "x  bbb" (field2 = "  bbb", two leading spaces)
        // sorts BEFORE "y aaa" (field2 = " aaa") because the extra space
        // (0x20) < 'a'. Proves the key retains leading blanks rather than
        // stripping or collapsing them.
        // Oracle: printf 'x  bbb\ny aaa\n' | sort -k2  -> x  bbb / y aaa
        var lines = RunLines("'x  bbb', 'y aaa' | Invoke-BashSort '-k2'");
        Assert.Equal(new[] { "x  bbb", "y aaa" }, lines);
    }

    [Fact]
    public void Sort_KeyField_BlankIgnore_StripsLeadingBlanksFromKey()
    {
        // BUG 1, -b counterpart of the discriminator: -b strips the field's
        // leading blanks so the key becomes "bbb" / "aaa" and order flips.
        // Oracle: printf 'x  bbb\ny aaa\n' | sort -b -k2  -> y aaa / x  bbb
        var lines = RunLines("'x  bbb', 'y aaa' | Invoke-BashSort -b '-k2'");
        Assert.Equal(new[] { "y aaa", "x  bbb" }, lines);
    }

    [Fact]
    public void Sort_FieldSeparator_EmptyFieldsAreReal_NumericKey()
    {
        // -t splits on the exact char with no blank-collapsing; empty fields are
        // real, so field 3 of "a::3" is "3".
        // Oracle: printf 'a::3\nb::1\nc::2\n' | sort -t: -k3 -n  -> b::1 / c::2 / a::3
        var lines = RunLines("'a::3', 'b::1', 'c::2' | Invoke-BashSort -t ':' -n '-k3'");
        Assert.Equal(new[] { "b::1", "c::2", "a::3" }, lines);
    }

    [Fact]
    public void Sort_VersionSort_HonorsKey_Field2()
    {
        // BUG 2: -V must compare the keyed field, not the whole line.
        // Oracle: printf 'x 1.10.0\ny 1.2.0\nz 1.9.0\n' | sort -V -k2
        //   -> y 1.2.0 / z 1.9.0 / x 1.10.0
        var lines = RunLines(
            "'x 1.10.0', 'y 1.2.0', 'z 1.9.0' | Invoke-BashSort -V '-k2'");
        Assert.Equal(new[] { "y 1.2.0", "z 1.9.0", "x 1.10.0" }, lines);
    }

    [Fact]
    public void Sort_VersionSort_HonorsKey_Field1Range()
    {
        // BUG 2, -k1,1 restricts the version compare to field 1.
        // Oracle: printf '1.10.0 b\n1.2.0 a\n1.9.0 c\n' | sort -V -k1,1
        //   -> 1.2.0 a / 1.9.0 c / 1.10.0 b
        var lines = RunLines(
            "'1.10.0 b', '1.2.0 a', '1.9.0 c' | Invoke-BashSort -V '-k1,1'");
        Assert.Equal(new[] { "1.2.0 a", "1.9.0 c", "1.10.0 b" }, lines);
    }

    [Fact]
    public void Sort_EqualKeys_LastResortWholeLineTieBreak()
    {
        // GNU last-resort: when the key compares equal (both field2 = 5), sort
        // falls back to comparing the ENTIRE line bytewise — "Apple\t5\talpha"
        // (A=0x41) sorts before "apple\t5\tzeta" (a=0x61) despite input order.
        // Oracle: printf 'apple\t5\tzeta\nApple\t5\talpha\n' | sort -n -k2
        //   -> Apple\t5\talpha / apple\t5\tzeta
        var lines = RunLines(
            "\"apple`t5`tzeta\", \"Apple`t5`talpha\" | Invoke-BashSort -n '-k2'");
        Assert.Equal(new[] { "Apple\t5\talpha", "apple\t5\tzeta" }, lines);
    }

    [Fact]
    public void Sort_StableFlag_SuppressesLastResortTieBreak()
    {
        // -s disables the last-resort whole-line tie-break: equal-key lines keep
        // their original input order ("apple" stays before "Apple").
        // Oracle: printf 'apple\t5\tzeta\nApple\t5\talpha\n' | sort -s -n -k2
        //   -> apple\t5\tzeta / Apple\t5\talpha
        var lines = RunLines(
            "\"apple`t5`tzeta\", \"Apple`t5`talpha\" | Invoke-BashSort -s -n '-k2'");
        Assert.Equal(new[] { "apple\t5\tzeta", "Apple\t5\talpha" }, lines);
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
