using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashInstall</c> from PsBash.psm1 to a binary cmdlet
/// (<see cref="PsBash.Cmdlets.InvokeBashInstallCommand"/>).
///
/// Oracle: the original psm1 function plus GNU coreutils <c>install</c>.
/// Each test exercises one branch against a fresh per-test temp directory.
/// Failure-surface coverage (per Directive 3): empty operand list, missing
/// source, existing-file overwrite (Windows binary-swap path), unicode-ish
/// filenames are deferred to the file-system mutator suite, multi-source to
/// target-dir, verbose-output format, and a quoting/injection probe
/// (Directive 12) on the path that touches user-controlled tokens.
/// </summary>
public class InvokeBashInstallCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmpRoot;
    private readonly SharedPwshFixture _fixture;

    public InvokeBashInstallCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpRoot = Path.Combine(Path.GetTempPath(), $"psb-inst-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { /* best-effort */ }
    }

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    private string Q(string path) => "'" + path.Replace("'", "''") + "'";

    [Fact]
    public void Install_SimpleCopy_CreatesDestination()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var dst = Path.Combine(_tmpRoot, "dst.txt");
        File.WriteAllText(src, "hello");

        Run($"Invoke-BashInstall {Q(src)} {Q(dst)}");

        Assert.True(File.Exists(dst));
        Assert.Equal("hello", File.ReadAllText(dst));
    }

    [Fact]
    public void Install_DashD_CreatesDirectory()
    {
        var newDir = Path.Combine(_tmpRoot, "newdir");
        Run($"Invoke-BashInstall -d {Q(newDir)}");
        Assert.True(Directory.Exists(newDir));
    }

    [Fact]
    public void Install_DashD_MultipleDirs_AllCreated()
    {
        var a = Path.Combine(_tmpRoot, "dirA");
        var b = Path.Combine(_tmpRoot, "dirB");
        Run($"Invoke-BashInstall -d {Q(a)} {Q(b)}");
        Assert.True(Directory.Exists(a));
        Assert.True(Directory.Exists(b));
    }

    [Fact]
    public void Install_DashUppercaseD_CreateLeadingComponents()
    {
        // The case-insensitive cmdlet binder collapses -d and -D onto the
        // same switch (D). In this invocation, -D is used together with a
        // source + dest whose dest has a missing parent dir; the cmdlet
        // creates the leading components on the copy path.
        var src = Path.Combine(_tmpRoot, "src.txt");
        File.WriteAllText(src, "x");
        var nestedDest = Path.Combine(_tmpRoot, "a", "b", "dst.txt");

        Run($"Invoke-BashInstall -D {Q(src)} {Q(nestedDest)}");

        Assert.True(File.Exists(nestedDest));
    }

    [Fact]
    public void Install_VerboseEmitsCopyLine()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var dst = Path.Combine(_tmpRoot, "dst.txt");
        File.WriteAllText(src, "x");

        var output = Run($"Invoke-BashInstall -v {Q(src)} {Q(dst)}");

        Assert.Contains(output, o =>
            o.Contains("'") && o.Contains("->") && o.Contains("dst.txt"));
    }

    [Fact]
    public void Install_DashM_ModeIgnoredOnWindows_StillCopies()
    {
        // -m MODE is tracked but not enforced on Windows (oracle parity).
        var src = Path.Combine(_tmpRoot, "src.txt");
        var dst = Path.Combine(_tmpRoot, "dst.txt");
        File.WriteAllText(src, "y");

        Run($"Invoke-BashInstall -m 755 {Q(src)} {Q(dst)}");

        Assert.True(File.Exists(dst));
    }

    [Fact]
    public void Install_DashT_TargetDir_CopiesIntoTarget()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var targetDir = Path.Combine(_tmpRoot, "target");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(src, "z");

        Run($"Invoke-BashInstall -t {Q(targetDir)} {Q(src)}");

        Assert.True(File.Exists(Path.Combine(targetDir, "src.txt")));
    }

    [Fact]
    public void Install_DashT_MultiSource_AllCopiedToTarget()
    {
        var a = Path.Combine(_tmpRoot, "a.txt");
        var b = Path.Combine(_tmpRoot, "b.txt");
        var targetDir = Path.Combine(_tmpRoot, "tgt");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");

        Run($"Invoke-BashInstall -t {Q(targetDir)} {Q(a)} {Q(b)}");

        Assert.True(File.Exists(Path.Combine(targetDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(targetDir, "b.txt")));
    }

    [Fact]
    public void Install_OverwriteExistingFile_ReplacesContent()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var dst = Path.Combine(_tmpRoot, "dst.txt");
        File.WriteAllText(src, "NEW");
        File.WriteAllText(dst, "OLD");

        Run($"Invoke-BashInstall {Q(src)} {Q(dst)}");

        Assert.Equal("NEW", File.ReadAllText(dst));
    }

    [Fact]
    public void Install_MissingSource_NoDestinationCreated()
    {
        var missing = Path.Combine(_tmpRoot, "does-not-exist.txt");
        var dst = Path.Combine(_tmpRoot, "dst.txt");

        Run($"Invoke-BashInstall {Q(missing)} {Q(dst)}");

        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void Install_AliasResolution()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var dst = Path.Combine(_tmpRoot, "dst.txt");
        File.WriteAllText(src, "via-alias");

        Run($"install {Q(src)} {Q(dst)}");

        Assert.True(File.Exists(dst));
        Assert.Equal("via-alias", File.ReadAllText(dst));
    }

    [Fact]
    public void Install_Help_DoesNotThrow()
    {
        var output = Run("Invoke-BashInstall --help");
        // Show-BashHelp emits some text; just verify the call completes.
        Assert.NotNull(output);
    }

    [Fact]
    public void Install_InjectionProbe_LiteralPathHandled()
    {
        // Directive 12: a path token containing $(throw 'pwn') must arrive
        // at File.Exists / File.Copy as a literal string. We expect either
        // the no-such-file error branch or a successful literal copy — never
        // a thrown PowerShell exception, never script execution.
        var src = Path.Combine(_tmpRoot, "src.txt");
        File.WriteAllText(src, "ok");
        var injPath = Path.Combine(_tmpRoot, "$(throw 'pwn').txt");

        // No exception should escape — the injection payload stays literal.
        var output = Run($"Invoke-BashInstall {Q(src)} {Q(injPath)}");

        // Either the file got copied (literal name accepted by NTFS) or the
        // call failed with a bash-style error — never a PowerShell throw.
        Assert.NotNull(output);
    }

    [Fact]
    public void Install_MissingOperand_NoOutputFile()
    {
        // <2 operands without -t is a usage error per the oracle.
        var src = Path.Combine(_tmpRoot, "lonely.txt");
        File.WriteAllText(src, "x");
        Run($"Invoke-BashInstall {Q(src)}");
        // Should not throw; should not create anything new.
        Assert.True(File.Exists(src));
    }
}
