using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashShuf</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashShufCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashShuf</c> function — random shuffle of
/// input lines / items. Determinism caveat: shuffle uses
/// <see cref="System.Random"/> with no seed, so per-run output is
/// non-deterministic. Tests assert that the output is a permutation of the
/// input (multiset equality), never an exact ordering.
///
/// Failure-surface axes covered (per Directive 3): empty input, file
/// mode (default), pipeline mode, echo mode (<c>-e a b c</c>), range mode
/// (<c>-i 1-5</c>), <c>-n N</c> output cap, unicode input, missing-file
/// silent skip (oracle uses <c>-ErrorAction SilentlyContinue</c>),
/// <c>--help</c>, alias resolution, and a Directive-12 injection probe.
/// </summary>
public class InvokeBashShufCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashShufCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-shuf-{Guid.NewGuid():N}".Substring(0, 22));
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
    public void Shuf_EmptyInput_NoOutput()
    {
        // No operands, no pipeline, no -e/-i — emits nothing.
        var lines = RunLines("Invoke-BashShuf");
        Assert.Empty(lines);
    }

    [Fact]
    public void Shuf_EchoMode_OutputIsPermutationOfInput()
    {
        // Determinism caveat: shuf is random. Assert multiset equality, not
        // ordering. With three items there is a 1-in-6 chance of identity
        // ordering — we don't bias the test against it.
        var lines = RunLines("Invoke-BashShuf -e a b c");
        Assert.Equal(3, lines.Length);
        Assert.Equal(
            new[] { "a", "b", "c" }.OrderBy(s => s).ToArray(),
            lines.OrderBy(s => s).ToArray());
    }

    [Fact]
    public void Shuf_NLimit_CapsOutputCount()
    {
        // `-n 2` over 5 items emits exactly 2 items, each from the input
        // set (assert subset + count).
        var lines = RunLines("Invoke-BashShuf -n 2 -e a b c d e");
        Assert.Equal(2, lines.Length);
        foreach (var l in lines)
        {
            Assert.Contains(l, new[] { "a", "b", "c", "d", "e" });
        }
        // No duplicates — shuffle then take-first emits each at most once.
        Assert.Equal(lines.Length, lines.Distinct().Count());
    }

    [Fact]
    public void Shuf_RangeMode_EmitsAllIntegersInRange()
    {
        // `-i 1-5` yields the integers 1..5 (as strings) in some order.
        var lines = RunLines("Invoke-BashShuf -i 1-5");
        Assert.Equal(5, lines.Length);
        Assert.Equal(
            new[] { "1", "2", "3", "4", "5" },
            lines.OrderBy(s => int.Parse(s)).ToArray());
    }

    [Fact]
    public void Shuf_RangeMode_WithNLimit_CapsAndAllInRange()
    {
        var lines = RunLines("Invoke-BashShuf -i 1-10 -n 3");
        Assert.Equal(3, lines.Length);
        foreach (var l in lines)
        {
            int v = int.Parse(l);
            Assert.InRange(v, 1, 10);
        }
        Assert.Equal(lines.Length, lines.Distinct().Count());
    }

    [Fact]
    public void Shuf_PipelineMode_OutputIsPermutationOfInput()
    {
        var lines = RunLines("'alpha','beta','gamma','delta' | Invoke-BashShuf");
        Assert.Equal(4, lines.Length);
        Assert.Equal(
            new[] { "alpha", "beta", "delta", "gamma" },
            lines.OrderBy(s => s).ToArray());
    }

    [Fact]
    public void Shuf_FileMode_OutputIsPermutationOfFileLines()
    {
        var file = Path.Combine(_tmpDir, "items.txt");
        File.WriteAllText(file, "one\ntwo\nthree\nfour\n");
        var lines = RunLines($"Invoke-BashShuf '{file.Replace("'", "''")}'");
        Assert.Equal(4, lines.Length);
        Assert.Equal(
            new[] { "four", "one", "three", "two" },
            lines.OrderBy(s => s).ToArray());
    }

    [Fact]
    public void Shuf_FileMode_Unicode_PreservesNonAsciiChars()
    {
        var file = Path.Combine(_tmpDir, "uni.txt");
        File.WriteAllText(file, "héllo\n世界\n🎲\n",
            new System.Text.UTF8Encoding(false));
        var lines = RunLines($"Invoke-BashShuf '{file.Replace("'", "''")}'");
        Assert.Equal(3, lines.Length);
        Assert.Equal(
            new[] { "héllo", "世界", "🎲" }.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            lines.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Shuf_FileMode_MissingFile_NoOutput_NoThrow()
    {
        // Oracle: `Get-Content -ErrorAction SilentlyContinue` — missing
        // file produces no items, no error, no output.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashShuf '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Shuf_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashShuf --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("shuf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Shuf_AliasResolution_ShufWorks()
    {
        // The psm1 module registers `Set-Alias shuf Invoke-BashShuf`.
        // Because the binary cmdlet loads before psm1 runs, the alias
        // resolves to the cmdlet.
        var lines = RunLines("shuf -e foo bar");
        Assert.Equal(2, lines.Length);
        Assert.Equal(
            new[] { "bar", "foo" },
            lines.OrderBy(s => s).ToArray());
    }

    [Fact]
    public void Shuf_InjectionProbe_OperandWithSemicolonsAndDollarParen_NoSideEffect()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. File-mode operand
        // routes through SessionState.Path — a path whose name contains
        // `; $(throw 'x')` is treated as a literal path → missing file →
        // no output, no thrown script.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var lines = RunLines(
            $"Invoke-BashShuf '{probe.Replace("'", "''")}' 2>$null");
        Assert.Empty(lines);
    }

    [Fact]
    public void Shuf_NLimit_ZeroEmitsNothing()
    {
        // `-n 0` after shuffle: emit zero items.
        var lines = RunLines("Invoke-BashShuf -n 0 -e a b c");
        Assert.Empty(lines);
    }

    [Fact]
    public void Shuf_NLimit_LargerThanInput_EmitsAll()
    {
        // `-n 10` over 3 items: emit all 3 (oracle uses
        // `Select-Object -First` which caps at the available count).
        var lines = RunLines("Invoke-BashShuf -n 10 -e a b c");
        Assert.Equal(3, lines.Length);
        Assert.Equal(
            new[] { "a", "b", "c" },
            lines.OrderBy(s => s).ToArray());
    }
}
