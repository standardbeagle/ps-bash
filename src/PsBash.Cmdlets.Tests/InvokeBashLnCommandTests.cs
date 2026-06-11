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
        var errs = RunErrors("Invoke-BashLn -s onlyone");
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
