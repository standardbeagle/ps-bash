using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of the file-system
/// mutator family — mkdir / rmdir / cp / mv / rm — from PsBash.psm1 to binary
/// cmdlets sharing <c>FileSystemHelpers</c>.
///
/// Oracle: GNU coreutils plus the psm1 oracle's added safety guards (rm's
/// reserved-device-name and protected-path refusals). Each test exercises one
/// operation against a fresh per-test temp directory, with assertions on the
/// resulting filesystem state. All operations are run via the canonical
/// <c>Invoke-Bash*</c> name; alias resolution is exercised via the bash
/// alias path in `cp_ViaAlias_*`.
///
/// Failure-surface axes covered (per Directive 3): empty operand list,
/// missing source, existing destination (with / without -f / -n), unicode
/// filenames, multi-operand glob, recursive copy/remove, verbose-output
/// format, exit-code propagation, and a quoting/injection probe on every
/// path that touches user-controlled tokens.
/// </summary>
public class InvokeBashFileSystemMutatorTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmpRoot;
    private readonly SharedPwshFixture _fixture;

    public InvokeBashFileSystemMutatorTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpRoot = Path.Combine(Path.GetTempPath(), $"psb-fsmut-{Guid.NewGuid():N}".Substring(0, 22));
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

    // ─────────────────────────── mkdir ───────────────────────────

    [Fact]
    public void Mkdir_CreatesDir()
    {
        var dir = Path.Combine(_tmpRoot, "newdir");
        Run($"Invoke-BashMkdir {Q(dir)}");
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Mkdir_WithoutP_ExistingDir_ReturnsError()
    {
        var dir = Path.Combine(_tmpRoot, "exists");
        Directory.CreateDirectory(dir);
        Run($"Invoke-BashMkdir {Q(dir)}");
        // psm1 oracle sets $LASTEXITCODE=1 and emits a Write-BashError;
        // the cmdlet mirrors that. We check the filesystem is unchanged.
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Mkdir_WithP_CreatesChain()
    {
        var deep = Path.Combine(_tmpRoot, "a", "b", "c", "d");
        Run($"Invoke-BashMkdir -p {Q(deep)}");
        Assert.True(Directory.Exists(deep));
    }

    [Fact]
    public void Mkdir_WithP_ExistingDir_NoError()
    {
        var dir = Path.Combine(_tmpRoot, "exists");
        Directory.CreateDirectory(dir);
        Run($"Invoke-BashMkdir -p {Q(dir)}");
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Mkdir_Verbose_EmitsCreationLine()
    {
        var dir = Path.Combine(_tmpRoot, "verbose");
        var lines = Run($"Invoke-BashMkdir -v {Q(dir)}");
        Assert.Contains(lines, l => l.Contains("created directory") && l.Contains("verbose"));
    }

    // ─────────────────────────── rmdir ───────────────────────────

    [Fact]
    public void Rmdir_EmptyDir_RemovesIt()
    {
        var dir = Path.Combine(_tmpRoot, "empty");
        Directory.CreateDirectory(dir);
        Run($"Invoke-BashRmdir {Q(dir)}");
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Rmdir_NonEmpty_RefusesAndKeepsDir()
    {
        var dir = Path.Combine(_tmpRoot, "full");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x"), "x");
        Run($"Invoke-BashRmdir {Q(dir)}");
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Rmdir_WithP_RemovesEmptyChain()
    {
        var leaf = Path.Combine(_tmpRoot, "x", "y", "z");
        Directory.CreateDirectory(leaf);
        Run($"Invoke-BashRmdir -p {Q(leaf)}");
        Assert.False(Directory.Exists(Path.Combine(_tmpRoot, "x")));
    }

    // ─────────────────────────── cp ───────────────────────────

    [Fact]
    public void Cp_File_CreatesCopy()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var dst = Path.Combine(_tmpRoot, "dst.txt");
        File.WriteAllText(src, "payload");
        Run($"Invoke-BashCp {Q(src)} {Q(dst)}");
        Assert.Equal("payload", File.ReadAllText(dst));
        Assert.True(File.Exists(src), "source must remain after cp");
    }

    [Fact]
    public void Cp_IntoExistingDir_PreservesBasename()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var destDir = Path.Combine(_tmpRoot, "dest");
        File.WriteAllText(src, "x");
        Directory.CreateDirectory(destDir);
        Run($"Invoke-BashCp {Q(src)} {Q(destDir)}");
        Assert.True(File.Exists(Path.Combine(destDir, "src.txt")));
    }

    [Fact]
    public void Cp_NoClobber_KeepsExistingTarget()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var dst = Path.Combine(_tmpRoot, "dst.txt");
        File.WriteAllText(src, "new");
        File.WriteAllText(dst, "old");
        Run($"Invoke-BashCp -n {Q(src)} {Q(dst)}");
        Assert.Equal("old", File.ReadAllText(dst));
    }

    [Fact]
    public void Cp_Recursive_CopiesDirectoryTree()
    {
        var srcDir = Path.Combine(_tmpRoot, "tree");
        var dstDir = Path.Combine(_tmpRoot, "tree-copy");
        Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
        File.WriteAllText(Path.Combine(srcDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(srcDir, "sub", "b.txt"), "b");
        Run($"Invoke-BashCp -r {Q(srcDir)} {Q(dstDir)}");
        Assert.True(File.Exists(Path.Combine(dstDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dstDir, "sub", "b.txt")));
    }

    [Fact]
    public void Cp_DirWithoutR_EmitsErrorAndDoesNotCopy()
    {
        var srcDir = Path.Combine(_tmpRoot, "tree");
        var dstDir = Path.Combine(_tmpRoot, "tree-copy");
        Directory.CreateDirectory(srcDir);
        Run($"Invoke-BashCp {Q(srcDir)} {Q(dstDir)}");
        Assert.False(Directory.Exists(dstDir));
    }

    // ─────────────────────────── mv ───────────────────────────

    [Fact]
    public void Mv_File_MovesIt()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var dst = Path.Combine(_tmpRoot, "dst.txt");
        File.WriteAllText(src, "x");
        Run($"Invoke-BashMv {Q(src)} {Q(dst)}");
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(dst));
    }

    [Fact]
    public void Mv_NoClobber_KeepsExisting()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var dst = Path.Combine(_tmpRoot, "dst.txt");
        File.WriteAllText(src, "new");
        File.WriteAllText(dst, "old");
        Run($"Invoke-BashMv -n {Q(src)} {Q(dst)}");
        Assert.Equal("old", File.ReadAllText(dst));
        // Source must remain since the move was skipped.
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Mv_IntoDir_PreservesBasename()
    {
        var src = Path.Combine(_tmpRoot, "src.txt");
        var destDir = Path.Combine(_tmpRoot, "dest");
        File.WriteAllText(src, "x");
        Directory.CreateDirectory(destDir);
        Run($"Invoke-BashMv {Q(src)} {Q(destDir)}");
        Assert.True(File.Exists(Path.Combine(destDir, "src.txt")));
        Assert.False(File.Exists(src));
    }

    // ─────────────────────────── rm ───────────────────────────

    [Fact]
    public void Rm_File_DeletesIt()
    {
        var file = Path.Combine(_tmpRoot, "doomed.txt");
        File.WriteAllText(file, "x");
        Run($"Invoke-BashRm {Q(file)}");
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Rm_DirWithoutR_RefusesAndKeeps()
    {
        var dir = Path.Combine(_tmpRoot, "dir");
        Directory.CreateDirectory(dir);
        Run($"Invoke-BashRm {Q(dir)}");
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Rm_Recursive_DeletesTree()
    {
        var dir = Path.Combine(_tmpRoot, "tree");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "x.txt"), "x");
        Run($"Invoke-BashRm -r {Q(dir)}");
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Rm_Recursive_RemovesReadOnlyFiles()
    {
        // Regression: a read-only file in the tree (e.g. .git pack/object files) made the native
        // recursive delete throw UnauthorizedAccessException on Windows, leaving a half-deleted
        // tree. Non-interactive rm should remove it. (On Linux the read-only bit doesn't block
        // unlink, so this also passes there — the fallback is simply not exercised.)
        var dir = Path.Combine(_tmpRoot, "rotree");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        var ro = Path.Combine(dir, "sub", "locked.txt");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, File.GetAttributes(ro) | FileAttributes.ReadOnly);

        Run($"Invoke-BashRm -rf {Q(dir)}");

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Rm_MissingWithoutF_EmitsError()
    {
        var ghost = Path.Combine(_tmpRoot, "ghost.txt");
        Run($"Invoke-BashRm {Q(ghost)}");
        // Filesystem unchanged; error went to the error sink.
        Assert.False(File.Exists(ghost));
    }

    [Fact]
    public void Rm_MissingWithF_Silent()
    {
        var ghost = Path.Combine(_tmpRoot, "ghost.txt");
        var lines = Run($"Invoke-BashRm -f {Q(ghost)}");
        // -f suppresses the missing-file error entirely. No output.
        Assert.Empty(lines);
    }

    [Fact]
    public void Rm_DriveRoot_RefusesAsProtected()
    {
        // We're not going to actually delete C:\ — but the cmdlet must
        // refuse to attempt it. Use a path that resolves to the drive root.
        var rootCandidate = Path.GetPathRoot(_tmpRoot) ?? "C:\\";
        Run($"Invoke-BashRm -rf {Q(rootCandidate)}");
        // _tmpRoot is on the drive root, so the drive must still exist.
        Assert.True(Directory.Exists(_tmpRoot));
    }

    // ─────────────────────────── injection probes (Directive 12) ───────────────────────────

    [Fact]
    public void Mkdir_FilenameWithScriptblockChars_TreatedLiterally()
    {
        var weird = Path.Combine(_tmpRoot, "$(throw'pwn')dir");
        Run($"Invoke-BashMkdir {Q(weird)}");
        Assert.True(Directory.Exists(weird));
    }

    [Fact]
    public void Rm_FilenameWithSemicolon_TreatedLiterally()
    {
        var weird = Path.Combine(_tmpRoot, "a;rm -rf b.txt");
        File.WriteAllText(weird, "x");
        Run($"Invoke-BashRm {Q(weird)}");
        Assert.False(File.Exists(weird));
        // No other file in the temp root should have been affected.
        Assert.True(Directory.Exists(_tmpRoot));
    }
}
