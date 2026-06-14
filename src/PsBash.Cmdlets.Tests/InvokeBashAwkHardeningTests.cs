using System.Diagnostics;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Resource-safety / robustness regressions for the AWK interpreter port. Each
/// case is an input that, before hardening, either crashed the host runspace
/// with an unhandled .NET exception, overflowed the native stack, or allocated
/// unbounded memory (DoS). The contract here is uniform: a hostile program must
/// produce a bounded awk error (or bounded output) — never a crash, hang, or OOM.
///
/// Oracle: hand-asserted (these are failure modes, not byte-parity behaviors;
/// real awk would itself OOM / hang on several of them). See QA rubric Directive
/// 12 (security probes) and 13 (known-bad memory).
/// </summary>
public class InvokeBashAwkHardeningTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashAwkHardeningTests(SharedPwshFixture fixture) => _fixture = fixture;

    private string[] RunBashText(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result
            .Select(o =>
            {
                var prop = o?.Properties["BashText"];
                return (prop != null ? prop.Value?.ToString() ?? "" : o?.ToString() ?? "")
                    .TrimEnd('\n', '\r');
            })
            .ToArray();
    }

    private string[] RunErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        return errs;
    }

    [Fact]
    public void Awk_HugeFieldIndexAssignment_ErrorsInsteadOfOom()
    {
        // (int)1e30 saturates to int.MaxValue; the old code then tried to append
        // ~2.1 billion empty field strings.
        var sw = Stopwatch.StartNew();
        var errs = RunErrors("Invoke-BashAwk 'BEGIN{$(1e30)=\"x\"}'");
        sw.Stop();

        Assert.NotEmpty(errs);
        Assert.Contains(errs, m => m.Contains("exceeds the maximum", StringComparison.OrdinalIgnoreCase));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"should fail fast, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Awk_HugeNfAssignment_ErrorsInsteadOfOom()
    {
        var errs = RunErrors("Invoke-BashAwk 'BEGIN{NF=1e30}'");

        Assert.NotEmpty(errs);
        Assert.Contains(errs, m => m.Contains("exceeds the maximum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Awk_InvalidDynamicRegex_ErrorsInsteadOfCrashing()
    {
        // "(" used as a dynamic regex is unbalanced; the old GetRegex let
        // RegexParseException escape and tear down the runspace.
        var errs = RunErrors("Invoke-BashAwk 'BEGIN{ if (\"x\" ~ \"(\") print \"m\" }'");

        Assert.NotEmpty(errs);
        Assert.Contains(errs, m => m.Contains("invalid regular expression", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Awk_CatastrophicBacktrackingRegex_TimesOutInsteadOfHanging()
    {
        // Classic ReDoS: (a+)+b against a long run of 'a' with no 'b'. Must return
        // via the 5s match-time budget, not hang the single-threaded host forever.
        var sw = Stopwatch.StartNew();
        var errs = RunErrors(
            "Invoke-BashAwk 'BEGIN{ s=\"\"; for(i=0;i<40;i++) s=s \"a\"; if (s ~ \"^(a+)+b\") print 1 }'");
        sw.Stop();

        Assert.Contains(errs, m => m.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        Assert.True(sw.ElapsedMilliseconds < 30000, $"timeout budget should bound this, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Awk_StarWidthOverflow_ProducesBoundedOutput()
    {
        // printf "%*d" with 1e20: the (int) cast saturated to int.MaxValue and the
        // padder tried to allocate a ~2 GB string. Now clamped to MaxSpec (1e6).
        var lines = RunBashText("Invoke-BashAwk 'BEGIN{printf \"%*d\\n\", 1e20, 5}'");

        Assert.Single(lines);
        Assert.Equal(1_000_000, lines[0].Length);
        Assert.EndsWith("5", lines[0]);
    }

    [Fact]
    public void Awk_DeeplyNestedParens_SyntaxErrorInsteadOfStackOverflow()
    {
        var prog = "BEGIN{x=" + new string('(', 5000) + "1" + new string(')', 5000) + "}";
        var errs = RunErrors($"Invoke-BashAwk '{prog}'");

        Assert.NotEmpty(errs);
        Assert.Contains(errs, m => m.Contains("too deep", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Awk_DeeplyNestedUnary_SyntaxErrorInsteadOfStackOverflow()
    {
        var prog = "BEGIN{x=" + new string('!', 5000) + "1; print x}";
        var errs = RunErrors($"Invoke-BashAwk '{prog}'");

        Assert.NotEmpty(errs);
        Assert.Contains(errs, m => m.Contains("too deep", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Awk_UnterminatedStringLiteral_SyntaxErrorInsteadOfMisparse()
    {
        var errs = RunErrors("Invoke-BashAwk 'BEGIN{print \"hi}'");

        Assert.NotEmpty(errs);
        Assert.Contains(errs, m => m.Contains("unterminated string", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Awk_OversizeHexLiteral_DoesNotCrash()
    {
        // 0xFFFFFFFFFFFFFFFFFF overflows Int64; the old Convert.ToInt64 threw.
        var lines = RunBashText("Invoke-BashAwk 'BEGIN{print (0xFFFFFFFFFFFFFFFFFF > 0)}'");

        Assert.Equal(new[] { "1" }, lines);
    }

    [Fact]
    public void Awk_BareHexPrefix_ParsesAsZero_DoesNotCrash()
    {
        // "0x" with no digits made Convert.ToInt64 throw FormatException.
        var lines = RunBashText("Invoke-BashAwk 'BEGIN{print 0x + 1}'");

        Assert.Equal(new[] { "1" }, lines);
    }

    [Fact]
    public void Awk_SplitWithEmptyRegex_CharSplits_NoSpuriousEmptyFields()
    {
        // gawk: split("ab",a,//) → 2 fields "a","b" (empty separator = char split).
        // .NET Regex.Split("") instead matched every boundary → ["","a","b",""]=4.
        var lines = RunBashText(
            "Invoke-BashAwk 'BEGIN{n=split(\"ab\",a,//); print n\"|\"a[1]\"|\"a[2]\"|\"a[3]}'");

        Assert.Equal(new[] { "2|a|b|" }, lines);
    }
}
