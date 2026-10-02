using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// ln <c>-r</c> / <c>-b</c> / <c>-S</c> / <c>-i</c>. Expectations were read from GNU coreutils 9.4
/// (<c>wsl bash</c>): the relative text, <c>'b~' ~ 'b' -> 'a'</c> verbose line, the <c>-if</c>/<c>-fi</c>
/// ordering, a declined prompt exiting 1. Hard links need no privilege, so the backup/prompt cases use
/// them; symbolic-link cases skip where the OS refuses to create one.
/// </summary>
public class LnOptionsTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public LnOptionsTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-lno-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private void Write(string rel, string content = "x")
    {
        var p = Path.Combine(_tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_tmp, rel));
    private bool Exists(string rel) => File.Exists(Path.Combine(_tmp, rel)) || Directory.Exists(Path.Combine(_tmp, rel));

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    private string? LinkTarget(string rel) => new FileInfo(Path.Combine(_tmp, rel)).LinkTarget?.Replace('\\', '/');

    private void RequireSymlinks()
    {
        var probe = Path.Combine(_tmp, "__probe");
        Write("__probe_t");
        try
        {
            File.CreateSymbolicLink(probe, Path.Combine(_tmp, "__probe_t"));
            File.Delete(probe);
        }
        catch { Skip.If(true, "symbolic link creation is not permitted in this environment"); }
    }

    // ───────────── -r (pure arithmetic, no disk) ─────────────

    private static string Rel(string target, string linkDir, Dictionary<string, string>? links = null) =>
        RelativeLinkPath.Compute(target, linkDir, p => links != null && links.TryGetValue(p, out var t) ? t : null);

    private static string P(string unix) => OperatingSystem.IsWindows() ? "C:" + unix.Replace('/', '\\') : unix;

    [Theory]
    [InlineData("/r/a", "/r", "a")]
    [InlineData("/r/f/b", "/r/d/e", "../../f/b")]
    [InlineData("/r/d/e", "/r/f", "../d/e")]
    [InlineData("/r/d/e", "/r/d", "e")]
    [InlineData("/r/a", "/r/d/e", "../../a")]
    [InlineData("/r/d/e/c", "/r/d/e", "c")]
    [InlineData("/r/d", "/r/d", ".")]
    [InlineData("/r/d/../a", "/r", "a")]
    [InlineData("/r/./a", "/r/", "a")]
    [InlineData("/r/nosuch", "/r", "nosuch")]
    [InlineData("/x/y", "/r/s/t", "../../../x/y")]
    public void Compute_IsGnusRelativePath(string target, string linkDir, string expected)
    {
        Assert.Equal(expected, Rel(P(target), P(linkDir)));
    }

    [Fact]
    public void Compute_ResolvesALinkInTheTarget()
    {
        // /r/ll -> a, so a link to /r/ll made in /r/d is a link to /r/a
        var links = new Dictionary<string, string> { [P("/r/ll")] = "a" };
        Assert.Equal("../a", Rel(P("/r/ll"), P("/r/d"), links));
    }

    [Fact]
    public void Compute_ResolvesALinkedDirectoryInTheLinkDirectory()
    {
        // /r/lnk -> /r/real, so a link made in /r/lnk lives in /r/real
        var links = new Dictionary<string, string> { [P("/r/lnk")] = P("/r/real") };
        Assert.Equal("../a", Rel(P("/r/a"), P("/r/lnk"), links));
    }

    [Fact]
    public void Compute_DotDotAfterALinkIsPhysical()
    {
        // /r/lnk -> /q/deep : "lnk/.." is /q, not /r
        var links = new Dictionary<string, string> { [P("/r/lnk")] = P("/q/deep") };
        Assert.Equal("x", Rel(P("/r/lnk/../x"), P("/q"), links));
    }

    [Fact]
    public void Compute_ALinkLoopDoesNotHang()
    {
        var links = new Dictionary<string, string> { [P("/r/a")] = "b", [P("/r/b")] = "a" };
        var text = Rel(P("/r/a"), P("/r"), links);
        Assert.False(string.IsNullOrEmpty(text));
    }

    // ───────────── -r (on disk) ─────────────

    [Fact]
    public void Relative_WithoutSymbolic_IsGnusError()
    {
        Write("a");
        InTmp("Invoke-BashLn '-r' a h").AssertFailed(1, "ln: cannot do --relative without --symbolic");
        Assert.False(Exists("h"));
    }

    [SkippableFact]
    public void Relative_Plain()
    {
        RequireSymlinks();
        Write("a");
        var r = InTmp("Invoke-BashLn '-svr' a l1").AssertSuccess();
        Assert.Equal("'l1' -> 'a'", r.Stdout.Trim());
        Assert.Equal("a", LinkTarget("l1"));
    }

    [SkippableFact]
    public void Relative_NestedUpAndDown()
    {
        RequireSymlinks();
        Write("f/b"); Directory.CreateDirectory(Path.Combine(_tmp, "d", "e"));
        var r = InTmp("Invoke-BashLn '-svr' f/b d/e/l2").AssertSuccess();
        Assert.Equal("'d/e/l2' -> '../../f/b'", r.Stdout.Trim());
        Assert.Equal("../../f/b", LinkTarget("d/e/l2"));
    }

    [SkippableFact]
    public void Relative_AbsoluteTarget_BecomesRelative()
    {
        RequireSymlinks();
        Write("f/b");
        var abs = Path.Combine(_tmp, "f", "b").Replace("'", "''");
        InTmp($"Invoke-BashLn '-sr' '{abs}' l7").AssertSuccess();
        Assert.Equal("f/b", LinkTarget("l7"));
    }

    [SkippableFact]
    public void Relative_IntoDirectory_UsesTheDirectoryAsLinkLocation()
    {
        RequireSymlinks();
        Write("f/b"); Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        var r = InTmp("Invoke-BashLn '-svr' f/b d").AssertSuccess();
        Assert.Equal("'d/b' -> '../f/b'", r.Stdout.Trim());
        Assert.Equal("../f/b", LinkTarget("d/b"));
    }

    [SkippableFact]
    public void Relative_SeveralTargets_AndMissingTarget()
    {
        RequireSymlinks();
        Write("a"); Write("f/b"); Directory.CreateDirectory(Path.Combine(_tmp, "g"));
        var r = InTmp("Invoke-BashLn '-svr' a f/b g").AssertSuccess();
        Assert.Equal(new[] { "'g/a' -> '../a'", "'g/b' -> '../f/b'" }, r.Lines.Select(l => l.Trim()).ToArray());
        InTmp("Invoke-BashLn '-sr' nosuch l5").AssertSuccess();
        Assert.Equal("nosuch", LinkTarget("l5"));
    }

    [SkippableFact]
    public void Relative_ResolvesALinkTarget()
    {
        RequireSymlinks();
        Write("a");
        InTmp("Invoke-BashLn '-s' a ll; Invoke-BashLn '-sr' ll l14").AssertSuccess();
        Assert.Equal("a", LinkTarget("l14"));
    }

    [SkippableFact]
    public void Relative_WithForce_ReplacesAnExistingLink()
    {
        RequireSymlinks();
        Write("a");
        InTmp("Invoke-BashLn '-s' zz l1; Invoke-BashLn '-srf' a l1").AssertSuccess();
        Assert.Equal("a", LinkTarget("l1"));
    }

    // ───────────── -b / -S (hard links: no privilege) ─────────────

    [Fact]
    public void Backup_NoForceNeeded_AndVerboseNotesIt()
    {
        Write("a", "A"); Write("b", "B");
        var r = InTmp("Invoke-BashLn '-bv' a b").AssertSuccess();
        Assert.Equal("'b~' ~ 'b' => 'a'", r.Stdout.Trim());
        Assert.Equal("A", Read("b"));
        Assert.Equal("B", Read("b~"));
    }

    [Fact]
    public void Backup_NoExistingName_MakesNone_AndNoNote()
    {
        Write("a", "A");
        var r = InTmp("Invoke-BashLn '-bv' a n").AssertSuccess();
        Assert.Equal("'n' => 'a'", r.Stdout.Trim());
        Assert.False(Exists("n~"));
    }

    [Fact]
    public void Backup_SuffixAloneTurnsBackupsOn()
    {
        Write("a", "A"); Write("b", "B");
        InTmp("Invoke-BashLn '-S' '.bak' a b").AssertSuccess();
        Assert.Equal("B", Read("b.bak"));
        Assert.Equal("A", Read("b"));
    }

    [Fact]
    public void Backup_Numbered_CountsUp()
    {
        Write("a", "A"); Write("b", "B");
        InTmp("Invoke-BashLn '--backup=numbered' a b").AssertSuccess();
        InTmp("Invoke-BashLn '--backup=numbered' '-f' a b").AssertSuccess();
        Assert.Equal("B", Read("b.~1~"));
        Assert.Equal("A", Read("b.~2~"));
        Assert.Equal("A", Read("b"));
    }

    [Fact]
    public void Backup_BadControlWord_IsGnusError()
    {
        Write("a"); Write("b");
        InTmp("Invoke-BashLn '--backup=bogus' a b").AssertFailed(1, "invalid argument 'bogus' for 'backup type'");
    }

    [Fact]
    public void Backup_IntoDirectory_NamesTheBackupInsideIt()
    {
        Write("a", "A"); Write("e/a", "OLD");
        var r = InTmp("Invoke-BashLn '-bv' a e").AssertSuccess();
        Assert.Equal("'e/a~' ~ 'e/a' => 'a'", r.Stdout.Trim());
        Assert.Equal("OLD", Read("e/a~"));
    }

    // ───────────── -i ─────────────

    [Fact]
    public void Interactive_Yes_Replaces_AndPromptsOnStderr()
    {
        Write("a", "A"); Write("b", "B");
        var r = InTmp("'y' | Invoke-BashLn '-i' a b");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("A", Read("b"));
        Assert.Contains("ln: replace 'b'? ", r.Stderr);
    }

    [Fact]
    public void Interactive_No_KeepsTheName_AndExitsOne()
    {
        Write("a", "A"); Write("b", "B");
        InTmp("'n' | Invoke-BashLn '-i' a b").AssertFailed(1, "ln: replace 'b'? ");
        Assert.Equal("B", Read("b"));
    }

    [Fact]
    public void Interactive_NoAnswer_IsNo()
    {
        Write("a", "A"); Write("b", "B");
        Assert.Equal(1, InTmp("Invoke-BashLn '-i' a b").ExitCode);
        Assert.Equal("B", Read("b"));
    }

    [Fact]
    public void Interactive_NewName_NoPrompt()
    {
        Write("a", "A");
        InTmp("Invoke-BashLn '-i' a n").AssertSuccess();
        Assert.Equal("A", Read("n"));
    }

    [Fact]
    public void Interactive_LastOfIAndFWins()
    {
        Write("a", "A"); Write("b", "B");
        // -if: -f last, no prompt, replaces.
        InTmp("Invoke-BashLn '-if' a b").AssertSuccess();
        Assert.Equal("A", Read("b"));
        Write("b", "B");
        // -fi: -i last, asks; no answer = no.
        Assert.Equal(1, InTmp("Invoke-BashLn '-fi' a b").ExitCode);
        Assert.Equal("B", Read("b"));
    }

    [Fact]
    public void Interactive_WithBackup_AsksThenBacksUp()
    {
        Write("a", "A"); Write("b", "B");
        var r = InTmp("'y' | Invoke-BashLn '-ibv' a b");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("B", Read("b~"));
        Assert.Equal("A", Read("b"));
        Assert.Contains("'b~' ~ 'b' => 'a'", r.Stdout);
    }

    [Fact]
    public void DirectCall_BareDashI_BindsDecoy()
    {
        Write("a", "A"); Write("b", "B");
        InTmp("'y' | Invoke-BashLn -i a b");
        Assert.Equal("A", Read("b"));
    }
}
