using System.Diagnostics;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>touch -t STAMP</c>, <c>-h</c>/<c>--no-dereference</c> and <c>--time=WORD</c>. Expected results
/// (which time changed, the error texts, whether anything was created) were checked against GNU
/// coreutils 9.4 under <c>wsl bash</c>. Link tests need a link the test process may create:
/// Unix symlinks, or Windows directory junctions (no privilege needed, unlike file symlinks); they
/// skip with a reason where neither is available.
/// </summary>
public class TouchOptionsTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private static readonly DateTime Old = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Local);
    private static readonly DateTime Stamp = new(2024, 1, 2, 15, 30, 0, DateTimeKind.Local);

    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public TouchOptionsTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-tch-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private string P(string rel) => Path.Combine(_tmp, rel);

    /// <summary>A file whose access AND modification times are both the well-known old instant.</summary>
    private string OldFile(string rel)
    {
        File.WriteAllText(P(rel), "x\n");
        File.SetLastWriteTime(P(rel), Old);
        File.SetLastAccessTime(P(rel), Old);
        return P(rel);
    }

    private static void AssertSame(DateTime expected, DateTime actual) =>
        Assert.True(Math.Abs((expected - actual).TotalSeconds) < 2, $"expected {expected:O}, got {actual:O}");

    // ───────────── -t ─────────────

    [Fact]
    public void T_SetsBothTimesToTheStamp()
    {
        InTmp("Invoke-BashTouch -t 202401021530 f").AssertSuccess();
        AssertSame(Stamp, File.GetLastWriteTime(P("f")));
        AssertSame(Stamp, File.GetLastAccessTime(P("f")));
    }

    [Fact]
    public void T_WithSeconds()
    {
        InTmp("Invoke-BashTouch -t 202401021530.45 f").AssertSuccess();
        AssertSame(Stamp.AddSeconds(45), File.GetLastWriteTime(P("f")));
    }

    [Fact]
    public void T_TwoDigitYear()
    {
        InTmp("Invoke-BashTouch -t 6912312359 f").AssertSuccess();
        AssertSame(new DateTime(1969, 12, 31, 23, 59, 0, DateTimeKind.Local), File.GetLastWriteTime(P("f")));
    }

    [Fact]
    public void T_Attached_AndLongSpelling()
    {
        InTmp("Invoke-BashTouch '-t202401021530' f; Invoke-BashTouch '-t' 202401021530 g").AssertSuccess();
        AssertSame(Stamp, File.GetLastWriteTime(P("f")));
        AssertSame(Stamp, File.GetLastWriteTime(P("g")));
    }

    [Theory]
    [InlineData("0102153")]
    [InlineData("202413021530")]
    [InlineData("202401021530.61")]
    [InlineData("abc")]
    [InlineData("202302291200")]
    public void T_InvalidStamp_IsInvalidDateFormat_AndCreatesNothing(string stamp)
    {
        InTmp($"Invoke-BashTouch -t {stamp} f").AssertFailed(1, $"touch: invalid date format '{stamp}'");
        Assert.False(File.Exists(P("f")));
    }

    [Fact]
    public void T_OnSeveralOperands_InvalidStampTouchesNone()
    {
        InTmp("Invoke-BashTouch -t bad x y").AssertFailed(1, "touch: invalid date format 'bad'");
        Assert.False(File.Exists(P("x")));
        Assert.False(File.Exists(P("y")));
    }

    [Fact]
    public void T_WithDashD_IsAMoreThanOneSourceError_AndCreatesNothing()
    {
        InTmp("Invoke-BashTouch -t 202401021530 -d 2020-01-01 f")
            .AssertFailed(1, "touch: cannot specify times from more than one source", "Try 'touch --help' for more information.");
        Assert.False(File.Exists(P("f")));
    }

    [Fact]
    public void T_WithDashR_IsAMoreThanOneSourceError()
    {
        OldFile("ref");
        InTmp("Invoke-BashTouch -t 202401021530 -r ref f")
            .AssertFailed(1, "touch: cannot specify times from more than one source");
        Assert.False(File.Exists(P("f")));
    }

    [Fact]
    public void T_OnlyAccessTime_WithDashA()
    {
        OldFile("f");
        InTmp("Invoke-BashTouch -a -t 202401021530 f").AssertSuccess();
        AssertSame(Stamp, File.GetLastAccessTime(P("f")));
        AssertSame(Old, File.GetLastWriteTime(P("f")));
    }

    [Fact]
    public void T_NoLongerRefusedAsUnsupported()
    {
        var r = InTmp("Invoke-BashTouch -t 202401021530 f");
        Assert.DoesNotContain("not supported", r.Stderr);
    }

    // ───────────── --time=WORD ─────────────

    [Theory]
    [InlineData("--time=atime")]
    [InlineData("--time=access")]
    [InlineData("--time=use")]
    [InlineData("--time=a")]
    [InlineData("--tim=atime")]   // abbreviated option name
    public void Time_AccessWords_ChangeOnlyTheAccessTime(string flag)
    {
        OldFile("tm");
        InTmp($"Invoke-BashTouch {flag} -t 202401021530 tm").AssertSuccess();
        AssertSame(Stamp, File.GetLastAccessTime(P("tm")));
        AssertSame(Old, File.GetLastWriteTime(P("tm")));
    }

    [Theory]
    [InlineData("--time=mtime")]
    [InlineData("--time=modify")]
    [InlineData("--time=m")]
    public void Time_ModifyWords_ChangeOnlyTheModificationTime(string flag)
    {
        OldFile("tm");
        InTmp($"Invoke-BashTouch {flag} -t 202401021530 tm").AssertSuccess();
        AssertSame(Stamp, File.GetLastWriteTime(P("tm")));
        AssertSame(Old, File.GetLastAccessTime(P("tm")));
    }

    [Fact]
    public void Time_CombinedWithDashM_ChangesBothTimes()
    {
        OldFile("tm");
        InTmp("Invoke-BashTouch --time=atime '-m' -t 202401021530 tm").AssertSuccess();
        AssertSame(Stamp, File.GetLastAccessTime(P("tm")));
        AssertSame(Stamp, File.GetLastWriteTime(P("tm")));
    }

    [Fact]
    public void Time_CombinedWithDashA_StillOnlyAccess()
    {
        OldFile("tm");
        InTmp("Invoke-BashTouch --time=atime -a -t 202401021530 tm").AssertSuccess();
        AssertSame(Stamp, File.GetLastAccessTime(P("tm")));
        AssertSame(Old, File.GetLastWriteTime(P("tm")));
    }

    [Fact]
    public void Time_Bogus_IsAGnuUsageError_AndCreatesNothing()
    {
        InTmp("Invoke-BashTouch --time=bogus x").AssertFailed(1,
            "touch: invalid argument 'bogus' for '--time'",
            "Valid arguments are:",
            "'atime', 'access', 'use'",
            "'mtime', 'modify'");
        Assert.False(File.Exists(P("x")));
    }

    [Fact]
    public void Time_WithoutAnArgument_IsAGetoptError()
    {
        InTmp("Invoke-BashTouch --time").AssertFailed(1, "touch: option '--time' requires an argument");
    }

    // ───────────── -h / --no-dereference ─────────────

    /// <summary>Creates a directory link <paramref name="link"/> -> <paramref name="target"/> (symlink on Unix, junction on Windows).</summary>
    private bool TryMakeDirLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode == 0 && Directory.Exists(link);
            }
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch { return false; }
    }

    private static void SetDirTimes(string dir, DateTime t)
    {
        Directory.SetLastWriteTime(dir, t);
        Directory.SetLastAccessTime(dir, t);
    }

    [SkippableFact]
    public void H_TouchesTheLinkItself_NotItsTarget()
    {
        Directory.CreateDirectory(P("tgt"));
        SetDirTimes(P("tgt"), Old);
        Skip.IfNot(TryMakeDirLink(P("lnk"), P("tgt")), "cannot create a directory link here");

        InTmp("Invoke-BashTouch -h -t 202401021530 lnk").AssertSuccess();

        AssertSame(Old, Directory.GetLastWriteTime(P("tgt")));              // the target is untouched
        AssertSame(Stamp, new DirectoryInfo(P("lnk")).LastWriteTime);       // the link entry itself moved
    }

    [SkippableFact]
    public void NoDereference_LongSpelling_IsTheSameAsDashH()
    {
        Directory.CreateDirectory(P("tgt"));
        SetDirTimes(P("tgt"), Old);
        Skip.IfNot(TryMakeDirLink(P("lnk"), P("tgt")), "cannot create a directory link here");

        InTmp("Invoke-BashTouch --no-dereference -t 202401021530 lnk").AssertSuccess();

        AssertSame(Old, Directory.GetLastWriteTime(P("tgt")));
    }

    [SkippableFact]
    public void WithoutH_TouchFollowsTheLinkAndChangesTheTarget()
    {
        Directory.CreateDirectory(P("tgt"));
        SetDirTimes(P("tgt"), Old);
        Skip.IfNot(TryMakeDirLink(P("lnk"), P("tgt")), "cannot create a directory link here");

        InTmp("Invoke-BashTouch -t 202401021530 lnk").AssertSuccess();

        AssertSame(Stamp, Directory.GetLastWriteTime(P("tgt")));
    }

    [Fact]
    public void H_OnARegularFile_TouchesTheFileAsUsual()
    {
        OldFile("f");
        InTmp("Invoke-BashTouch -h -t 202401021530 f").AssertSuccess();
        AssertSame(Stamp, File.GetLastWriteTime(P("f")));
    }

    [Fact]
    public void H_OnAMissingOperand_DoesNotCreateIt_AndFailsLikeGnu()
    {
        InTmp("Invoke-BashTouch -h nosuchlink")
            .AssertFailed(1, "touch: setting times of 'nosuchlink': No such file or directory");
        Assert.False(File.Exists(P("nosuchlink")));
    }

    [Fact]
    public void H_WithC_MissingOperandIsSilent()
    {
        InTmp("Invoke-BashTouch -hc nosuchlink").AssertSuccess();
        Assert.False(File.Exists(P("nosuchlink")));
    }

    [Fact]
    public void H_DirectCall_BareDashH_IsNotEatenByTheBinder()
    {
        OldFile("f");
        // `-h` has no PowerShell parameter to collide with; this pins that it reaches the cmdlet.
        InTmp("Invoke-BashTouch -h f").AssertSuccess();
        Assert.True((DateTime.Now - File.GetLastWriteTime(P("f"))).TotalMinutes < 5);
    }
}
