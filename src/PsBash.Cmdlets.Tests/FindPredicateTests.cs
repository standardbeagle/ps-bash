using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The find predicates / actions that used to be refused ("unsupported predicate"): -perm -user -group
/// -uid -gid -nouser -nogroup -printf -fprint* -ls -fls -L -H -follow -newerXY -amin/-cmin/-mmin/-atime/-ctime
/// -links -inum -samefile -executable/-readable/-writable -lname/-ilname -regextype -quit -ok/-okdir
/// -execdir -xdev/-mount -noleaf -ignore_readdir_race -daystart and every -size unit. Expected values are
/// GNU find 4.9 output (`wsl bash`, TZ=UTC fixture; the probe scripts are described per test). Fixture:
/// a.txt (2 bytes), sub/b.txt (3), d2/c.log (4); modes are 0644 files / 0755 dirs (the Windows mapping
/// gives exactly those for non-executable files, so one expectation serves every OS).
/// </summary>
internal static class FindResultExt
{
    /// <summary>Exit 0 only: stderr diagnostics (GNU warnings, -ok prompts) are expected on these paths.</summary>
    public static CmdResult NoFail(this CmdResult r) { Assert.Equal(0, r.ExitCode); return r; }
}

public class FindPredicateTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public FindPredicateTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-findp-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        Directory.CreateDirectory(Path.Combine(_dir, "d2"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x\n");
        File.WriteAllText(Path.Combine(_dir, "sub", "b.txt"), "yy\n");
        File.WriteAllText(Path.Combine(_dir, "d2", "c.log"), "zzz\n");
        Mode("a.txt", 0x1A4); Mode("sub/b.txt", 0x1A4); Mode("d2/c.log", 0x1A4);   // 0644
        Mode("sub", 0x1ED); Mode("d2", 0x1ED); Mode("", 0x1ED);                    // 0755
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(Path.GetTempPath()); Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

#pragma warning disable CA1416
    private void Mode(string rel, int mode)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(Path.Combine(_dir, rel), (UnixFileMode)mode);
    }
#pragma warning restore CA1416

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>One find invocation from inside the fixture tree. <paramref name="args"/> are PowerShell tokens (quote dash words).</summary>
    private CmdResult Find(string args, string? pipeline = null, bool raw = false)
    {
        string d = Q(_dir);
        string tail = raw ? "" : " | ForEach-Object { $_.BashText.TrimEnd([char]10) }";
        string head = pipeline is null ? "" : pipeline + " | ";
        return CmdResult.Run(_fixture.AcquireFresh(),
            $"$__old = Get-Location; Set-Location {d}; try {{ {head}Invoke-BashFind {args}{tail} }} finally {{ Set-Location $__old }}");
    }

    private string[] Sorted(string args) => Find(args).AssertSuccess().Lines.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static string Words(IEnumerable<string> l) => string.Join(" ", l);

    private void Age(string rel, TimeSpan age)
    {
        File.SetLastWriteTimeUtc(P(rel), DateTime.UtcNow - age);
        File.SetLastAccessTimeUtc(P(rel), DateTime.UtcNow - age);
    }

    /// <summary>A directory link: a junction on Windows (no privilege), a symlink elsewhere.</summary>
    private void DirLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{P(link)}\" \"{P(target)}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, "mklink /J failed");
        }
        else Directory.CreateSymbolicLink(P(link), Path.GetFullPath(P(target)));
    }

    /// <summary>A file symlink, or a skip when the OS will not let this account create one.</summary>
    private void FileLink(string link, string target)
    {
        try { File.CreateSymbolicLink(P(link), target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { Skip.If(true, "creating a file symlink needs privilege here: " + ex.Message); }
    }

    // ───────────── -perm ─────────────

    [Fact]
    public void Perm_Exact_All_Any_NumericAndSymbolic()   // oracle: perm 644 / -644 / /222 / u=rw / -u=rw / /u=x ...
    {
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-perm' 644"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-perm' 0644"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-perm' '-644'"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-perm' '-u=rw'"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-perm' '/222'"));
        Assert.Empty(Sorted(". '-type' f '-perm' '-111'"));
        Assert.Empty(Sorted(". '-type' f '-perm' '/111'"));
        Assert.Empty(Sorted(". '-type' f '-perm' '/u=x'"));
        Assert.Empty(Sorted(". '-type' f '-perm' 600"));
        Assert.Empty(Sorted(". '-type' f '-perm' 'u=rw'"));                       // exact 0600
        Assert.Equal(new[] { ".", "./d2", "./sub" }, Sorted(". '-type' d '-perm' 755"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-perm' 'u=rw,g=r,o=r'"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-perm' '-a=r'"));
    }

    [Fact]
    public void Perm_SlashZero_MatchesAll_WithTheGnuWarning()
    {
        var r = Find(". '-maxdepth' 0 '-perm' '/000'");
        Assert.Equal(new[] { "." }, r.Lines);
        Assert.Contains("you have specified a mode pattern /000", r.Stderr);
        Assert.Equal(new[] { "." }, Sorted(". '-maxdepth' 0 '-perm' '-000'"));
    }

    [Fact]
    public void Perm_InvalidModes_AreUsageErrors()
    {
        foreach (var bad in new[] { "9", "08", "u=z", "+100", "-u", "+111" })
            Find($". '-perm' {Q(bad)}").AssertFailed(1, $"invalid mode ‘{bad}’");
        Find(". '-perm'").AssertFailed(1, "missing argument to `-perm'");
    }

    // ───────────── -user / -group / -uid / -gid ─────────────

    [Fact]
    public void UserGroupUidGid_AreMutuallyConsistent_AndRejectUnknownNames()
    {
        var info = Find(". '-maxdepth' 0 '-printf' '%u|%g|%U|%G'", raw: true).AssertSuccess().Stdout.Trim().Split('|');
        Assert.Equal(4, info.Length);
        Assert.NotEmpty(info[0]);
        Assert.Equal(new[] { "." }, Sorted($". '-maxdepth' 0 '-user' {Q(info[0])}"));
        Assert.Equal(new[] { "." }, Sorted($". '-maxdepth' 0 '-group' {Q(info[1])}"));
        Assert.Equal(new[] { "." }, Sorted($". '-maxdepth' 0 '-uid' {info[2]}"));
        Assert.Equal(new[] { "." }, Sorted($". '-maxdepth' 0 '-gid' {info[3]}"));
        Assert.Equal(new[] { "." }, Sorted($". '-maxdepth' 0 '-user' {info[2]}"));        // numeric -user = uid
        Assert.Equal(new[] { "." }, Sorted($". '-maxdepth' 0 '-uid' '+{Math.Max(0, long.Parse(info[2]) - 1)}'"));
        Assert.Empty(Sorted(". '-maxdepth' 0 '-uid' 99999999"));                            // rc 0, no output
        Assert.Equal(new[] { "." }, Sorted(". '-maxdepth' 0 '-uid' '-99999999'"));
        Assert.Empty(Sorted(". '-maxdepth' 0 '-nouser'"));
        Assert.Empty(Sorted(". '-maxdepth' 0 '-nogroup'"));
        Find(". '-user' nosuchuser_zz").AssertFailed(1, "‘nosuchuser_zz’ is not the name of a known user");
        Find(". '-group' nosuchgroup_zz").AssertFailed(1, "‘nosuchgroup_zz’ is not the name of a known group");
    }

    // ───────────── time predicates ─────────────

    [Fact]
    public void Mmin_FollowsTheGnuWindow_CeilForEqual_RealValueForGtLt()   // oracle ages 30/70/100/130/190 s
    {
        Directory.CreateDirectory(P("t"));
        foreach (var a in new[] { 30, 70, 100, 130, 190 }) { File.WriteAllText(P($"t/f{a}"), "x"); Age($"t/f{a}", TimeSpan.FromSeconds(a)); }
        string S(string x) => Words(Sorted($"t '-type' f {x}").Select(Path.GetFileName)!);
        Assert.Equal("", S("'-mmin' 0"));
        Assert.Equal("f30", S("'-mmin' 1"));
        Assert.Equal("f100 f70", S("'-mmin' 2"));
        Assert.Equal("f130", S("'-mmin' 3"));
        Assert.Equal("f30", S("'-mmin' '-1'"));
        Assert.Equal("f100 f30 f70", S("'-mmin' '-2'"));
        Assert.Equal("f100 f130 f190 f30 f70", S("'-mmin' '+0'"));
        Assert.Equal("f100 f130 f190 f70", S("'-mmin' '+1'"));
        Assert.Equal("f130 f190", S("'-mmin' '+2'"));
        Assert.Equal("f190", S("'-mmin' '+3'"));
    }

    [Fact]
    public void Mtime_DaysAreRoundedDown_AndFractionsWork()   // oracle ages 1h 23h 25h 47.8h 50h
    {
        Directory.CreateDirectory(P("t"));
        foreach (var h in new[] { 3600, 82800, 90000, 172000, 180000 }) { File.WriteAllText(P($"t/d{h}"), "x"); Age($"t/d{h}", TimeSpan.FromSeconds(h)); }
        string S(string x) => Words(Sorted($"t '-type' f {x}").Select(Path.GetFileName)!);
        Assert.Equal("d3600 d82800", S("'-mtime' 0"));
        Assert.Equal("d172000 d90000", S("'-mtime' 1"));
        Assert.Equal("d180000", S("'-mtime' 2"));
        Assert.Equal("d172000 d180000 d90000", S("'-mtime' '+0'"));
        Assert.Equal("d180000", S("'-mtime' '+1'"));
        Assert.Equal("d3600 d82800", S("'-mtime' '-1'"));
        Assert.Equal("d172000 d3600 d82800 d90000", S("'-mtime' '-2'"));
        Assert.Equal("d82800 d90000", S("'-mtime' 0.5"));
        Assert.Equal("d172000 d180000", S("'-mtime' '+0.5'"));
        Find("t '-mmin' abc").AssertFailed(1, "invalid argument `abc' to `-mmin'");
        Find("t '-mtime'").AssertFailed(1, "missing argument to `-mtime'");
    }

    [Fact]
    public void AtimeCtimeAmminCmin_UseTheirOwnTimestamps()
    {
        Directory.CreateDirectory(P("t"));
        File.WriteAllText(P("t/old"), "x");
        File.SetLastAccessTimeUtc(P("t/old"), DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(P("t/old"), DateTime.UtcNow);
        Assert.Equal(new[] { "t/old" }, Sorted("t '-type' f '-atime' 3"));
        Assert.Equal(new[] { "t/old" }, Sorted("t '-type' f '-amin' '+3000'"));
        Assert.Empty(Sorted("t '-type' f '-mtime' 3"));
        Assert.Equal(new[] { "t/old" }, Sorted("t '-type' f '-mmin' '-2'"));
        // ctime (change time) of a file written just now is "now": within the last minute.
        Assert.Equal(new[] { "t/old" }, Sorted("t '-type' f '-cmin' '-1'"));
        Assert.Empty(Sorted("t '-type' f '-ctime' '+5'"));
    }

    [Fact]
    public void Daystart_MeasuresFromTheMidnightEndingToday()   // oracle: origin = start of TOMORROW
    {
        Directory.CreateDirectory(P("t"));
        File.WriteAllText(P("t/today"), "x"); File.WriteAllText(P("t/yday"), "x"); File.WriteAllText(P("t/old2"), "x");
        File.SetLastWriteTime(P("t/today"), DateTime.Today.AddSeconds(2));
        File.SetLastWriteTime(P("t/yday"), DateTime.Today.AddHours(-12));
        File.SetLastWriteTime(P("t/old2"), DateTime.Today.AddDays(-2).AddHours(-1));
        string S(string x) => Words(Sorted($"t '-type' f '-daystart' {x}").Select(Path.GetFileName)!);
        Assert.Equal("today", S("'-mtime' 0"));
        Assert.Equal("yday", S("'-mtime' 1"));
        Assert.Equal("old2", S("'-mtime' 3"));
        Assert.Equal("old2 yday", S("'-mtime' '+0'"));
        // the option may come after the test it modifies (GNU applies it globally)
        Assert.Equal("today", Words(Sorted("t '-type' f '-mtime' 0 '-daystart'").Select(Path.GetFileName)!));
    }

    [Fact]
    public void NewerXY_AllReferenceKinds()   // oracle: -newer/-newermm/-newermt/-newercm/-anewer/-cnewer
    {
        File.SetLastWriteTimeUtc(P("sub/b.txt"), new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(P("d2/c.log"), new DateTime(2024, 3, 5, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(P("a.txt"), new DateTime(2024, 3, 10, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-type' f '-newer' d2/c.log"));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-type' f '-newermm' d2/c.log"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log" }, Sorted(". '-type' f '-newermt' 2024-03-04T00:00:00Z"));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-type' f '-newermt' '@1709600000'"));            // 2024-03-05T01:33:20Z
        Assert.Equal(new[] { "./sub/b.txt" }, Sorted(". '-type' f '!' '-newermt' 2024-03-02T00:00:00Z"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-newerBt' 2000-01-01T00:00:00Z"));
        File.SetLastAccessTimeUtc(P("sub/b.txt"), new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastAccessTimeUtc(P("a.txt"), new DateTime(2024, 3, 11, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastAccessTimeUtc(P("d2/c.log"), new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-type' f '-anewer' d2/c.log"));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-type' f '-neweram' d2/c.log"));                  // atime of item vs mtime of ref
        Find(". '-newerat' d2/c.log").AssertFailed(1, "I cannot figure out how to interpret ‘d2/c.log’ as a date or time");
        Find(". '-newermt' garbage").AssertFailed(1, "I cannot figure out how to interpret ‘garbage’ as a date or time");
        Find(". '-newerXm' a.txt").AssertFailed(1, "invalid predicate `-newerXm'");
        Find(". '-newer' nosuch").AssertFailed(1, "‘nosuch’: No such file or directory");
    }

    // ───────────── size / links / inode / access ─────────────

    [Fact]
    public void Size_AllUnits_RoundUp()   // oracle files of 0 1 511 512 513 1024 1025 3000 1M 1M+1 bytes
    {
        Directory.CreateDirectory(P("z"));
        foreach (var n in new[] { 0, 1, 511, 512, 513, 1024, 1025, 3000, 1048576, 1048577 })
            File.WriteAllBytes(P($"z/s{n}"), new byte[n]);
        string S(string x) => Words(Sorted($"z '-type' f '-size' {x}").Select(Path.GetFileName)!);
        Assert.Equal("s0", S("0"));
        Assert.Equal("s1 s511 s512", S("1"));
        Assert.Equal("s1024 s513", S("2"));
        Assert.Equal("s0", S("-1"));
        Assert.Equal("s1 s511 s512", S("1b"));
        Assert.Equal("s512", S("512c"));
        Assert.Equal("s1", S("1c"));
        Assert.Equal("s1 s1024 s511 s512 s513", S("1k"));
        Assert.Equal("s1025", S("2k"));
        Assert.Equal("s0 s1 s1024 s511 s512 s513", S("-2k"));
        Assert.Equal("s1", S("1w"));
        Assert.Equal("s511 s512", S("256w"));
        Assert.Equal("s1048577", S("2M"));
        Assert.Equal("s1048577", S("+1M"));
        Assert.Equal("s0", S("-1G"));
        Assert.Equal("s1048577", S("+1024k"));
        Assert.Equal("s3000", S("3000c"));
        Find("z '-size' 1kB").AssertFailed(1, "invalid -size type `B'");
        Find("z '-size' 1T").AssertFailed(1, "invalid -size type `T'");
        Find("z '-size' 1x").AssertFailed(1, "invalid -size type `x'");
        Find("z '-size'").AssertFailed(1, "missing argument to `-size'");
    }

    private bool HardLink(string link, string existing)
    {
        if (OperatingSystem.IsWindows())
            return CreateHardLinkW(P(link), P(existing), IntPtr.Zero);
        var psi = new ProcessStartInfo("ln") { UseShellExecute = false };
        psi.ArgumentList.Add(P(existing)); psi.ArgumentList.Add(P(link));
        using var p = Process.Start(psi)!; p.WaitForExit(); return p.ExitCode == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLinkW(string newFile, string existing, IntPtr sec);

    [SkippableFact]
    public void LinksInumSamefile_SeeHardLinks()
    {
        Skip.IfNot(HardLink("hard.txt", "a.txt"), "hard links are not available on this volume");
        Assert.Equal(new[] { "./a.txt", "./hard.txt" }, Sorted(". '-type' f '-links' 2"));
        Assert.Equal(new[] { "./a.txt", "./hard.txt" }, Sorted(". '-type' f '-links' '+1'"));
        Assert.Equal(new[] { "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-links' '-2'"));
        Assert.Empty(Sorted(". '-type' f '-links' 0"));
        Assert.Equal(new[] { "./a.txt", "./hard.txt" }, Sorted(". '-samefile' a.txt"));
        var inum = Find("a.txt '-printf' '%i'", raw: true).AssertSuccess().Stdout.Trim();
        Assert.Equal(new[] { "./a.txt", "./hard.txt" }, Sorted($". '-inum' {inum}"));
        Find(". '-links' x").AssertFailed(1, "invalid argument `x' to `-links'");
        Find(". '-samefile' nosuch").AssertFailed(1, "‘nosuch’: No such file or directory");
    }

    [Fact]
    public void ExecutableReadableWritable_UseTheEffectiveUsersAccess()
    {
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-readable'"));
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./sub/b.txt" }, Sorted(". '-type' f '-writable'"));
        Assert.Empty(Sorted(". '-type' f '-executable'"));
        Assert.Equal(new[] { ".", "./d2", "./sub" }, Sorted(". '-type' d '-executable'"));     // search permission
    }

    [SkippableFact]
    public void Lname_Ilname_MatchTheLinkTarget()
    {
        FileLink("lnk", "a.txt");
        FileLink("sub/l2", "sub/b.txt");
        Assert.Equal(new[] { "./lnk" }, Sorted(". '-lname' 'a*'"));
        Assert.Equal(new[] { "./sub/l2" }, Sorted(". '-lname' '*b.txt'"));
        Assert.Equal(new[] { "./lnk" }, Sorted(". '-ilname' 'A*'"));
        Assert.Empty(Sorted(". '-lname' ''"));
        Assert.Equal(new[] { "./lnk", "./sub/l2" }, Sorted(". '-lname' '*'"));                 // only links match
        Assert.Equal(new[] { "./lnk", "./sub/l2" }, Sorted(". '-type' l"));
    }

    [Fact]
    public void Wholename_IsPath_AndNameNeverMatchesASlash()
    {
        Assert.Equal(new[] { "./sub/b.txt" }, Sorted(". '-wholename' './sub/*'"));
        Assert.Equal(new[] { "./sub/b.txt" }, Sorted(". '-iwholename' './SUB/*'"));
        Assert.Equal(new[] { "./sub", "./sub/b.txt" }, Sorted(". '-path' '*b*'"));
        Assert.Empty(Sorted(". '-name' 'sub/b.txt'"));
        Assert.Empty(Sorted(". '-name' 'sub/'"));
    }

    // ───────────── -regextype ─────────────

    [Fact]
    public void Regextype_DefaultIsEmacs_AndTheOthersFollowTheirSyntax()
    {
        Assert.Equal(new[] { "./a.txt", "./sub/b.txt" }, Sorted(". '-regex' '.*/[a-c]\\.txt'"));
        Assert.Equal(new[] { "./a.txt", "./sub/b.txt" }, Sorted(". '-regex' '.*/\\(a\\|b\\)\\.txt'"));           // emacs: \( \| \)
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-regex' '.*/a\\.t+xt'"));                                    // emacs: + is an operator
        Assert.Empty(Sorted(". '-regex' '.*/a\\.t\\+xt'"));                                                       // \+ is a literal plus
        Assert.Empty(Sorted(". '-regex' '.*/.\\{1\\}\\.txt'"));                                                    // emacs has no intervals
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-regex' '.*/a\\.t*?xt'"));                                    // emacs non-greedy
        Assert.Equal(new[] { "./a.txt", "./sub/b.txt" }, Sorted(". '-regex' '.*/\\w\\.txt'"));
        Assert.Equal(new[] { "./sub/b.txt" }, Sorted(". '-regex' '.*/\\bb\\.txt'"));
        Assert.Empty(Sorted(". '-regex' '.*/[[:alpha:]]\\.txt'"));                                                // emacs: no character classes
        Assert.Equal(new[] { "./a.txt", "./sub/b.txt" }, Sorted(". '-regextype' posix-extended '-regex' '.*/(a|b)\\.txt'"));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-regextype' posix-extended '-regex' '.*/a\\.t+xt'"));
        Assert.Equal(new[] { "./a.txt", "./sub/b.txt" }, Sorted(". '-regextype' posix-extended '-regex' '.*/.{1}\\.txt'"));
        Assert.Equal(new[] { "./a.txt", "./sub/b.txt" }, Sorted(". '-regextype' egrep '-regex' '.*/(a|b)\\.txt'"));
        Assert.Equal(new[] { "./a.txt", "./sub/b.txt" }, Sorted(". '-regextype' posix-basic '-regex' '.*/\\(a\\|b\\)\\.txt'"));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-regextype' posix-basic '-regex' '.*/a\\.t\\+xt'"));
        Assert.Equal(new[] { "./a.txt", "./sub/b.txt" }, Sorted(". '-regextype' posix-basic '-regex' '.*/[[:alpha:]]\\.txt'"));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-regextype' posix-minimal-basic '-regex' '.*/a\\.txt'"));
        Assert.Empty(Sorted(". '-regex' 'a\\.txt'"));                                                              // whole path, anchored
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-regex' './a\\.txt'"));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-iregex' '.*/A\\.TXT'"));
    }

    [Fact]
    public void Regextype_Errors()
    {
        Find(". '-regextype' bogus '-regex' x").AssertFailed(1,
            "Unknown regular expression type ‘bogus’; valid types are ‘findutils-default’, ‘ed’, ‘emacs’, ‘gnu-awk’, ‘grep’, ‘posix-awk’, ‘awk’, ‘posix-basic’, ‘posix-egrep’, ‘egrep’, ‘posix-extended’, ‘posix-minimal-basic’, ‘sed’.");
        Find(". '-regex' '\\('").AssertFailed(1, "failed to compile regular expression '\\(': Unmatched ( or \\(");
    }

    // ───────────── -printf and friends ─────────────

    [Fact]
    public void Printf_PathDirectives()
    {
        Assert.Equal(new[] { "[./a.txt|a.txt|.|a.txt|.|2|f|f|1|%]" },
            Find(". '-type' f '-name' a.txt '-printf' '[%p|%f|%h|%P|%H|%s|%y|%Y|%d|%%]\\n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "[./sub/b.txt|b.txt|./sub|sub/b.txt|.|3|f|2]" },
            Find(". '-type' f '-name' b.txt '-printf' '[%p|%f|%h|%P|%H|%s|%y|%d]\\n'").AssertSuccess().Lines);
        // starting-point conventions (oracle): %P empty, %h = dirname, trailing slashes kept by %f
        Assert.Equal(new[] { "sub/||.|sub/|sub/" }, Find("sub/ '-maxdepth' 0 '-printf' '%p|%P|%h|%f|%H\\n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "./sub|sub|.|./sub||0" }, Find("./sub '-maxdepth' 0 '-printf' '%p|%f|%h|%H|%P|%d\\n'").AssertSuccess().Lines);
        Assert.Equal(new[] { ".|.|.|.|" }, Find(". '-maxdepth' 0 '-printf' '%p|%f|%h|%H|%P\\n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "sub//b.txt|b.txt|sub/|sub//" }, Find("sub// '-name' b.txt '-printf' '%p|%f|%h|%H\\n'").AssertSuccess().Lines);
    }

    [Fact]
    public void Printf_ModeAndSymbolicMode()
    {
        Assert.Equal(new[] { "644|-rw-r--r--" }, Find("a.txt '-printf' '%m|%M\\n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "755|drwxr-xr-x|d" }, Find("sub '-maxdepth' 0 '-printf' '%m|%M|%y\\n'").AssertSuccess().Lines);
    }

    [Fact]
    public void Printf_Sizes_Blocks_Sparseness_Links()
    {
        Assert.Equal(new[] { "2|4|8|1|2048" }, Find("a.txt '-printf' '%s|%k|%b|%n|%S\\n'").AssertSuccess().Lines);
        var dirLine = Find("sub '-maxdepth' 0 '-printf' '%s|%k|%b|%S\\n'").AssertSuccess().Lines.Single();
        if (OperatingSystem.IsMacOS())
            Assert.Matches(@"^\d+\|0\|0\|0$", dirLine);   // APFS: a directory occupies no allocation blocks
        else
            Assert.Equal("4096|4|8|1", dirLine);           // ext4 / synthesised 4 KiB on Windows
    }

    [Fact]
    public void Printf_WidthsFlagsPrecision_FollowGnu()   // oracle: [%10p][%-10p][%.3p][%10.3f][%5s][%-5s][%05d][%05m][%+d][% d][%#m][%05k]
    {
        Assert.Equal(new[] { "[     a.txt][a.txt     ][a.t][       a.t][    2][2    ][00000][00644][+0][ 0][0644][    4]" },
            Find("a.txt '-printf' '[%10p][%-10p][%.3p][%10.3f][%5s][%-5s][%05d][%05m][%+d][% d][%#m][%05k]\\n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "[   a.]" }, Find("a.txt '-printf' '[%5.2p]\\n'").AssertSuccess().Lines);
    }

    [Fact]
    public void Printf_Escapes()   // oracle: a\tb\\c\101\x41 -> a<TAB>b\cA\x41 ; \c stops output ; \0 is NUL
    {
        Assert.Equal("a\tb\\cA\\x41\n", Find("a.txt '-printf' 'a\\tb\\\\c\\101\\x41\\n'", raw: true).NoFail().Stdout.Replace("\r\n", "\n"));
        Assert.Equal("one", Find("a.txt '-printf' 'one\\ctwo'", raw: true).NoFail().Stdout.TrimEnd());
        Assert.Equal("a\0b", Find("a.txt '-printf' 'a\\0b'", raw: true).NoFail().Stdout);
        Assert.Equal("no newline", Find("a.txt '-printf' 'no newline'", raw: true).NoFail().Stdout);   // -printf adds nothing
    }

    [Fact]
    public void Printf_Errors_And_Warnings()
    {
        Find("a.txt '-printf' '%'").AssertFailed(1, "find: error: % at end of format string");
        Find("a.txt '-printf'").AssertFailed(1, "missing argument to `-printf'");
        var r = Find("a.txt '-printf' '%z\\n'", raw: true);
        Assert.Contains("unrecognized format directive `%z'", r.Stderr);
        Assert.Equal("%z", r.Stdout.Trim());
    }

    [Fact]
    public void Printf_TimeDirectives()   // oracle formats (TZ-independent: rendered in the local zone like GNU)
    {
        var t = new DateTime(2024, 3, 5, 14, 7, 9, 123, DateTimeKind.Utc).AddTicks(4567);   // .1234567
        File.SetLastWriteTimeUtc(P("a.txt"), t);
        var l = t.ToLocalTime();
        string frac = "." + (l.Ticks % TimeSpan.TicksPerSecond).ToString("D7") + "000";
        var expected = new[]
        {
            $"[{l.ToString("ddd MMM", CultureInfo.InvariantCulture)} {l.Day,2} {l:HH:mm:ss}{frac} {l:yyyy}]",
            $"[{l:yyyy-MM-dd HH:mm:ss}{frac}]",
            $"[{new DateTimeOffset(t).ToUnixTimeSeconds()}{frac}]",
        };
        Assert.Equal(expected, Find("a.txt '-printf' '[%t]\\n[%TY-%Tm-%Td %TH:%TM:%TS]\\n[%T@]\\n'", raw: true).AssertSuccess().Stdout.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
        Assert.Equal($"[{l:yy}|{l.DayOfYear:D3}|{(int)l.DayOfWeek}|{l.ToString("MMMM|dddd", CultureInfo.InvariantCulture)}|{l:hh tt}|{l:HH:mm}|{l:MM/dd/yy}|{l:yyyy-MM-dd}|{new DateTimeOffset(t).ToUnixTimeSeconds()}]",
            Find("a.txt '-printf' '[%Ty|%Tj|%Tw|%TB|%TA|%TI %Tp|%TR|%Tx|%TF|%Ts]'", raw: true).AssertSuccess().Stdout.Replace("PM", "PM").Replace("AM", "AM"));
        Assert.Equal($"[{l.Hour,2}|{(l.Hour % 12 == 0 ? 12 : l.Hour % 12),2}|{l:hh:mm:ss tt}|{l:HH:mm:ss}{frac}]",
            Find("a.txt '-printf' '[%Tk|%Tl|%Tr|%TT]'", raw: true).AssertSuccess().Stdout);
    }

    [Fact]
    public void Printf_IsExactBytes_NoImplicitNewline_AndReplacesTheImplicitPrint()
    {
        Assert.Equal("a.txt;", Find(". '-name' a.txt '-printf' '%f;'", raw: true).AssertSuccess().Stdout);
    }

    // ───────────── -ls / -fls / -fprint* ─────────────

    [Fact]
    public void Ls_UsesTheGnuColumnLayout()   // oracle: inode(9) blocks(6) perms nlink(3) owner(-8) group(-8) size(8) date name [-> target]
    {
        File.SetLastWriteTime(P("a.txt"), new DateTime(2020, 3, 5, 14, 7, 9, DateTimeKind.Local));
        var line = Find("a.txt '-ls'").AssertSuccess().Lines.Single();
        Assert.Matches(@"^\s*\d+ +4 -rw-r--r-- +\d+ \S+ +\S+ +2 Mar  5  2020 a\.txt$", line);
        Directory.SetLastWriteTime(P("sub"), DateTime.Now.AddDays(-1));
        var dir = Find("sub '-maxdepth' 0 '-ls'").AssertSuccess().Lines.Single();
        Assert.Matches(OperatingSystem.IsMacOS()   // APFS directories: 0 blocks and a size that is not 4096
            ? @"^\s*\d+ +0 drwxr-xr-x +\d+ \S+ +\S+ +\d+ [A-Z][a-z]{2} [ 0-3]\d \d\d:\d\d sub$"
            : @"^\s*\d+ +4 drwxr-xr-x +\d+ \S+ +\S+ +4096 [A-Z][a-z]{2} [ 0-3]\d \d\d:\d\d sub$", dir);
    }

    [Fact]
    public void Fprint_Fprintf_Fprint0_Fls_WriteFiles_AndTruncateAtParseTime()
    {
        string o = P("out.dat");
        Find($". '-name' '*.txt' '-fprint' {Q(o)}").AssertSuccess();
        Assert.Equal(new[] { "./a.txt", "./sub/b.txt" }, File.ReadAllLines(o).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Find($". '-name' a.txt '-fprint' {Q(o)} '-fprint' {Q(o)}").AssertSuccess();
        Assert.Equal(new[] { "./a.txt", "./a.txt" }, File.ReadAllLines(o));                 // one shared handle
        Find($". '-name' nomatch '-fprint' {Q(o)}").AssertSuccess();
        Assert.Equal("", File.ReadAllText(o));                                              // opened (truncated) even without matches
        Find($". '-name' a.txt '-fprintf' {Q(o)} '[%p|%s]\\n'").AssertSuccess();
        Assert.Equal("[./a.txt|2]\n", File.ReadAllText(o).Replace("\r\n", "\n"));
        Find($". '-name' '*.txt' '-fprint0' {Q(o)}").AssertSuccess();
        // find does not sort (readdir order differs per filesystem: APFS vs ext4/NTFS): compare the NUL records as a set.
        var nul = File.ReadAllText(o);
        Assert.EndsWith("\0", nul);
        Assert.Equal(new[] { "./a.txt", "./sub/b.txt" }, nul.Split('\0', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Find($". '-name' a.txt '-fls' {Q(o)}").AssertSuccess();
        Assert.Matches(@"^\s*\d+ +4 -rw-r--r-- .* \./a\.txt\r?\n$", File.ReadAllText(o));
        var withPrint = Find($". '-name' a.txt '-fprint' {Q(o)} '-print'").AssertSuccess();
        Assert.Equal(new[] { "./a.txt" }, withPrint.Lines);
        Assert.Equal("./a.txt\n", File.ReadAllText(o).Replace("\r\n", "\n"));
        Find($". '-name' a.txt '-fprint' {Q(P("nodir/x"))}").AssertFailed(1, "No such file or directory");
        Assert.Equal(new[] { "./a.txt" }, Find(". '-name' a.txt '-fprint' /dev/stdout").AssertSuccess().Lines);
    }

    // ───────────── actions are expression members ─────────────

    [Fact]
    public void ExplicitActions_ReplaceTheImplicitPrint()   // oracle: -print -print, -name a -o -name d2 -print, -true -false
    {
        Assert.Equal(new[] { "./a.txt", "./a.txt" }, Find(". '-name' a.txt '-print' '-print'").AssertSuccess().Lines);
        Assert.Equal(new[] { "./d2" }, Find(". '-name' a.txt '-o' '-name' d2 '-print'").AssertSuccess().Lines);
        Assert.Equal(new[] { "p", "./a.txt" }, Find(". '-name' a.txt '-printf' 'p\\n' '-print'").AssertSuccess().Lines);
        Assert.Empty(Find(". '-maxdepth' 0 '-true' '-false'").AssertSuccess().Lines);
        Assert.Equal(new[] { "./a.txt", "./sub" }, Sorted(". '-maxdepth' 1 '(' '-name' a.txt '-print' ',' '-name' sub '-print' ')'"));
        Assert.Equal(new[] { "./sub" }, Sorted(". '-maxdepth' 1 '-name' a.txt ',' '-name' sub"));           // value of a list = its last member
        Assert.Equal(new[] { "./a.txt" }, Find(". '-name' a.txt '-print' '-quit'").AssertSuccess().Lines);
    }

    [Fact]
    public void ExpressionSyntaxErrors_UseGnuWording()
    {
        Find(". '(' ')'").AssertFailed(1, "invalid expression; empty parentheses are not allowed.");
        Find(". '-a'").AssertFailed(1, "expected an expression after '-a'");
        Find(". '-name' x '-o'").AssertFailed(1, "expected an expression after '-o'");
        Find(". '!'").AssertFailed(1, "expected an expression after '!'");
        Find(". ','").AssertFailed(1, "expected an expression after ','");
    }

    [Fact]
    public void Quit_StopsTheWalk_Immediately()
    {
        Assert.Single(Find(". '-name' '*.txt' '-print' '-quit'").AssertSuccess().Lines);
        Assert.Empty(Find(". '-type' f '-quit'").AssertSuccess().Lines);                       // quit before the implicit print
        Assert.Equal(0, Find(". '-name' nomatch '-o' '-quit'").AssertSuccess().ExitCode);
    }

    [Fact]
    public void Prune_PrintsThePrunedDirectory_AndIsIgnoredUnderDepth()
    {
        Assert.Equal(new[] { "./a.txt", "./d2/c.log" }, Sorted(". '-name' sub '-prune' '-o' '-type' f '-print'"));
        Assert.Equal(new[] { "./sub" }, Sorted(". '-name' sub '-prune' '-print'"));
        Assert.Equal(new[] { "./sub" }, Sorted(". '-depth' '-name' sub '-prune' '-print'"));    // does nothing under -depth
        Assert.Equal(new[] { "." }, Sorted(". '-prune'"));
        Assert.Equal(new[] { ".", "sub" }.OrderBy(x => x, StringComparer.Ordinal).ToArray(), Sorted("sub . '-prune'"));
    }

    [Fact]
    public void Delete_WithPrune_IsGnusError()
    {
        Directory.CreateDirectory(P("pp"));
        Find("pp '-prune' '-delete'").AssertFailed(1, "The -delete action automatically turns on -depth, but -prune does nothing when -depth is in effect.");
        Assert.True(Directory.Exists(P("pp")));
    }

    // ───────────── -exec / -execdir / -ok ─────────────

    [Fact]
    public void Exec_BracesAreReplacedInsideWords_AndStatusIsTheTestResult()
    {
        Assert.Equal("./a.txtx", Find(". '-name' a.txt '-exec' echo '{}x' ';'", raw: true).AssertSuccess().Lines.Single());
        Assert.Equal("x./a.txt", Find(". '-name' a.txt '-exec' echo 'x{}' ';'", raw: true).AssertSuccess().Lines.Single());
        Assert.Equal("a ./a.txt b ./a.txt", Find(". '-name' a.txt '-exec' echo a '{}' b '{}' ';'", raw: true).AssertSuccess().Lines.Single());
        // a failing command makes the -exec test false: the following -print does not run
        Assert.Empty(Find(". '-name' a.txt '-exec' false ';' '-print'").Lines);
    }

    [Fact]
    public void ExecPlus_NeedsBracesImmediatelyBeforeThePlus_AndRunsNothingWithoutMatches()
    {
        Find(". '-name' a.txt '-exec' echo A '{}' B '+'").AssertFailed(1, "missing argument to `-exec'");
        Find(". '-name' a.txt '-exec' echo ran '+'").AssertFailed(1, "missing argument to `-exec'");
        Find(". '-name' a.txt '-exec' echo '{}' '{}' '+'").AssertFailed(1, "Only one instance of {} is supported with -exec ... +");
        Find(". '-name' a.txt '-exec' ';'").AssertFailed(1, "missing argument to `-exec'");
        Assert.Empty(Find(". '-name' zzz '-exec' echo ran '{}' '+'", raw: true).AssertSuccess().Lines);
        var batched = Find(". '-name' '*.txt' '-exec' echo B '{}' '+'", raw: true).AssertSuccess().Lines.Single();
        Assert.Contains("./a.txt", batched); Assert.Contains("./sub/b.txt", batched);
    }

    [Fact]
    public void Execdir_RunsInTheDirectoryOfTheMatch_WithDotSlashNames()
    {
        Assert.Equal(new[] { "./a.txt", "./b.txt" }, Find(". '-name' '*.txt' '-execdir' echo '{}' ';'", raw: true).AssertSuccess().Lines.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("./b.txt x ./b.txt", Find("sub '-name' '*.txt' '-execdir' echo '{}' x '{}' ';'", raw: true).AssertSuccess().Lines.Single());
        Assert.Equal(new[] { "./." }, Find(". '-maxdepth' 0 '-execdir' echo '{}' ';'", raw: true).AssertSuccess().Lines);
        Assert.Equal(new[] { "./a.txt" }, Find("a.txt '-maxdepth' 0 '-execdir' echo '{}' ';'", raw: true).AssertSuccess().Lines);
        var cwd = Find("sub '-name' b.txt '-execdir' pwd ';'").AssertSuccess().Lines.Single();
        Assert.Equal(Path.GetFullPath(P("sub")).Replace('\\', '/').TrimEnd('/'), cwd.Trim().Replace('\\', '/').TrimEnd('/'), ignoreCase: OperatingSystem.IsWindows());
        // + batches the matches of one directory (separate runs when the directory changes)
        var plus = Find(". '-type' f '-execdir' echo + '{}' '+'", raw: true).AssertSuccess().Lines.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "+ ./a.txt", "+ ./b.txt", "+ ./c.log" }, plus);
    }

    [Fact]
    public void Ok_AsksOnStderr_AndReadsTheAnswerFromStdin()
    {
        var yes = Find(". '-name' a.txt '-ok' echo OK '{}' ';'", pipeline: "'y'", raw: true).NoFail();
        Assert.Contains("< echo ... ./a.txt > ? ", yes.Stderr);
        Assert.Equal(new[] { "OK ./a.txt" }, yes.Lines);
        Assert.Empty(Find(". '-name' a.txt '-ok' echo OK '{}' ';'", pipeline: "'n'", raw: true).NoFail().Lines);
        Assert.Empty(Find(". '-name' a.txt '-ok' echo OK '{}' ';'", pipeline: "''", raw: true).NoFail().Lines);        // empty answer = no
        Assert.Equal(new[] { "OK ./a.txt" }, Find(". '-name' a.txt '-ok' echo OK '{}' ';'", pipeline: "'Yes'", raw: true).NoFail().Lines);
        Assert.Empty(Find(". '-name' a.txt '-ok' echo OK '{}' ';'", raw: true).NoFail().Lines);                        // EOF = no
        var two = Find(". '-name' '*.txt' '-ok' echo OK '{}' ';'", pipeline: "'y','n'", raw: true).NoFail();
        Assert.Single(two.Lines);                                                                                               // one shared stdin: y, then n
        Assert.Equal(new[] { "OK ./a.txt" }, Find(". '-name' a.txt '-okdir' echo OK '{}' ';'", pipeline: "'y'", raw: true).NoFail().Lines);
        Assert.Contains("./a.txt", Find(". '-name' a.txt '-ok' echo OK '{}' ';' '-o' '-print'", pipeline: "'n'").NoFail().Lines);
    }

    // ───────────── -L / -H / -follow / -xdev and the accepted no-ops ─────────────

    [Fact]
    public void AcceptedNoOps_DoNotChangeTheResult()
    {
        foreach (var o in new[] { "'-xdev'", "'-mount'", "'-noleaf'", "'-ignore_readdir_race'", "'-nowarn'", "'-warn'", "'-follow'" })
            Assert.Equal(new[] { "./a.txt" }, Sorted($". {o} '-name' a.txt"));
        Assert.Equal(new[] { "./a.txt" }, Sorted(". '-name' a.txt '-xdev' '-print'"));
    }

    [Fact]
    public void DirLinks_P_H_L_Follow()   // oracle: lt/dl -> d2
    {
        Directory.CreateDirectory(P("lt"));
        DirLink("lt/dl", "d2");
        Assert.Equal(new[] { "lt", "lt/dl" }, Sorted("lt"));                                          // -P (default): the link is a leaf
        Assert.Equal(new[] { "lt", "lt/dl" }, Sorted("'-P' lt"));
        Assert.Equal(new[] { "lt", "lt/dl" }, Sorted("'-H' lt"));                                     // -H only follows command-line links
        Assert.Equal(new[] { "lt", "lt/dl", "lt/dl/c.log" }, Sorted("'-L' lt"));
        Assert.Equal(new[] { "lt", "lt/dl", "lt/dl/c.log" }, Sorted("lt '-follow'"));
        Assert.Equal(new[] { "lt/dl" }, Sorted("lt/dl"));                                             // a link root is not entered by default ...
        Assert.Equal(new[] { "lt/dl", "lt/dl/c.log" }, Sorted("'-H' lt/dl"));                        // ... but is under -H and -L
        Assert.Equal(new[] { "lt/dl", "lt/dl/c.log" }, Sorted("'-L' lt/dl"));
        Assert.Equal(new[] { "lt", "lt/dl" }, Sorted("'-L' lt '-type' d"));                          // followed dir links are directories
        Assert.Equal(new[] { "lt/dl" }, Sorted("lt '-type' l"));
        Assert.Empty(Sorted("'-L' lt '-type' l"));
        Assert.Equal(new[] { "lt/dl l", "lt d" }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Sorted("lt '-printf' '%p %y\\n'").Select(x => x).ToArray());
        Assert.Equal(new[] { "lt d", "lt/dl d", "lt/dl/c.log f" }, Sorted("'-L' lt '-printf' '%p %y\\n'"));
    }

    [SkippableFact]
    public void FileLinks_P_L_TypeAndTarget()
    {
        FileLink("lnk", "a.txt");
        FileLink("dang", "nosuch");
        Assert.Equal(new[] { "lnk l f", "dang l N" }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Sorted(". '-type' l '-printf' '%p %y %Y\\n'").Select(x => x.Substring(2)).ToArray());
        Assert.Equal(new[] { "./dang" }, Sorted("'-L' . '-type' l"));                                // dangling links stay links under -L
        Assert.Equal(new[] { "./a.txt", "./d2/c.log", "./lnk", "./sub/b.txt" }, Sorted("'-L' . '-type' f"));
        Assert.Equal(new[] { "lnk f f 2" }, Find("'-L' lnk '-printf' '%p %y %Y %s\\n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "lnk l f" }, Find("'-P' lnk '-printf' '%p %y %Y\\n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "lnk f f" }, Find("'-H' lnk '-printf' '%p %y %Y\\n'").AssertSuccess().Lines);
        Assert.Equal(new[] { "./lnk|a.txt", "./dang|nosuch" }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Sorted(". '-type' l '-printf' '%p|%l\\n'"));
    }

    [Fact]
    public void FileSystemLoops_AreReported_AndTheWalkContinues()   // oracle: find: File system loop detected; ‘lt3/loop/lt3’ is part of the same file system loop as ‘lt3’.
    {
        Directory.CreateDirectory(P("lt3"));
        DirLink("lt3/loop", ".");   // a link to its own parent directory
        var r = Find("'-L' lt3");
        Assert.Contains("File system loop detected", r.Stderr);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("lt3", r.Lines);
    }

    [Fact]
    public void Walk_StaysLazy_ForActionsThatPrint()
    {
        // -print on a deep tree is consumed incrementally: asking for the first record must not need the whole tree.
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"Set-Location {Q(_dir)}; Invoke-BashFind . '-name' '*.txt' '-print' | Select-Object -First 1 | ForEach-Object {{ $_.BashText }}");
        r.AssertSuccess();
        Assert.Single(r.Lines);
    }
}
