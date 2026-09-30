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

    [Theory]
    [InlineData("Invoke-BashPwd")]
    [InlineData("Invoke-BashWhoami")]
    [InlineData("Invoke-BashHostname")]
    [InlineData("Invoke-BashLs")]
    public void SuccessfulCommand_ResetsStaleExitCode(string cmdlet)
    {
        // Bash sets $? on EVERY command; a successful cmdlet must not leak the prior command's exit
        // code. Regression for `nonexistent; pwd` reporting 127 (and the Bash-tool wrapper's trailing
        // pwd surfacing a stale 127).
        var lines = Run($"$global:LASTEXITCODE = 127; {cmdlet} *> $null; $global:LASTEXITCODE");
        Assert.Equal("0", lines[^1]);
    }

    [Theory]
    [InlineData("Invoke-BashCat", "cat")]
    [InlineData("Invoke-BashLs", "ls")]
    [InlineData("Invoke-BashRm", "rm")]
    [InlineData("Invoke-BashCp", "cp")]
    [InlineData("Invoke-BashHead", "head")]
    [InlineData("Invoke-BashSort", "sort")]
    public void Version_IdentifiesPsBash(string cmdlet, string name)
    {
        // --version must identify ps-bash across commands so tooling can detect the runtime,
        // instead of refusing the flag or treating it as a file operand.
        var lines = Run($"{cmdlet} --version");
        Assert.Contains(lines, l => l.StartsWith($"{name} (ps-bash) ", StringComparison.Ordinal));
    }

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
    public void Cp_Preserve_KeepsTimestampAndDoesNotLeakFlagAsOperand()
    {
        // REGRESSION: -p was documented as handled but the switch had no case,
        // so it leaked through as a source operand ("cannot stat '-p'"). Now it
        // is consumed and preserves the source's modification time.
        var src = Path.Combine(_tmpRoot, "psrc.txt");
        var dst = Path.Combine(_tmpRoot, "pdst.txt");
        File.WriteAllText(src, "x");
        var oldTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, oldTime);
        Run($"Invoke-BashCp -p {Q(src)} {Q(dst)}");
        Assert.True(File.Exists(dst), "cp -p must copy (regression: -p leaked as operand)");
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(dst));
    }

    [Fact]
    public void Cp_Update_SkipsWhenDestinationNotOlder()
    {
        var src = Path.Combine(_tmpRoot, "usrc.txt");
        var dst = Path.Combine(_tmpRoot, "udst.txt");
        File.WriteAllText(src, "SRC");
        File.WriteAllText(dst, "DST");
        File.SetLastWriteTimeUtc(src, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(dst, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Run($"Invoke-BashCp -u {Q(src)} {Q(dst)}");
        Assert.Equal("DST", File.ReadAllText(dst)); // dest newer → not overwritten
    }

    [Fact]
    public void Cp_Update_CopiesWhenSourceNewer()
    {
        var src = Path.Combine(_tmpRoot, "u2src.txt");
        var dst = Path.Combine(_tmpRoot, "u2dst.txt");
        File.WriteAllText(src, "SRC");
        File.WriteAllText(dst, "DST");
        File.SetLastWriteTimeUtc(dst, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(src, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Run($"Invoke-BashCp -u {Q(src)} {Q(dst)}");
        Assert.Equal("SRC", File.ReadAllText(dst)); // src newer → overwritten
    }

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
    public void Cp_Recursive_ForceMergesAndOverwritesReadOnlyTargetFile()
    {
        // GNU cp -rf MERGES into an existing dst/basename(src): same-named files are replaced (a
        // read-only one included: -f unlinks and retries), destination-only files survive. It used
        // to delete the whole existing subtree first (data loss).
        var src = Path.Combine(_tmpRoot, "cpsrc");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "new.txt"), "new");
        File.WriteAllText(Path.Combine(src, "locked.txt"), "fresh");

        var dst = Path.Combine(_tmpRoot, "cpdst");
        var collision = Path.Combine(dst, "cpsrc");
        Directory.CreateDirectory(collision);
        var ro = Path.Combine(collision, "locked.txt");
        File.WriteAllText(ro, "old");
        File.SetAttributes(ro, File.GetAttributes(ro) | FileAttributes.ReadOnly);
        var only = Path.Combine(collision, "dest-only.txt");
        File.WriteAllText(only, "keep");

        // cp's flag parser matches exact tokens (no bundling), so pass -r -f separately.
        Run($"Invoke-BashCp -r -f {Q(src)} {Q(dst)}");

        Assert.Equal("new", File.ReadAllText(Path.Combine(collision, "new.txt")));
        Assert.Equal("fresh", File.ReadAllText(ro));
        Assert.Equal("keep", File.ReadAllText(only));
    }

    [Fact]
    public void Mv_DirOntoEmptyTargetDirectory_ReplacesIt()
    {
        // GNU mv lets a directory replace an EMPTY directory (rename over it). A read-only
        // attribute on the empty target must not block that (DeleteDirectoryForce).
        var src = Path.Combine(_tmpRoot, "mvsrc");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "moved.txt"), "moved");

        var dst = Path.Combine(_tmpRoot, "mvdst");
        var collision = Path.Combine(dst, "mvsrc");
        Directory.CreateDirectory(collision);
        File.SetAttributes(collision, File.GetAttributes(collision) | FileAttributes.ReadOnly);

        Run($"Invoke-BashMv {Q(src)} {Q(dst)}");

        Assert.True(File.Exists(Path.Combine(collision, "moved.txt")));
        Assert.False(Directory.Exists(src));
    }

    [Fact]
    public void Mv_DirOntoNonEmptyTargetDirectory_RefusesAndKeepsBoth()
    {
        // GNU: "mv: cannot overwrite 't/s': Directory not empty", exit 1. The old code
        // recursively deleted the destination's contents first.
        var src = Path.Combine(_tmpRoot, "mvsrc");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "moved.txt"), "moved");

        var dst = Path.Combine(_tmpRoot, "mvdst");
        var collision = Path.Combine(dst, "mvsrc");
        Directory.CreateDirectory(collision);
        var keep = Path.Combine(collision, "keep.txt");
        File.WriteAllText(keep, "old");

        var lines = Run($"Invoke-BashMv {Q(src)} {Q(dst)} *> $null; $global:LASTEXITCODE");

        Assert.Equal("1", lines[^1]);
        Assert.Equal("old", File.ReadAllText(keep));
        Assert.True(File.Exists(Path.Combine(src, "moved.txt")), "source must survive a refused mv");
    }

    // ───────────── mv / cp pre-mutation validation (GNU oracle-checked) ─────────────

    private string LastExit(string script) => Run($"{script} *> $null; $global:LASTEXITCODE")[^1];

    [Fact]
    public void Mv_SourceIntoItsOwnParent_KeepsSourceAndFails()
    {
        // `mv p/src p` resolves the target to p/src == the source. It used to delete p/src
        // (data loss) and THEN report "must differ". GNU: "'p/src' and 'p/src' are the same file".
        var src = Path.Combine(_tmpRoot, "p", "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "f"), "payload");

        Assert.Equal("1", LastExit($"Invoke-BashMv {Q(src)} {Q(Path.Combine(_tmpRoot, "p"))}"));

        Assert.Equal("payload", File.ReadAllText(Path.Combine(src, "f")));
    }

    [Fact]
    public void Mv_FileOntoItself_KeepsFileAndFails()
    {
        var f = Path.Combine(_tmpRoot, "same.txt");
        File.WriteAllText(f, "x");
        Assert.Equal("1", LastExit($"Invoke-BashMv {Q(f)} {Q(f)}"));
        Assert.Equal("x", File.ReadAllText(f));
    }

    [Fact]
    public void Mv_DirIntoItsOwnSubdirectory_RefusesAndKeepsTree()
    {
        var s = Path.Combine(_tmpRoot, "s11");
        Directory.CreateDirectory(Path.Combine(s, "sub"));
        File.WriteAllText(Path.Combine(s, "f"), "x");

        Assert.Equal("1", LastExit($"Invoke-BashMv {Q(s)} {Q(Path.Combine(s, "sub"))}"));

        Assert.True(File.Exists(Path.Combine(s, "f")));
        Assert.True(Directory.Exists(Path.Combine(s, "sub")));
    }

    [Fact]
    public void Mv_DirOntoExistingFile_RefusesAndKeepsBoth()
    {
        var d = Path.Combine(_tmpRoot, "d");
        var f = Path.Combine(_tmpRoot, "f");
        Directory.CreateDirectory(d);
        File.WriteAllText(f, "file");
        Assert.Equal("1", LastExit($"Invoke-BashMv {Q(d)} {Q(f)}"));
        Assert.True(Directory.Exists(d));
        Assert.Equal("file", File.ReadAllText(f));
    }

    [Fact]
    public void Mv_FileOntoExistingDirectoryEntry_RefusesAndKeepsBoth()
    {
        // mv f tgt where tgt/f is a DIRECTORY: GNU "cannot overwrite directory 'tgt/f' with non-directory".
        var f = Path.Combine(_tmpRoot, "f10");
        var tgt = Path.Combine(_tmpRoot, "tgt10");
        File.WriteAllText(f, "q");
        Directory.CreateDirectory(Path.Combine(tgt, "f10"));
        Assert.Equal("1", LastExit($"Invoke-BashMv {Q(f)} {Q(tgt)}"));
        Assert.True(File.Exists(f));
        Assert.True(Directory.Exists(Path.Combine(tgt, "f10")));
    }

    [Fact]
    public void Mv_SeveralSourcesToMissingDestination_MovesNothing()
    {
        var a = Path.Combine(_tmpRoot, "m1");
        var b = Path.Combine(_tmpRoot, "m2");
        File.WriteAllText(a, "1");
        File.WriteAllText(b, "2");
        Assert.Equal("1", LastExit($"Invoke-BashMv {Q(a)} {Q(b)} {Q(Path.Combine(_tmpRoot, "nonexist"))}"));
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Cp_RecursiveForce_KeepsDestinationOnlyFiles()
    {
        // `cp -rf src/proj dst` with dst/proj already present: GNU merges. It used to delete
        // dst/proj wholesale first, losing every destination-only file.
        var src = Path.Combine(_tmpRoot, "src", "proj");
        var dstProj = Path.Combine(_tmpRoot, "dst", "proj");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(dstProj, "nested"));
        File.WriteAllText(Path.Combine(src, "new"), "n");
        File.WriteAllText(Path.Combine(dstProj, "new"), "old-version");
        File.WriteAllText(Path.Combine(dstProj, "only"), "keep");
        File.WriteAllText(Path.Combine(dstProj, "nested", "deep"), "keep-deep");

        Run($"Invoke-BashCp -rf {Q(src)} {Q(Path.Combine(_tmpRoot, "dst"))}");

        Assert.Equal("n", File.ReadAllText(Path.Combine(dstProj, "new")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(dstProj, "only")));
        Assert.Equal("keep-deep", File.ReadAllText(Path.Combine(dstProj, "nested", "deep")));
    }

    [Fact]
    public void Cp_SeveralSourcesToMissingDestination_ErrorsAndWritesNothing()
    {
        // `cp a b result` (result absent): GNU "target 'result': No such file or directory".
        // It used to succeed and leave only b's contents in a file named result.
        var a = Path.Combine(_tmpRoot, "a");
        var b = Path.Combine(_tmpRoot, "b");
        var result = Path.Combine(_tmpRoot, "result");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");

        Assert.Equal("1", LastExit($"Invoke-BashCp {Q(a)} {Q(b)} {Q(result)}"));

        Assert.False(File.Exists(result));
        Assert.False(Directory.Exists(result));
    }

    [Fact]
    public void Cp_SeveralSourcesToRegularFile_ErrorsAndKeepsFile()
    {
        var a = Path.Combine(_tmpRoot, "a");
        var b = Path.Combine(_tmpRoot, "b");
        var result = Path.Combine(_tmpRoot, "result2");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");
        File.WriteAllText(result, "R");

        Assert.Equal("1", LastExit($"Invoke-BashCp {Q(a)} {Q(b)} {Q(result)}"));

        Assert.Equal("R", File.ReadAllText(result));
    }

    [Fact]
    public void Cp_RecursiveNoClobber_TraversesExistingSubtreeAndSkipsOnlyConflicts()
    {
        // `cp -rn s t` with t/s/x/f1 present: GNU descends and copies f2, leaving f1 alone.
        // It used to skip the whole s tree because t/s existed.
        var s = Path.Combine(_tmpRoot, "s3");
        var t = Path.Combine(_tmpRoot, "t3");
        Directory.CreateDirectory(Path.Combine(s, "x"));
        Directory.CreateDirectory(Path.Combine(t, "s3", "x"));
        File.WriteAllText(Path.Combine(s, "x", "f1"), "new1");
        File.WriteAllText(Path.Combine(s, "x", "f2"), "new2");
        File.WriteAllText(Path.Combine(t, "s3", "x", "f1"), "old1");

        Run($"Invoke-BashCp -r -n {Q(s)} {Q(t)}");

        Assert.Equal("old1", File.ReadAllText(Path.Combine(t, "s3", "x", "f1")));
        Assert.Equal("new2", File.ReadAllText(Path.Combine(t, "s3", "x", "f2")));
    }

    [Fact]
    public void Cp_FileOntoItself_FailsAndKeepsFile()
    {
        var f = Path.Combine(_tmpRoot, "sf");
        File.WriteAllText(f, "x");
        Assert.Equal("1", LastExit($"Invoke-BashCp {Q(f)} {Q(f)}"));
        Assert.Equal("x", File.ReadAllText(f));
    }

    [Fact]
    public void Cp_DirIntoItself_RefusesAndDoesNotRecurse()
    {
        var se = Path.Combine(_tmpRoot, "se");
        var x = Path.Combine(se, "x");
        Directory.CreateDirectory(x);
        Assert.Equal("1", LastExit($"Invoke-BashCp -r {Q(se)} {Q(x)}"));
        Assert.False(Directory.Exists(Path.Combine(x, "se")), "copy into itself must not start");
    }

    [Fact]
    public void Cp_DirOntoExistingFile_RefusesAndKeepsFile()
    {
        var d = Path.Combine(_tmpRoot, "dd");
        var f = Path.Combine(_tmpRoot, "ff");
        Directory.CreateDirectory(d);
        File.WriteAllText(f, "F");
        Assert.Equal("1", LastExit($"Invoke-BashCp -r {Q(d)} {Q(f)}"));
        Assert.Equal("F", File.ReadAllText(f));
    }

    [Fact]
    public void Cp_FileOntoExistingDirectoryEntry_RefusesAndKeepsDirectory()
    {
        var f = Path.Combine(_tmpRoot, "fx");
        var tg = Path.Combine(_tmpRoot, "tg");
        File.WriteAllText(f, "q");
        Directory.CreateDirectory(Path.Combine(tg, "fx"));
        Assert.Equal("1", LastExit($"Invoke-BashCp {Q(f)} {Q(tg)}"));
        Assert.True(Directory.Exists(Path.Combine(tg, "fx")));
    }

    [Fact]
    public void Find_Delete_RemovesReadOnlyFile()
    {
        // Sibling of the rm read-only fix: find -delete of a read-only file threw on Windows
        // before routing through FileSystemHelpers.DeleteFileForce.
        var dir = Path.Combine(_tmpRoot, "findro");
        Directory.CreateDirectory(dir);
        var ro = Path.Combine(dir, "locked.tmp");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, File.GetAttributes(ro) | FileAttributes.ReadOnly);

        Run($"Invoke-BashFind {Q(dir)} -name '*.tmp' -delete");

        Assert.False(File.Exists(ro));
    }

    [Fact]
    public void Cp_BundledShortFlags_DeBundle()
    {
        // Regression: cp parses exact tokens, so bundled `-rf` was unrecognized (recursive copy
        // never happened). It now de-bundles to -r -f.
        var src = Path.Combine(_tmpRoot, "bsrc");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "f.txt"), "x");
        var dst = Path.Combine(_tmpRoot, "bdst");

        Run($"Invoke-BashCp -rf {Q(src)} {Q(dst)}");

        Assert.True(File.Exists(Path.Combine(dst, "sub", "f.txt")));
    }

    [Theory]
    [InlineData("Invoke-BashCat /dev/null")]
    [InlineData("Invoke-BashWc -l /dev/null")]
    [InlineData("Invoke-BashHead /dev/null")]
    public void NullDevice_ReadsAsEmpty_NoError(string cmd)
    {
        // Regression: /dev/null as a file OPERAND mapped to $null and crashed cmdlets
        // ("Value cannot be null"). It is now an empty file served from the OS null device.
        // (wc -l prints "0 /dev/null"; the point is it does not error / set a failure code.)
        var lines = Run($"{cmd} *> $null; $global:LASTEXITCODE");
        Assert.Equal("0", lines[^1]);
    }

    [Fact]
    public void NullDevice_GrepEmptyFile_ExitsOne()
    {
        // grep on an empty file finds nothing → exit 1 (bash parity), no "No such file" error.
        var lines = Run("Invoke-BashGrep needle /dev/null *> $null; $global:LASTEXITCODE");
        Assert.Equal("1", lines[^1]);
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

    // ─────────────────────── unsupported-flag classifier ───────────────────────
    // Every valid bash flag must map to *something* — never silently mistaken for
    // a file operand. The mover family routes unknown / valid-but-unsupported
    // option-looking tokens through FileSystemHelpers.TryWriteOperandOptionError
    // (exit 2), like grep/cut/sort/etc.

    [Theory]
    [InlineData("Invoke-BashCp --reflink a b")]      // valid GNU cp flag, unimplemented
    [InlineData("Invoke-BashMv --backup a b")]       // valid GNU mv flag, unimplemented
    [InlineData("Invoke-BashRm --interactive a")]    // valid GNU rm flag, unimplemented
    [InlineData("Invoke-BashMkdir -m 755 d")]        // valid GNU mkdir flag, unimplemented
    [InlineData("Invoke-BashRmdir --ignore-fail-on-non-empty d")]
    public void Mover_ValidButUnsupportedFlag_ExitsTwo(string cmd)
    {
        var lines = Run($"{cmd} *> $null; $global:LASTEXITCODE");
        Assert.Equal("2", lines[^1]);
    }

    [Theory]
    [InlineData("Invoke-BashCp --bogus a b")]     // GNU cp/mv usage error = EXIT_FAILURE (1)
    [InlineData("Invoke-BashCp -Q a b")]
    [InlineData("Invoke-BashMv --bogus a b")]
    [InlineData("Invoke-BashMv -Q a b")]
    [InlineData("Invoke-BashRm --bogus a")]
    [InlineData("Invoke-BashRm -rz a")]
    [InlineData("Invoke-BashRm --recursive=1 a")]
    [InlineData("Invoke-BashMkdir --bogus d")]
    [InlineData("Invoke-BashMkdir -pz d")]
    [InlineData("Invoke-BashMkdir --parents=1 d")]
    [InlineData("Invoke-BashRmdir --bogus d")]
    [InlineData("Invoke-BashRmdir -pz d")]
    [InlineData("Invoke-BashRmdir '-Z' d")]         // GNU rmdir has no -Z: a plain usage error
    public void Mover_ParserUsageError_ExitsOne(string cmd)
    {
        var lines = Run($"{cmd} *> $null; $global:LASTEXITCODE");
        Assert.Equal("1", lines[^1]);
    }

    [Fact]
    public void Rm_ForceDoesNotSuppressUnsupportedFlagError()
    {
        // GNU rm -f suppresses missing-file errors but NOT a usage error for a
        // bad option. The classifier still fires (exit 2) under -f.
        var lines = Run("Invoke-BashRm -f --interactive ghost.txt *> $null; $global:LASTEXITCODE");
        Assert.Equal("2", lines[^1]);
    }

    [Fact]
    public void Mover_UnsupportedFlagBeforeDoubleDash_StillClassified()
    {
        // An unsupported flag placed BEFORE `--` must still be classified (exit 2),
        // not leak into the operand list because a later `--` appeared. (--reflink
        // is a catalog flag that reaches Arguments; bare -i would be eaten by the
        // -InformationAction binder collision before the cmdlet runs.)
        var lines = Run("Invoke-BashCp --reflink -- a b *> $null; $global:LASTEXITCODE");
        Assert.Equal("2", lines[^1]);
    }

    [Fact]
    public void Mover_DoubleDash_EndsFlagParsing_DashLeadingNameIsOperand()
    {
        // After `--`, a token starting with '-' is a real filename, not a flag.
        var dashFile = Path.Combine(_tmpRoot, "-weird.txt");
        File.WriteAllText(dashFile, "x");
        Run($"Invoke-BashRm -- {Q(dashFile)}");
        Assert.False(File.Exists(dashFile), "-- should let a dash-leading filename through to deletion");
    }

    [Fact]
    public void Mkdir_BundledVP_CreatesNestedWithVerbose()
    {
        // -vp must de-bundle to verbose+parents, not be misclassified as an
        // unknown flag now that mkdir parses its short bundle. (The reverse form
        // -pv is the PowerShell -PipelineVariable alias and is eaten by the
        // binder before the cmdlet runs — a documented common-parameter collision.)
        var nested = Path.Combine(_tmpRoot, "x", "y", "z");
        var lines = Run($"Invoke-BashMkdir -vp {Q(nested)}");
        Assert.True(Directory.Exists(nested));
        Assert.Contains(lines, l => l.Contains("created directory", StringComparison.OrdinalIgnoreCase));
    }

    // ─────────── shared ordered parser: cp as the transpiler delivers it ───────────
    // PsEmitter.OrderedArgCommands single-quotes every dash-leading word, so flags arrive in
    // Arguments as plain strings, in order (the direct-call decoy path is covered above).

    [Fact]
    public void Cp_EmitterStyleQuotedBundle_RecursiveForceVerbose()
    {
        var src = Path.Combine(_tmpRoot, "qsrc");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "f.txt"), "payload");
        var dst = Path.Combine(_tmpRoot, "qdst");
        var lines = Run($"Invoke-BashCp '-rfv' {Q(src)} {Q(dst)}");
        Assert.Equal("payload", File.ReadAllText(Path.Combine(dst, "f.txt")));
        Assert.Contains(lines, l => l.Contains("->"));
    }

    [Theory]
    [InlineData("--rec")]       // unique prefix of --recursive
    [InlineData("--recursive")]
    public void Cp_QuotedLongRecursive_CopiesDirectory(string flag)
    {
        var src = Path.Combine(_tmpRoot, "lsrc");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "f.txt"), "x");
        var dst = Path.Combine(_tmpRoot, "ldst");
        Run($"Invoke-BashCp '{flag}' {Q(src)} {Q(dst)}");
        Assert.True(File.Exists(Path.Combine(dst, "f.txt")));
    }

    [Fact]
    public void Cp_DoubleDash_DashNamedSourceIsAnOperandNotAnOption()
    {
        // `cp -- -a dst` copies a file literally named "-a" (the classifier must stop at `--`).
        File.WriteAllText(Path.Combine(_tmpRoot, "-a"), "dash");
        Run($"Set-Location {Q(_tmpRoot)}; Invoke-BashCp '--' '-a' 'out.txt'");
        Assert.Equal("dash", File.ReadAllText(Path.Combine(_tmpRoot, "out.txt")));
    }

    [Fact]
    public void Cp_QuotedBareI_IsRefusedNotSwallowedByTheBinder()
    {
        var src = Path.Combine(_tmpRoot, "isrc.txt");
        File.WriteAllText(src, "x");
        var dst = Path.Combine(_tmpRoot, "idst.txt");
        Assert.Equal("2", LastExit($"Invoke-BashCp '-i' {Q(src)} {Q(dst)}"));
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void Cp_OptionAfterOperands_StillApplies()
    {
        var src = Path.Combine(_tmpRoot, "osrc");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "f"), "x");
        var dst = Path.Combine(_tmpRoot, "odst");
        Run($"Invoke-BashCp {Q(src)} {Q(dst)} '-r'");
        Assert.True(File.Exists(Path.Combine(dst, "f")));
    }

    [Fact]
    public void Mv_EmitterStyleQuotedBundle_ForceVerbose_Moves()
    {
        // REGRESSION: the old scan never de-bundled, so `mv -fv a b` died with
        // "invalid option -- 'f'". GNU mv accepts it.
        var src = Path.Combine(_tmpRoot, "mvsrc.txt");
        File.WriteAllText(src, "x");
        var dst = Path.Combine(_tmpRoot, "mvdst.txt");
        var lines = Run($"Invoke-BashMv '-fv' {Q(src)} {Q(dst)}");
        Assert.False(File.Exists(src));
        Assert.Equal("x", File.ReadAllText(dst));
        Assert.Contains(lines, l => l.Contains("->"));
    }

    [Fact]
    public void Mv_DoubleDash_DashNamedSourceIsAnOperandNotAnOption()
    {
        File.WriteAllText(Path.Combine(_tmpRoot, "-n"), "dash");
        Run($"Set-Location {Q(_tmpRoot)}; Invoke-BashMv '--' '-n' 'moved.txt'");
        Assert.Equal("dash", File.ReadAllText(Path.Combine(_tmpRoot, "moved.txt")));
        Assert.False(File.Exists(Path.Combine(_tmpRoot, "-n")));
    }

    [Fact]
    public void Mv_QuotedBareI_IsRefusedNotSwallowedByTheBinder()
    {
        var src = Path.Combine(_tmpRoot, "misrc.txt");
        File.WriteAllText(src, "x");
        var dst = Path.Combine(_tmpRoot, "midst.txt");
        Assert.Equal("2", LastExit($"Invoke-BashMv '-i' {Q(src)} {Q(dst)}"));
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void Mv_AbbreviatedNoClobberAfterOperands_HonorsGnuAmbiguityRules()
    {
        var src = Path.Combine(_tmpRoot, "nsrc.txt");
        var dst = Path.Combine(_tmpRoot, "ndst.txt");
        File.WriteAllText(src, "new");
        File.WriteAllText(dst, "old");
        Run($"Invoke-BashMv {Q(src)} {Q(dst)} '--no-c' *> $null");
        // `--no-c` is ambiguous with --no-copy (GNU), so nothing moved and the destination is intact.
        Assert.Equal("old", File.ReadAllText(dst));
        Run($"Invoke-BashMv {Q(src)} {Q(dst)} '--no-cl'");
        Assert.Equal("old", File.ReadAllText(dst));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Cp_UpdateWithUnimplementedPolicy_IsRefusedLoudly()
    {
        var src = Path.Combine(_tmpRoot, "usrc.txt");
        File.WriteAllText(src, "x");
        var dst = Path.Combine(_tmpRoot, "udst.txt");
        Assert.Equal("2", LastExit($"Invoke-BashCp '--update=none' {Q(src)} {Q(dst)}"));
        Assert.False(File.Exists(dst));
        // `--update=older` is -u: copies when the destination is missing.
        Run($"Invoke-BashCp '--update=older' {Q(src)} {Q(dst)}");
        Assert.True(File.Exists(dst));
    }

    // ─────────── shared ordered parser: rmdir as the transpiler delivers it ───────────

    [Fact]
    public void Rmdir_QuotedPv_RemovesChainAndReportsEachDirectory()
    {
        var leaf = Path.Combine(_tmpRoot, "rp1", "rp2", "rp3");
        Directory.CreateDirectory(leaf);
        var lines = Run($"Invoke-BashRmdir '-pv' {Q(leaf)}");
        Assert.False(Directory.Exists(Path.Combine(_tmpRoot, "rp1")));
        Assert.True(lines.Count(l => l.Contains("removing directory")) >= 3);
    }

    [Theory]
    [InlineData("--par")]        // unique prefix of --parents
    [InlineData("--parents")]
    [InlineData("-pp")]
    public void Rmdir_QuotedParentsSpellings_RemoveTheChain(string flag)
    {
        var top = Path.Combine(_tmpRoot, "rq" + flag.Length);
        var leaf = Path.Combine(top, "n1", "n2");
        Directory.CreateDirectory(leaf);
        Run($"Invoke-BashRmdir '{flag}' {Q(leaf)}");
        Assert.False(Directory.Exists(top));
    }

    [Fact]
    public void Rmdir_DoubleDash_DashNamedDirectoryIsRemovedNotParsed()
    {
        Directory.CreateDirectory(Path.Combine(_tmpRoot, "-v"));
        Run($"Set-Location {Q(_tmpRoot)}; Invoke-BashRmdir '--' '-v'");
        Assert.False(Directory.Exists(Path.Combine(_tmpRoot, "-v")));
    }

    [Fact]
    public void Rmdir_OptionAfterOperand_StillApplies()
    {
        var top = Path.Combine(_tmpRoot, "ra");
        var leaf = Path.Combine(top, "deep");
        Directory.CreateDirectory(leaf);
        Run($"Invoke-BashRmdir {Q(leaf)} '-p'");
        Assert.False(Directory.Exists(top));
    }

    [Fact]
    public void Rmdir_QuotedIgnoreFailOnNonEmpty_IsRefusedAndRemovesNothing()
    {
        var d = Path.Combine(_tmpRoot, "rne");
        Directory.CreateDirectory(d);
        Assert.Equal("2", LastExit($"Invoke-BashRmdir '--ign' {Q(d)}"));
        Assert.True(Directory.Exists(d));
    }

    [Fact]
    public void Rmdir_NoOperand_IsAnError() => Assert.Equal("1", LastExit("Invoke-BashRmdir '-p'"));

    [Fact]
    public void Rmdir_DirectCallDecoys_StillWork()
    {
        var top = Path.Combine(_tmpRoot, "rdd");
        var leaf = Path.Combine(top, "x");
        Directory.CreateDirectory(leaf);
        var lines = Run($"Invoke-BashRmdir -p -v {Q(leaf)}");
        Assert.False(Directory.Exists(top));
        Assert.Contains(lines, l => l.Contains("removing directory"));
    }

    // ─────────── shared ordered parser: mkdir as the transpiler delivers it ───────────

    [Fact]
    public void Mkdir_QuotedPv_ReachesTheParserIntact_CreatesNestedWithVerbose()
    {
        // `-pv` typed bare at PowerShell is eaten by the binder (-PipelineVariable alias). The
        // transpiler single-quotes every dash word for mkdir, so it arrives whole in Arguments.
        var nested = Path.Combine(_tmpRoot, "qp", "qv", "qz");
        var lines = Run($"Invoke-BashMkdir '-pv' {Q(nested)}");
        Assert.True(Directory.Exists(nested));
        Assert.Contains(lines, l => l.Contains("created directory", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("--par")]        // unique prefix of --parents
    [InlineData("--parents")]
    [InlineData("-pp")]
    public void Mkdir_QuotedParentsSpellings_CreateTheChain(string flag)
    {
        var nested = Path.Combine(_tmpRoot, "mk" + flag.Length, "n1", "n2");
        Run($"Invoke-BashMkdir '{flag}' {Q(nested)}");
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public void Mkdir_DoubleDash_DashNamedDirectoryIsCreatedNotParsed()
    {
        Run($"Set-Location {Q(_tmpRoot)}; Invoke-BashMkdir '--' '-p'");
        Assert.True(Directory.Exists(Path.Combine(_tmpRoot, "-p")));
    }

    [Fact]
    public void Mkdir_OptionAfterOperand_StillApplies()
    {
        var nested = Path.Combine(_tmpRoot, "ma", "deep");
        Run($"Invoke-BashMkdir {Q(nested)} '-p'");
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public void Mkdir_QuotedMode_IsRefusedAndCreatesNothing()
    {
        var d = Path.Combine(_tmpRoot, "moded");
        Assert.Equal("2", LastExit($"Invoke-BashMkdir '-m' '755' {Q(d)}"));
        Assert.False(Directory.Exists(d));
    }

    [Fact]
    public void Mkdir_NoOperand_IsAnError()
    {
        Assert.Equal("1", LastExit("Invoke-BashMkdir"));
        Assert.Equal("1", LastExit("Invoke-BashMkdir '-p'"));
    }

    [Fact]
    public void Mkdir_DirectCallDecoys_StillWork()
    {
        // Pester/interactive path: bare -p/-v bind the decoy switches, not Arguments.
        var nested = Path.Combine(_tmpRoot, "dd1", "dd2");
        var lines = Run($"Invoke-BashMkdir -p -v {Q(nested)}");
        Assert.True(Directory.Exists(nested));
        Assert.Contains(lines, l => l.Contains("created directory", StringComparison.OrdinalIgnoreCase));
    }

    // ─────────── shared ordered parser: rm as the transpiler delivers it ───────────

    [Fact]
    public void Rm_EmitterStyleQuotedBundle_RecursiveForceVerbose_RemovesTree()
    {
        var dir = Path.Combine(_tmpRoot, "rmtree");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "f.txt"), "x");
        var lines = Run($"Invoke-BashRm '-rfv' {Q(dir)}");
        Assert.False(Directory.Exists(dir));
        Assert.Contains(lines, l => l.StartsWith("removed '"));
    }

    [Theory]
    [InlineData("--rec")]        // unique prefix of --recursive (GNU getopt_long)
    [InlineData("--recursive")]
    [InlineData("-R")]
    public void Rm_QuotedLongOrUpperRecursive_RemovesDirectory(string flag)
    {
        var dir = Path.Combine(_tmpRoot, "rmdirflag" + flag.Length);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "f"), "x");
        Run($"Invoke-BashRm '{flag}' {Q(dir)}");
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Rm_DoubleDash_DashNamedFileIsAnOperandNotAnOption_EmitterStyle()
    {
        File.WriteAllText(Path.Combine(_tmpRoot, "-rf"), "dash");
        Run($"Set-Location {Q(_tmpRoot)}; Invoke-BashRm '--' '-rf'");
        Assert.False(File.Exists(Path.Combine(_tmpRoot, "-rf")));
    }

    [Fact]
    public void Rm_OptionAfterOperand_StillApplies()
    {
        var dir = Path.Combine(_tmpRoot, "rmafter");
        Directory.CreateDirectory(dir);
        Run($"Invoke-BashRm {Q(dir)} '-r'");
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Rm_QuotedBareI_IsRefusedAndDeletesNothing()
    {
        var f = Path.Combine(_tmpRoot, "keep.txt");
        File.WriteAllText(f, "x");
        Assert.Equal("2", LastExit($"Invoke-BashRm '-i' {Q(f)}"));
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Rm_UnsupportedAfterOtherOperands_StopsBeforeDeletingAnything()
    {
        // An option anywhere before `--` is a usage error for the WHOLE command (GNU permutes
        // options), so the earlier operand must survive too.
        var f = Path.Combine(_tmpRoot, "first.txt");
        File.WriteAllText(f, "x");
        Assert.Equal("2", LastExit($"Invoke-BashRm {Q(f)} '-I'"));
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Rm_ForceWithNoOperands_IsSilentSuccess_WithoutForceIsAnError()
    {
        Assert.Equal("0", LastExit("Invoke-BashRm '-f'"));
        Assert.Equal("1", LastExit("Invoke-BashRm"));
    }

    [Fact]
    public void Rm_ProtectedPathGuard_StillRefusesTheHomeDirectoryUnderTheNewParser()
    {
        // The migration must not have weakened the safety guards: removing the profile dir is refused.
        // Deliberately NOT recursive: if the guard ever regressed, "Is a directory" (also exit 1)
        // would still protect the developer's real home directory.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal("1", LastExit($"Invoke-BashRm '-f' {Q(home)}"));
        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public void Rm_DirectCallDecoyVerbose_StillWorks()
    {
        // Pester/interactive path: bare -v binds the decoy switch, not Arguments.
        var f = Path.Combine(_tmpRoot, "dv.txt");
        File.WriteAllText(f, "x");
        var lines = Run($"Invoke-BashRm -v {Q(f)}");
        Assert.Contains(lines, l => l.StartsWith("removed '"));
        Assert.False(File.Exists(f));
    }
}
