using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>ls</c> owner columns (<c>-n -g -o -G</c>), sort keys (<c>-U -X --sort=extension|none</c>), the time selected by
/// <c>-u -c --time</c> and the <c>--time-style</c> / <c>--full-time</c> date forms. Expected shapes are GNU coreutils 9.4
/// (read from <c>wsl ls</c>); the dates come from files whose times the test sets.
/// </summary>
public class LsTimeSortTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public LsTimeSortTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psb-lst-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        // modified: aaaa oldest .. cccc newest; accessed: aaaa newest .. cccc oldest
        Make("aaaa.c", new DateTime(2024, 1, 1, 10, 0, 0), new DateTime(2022, 1, 1, 10, 0, 0));
        Make("bbbb.h", new DateTime(2024, 1, 2, 10, 0, 0), new DateTime(2021, 1, 1, 10, 0, 0));
        Make("cccc", new DateTime(2024, 1, 3, 10, 0, 0), new DateTime(2020, 1, 1, 10, 0, 0));
        Make("dddd.a.c", new DateTime(2023, 6, 1, 10, 0, 0), new DateTime(2023, 6, 1, 10, 0, 0));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Make(string name, DateTime mtime, DateTime atime)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        File.SetLastWriteTime(p, mtime);
        File.SetLastAccessTime(p, atime);
        File.SetLastWriteTime(p, mtime);
    }

    private string[] Ls(string args)
    {
        var pwsh = _fixture.AcquireFresh();
        var res = pwsh.AddScript($"Invoke-BashLs {args} '{_dir}'").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(pwsh.Streams.Error);
        return res.Select(o => o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
    }

    private static string[] Names(IEnumerable<string> lines) => lines.Where(l => !l.StartsWith("total ")).Select(l => l.Split(' ').Last()).ToArray();

    // ---- time selection ----

    [Fact]
    public void T_SortsByModificationTime_UAndTSortByAccessTime()
    {
        Assert.Equal(new[] { "cccc", "bbbb.h", "aaaa.c", "dddd.a.c" }, Ls("'-t'"));
        // GNU: -u without -l sorts by access time, newest first; -t is implied
        Assert.Equal(new[] { "dddd.a.c", "aaaa.c", "bbbb.h", "cccc" }, Ls("'-u'"));
        Assert.Equal(new[] { "dddd.a.c", "aaaa.c", "bbbb.h", "cccc" }, Ls("'-ut'"));
        Assert.Equal(new[] { "cccc", "bbbb.h", "aaaa.c", "dddd.a.c" }, Ls("'-u' '-t' '--time=mtime'"));
        Assert.Equal(new[] { "dddd.a.c", "aaaa.c", "bbbb.h", "cccc" }, Ls("'--time=atime'"));
        Assert.Equal(new[] { "dddd.a.c", "aaaa.c", "bbbb.h", "cccc" }, Ls("'--time=access' '-t'"));
        Assert.Equal(new[] { "dddd.a.c", "aaaa.c", "bbbb.h", "cccc" }, Ls("'--time=use'"));
    }

    [Fact]
    public void LongListing_ShowsTheSelectedTime_AndKeepsNameOrderWithoutT()
    {
        var byName = Names(Ls("'-lu'"));
        Assert.Equal(new[] { "aaaa.c", "bbbb.h", "cccc", "dddd.a.c" }, byName);
        var shown = Ls("'-lu'").Where(l => !l.StartsWith("total ")).ToArray();
        Assert.Contains("2022", shown[0]);
        Assert.Contains("2021", shown[1]);
        Assert.Contains("2020", shown[2]);
        Assert.Equal(new[] { "dddd.a.c", "aaaa.c", "bbbb.h", "cccc" }, Names(Ls("'-lut'")));
        Assert.Contains("Jan  3  2024", Ls("'-l'").First(l => l.EndsWith("cccc")));
    }

    [Fact]
    public void Ctime_IsAccepted_AndLastOfUCAndTimeWins()
    {
        Assert.Equal(4, Names(Ls("'-lc'")).Length);
        Assert.Equal(4, Names(Ls("'-c'")).Length);
        // last wins: -c then -u shows atimes, -u then -c does not
        Assert.Contains("2022", Ls("'-l' '-c' '-u'").First(l => l.EndsWith("aaaa.c")));
        Assert.DoesNotContain("2022", Ls("'-l' '-u' '-c'").First(l => l.EndsWith("aaaa.c")));
        Assert.Contains("2022", Ls("'-l' '--time=ctime' '--time=atime'").First(l => l.EndsWith("aaaa.c")));
    }

    [Theory]
    [InlineData("'--time=bogus'", "invalid argument 'bogus' for '--time'")]
    public void BadTimeWord_IsExit1(string args, string stderr)
        => CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashLs {args} '{_dir}'").AssertFailed(1, stderr);

    [Theory]
    [InlineData("'-l' '--time-style=bogus'", "invalid argument 'bogus' for 'time style'")]
    [InlineData("'-l' '--time-style=l'", "ambiguous argument 'l' for 'time style'")]
    public void BadTimeStyle_IsExit2(string args, string stderr)
        => CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashLs {args} '{_dir}'").AssertFailed(2, stderr);

    [Fact]
    public void TimeStyle_IsOnlyChecked_ForALongListing()
        => Assert.Equal(4, Ls("'--time-style=bogus'").Length);

    [Fact]
    public void TimeStyle_PlusFormat_IsRefused()
        => CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashLs '-l' '--time-style=+%Y' '{_dir}'").AssertFailed(2, "not supported by ps-bash");

    // ---- time styles ----

    private string DateColumn(string style, string name)
    {
        var line = Ls($"'-l' '--time-style={style}'").First(l => l.EndsWith(" " + name));
        var m = Regex.Match(line, @"^\S+\s+\d+\s+\S*\s+\S*\s+\d+ (?<d>.*) " + Regex.Escape(name) + "$");
        Assert.True(m.Success, line);
        return m.Groups["d"].Value;
    }

    [Fact]
    public void LongIso_FullIso_Iso_Locale()
    {
        Assert.Equal("2024-01-03 10:00", DateColumn("long-iso", "cccc"));
        Assert.Matches(@"^2024-01-03 10:00:00\.\d{9} [+-]\d{4}$", DateColumn("full-iso", "cccc"));
        Assert.Equal("2024-01-03 ", DateColumn("iso", "cccc"));          // an old file: the date plus a blank (GNU pads to 11)
        Assert.Equal("Jan  3  2024", DateColumn("locale", "cccc"));
        Assert.Equal("Jan  3  2024", DateColumn("posix-locale", "cccc"));
        Assert.Equal("2024-01-03 10:00", DateColumn("long-i", "cccc"));  // unique prefix of long-iso
        Assert.Matches(@"^2024-01-03 10:00:00\.\d{9} [+-]\d{4}$", DateColumn("f", "cccc"));
    }

    [Fact]
    public void FullTime_IsLongWithFullIso_AndLastOfFullTimeAndStyleWins()
    {
        var full = Ls("'--full-time'").First(l => l.EndsWith("cccc"));
        Assert.Matches(@"2024-01-03 10:00:00\.\d{9} [+-]\d{4} cccc$", full);
        Assert.Matches(@" 2024-01-03  cccc$", Ls("'--full-time' '--time-style=iso'").First(l => l.EndsWith("cccc")));
        Assert.Matches(@"2024-01-03 10:00:00\.\d{9} [+-]\d{4} cccc$", Ls("'--time-style=iso' '--full-time'").First(l => l.EndsWith("cccc")));
        // a later -C beats --full-time's long format; -1 does not
        Assert.Single(Ls("'--full-time' '-C' '-w' '80'"));
        Assert.Equal(5, Ls("'--full-time' '-1'").Length);
    }

    [Fact]
    public void RecentFile_Iso_ShowsMonthDayAndTime()
    {
        File.SetLastWriteTime(Path.Combine(_dir, "cccc"), DateTime.Now.AddDays(-2));
        Assert.Matches(@" \d{2}-\d{2} \d{2}:\d{2} cccc$", Ls("'-l' '--time-style=iso'").First(l => l.EndsWith("cccc")));
    }

    // ---- owner columns ----

    private static readonly Regex Long = new(@"^(?<p>\S+) +(?<l>\d+) (?<rest>.*)$");

    [Fact]
    public void G_O_NoGroup_DropTheirColumns_AndImplyLong()
    {
        string[] Rest(string args) => Ls(args).Where(l => !l.StartsWith("total ")).Select(l => Long.Match(l).Groups["rest"].Value).ToArray();

        var full = Rest("'-l'")[0];                 // OWNER GROUP SIZE DATE NAME
        var cols = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(cols.Length >= 6, full);
        Assert.Equal(cols.Length - 1, Rest("'-g'")[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);   // owner gone
        Assert.Equal(cols.Length - 1, Rest("'-o'")[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);   // group gone
        Assert.Equal(cols.Length - 1, Rest("'-l' '-G'")[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(cols.Length - 1, Rest("'-l' '--no-group'")[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(cols.Length - 2, Rest("'-go'")[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(cols.Length - 2, Rest("'-g' '-o'")[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(4, Ls("'-G'").Length);          // -G alone is not long
        Assert.All(Ls("'-go'").Skip(1), l => Assert.Matches(@"^-[rwx-]{9} +1 +\d+ ", l));
    }

    [Fact]
    public void N_ShowsNumericOwnerAndGroup()
    {
        foreach (var line in Ls("'-n'").Where(l => !l.StartsWith("total ")))
            Assert.Matches(@"^-[rwx-]{9} +\d+ \d+ +\d+ +\d+ ", line);
        foreach (var line in Ls("'--numeric-uid-gid'").Where(l => !l.StartsWith("total ")))
            Assert.Matches(@"^-[rwx-]{9} +\d+ \d+ +\d+ +\d+ ", line);
    }

    // ---- sort keys ----

    [Fact]
    public void X_SortsByExtensionThenName()
    {
        // extensions: aaaa.c ".c", bbbb.h ".h", cccc "", dddd.a.c ".c"
        Assert.Equal(new[] { "cccc", "aaaa.c", "dddd.a.c", "bbbb.h" }, Ls("'-X'"));
        Assert.Equal(new[] { "cccc", "aaaa.c", "dddd.a.c", "bbbb.h" }, Ls("'--sort=extension'"));
        Assert.Equal(new[] { "bbbb.h", "dddd.a.c", "aaaa.c", "cccc" }, Ls("'-Xr'"));
        Assert.Equal(new[] { "cccc", "bbbb.h", "aaaa.c", "dddd.a.c" }, Ls("'-X' '-t'"));          // last sort option wins
        Assert.Equal(new[] { "cccc", "aaaa.c", "dddd.a.c", "bbbb.h" }, Ls("'-t' '-X'"));
    }

    [Fact]
    public void U_And_SortNone_ListEveryEntry_InDirectoryOrder()
    {
        Assert.Equal(new[] { "aaaa.c", "bbbb.h", "cccc", "dddd.a.c" }, Ls("'-U'").OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(4, Ls("'--sort=none'").Length);
        Assert.Equal(4, Ls("'-U' '-t'").Length);       // -t after -U sorts again
        Assert.Equal(new[] { "cccc", "bbbb.h", "aaaa.c", "dddd.a.c" }, Ls("'-U' '-t'"));
    }
}
