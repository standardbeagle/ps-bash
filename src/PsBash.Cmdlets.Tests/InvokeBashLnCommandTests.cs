using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Regression + parity tests for <c>Invoke-BashLn</c> (PsBash.Cmdlets).
///
/// HEADLINE REGRESSION (Directive 13 — known-bad gets a permanent test):
/// <c>ln -f TARGET EXISTING_DIR</c> used to run
/// <c>Directory.Delete(linkAbsolute, recursive: true)</c>, silently destroying
/// a populated real directory. GNU <c>ln</c> NEVER removes a directory: when the
/// link name is an existing directory it creates the link *inside* it as
/// basename(TARGET). The fix redirects into the directory and only ever
/// force-removes a file or a symlink. <see cref="Ln_Force_IntoExistingDirectory_DoesNotDeleteIt"/>
/// is the data-loss guard and must never regress.
///
/// Most cases use HARD links (no <c>-s</c>) so they need no symlink privilege —
/// on Windows symlink creation requires Developer Mode / SeCreateSymbolicLink,
/// which CI runners may lack. The few symlink-specific cases are
/// <see cref="SkippableFact"/> gated on a runtime capability probe.
/// </summary>
public class InvokeBashLnCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashLnCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), "psbash-ln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string Q(string path) => "'" + path.Replace("'", "''") + "'";

    private void Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
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

    private string Mk(string name, string content)
    {
        var p = Path.Combine(_tmpDir, name);
        File.WriteAllText(p, content);
        return p;
    }

    // ───────────────────────── DATA-LOSS REGRESSION (critical) ─────────────────────────

    [Fact]
    public void Ln_Force_IntoExistingDirectory_DoesNotDeleteIt()
    {
        // ln -f TARGET DIR must NOT delete DIR. GNU ln links inside it.
        var victim = Path.Combine(_tmpDir, "victim");
        Directory.CreateDirectory(victim);
        var keep = Path.Combine(victim, "important.txt");
        File.WriteAllText(keep, "DO NOT DELETE");
        Directory.CreateDirectory(Path.Combine(victim, "subdir"));
        File.WriteAllText(Path.Combine(victim, "subdir", "nested.txt"), "also keep");

        var target = Mk("tgt.txt", "payload");

        Run($"Invoke-BashLn -f {Q(target)} {Q(victim)}");

        Assert.True(Directory.Exists(victim), "ln -f must NOT delete a real directory");
        Assert.True(File.Exists(keep), "directory contents must survive ln -f");
        Assert.Equal("DO NOT DELETE", File.ReadAllText(keep));
        Assert.True(File.Exists(Path.Combine(victim, "subdir", "nested.txt")),
            "nested contents must survive ln -f");
        // GNU ln places the link inside the directory as basename(TARGET).
        Assert.True(File.Exists(Path.Combine(victim, "tgt.txt")),
            "link should be created inside the directory as basename(target)");
        Assert.Equal("payload", File.ReadAllText(Path.Combine(victim, "tgt.txt")));
    }

    [Fact]
    public void Ln_NoForce_IntoExistingDirectory_StillLinksInside_NeverDeletes()
    {
        var dir = Path.Combine(_tmpDir, "box");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "x");
        var target = Mk("file.txt", "data");

        Run($"Invoke-BashLn {Q(target)} {Q(dir)}");

        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "file.txt")), "hard link created inside the dir");
    }

    // ───────────────────────── force over file / symlink ─────────────────────────

    [Fact]
    public void Ln_Force_OverwritesExistingFile()
    {
        var target = Mk("new.txt", "NEW");
        var link = Mk("link.txt", "OLD");

        Run($"Invoke-BashLn -f {Q(target)} {Q(link)}");

        // Hard link now shares the target's content.
        Assert.Equal("NEW", File.ReadAllText(link));
    }

    [Fact]
    public void Ln_NoForce_ExistingFile_ErrorsAndLeavesOriginalIntact()
    {
        var target = Mk("src.txt", "NEW");
        var link = Mk("dst.txt", "ORIGINAL");

        var errs = RunErrors($"Invoke-BashLn {Q(target)} {Q(link)}");

        Assert.Equal("ORIGINAL", File.ReadAllText(link));
        Assert.Contains(errs, m => m.Contains("File exists", StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── basic creation ─────────────────────────

    [Fact]
    public void Ln_HardLink_CreatesLinkSharingContent()
    {
        var target = Mk("orig.txt", "shared");
        var link = Path.Combine(_tmpDir, "hard.txt");

        Run($"Invoke-BashLn {Q(target)} {Q(link)}");

        Assert.True(File.Exists(link));
        Assert.Equal("shared", File.ReadAllText(link));
        // Hard link: mutating the target is visible through the link.
        File.WriteAllText(target, "mutated");
        Assert.Equal("mutated", File.ReadAllText(link));
    }

    [Fact]
    public void Ln_MissingOperand_Errors()
    {
        // No operands at all (a single operand is GNU form 2 now — see the operand-forms section).
        var errs = RunErrors("Invoke-BashLn -s");
        Assert.Contains(errs, m => m.Contains("missing file operand", StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── symlink-specific (privilege-gated) ─────────────────────────

    [SkippableFact]
    public void Ln_SymlinkForce_OverExistingFile_DoesNotTouchUnrelatedSiblings()
    {
        Skip.IfNot(SymlinksSupported(), "symlink creation not permitted in this environment");

        var target = Mk("real-target.txt", "TGT");
        var link = Mk("sym.txt", "old-regular-file");
        var sibling = Mk("sibling.txt", "UNRELATED");

        Run($"Invoke-BashLn -s -f {Q(target)} {Q(link)}");

        Assert.True(IsSymlink(link), "link should now be a symlink");
        Assert.Equal("UNRELATED", File.ReadAllText(sibling));
    }

    // ───────────────────── shared ordered parser: ln as the transpiler delivers it ─────────────────────
    // PsEmitter.OrderedArgCommands single-quotes every dash-leading word, so flags arrive in
    // Arguments as plain strings, in order. Hard links keep these privilege-free.

    private string RunLastExit(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript($"{script} *> $null; $global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        return result[^1].ToString()!;
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(r => r.BaseObject is PSObject p ? p.ToString() : (r.ToString() ?? "")).ToArray();
    }

    [Fact]
    public void Ln_QuotedForceVerboseBundle_OverwritesAndReports()
    {
        var target = Mk("qt.txt", "NEW");
        var link = Mk("ql.txt", "OLD");
        var lines = RunLines($"Invoke-BashLn '-fv' {Q(target)} {Q(link)}");
        Assert.Equal("NEW", File.ReadAllText(link));
        Assert.Contains(lines, l => l.Contains("=>"));
    }

    [Theory]
    [InlineData("-L")]
    [InlineData("-d")]
    [InlineData("--physical")]
    [InlineData("--directory")]
    public void Ln_ValidButUnsupportedOption_IsRefusedAndCreatesNoLink(string flag)
    {
        // REGRESSION: with no classifier the flag was taken as the link TARGET and a wrongly named
        // link was created at exit 0.
        var target = Mk("rt.txt", "x");
        var link = Path.Combine(_tmpDir, "rl.txt");
        Assert.Equal("2", RunLastExit($"Invoke-BashLn '{flag}' {Q(target)} {Q(link)}"));
        Assert.False(File.Exists(link));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-z")]
    [InlineData("-sz")]
    [InlineData("--symbolic=1")]
    [InlineData("--s")]
    public void Ln_UsageError_ExitsOneAndCreatesNoLink(string flag)
    {
        var target = Mk("ut.txt", "x");
        var link = Path.Combine(_tmpDir, "ul.txt");
        Assert.Equal("1", RunLastExit($"Invoke-BashLn '{flag}' {Q(target)} {Q(link)}"));
        Assert.False(File.Exists(link));
    }

    [Fact]
    public void Ln_DoubleDash_DashNamedTargetIsAnOperandNotAnOption()
    {
        Mk("-a", "dash");
        Run($"Set-Location {Q(_tmpDir)}; Invoke-BashLn '--' '-a' 'dashlink.txt'");
        Assert.Equal("dash", File.ReadAllText(Path.Combine(_tmpDir, "dashlink.txt")));
    }

    [Fact]
    public void Ln_OptionAfterOperands_StillApplies()
    {
        var target = Mk("ot.txt", "NEW");
        var link = Mk("ol.txt", "OLD");
        Run($"Invoke-BashLn {Q(target)} {Q(link)} '-f'");
        Assert.Equal("NEW", File.ReadAllText(link));
    }

    [Fact]
    public void Ln_DirectCallDecoyD_IsClassifiedNotSwallowedByTheBinder()
    {
        // Pester/interactive path: bare -d silently binds -Debug;
        // the decoy re-injects it so the classifier refuses it (exit 2) and nothing is created.
        var target = Mk("dt.txt", "x");
        var link = Path.Combine(_tmpDir, "dl.txt");
        Assert.Equal("2", RunLastExit($"Invoke-BashLn -d {Q(target)} {Q(link)}"));
        Assert.False(File.Exists(link));
    }

    [Fact]
    public void Ln_DirectCallDecoyVerbose_StillWorks()
    {
        var target = Mk("vt.txt", "x");
        var link = Path.Combine(_tmpDir, "vl.txt");
        var lines = RunLines($"Invoke-BashLn -v {Q(target)} {Q(link)}");
        Assert.True(File.Exists(link));
        Assert.Contains(lines, l => l.Contains("=>"));
    }

    [SkippableFact]
    public void Ln_SfnRepointsAnExistingSymlink_TheCommonIdiom()
    {
        // REGRESSION: `ln -sfn` (bundle containing -n) was an operand list [-sfn, a, b] before.
        Skip.IfNot(SymlinksSupported(), "symlink creation not permitted in this environment");
        var t1 = Mk("n1.txt", "one");
        var t2 = Mk("n2.txt", "two");
        var link = Path.Combine(_tmpDir, "cur");
        Run($"Invoke-BashLn '-sfn' {Q(t1)} {Q(link)}; Invoke-BashLn '-sfn' {Q(t2)} {Q(link)}");
        Assert.True(IsSymlink(link));
        Assert.Equal("two", File.ReadAllText(link));
    }

    // ───────────────────── GNU operand forms (oracle: coreutils 9.4 via FsStateDifferentialTests) ─────────────────────

    /// <summary>Fresh directory holding files a, b, and directories d (with d/c) and e.</summary>
    private string Fixture()
    {
        var root = Path.Combine(_tmpDir, "fx" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(root, "d"));
        Directory.CreateDirectory(Path.Combine(root, "e"));
        File.WriteAllText(Path.Combine(root, "a"), "a\n");
        File.WriteAllText(Path.Combine(root, "b"), "b\n");
        File.WriteAllText(Path.Combine(root, "d", "c"), "c\n");
        return root;
    }

    private CmdResult RunIn(string cwd, string script) =>
        CmdResult.Run(_fixture.AcquireFresh(), $"Set-Location {Q(cwd)}; {script}");

    [Fact]
    public void Ln_SingleOperand_LinksBasenameIntoCwd()
    {
        var root = Fixture();
        var r = RunIn(root, "Invoke-BashLn -v 'd/c'").AssertSuccess();
        Assert.Equal("'./c' => 'd/c'", r.Stdout.TrimEnd());
        Assert.Equal("c\n", File.ReadAllText(Path.Combine(root, "c")));
    }

    [Fact]
    public void Ln_SingleOperand_ExistingName_FailsLikeGnu()
    {
        var root = Fixture();
        RunIn(root, "Invoke-BashLn 'a'").AssertFailed(1, "ln: failed to create hard link './a': File exists");
    }

    [Fact]
    public void Ln_MultipleTargets_IntoDirectory_OneLinkEach()
    {
        var root = Fixture();
        var r = RunIn(root, "Invoke-BashLn -v 'a' 'b' 'd'").AssertSuccess();
        Assert.Equal(new[] { "'d/a' => 'a'", "'d/b' => 'b'" }, r.Lines.Select(l => l.TrimEnd()).ToArray());
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(root, "d", "a")));
        Assert.Equal("b\n", File.ReadAllText(Path.Combine(root, "d", "b")));
    }

    [Fact]
    public void Ln_MultipleTargets_TrailingSlashDirectory_NoDoubleSlash()
    {
        var root = Fixture();
        var r = RunIn(root, "Invoke-BashLn -v 'a' 'b' 'e/'").AssertSuccess();
        Assert.Equal(new[] { "'e/a' => 'a'", "'e/b' => 'b'" }, r.Lines.Select(l => l.TrimEnd()).ToArray());
    }

    [Fact]
    public void Ln_MultipleTargets_LastOperandMissing_Fails()
    {
        var root = Fixture();
        RunIn(root, "Invoke-BashLn 'a' 'b' 'nodir'").AssertFailed(1, "ln: target 'nodir': No such file or directory");
        Assert.False(File.Exists(Path.Combine(root, "nodir")));
    }

    [Fact]
    public void Ln_MultipleTargets_LastOperandIsAFile_Fails()
    {
        var root = Fixture();
        RunIn(root, "Invoke-BashLn 'a' 'b' 'b'").AssertFailed(1, "ln: target 'b': Not a directory");
    }

    [Fact]
    public void Ln_MultipleTargets_OneFailure_KeepsGoingAndExitsOne()
    {
        var root = Fixture();
        File.WriteAllText(Path.Combine(root, "e", "a"), "existing\n");
        var r = RunIn(root, "Invoke-BashLn 'a' 'b' 'e'");
        r.AssertFailed(1, "ln: failed to create hard link 'e/a': File exists");
        Assert.Equal("b\n", File.ReadAllText(Path.Combine(root, "e", "b")));
        Assert.Equal("existing\n", File.ReadAllText(Path.Combine(root, "e", "a")));
    }

    [Fact]
    public void Ln_TwoOperands_LinkNameIsDirectory_LinksInside()
    {
        var root = Fixture();
        var r = RunIn(root, "Invoke-BashLn -v 'a' 'e'").AssertSuccess();
        Assert.Equal("'e/a' => 'a'", r.Stdout.TrimEnd());
        Assert.True(File.Exists(Path.Combine(root, "e", "a")));
    }

    [Fact]
    public void Ln_TargetDirectoryOption_LinksEachOperandInside()
    {
        var root = Fixture();
        var r = RunIn(root, "Invoke-BashLn '-v' '-t' 'e' 'a' 'b'").AssertSuccess();
        Assert.Equal(new[] { "'e/a' => 'a'", "'e/b' => 'b'" }, r.Lines.Select(l => l.TrimEnd()).ToArray());
    }

    [Fact]
    public void Ln_TargetDirectoryOption_MissingDirectory_Fails()
    {
        var root = Fixture();
        RunIn(root, "Invoke-BashLn '-t' 'nodir' 'a'").AssertFailed(1, "ln: failed to access 'nodir': No such file or directory");
    }

    [Fact]
    public void Ln_NoTargetDirectory_ExistingDirectoryIsNotDescended()
    {
        var root = Fixture();
        RunIn(root, "Invoke-BashLn '-T' 'a' 'd'").AssertFailed(1, "ln: failed to create hard link 'd': File exists");
        Assert.False(File.Exists(Path.Combine(root, "d", "a")));
    }

    [Fact]
    public void Ln_NoTargetDirectory_PlainName_Links()
    {
        var root = Fixture();
        var r = RunIn(root, "Invoke-BashLn '-Tv' 'a' 'b2'").AssertSuccess();
        Assert.Equal("'b2' => 'a'", r.Stdout.TrimEnd());
    }

    [Fact]
    public void Ln_NoTargetDirectory_ThreeOperands_ExtraOperand()
    {
        var root = Fixture();
        RunIn(root, "Invoke-BashLn '-T' 'a' 'b2' 'c'").AssertFailed(1, "ln: extra operand 'c'");
    }

    [Fact]
    public void Ln_TargetAndNoTargetDirectory_Conflict()
    {
        var root = Fixture();
        RunIn(root, "Invoke-BashLn '-t' 'e' '-T' 'a'")
            .AssertFailed(1, "ln: cannot combine --target-directory and --no-target-directory");
    }

    [Fact]
    public void Ln_HardLink_MissingTarget_FailsLikeGnu()
    {
        var root = Fixture();
        RunIn(root, "Invoke-BashLn 'missing' 'zz'").AssertFailed(1, "ln: failed to access 'missing': No such file or directory");
        Assert.False(File.Exists(Path.Combine(root, "zz")));
    }

    [Fact]
    public void Ln_HardLink_DirectoryTarget_NotAllowed()
    {
        var root = Fixture();
        RunIn(root, "Invoke-BashLn 'd' 'zz'").AssertFailed(1, "ln: d: hard link not allowed for directory");
    }

    [SkippableFact]
    public void Ln_Symlink_SingleOperandAndMultiTarget()
    {
        Skip.IfNot(SymlinksSupported(), "symlink creation not permitted in this environment");
        var root = Fixture();
        var one = RunIn(root, "Invoke-BashLn '-sv' 'd/c'").AssertSuccess();
        Assert.Equal("'./c' -> 'd/c'", one.Stdout.TrimEnd());
        Assert.True(IsSymlink(Path.Combine(root, "c")));
        var many = RunIn(root, "Invoke-BashLn '-sv' 'a' 'b' 'e'").AssertSuccess();
        Assert.Equal(new[] { "'e/a' -> 'a'", "'e/b' -> 'b'" }, many.Lines.Select(l => l.TrimEnd()).ToArray());
        Assert.True(IsSymlink(Path.Combine(root, "e", "a")));
    }

    private bool SymlinksSupported()
    {
        var probeTarget = Mk("__probe_target", "x");
        var probeLink = Path.Combine(_tmpDir, "__probe_link");
        try
        {
            File.CreateSymbolicLink(probeLink, probeTarget);
            var ok = IsSymlink(probeLink);
            File.Delete(probeLink);
            return ok;
        }
        catch { return false; }
    }

    private static bool IsSymlink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }
}
