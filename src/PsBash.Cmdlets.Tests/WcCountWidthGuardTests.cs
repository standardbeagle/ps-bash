using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// wc's column width is GNU's compute_number_width, decided by ONE function, <see cref="InvokeBashWcCommand.CountWidth"/>.
/// REGRESSION: the cmdlet had its own fixed 7/8 widths and a "trim when one column" branch, the fused stage another, so
/// <c>printf 'a\n' | wc -lc</c> printed <c>1 2</c> (GNU <c>      1       2</c>) and <c>wc -lc &lt; f</c> padded a regular file
/// as a pipe. The unit cases pin the rule (values from GNU coreutils 9.4); the scan keeps every wc line on
/// <c>FormatWcText</c>, whose width comes from <c>CountWidth</c>. Oracle: WcCountWidthDifferentialTests.
/// </summary>
public class WcCountWidthGuardTests
{
    [Theory]
    [InlineData(1, 1, true, 0L, 1)]          // printf 'a\n' | wc -l -> "1"
    [InlineData(2, 1, true, 0L, 7)]          // printf 'a\n' | wc -lc -> "      1       2"
    [InlineData(3, 1, true, 0L, 7)]          // printf 'a\n' | wc
    [InlineData(2, 1, false, 3893L, 4)]      // wc -lc < f (3893 bytes) -> "1000 3893"
    [InlineData(1, 1, false, 3893L, 1)]      // wc -l f -> "1000 f"
    [InlineData(1, 2, false, 3903L, 4)]      // wc -l f g -> "1000 f" / "   5 g"
    [InlineData(2, 2, true, 10L, 7)]         // wc -lc g d (a directory) -> width 7
    [InlineData(2, 1, false, 22888896L, 8)]  // a regular file of 22888896 bytes
    [InlineData(2, 1, true, 22888896L, 8)]   // past 7 digits the size wins over the pipe minimum
    public void CountWidth_IsGnuComputeNumberWidth(int columns, int inputs, bool anyNotRegular, long regularBytes, int expected)
        => Assert.Equal(expected, InvokeBashWcCommand.CountWidth(columns, inputs, anyNotRegular, regularBytes));

    [Fact]
    public void FormatWcText_RightAlignsEveryColumnAndJoinsWithOneSpace()
    {
        Assert.Equal("      1       2", InvokeBashWcCommand.FormatPipeWcText(true, false, false, true, false, 1, 1, 2, 2, 1));
        Assert.Equal("1", InvokeBashWcCommand.FormatPipeWcText(true, false, false, false, false, 1, 1, 2, 2, 1));
    }

    [Fact]
    public void WcWidth_IsComputedOnlyByCountWidth()
    {
        string dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        string wc = File.ReadAllText(Path.Combine(dir, "InvokeBashWcCommand.cs"));
        string stages = File.ReadAllText(Path.Combine(dir, "LineStreamStages.cs"));

        // The only PadLeft in wc is FormatWcText's, by the computed width; no literal widths.
        Assert.Single(Regex.Matches(wc, @"\.PadLeft\("));
        Assert.Contains(".PadLeft(width)", wc);
        Assert.DoesNotMatch(new Regex(@"_fileWidth\s*=\s*[0-9]"), wc.Replace("_fileWidth = 1;", ""));
        // The fused stage formats through the cmdlet's pipe layout, never its own.
        Assert.Contains("InvokeBashWcCommand.FormatPipeWcText(", stages);
        Assert.DoesNotMatch(new Regex(@"InvokeBashWcCommand\.FormatWcText\("), stages);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }
}
