using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Parity for malformed-argument edge cases where GNU coreutils reject with an
/// error + non-zero exit, but ps-bash previously produced silent empty / wrong
/// output. Each case asserts the GNU-matching error message line, the exit code,
/// and that no garbage landed on stdout.
///
/// Oracle: GNU coreutils (cut/tr/sort/seq/sed), verified via WSL. Hand-asserted
/// (M5 cmdlet surface) rather than byte-differential because GNU appends locale-
/// specific "Try '… --help'" lines that ps-bash does not emit.
/// </summary>
public class CoreutilsErrorParityTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public CoreutilsErrorParityTests(SharedPwshFixture fixture) => _fixture = fixture;

    private (string[] Out, string[] Err, int Exit) Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var output = pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        var exitObj = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        int exit = exitObj.Count > 0 && exitObj[0]?.BaseObject is int e ? e : 0;
        var outStr = output.Select(o =>
        {
            var p = o?.Properties["BashText"];
            return p != null ? p.Value?.ToString() ?? "" : o?.ToString() ?? "";
        }).ToArray();
        return (outStr, errs, exit);
    }

    private static void AssertErr(string[] errs, string needle) =>
        Assert.Contains(errs, m => m.Contains(needle, StringComparison.Ordinal));

    [Fact]
    public void Cut_FieldZero_ErrorsNumberedFromOne()
    {
        var (outp, err, exit) = Run("\"a`tb\" | Invoke-BashCut -f0");
        AssertErr(err, "fields are numbered from 1");
        Assert.Equal(1, exit);
        Assert.Empty(outp.Where(s => s.Length > 0));
    }

    [Fact]
    public void Cut_CharZero_ErrorsBytePositionsNumberedFromOne()
    {
        var (_, err, exit) = Run("'abc' | Invoke-BashCut -c0");
        AssertErr(err, "byte/character positions are numbered from 1");
        Assert.Equal(1, exit);
    }

    [Fact]
    public void Cut_DecreasingRange_Errors()
    {
        var (outp, err, exit) = Run("\"a`tb`tc\" | Invoke-BashCut -f3-1");
        AssertErr(err, "invalid decreasing range");
        Assert.Equal(1, exit);
        Assert.Empty(outp.Where(s => s.Length > 0));
    }

    [Fact]
    public void Cut_NormalRange_StillWorks()
    {
        // Guard: the new range validation must not break a valid range.
        var (outp, _, exit) = Run("\"a`tb`tc\" | Invoke-BashCut -f2-3");
        Assert.Equal(0, exit);
        Assert.Contains(outp, s => s.Contains("b") && s.Contains("c"));
    }

    [Fact]
    public void Tr_ReverseRange_Errors()
    {
        var (_, err, exit) = Run("'test' | Invoke-BashTr 'a-A' 'xyz'");
        AssertErr(err, "reverse collating sequence order");
        Assert.Equal(1, exit);
    }

    [Fact]
    public void Tr_NormalRange_StillWorks()
    {
        var (outp, _, exit) = Run("'abc' | Invoke-BashTr 'a-c' 'A-C'");
        Assert.Equal(0, exit);
        Assert.Contains("ABC", outp);
    }

    [Fact]
    public void Sort_EmptyDelimiter_ErrorsExit2()
    {
        var (_, err, exit) = Run("'a:b' | Invoke-BashSort -t ''");
        AssertErr(err, "empty tab");
        Assert.Equal(2, exit);
    }

    [Fact]
    public void Seq_ZeroIncrement_Errors()
    {
        var (outp, err, exit) = Run("Invoke-BashSeq 1 0 5");
        AssertErr(err, "invalid Zero increment value: '0'");
        Assert.Equal(1, exit);
        Assert.Empty(outp.Where(s => s.Length > 0));
    }

    [Fact]
    public void Seq_NormalStep_StillWorks()
    {
        var (outp, _, exit) = Run("Invoke-BashSeq 1 2 5");
        Assert.Equal(0, exit);
        Assert.Equal(new[] { "1", "3", "5" }, outp.Where(s => s.Length > 0).ToArray());
    }

    [Fact]
    public void Sed_EmptyRegex_ErrorsNoPreviousRegex()
    {
        var (outp, err, exit) = Run("'test' | Invoke-BashSed 's//X/'");
        AssertErr(err, "no previous regular expression");
        Assert.Equal(1, exit);
        // Must NOT inject the replacement at every position.
        Assert.DoesNotContain(outp, s => s.Contains("XtX", StringComparison.Ordinal));
    }
}
