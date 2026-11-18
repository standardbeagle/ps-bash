using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Invoke-BashReadlink against GNU coreutils readlink (oracle: coreutils 9.4). The default form prints a symbolic
/// link's target and fails SILENTLY (status 1) for anything else; <c>-v</c> names the reason. <c>-f/-e/-m</c>
/// canonicalize through <see cref="CanonicalPath"/> (its walk is unit-tested in CanonicalPathTests). The symlink
/// cases skip where the host may not create links (Windows without Developer Mode).
/// </summary>
public class InvokeBashReadlinkCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;
    private readonly string _regularFile;

    public InvokeBashReadlinkCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), $"psb-readlink-{Guid.NewGuid():N}".Substring(0, 25));
        Directory.CreateDirectory(_tmpDir);
        _tmpDir = PsBash.Testing.PhysicalPath.Of(_tmpDir); // the answer is canonical (macOS: /var -> /private/var)
        _regularFile = Path.Combine(_tmpDir, "regular.txt");
        File.WriteAllText(_regularFile, "hi\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult Invoke(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'; Set-Location -LiteralPath '" + _tmpDir.Replace("'", "''") + "'").Invoke();
        return CmdResult.Run(pwsh, script);
    }

    private static string[] StderrLines(CmdResult r) => r.Stderr.Length == 0 ? [] : r.Stderr.Split('\n');

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    private static bool TryCreateSymlink(string linkPath, string target, bool directory = false)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(linkPath, target);
            else File.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
        catch (PlatformNotSupportedException) { return false; }
    }

    private static void SamePath(string expected, string actual)
        => Assert.Equal(new FileInfo(expected).FullName, new FileInfo(actual).FullName, ignoreCase: OperatingSystem.IsWindows());

    [Fact]
    public void Readlink_RegularFile_FailsSilently()
    {
        // GNU: readlink(2) says EINVAL for a non-link; without -v nothing is printed, status 1.
        var r = Invoke($"Invoke-BashReadlink {Q(_regularFile)}");
        Assert.Empty(r.Lines);
        Assert.Empty(StderrLines(r));
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Readlink_DashV_RegularFile_SaysInvalidArgument()
    {
        // Bare -v must not bind -Verbose (V decoy); it makes the failure speak.
        var r = Invoke("Invoke-BashReadlink -v regular.txt");
        Assert.Empty(r.Lines);
        Assert.Contains(StderrLines(r), m => m == "readlink: regular.txt: Invalid argument");
        Assert.Equal(1, r.ExitCode);
    }

    [Theory]
    [InlineData("nosuch", "readlink: nosuch: No such file or directory")]
    [InlineData("regular.txt/", "readlink: regular.txt/: Not a directory")]
    [InlineData("regular.txt/x", "readlink: regular.txt/x: Not a directory")]
    [InlineData("regular.txt/..", "readlink: regular.txt/..: Not a directory")]
    [InlineData("a b", "readlink: 'a b': No such file or directory")]
    [InlineData("", "readlink: '': No such file or directory")]
    public void Readlink_DashV_NamesTheReasonAsGnuQuotesIt(string operand, string message)
    {
        var r = Invoke($"Invoke-BashReadlink -v {Q(operand)}");
        Assert.Empty(r.Lines);
        Assert.Contains(StderrLines(r), m => m == message);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Readlink_DashV_AbsoluteOperandInsideCwd_KeptAsTyped()
    {
        var typed = PsBash.Core.BashPath.ToShell(Path.Combine(_tmpDir, "regular.txt"));
        var r = Invoke($"Invoke-BashReadlink -v {Q(typed)}");
        Assert.Contains(StderrLines(r), m => m == $"readlink: {typed}: Invalid argument");
    }

    [Fact]
    public void Readlink_DashE_CanonicalizesWithoutBinderCrash()
    {
        // Bare -e prefix-collides with -ErrorAction/-ErrorVariable; the E decoy binds it.
        var r = Invoke($"Invoke-BashReadlink -e {Q(_regularFile)}");
        Assert.Single(r.Lines);
        SamePath(_regularFile, r.Lines[0]);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void Readlink_DashF_LastComponentMayBeMissing_IntermediateMayNot()
    {
        var r = Invoke("Invoke-BashReadlink -f ghost.txt nosuch/x");
        Assert.Single(r.Lines);
        SamePath(Path.Combine(_tmpDir, "ghost.txt"), r.Lines[0]);
        Assert.Empty(StderrLines(r));     // silent without -v
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Readlink_DashM_NothingNeedExist()
    {
        var r = Invoke("Invoke-BashReadlink -m nosuch/x/../y");
        Assert.Single(r.Lines);
        SamePath(Path.Combine(_tmpDir, "nosuch", "y"), r.Lines[0]);
    }

    [Fact]
    public void Readlink_LastModeFlagWins()
    {
        // As the emitter passes them (OrderedArgCommands quotes every dash word): order kept. A direct call's decoy
        // switches are re-injected first, so only the quoted form can show which came last.
        Assert.Single(Invoke("Invoke-BashReadlink '-e' '-m' nosuch/x").Lines);
        Invoke("Invoke-BashReadlink '-m' '-e' nosuch/x").AssertFailed(1);
    }

    [Fact]
    public void Readlink_DashZ_EndsEachNameWithNul()
    {
        var r = Invoke("(Invoke-BashReadlink -fz regular.txt | ForEach-Object { $_.BashText }) -join ''");
        Assert.EndsWith("regular.txt\0", r.Lines[0]);
    }

    [Fact]
    public void Readlink_NoNewlineWithManyOperands_Warns()
    {
        var r = Invoke("Invoke-BashReadlink -fn regular.txt regular.txt");
        Assert.Equal(2, r.Lines.Count);
        Assert.Contains(StderrLines(r), m => m == "readlink: ignoring --no-newline with multiple arguments");
    }

    [Fact]
    public void Readlink_MissingOperand_Errors()
    {
        var r = Invoke("Invoke-BashReadlink");
        Assert.Empty(r.Lines);
        Assert.Contains(StderrLines(r), m => m.StartsWith("readlink: missing operand"));
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Readlink_UnicodeFilename_Roundtrips()
    {
        var unicodeFile = Path.Combine(_tmpDir, "файл-📄.txt");
        File.WriteAllText(unicodeFile, "hi\n");
        var r = Invoke($"Invoke-BashReadlink -f {Q(unicodeFile)}");
        Assert.Single(r.Lines);
        SamePath(unicodeFile, r.Lines[0]);
    }

    [Fact]
    public void Readlink_ViaAlias_Works()
    {
        var r = Invoke("readlink -f regular.txt");
        Assert.Single(r.Lines);
        SamePath(_regularFile, r.Lines[0]);
    }

    [Fact]
    public void Readlink_HelpFlag_EmitsUsage()
    {
        var r = Invoke("Invoke-BashReadlink --help");
        Assert.Contains(r.Lines, l => l.Contains("readlink", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Readlink_PathWithScriptblockChars_TreatedAsLiteralPath()
    {
        // A literal path containing $() is never evaluated, and no file-name glob runs on [ or *.
        var r = Invoke("Invoke-BashReadlink -f '$(throw''pwn'').txt' 'a[1]*'");
        Assert.Equal(2, r.Lines.Count);
        Assert.EndsWith("$(throw'pwn').txt", r.Lines[0]);
        Assert.EndsWith("a[1]*", r.Lines[1]);
    }

    // ---- symbolic links (skipped where the host may not create them) ----

    [SkippableFact]
    public void Readlink_Symlink_PrintsTargetAsStored()
    {
        Skip.IfNot(TryCreateSymlink(Path.Combine(_tmpDir, "l"), "regular.txt"), "Symlink creation not permitted on this host");
        var r = Invoke("Invoke-BashReadlink l");
        Assert.Equal(new[] { "regular.txt" }, r.Lines);
        Assert.Equal(0, r.ExitCode);
    }

    [SkippableFact]
    public void Readlink_DashF_FollowsLinkThenDotDot()
    {
        // dl -> sub/deep: dl/.. is sub (the TARGET's parent), not the directory holding dl.
        Directory.CreateDirectory(Path.Combine(_tmpDir, "sub", "deep"));
        Skip.IfNot(TryCreateSymlink(Path.Combine(_tmpDir, "dl"), Path.Combine("sub", "deep"), directory: true),
            "Symlink creation not permitted on this host");
        var r = Invoke("Invoke-BashReadlink -f dl/..");
        Assert.Single(r.Lines);
        SamePath(Path.Combine(_tmpDir, "sub"), r.Lines[0]);
    }

    [SkippableFact]
    public void Readlink_DashF_DanglingLink_ResolvesToMissingTarget()
    {
        Skip.IfNot(TryCreateSymlink(Path.Combine(_tmpDir, "dang"), "nowhere"), "Symlink creation not permitted on this host");
        var f = Invoke("Invoke-BashReadlink -f dang");
        Assert.Single(f.Lines);
        SamePath(Path.Combine(_tmpDir, "nowhere"), f.Lines[0]);
        Assert.Empty(Invoke("Invoke-BashReadlink -e dang").Lines);
    }

    [SkippableFact]
    public void Readlink_Loop_TooManyLevels_UnderDashMKeptAsName()
    {
        Skip.IfNot(TryCreateSymlink(Path.Combine(_tmpDir, "l1"), "l2") && TryCreateSymlink(Path.Combine(_tmpDir, "l2"), "l1"),
            "Symlink creation not permitted on this host");
        var f = Invoke("Invoke-BashReadlink -fv l1");
        Assert.Empty(f.Lines);
        Assert.Contains(StderrLines(f), m => m == "readlink: l1: Too many levels of symbolic links");
        var m = Invoke("Invoke-BashReadlink -m l1");
        Assert.Single(m.Lines);
    }
}
