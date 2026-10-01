using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>ls -l</c> column layout, oracle GNU coreutils 9.4 (`wsl ls -l`): per listing block the link count and size are
/// right-aligned and owner/group left-aligned to the widest entry; the date is `Mon dd HH:mm` inside the last six
/// months (and not in the future), else `Mon dd  yyyy`; real hard-link counts.
/// </summary>
public class LsLongFormatTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 8, 12, 0);

    [Fact]
    public void Align_PadsEachColumnToTheWidestEntry_AsGnu()
    {
        // oracle: ls -ldh /etc/passwd /usr/lib small big
        var rows = new[]
        {
            new LsLongRow("-rw-r--r--", "1", "root", "root", "1.8K", "May  5 13:09", "/etc/passwd"),
            new LsLongRow("drwxr-xr-x", "83", "root", "root", "4.0K", "Sep  4 07:06", "/usr/lib"),
            new LsLongRow("-rw-r--r--", "1", "beagle", "wheel", "13K", "Oct  1 08:12", "big"),
            new LsLongRow("-rw-r--r--", "2", "beagle", "wheel", "6", "Oct  1 08:12", "small"),
        };
        Assert.Equal(new[]
        {
            "-rw-r--r--  1 root   root  1.8K May  5 13:09 /etc/passwd",
            "drwxr-xr-x 83 root   root  4.0K Sep  4 07:06 /usr/lib",
            "-rw-r--r--  1 beagle wheel  13K Oct  1 08:12 big",
            "-rw-r--r--  2 beagle wheel    6 Oct  1 08:12 small",
        }, LsLongFormat.Align(rows));
    }

    [Fact]
    public void Align_PlainSizes_AreOnlyAsWideAsTheLargest()
    {
        // oracle: ls -l (12345-byte file next to a 6-byte one) -> no fixed 8-wide size column
        var rows = new[]
        {
            new LsLongRow("-rw-r--r--", "1", "beagle", "wheel", "12345", "Oct  1 08:12", "big"),
            new LsLongRow("-rw-r--r--", "2", "beagle", "wheel", "6", "Oct  1 08:12", "small"),
        };
        Assert.Equal(new[]
        {
            "-rw-r--r-- 1 beagle wheel 12345 Oct  1 08:12 big",
            "-rw-r--r-- 2 beagle wheel     6 Oct  1 08:12 small",
        }, LsLongFormat.Align(rows));
        Assert.Equal(new[] { "-rw-r--r-- 2 beagle wheel 6 Oct  1 08:12 small" },
            LsLongFormat.Align(new[] { rows[1] }));
    }

    [Theory]
    [InlineData("2026-05-01 08:12:00", "May  1 08:12")]   // 5 months ago: time of day (oracle)
    [InlineData("2026-10-01 07:12:00", "Oct  1 07:12")]
    [InlineData("2026-03-01 08:12:00", "Mar  1  2026")]   // 7 months ago: year (oracle)
    [InlineData("2020-01-02 03:04:00", "Jan  2  2020")]
    [InlineData("2026-10-03 08:12:00", "Oct  3  2026")]   // in the future: year (oracle: touch -d +2days)
    [InlineData("2027-10-01 08:12:00", "Oct  1  2027")]
    [InlineData("2026-10-01 08:12:00", "Oct  1 08:12")]   // exactly now is not the future
    public void Date_SwitchesToTheYearForOldOrFutureFiles(string stamp, string expected) =>
        Assert.Equal(expected, LsLongFormat.Date(DateTime.Parse(stamp, CultureInfo.InvariantCulture), Now));

    [Fact]
    public void Date_SixMonthBoundary_IsGnusAverageMonthLength()
    {
        // 6 months = 15778476 s (182.6 days): one second inside prints the time, one second outside the year.
        var edge = Now.AddSeconds(-15778476);
        Assert.Contains(":", LsLongFormat.Date(edge.AddSeconds(1), Now));
        Assert.DoesNotContain(":", LsLongFormat.Date(edge.AddSeconds(-1), Now));
    }
}

/// <summary>End to end through <c>Invoke-BashLs -l</c>: per-block alignment, real link counts, year dates.</summary>
public class LsLongListingTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public LsLongListingTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), $"psb-lsl-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "small"), "hello\n");
        File.WriteAllBytes(Path.Combine(_dir, "big"), new byte[12345]);
        File.WriteAllText(Path.Combine(_dir, "old"), "x");
        File.SetLastWriteTime(Path.Combine(_dir, "old"), new DateTime(2020, 1, 2, 3, 4, 0));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult Run(string call) =>
        CmdResult.Run(_fixture.AcquireFresh(), $"Set-Location -LiteralPath '{_dir.Replace("'", "''")}'; {call}");

    private static readonly Regex Row = new(
        @"^(?<pre>\S{10} +\d+ \S+ +\S+ +[\d.]+[KMGTP]?) (?<date>\w{3} [ \d]\d (?:[ \d]\d:\d\d|  \d{4})) (?<name>.+)$");

    [Fact]
    public void SizeColumn_IsRightAlignedToTheWidestEntry_NotAFixedEight()
    {
        var lines = Run("Invoke-BashLs '-l' big small").AssertSuccess().Lines;
        var m = lines.Select(l => Row.Match(l)).ToArray();
        Assert.All(m, x => Assert.True(x.Success));
        Assert.Equal(m[0].Groups["pre"].Length, m[1].Groups["pre"].Length);   // every row has the same width
        Assert.EndsWith(" 12345", m[0].Groups["pre"].Value);
        Assert.EndsWith("     6", m[1].Groups["pre"].Value);                  // right-aligned to 5 digits, not 8
        Assert.Matches(@"^\S{10} \d+ \S+ +\S+ 12345 ", lines[0]);             // only one space before a full-width size
    }

    [Fact]
    public void LinkCount_IsTheRealHardLinkCount()
    {
        var lines = Run("New-Item -ItemType HardLink -Path hl -Target small | Out-Null; Invoke-BashLs '-l' hl small big").AssertSuccess().Lines;
        string Links(string name) => Regex.Match(lines.Single(l => l.EndsWith(" " + name)), @"^\S{10} +(\d+) ").Groups[1].Value;
        Assert.Equal("2", Links("hl"));
        Assert.Equal("2", Links("small"));
        Assert.Equal("1", Links("big"));
    }

    [Fact]
    public void OldFile_PrintsTheYear()
    {
        var line = Run("Invoke-BashLs '-l' old").AssertSuccess().Lines.Single();
        Assert.Contains(" Jan  2  2020 old", line);
    }

    [Fact]
    public void HumanSizes_AreAlignedToo()
    {
        var lines = Run("Invoke-BashLs '-lh' big small").AssertSuccess().Lines;
        var m = lines.Select(l => Row.Match(l)).ToArray();
        Assert.All(m, x => Assert.True(x.Success, x.Value));
        Assert.Equal(m[0].Groups["pre"].Length, m[1].Groups["pre"].Length);
    }

    [Fact]
    public void Blocks_AlignIndependently_PerDirectorySection()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "tiny"), "z");
        var lines = Run("Invoke-BashLs '-l' sub .").AssertSuccess().Lines;
        // the ./sub section holds only a 1-byte file: its size column is 1 wide, whatever the other section has
        var tiny = lines.Single(l => l.EndsWith(" tiny"));
        Assert.Matches(@"^\S{10} \d+ \S+ +\S+ 1 \w{3} ", tiny);
    }
}
