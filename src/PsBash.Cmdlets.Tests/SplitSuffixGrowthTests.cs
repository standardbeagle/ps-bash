using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// split's output-name sequence, checked against GNU split 9.4 (`split --verbose`, creation order):
/// with NO <c>-a</c> (or <c>-a 0</c>) and no <c>--numeric-suffixes=FROM</c> the suffix AUTO-EXTENDS —
/// <c>xaa..xyz</c> (25*26), then <c>xzaaa..</c> (a <c>z</c> marker + a counter one longer, first
/// counter letter again stopping short of <c>z</c>), then <c>xzzaaaa..</c>; numerically
/// <c>x00..x89, x9000..x9899, x99000000..</c>. An explicit <c>-a N</c> (and <c>--numeric-suffixes=FROM</c>,
/// which also fixes the length) is NOT auto: the names run out with
/// <c>split: output file suffixes exhausted</c> (exit 1) once every N-length name was used.
/// </summary>
public class SplitSuffixGrowthTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public SplitSuffixGrowthTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), $"psb-spg-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "in.txt"), string.Concat(Enumerable.Range(1, 1100).Select(i => i + "\n")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Runs split in an empty per-call subdirectory; returns (exit, stderr text, file names in creation-name order).</summary>
    private (int exit, string err, string[] names) Split(string args, int lines = 1100)
    {
        var work = Path.Combine(_dir, "w" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(work);
        var input = Path.Combine(_dir, "in" + lines + ".txt");
        if (!File.Exists(input))
            File.WriteAllText(input, string.Concat(Enumerable.Range(1, lines).Select(i => i + "\n")));
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"Set-Location -LiteralPath '{work.Replace("'", "''")}'; Invoke-BashSplit {args} '{input.Replace("'", "''")}'");
        var names = Directory.GetFiles(work).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
        return (r.ExitCode, r.Stderr + string.Join("\n", r.Errors), names);
    }

    // The n-th (1-based) piece in creation order is the n-th name when sorted, because every name
    // sorts after the previous ones in GNU's scheme (z / 9 markers keep the order).
    [Fact]
    public void HexSuffixes_AutoExtend_AsGnu()
    {
        // oracle: `split -x -l1` on 300 lines names x00..xef (240), then xf000..
        var (exit, _, names) = Split("'-x' '-l' 1", 300);
        Assert.Equal(0, exit);
        Assert.Equal(300, names.Length);
        Assert.Equal("x00", names[0]);
        Assert.Equal("x0a", names[10]);
        Assert.Equal("xef", names[239]);
        Assert.Equal("xf000", names[240]);
        Assert.Equal("xf03b", names[299]);
    }

    [Fact]
    public void HexSuffixes_FixedLength_ExhaustsAfter16()
    {
        var (exit, err, names) = Split("'--hex-suffixes' '-a' 1 '-l' 1", 20);
        Assert.Equal(1, exit);
        Assert.Contains("output file suffixes exhausted", err);
        Assert.Equal(16, names.Length);
        Assert.Equal("xf", names[^1]);
    }

    [Fact]
    public void HexSuffixes_From_StartsThere()
    {
        // oracle: --hex-suffixes=10 -l1 -> x10 .. xff (240 names), then exhausted (FROM fixes the length)
        var (exit, err, names) = Split("'--hex-suffixes=10' '-l' 1", 300);
        Assert.Equal(1, exit);
        Assert.Contains("output file suffixes exhausted", err);
        Assert.Equal(240, names.Length);
        Assert.Equal("x10", names[0]);
        Assert.Equal("xff", names[^1]);
    }

    [Fact]
    public void ShortD_BeatsX_AsGnu()
    {
        var (_, _, names) = Split("'-x' '-d' '-l' 1", 120);
        Assert.Equal("x9009", names[99]);   // decimal auto-extension (x00..x89, x9000..), not hex ("x63")
    }

    [Fact]
    public void Default_AutoExtendsAlphabetic_PastXyz()
    {
        var (exit, _, names) = Split("'-l' 1", 700);
        Assert.Equal(0, exit);
        Assert.Equal(700, names.Length);
        Assert.Equal("xaa", names[0]);
        Assert.Equal("xyz", names[649]);
        Assert.Equal("xzaaa", names[650]);
        Assert.Equal("xzaaz", names[675]);
        Assert.Equal("xzaba", names[676]);
        Assert.Equal("xzabx", names[699]);
    }

    [Fact]
    public void SuffixLengthZero_IsTheSameAutoMode()
    {
        var (exit, _, names) = Split("'-a' 0 '-l' 1", 700);
        Assert.Equal(0, exit);
        Assert.Equal(700, names.Length);
        Assert.Equal("xzaaa", names[650]);
    }

    [Fact]
    public void NumericDefault_AutoExtends_AtNine()
    {
        var (exit, _, names) = Split("'-d' '-l' 1");
        Assert.Equal(0, exit);
        Assert.Equal(1100, names.Length);
        Assert.Equal("x00", names[0]);
        Assert.Equal("x89", names[89]);
        Assert.Equal("x9000", names[90]);      // 'marker 9' + a 3-digit counter
        Assert.Equal("x9899", names[989]);
        Assert.Equal("x990000", names[990]);   // '99' + a 4-digit counter
        Assert.Equal("x990109", names[1099]);
    }

    [Fact]
    public void BareNumericSuffixes_IsAutoToo()
    {
        var (_, _, names) = Split("'--numeric-suffixes' '-l' 1");
        Assert.Equal(1100, names.Length);
        Assert.Equal("x9000", names[90]);
    }

    [Fact]
    public void ExplicitLength_DoesNotExtend_ExhaustsWithExit1()
    {
        var (exit, err, names) = Split("'-a' 2 '-l' 1", 700);
        Assert.Equal(1, exit);
        Assert.Equal(676, names.Length);   // xaa..xzz
        Assert.Equal("xzz", names[^1]);
        Assert.Contains("output file suffixes exhausted", err);

        var (exit2, err2, n2) = Split("'-d' '-a' 3 '-l' 1");
        Assert.Equal(1, exit2);
        Assert.Equal(1000, n2.Length);     // x000..x999 (the whole digit range, no marker)
        Assert.Equal("x999", n2[^1]);
        Assert.Contains("output file suffixes exhausted", err2);
    }

    [Fact]
    public void NumericFrom_FixesTheLength_AndExhausts()
    {
        var (exit, err, names) = Split("'--numeric-suffixes=5' '-l' 1");
        Assert.Equal(1, exit);
        Assert.Equal(95, names.Length);    // x05..x99
        Assert.Equal("x05", names[0]);
        Assert.Equal("x99", names[^1]);
        Assert.Contains("output file suffixes exhausted", err);

        var (_, _, n4) = Split("'--numeric-suffixes=5' '-a' 4 '-l' 1");
        Assert.Equal(1100, n4.Length);
        Assert.Equal("x0005", n4[0]);
        Assert.Equal("x1104", n4[^1]);
    }

    [Fact]
    public void NumericFrom_TooWideForTheLength_IsAnErrorBeforeAnyOutput()
    {
        var (exit, err, names) = Split("'--numeric-suffixes=123' '-l' 1");
        Assert.Equal(1, exit);
        Assert.Empty(names);
        Assert.Contains("numerical suffix start value is too large for the suffix length", err);
    }

    [Fact]
    public void NumericFrom_NotADecimal_IsGnuWorded()
    {
        var (exit, err, names) = Split("'--numeric-suffixes=x' '-l' 1");
        Assert.Equal(1, exit);
        Assert.Empty(names);
        Assert.Contains("invalid start value for numerical suffix", err);
    }

    [Fact]
    public void ByteMode_SharesTheSequence()
    {
        // 1100 lines is ~4.4 KB: -b 6 gives ~730 pieces, past the 650 that fit two letters
        var (exit, _, names) = Split("'-b' 6");
        Assert.Equal(0, exit);
        Assert.True(names.Length > 650);
        Assert.Equal("xzaaa", names[650]);
    }
}
