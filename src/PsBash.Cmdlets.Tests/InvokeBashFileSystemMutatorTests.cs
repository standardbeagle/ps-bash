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
    public void Cp_Recursive_OverwritesReadOnlyTarget()
    {
        // Sibling of the rm read-only fix: cp -rf into an existing dir resolves the target to
        // dst/basename(src) and force-deletes it first when it already exists. A read-only
        // descendant in that target threw UnauthorizedAccessException on Windows before cp routed
        // through the shared FileSystemHelpers.DeleteDirectoryForce.
        var src = Path.Combine(_tmpRoot, "cpsrc");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "new.txt"), "new");

        // Existing dest dir already holds a "cpsrc" subtree (same basename) with a read-only file.
        var dst = Path.Combine(_tmpRoot, "cpdst");
        var collision = Path.Combine(dst, "cpsrc");
        Directory.CreateDirectory(collision);
        var ro = Path.Combine(collision, "locked.txt");
        File.WriteAllText(ro, "old");
        File.SetAttributes(ro, File.GetAttributes(ro) | FileAttributes.ReadOnly);

        // cp's flag parser matches exact tokens (no bundling), so pass -r -f separately.
        Run($"Invoke-BashCp -r -f {Q(src)} {Q(dst)}");

        // The read-only target subtree was force-deleted and replaced by the source.
        Assert.True(File.Exists(Path.Combine(collision, "new.txt")));
        Assert.False(File.Exists(ro));
    }

    [Fact]
    public void Mv_OverwritesReadOnlyTargetDirectory()
    {
        // Sibling of the rm read-only fix: mv into an existing dir resolves to dst/basename(src)
        // and does remove-then-move when it already exists. A read-only descendant in the target
        // threw on Windows before mv routed through FileSystemHelpers.DeleteDirectoryForce.
        var src = Path.Combine(_tmpRoot, "mvsrc");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "moved.txt"), "moved");

        var dst = Path.Combine(_tmpRoot, "mvdst");
        var collision = Path.Combine(dst, "mvsrc");
        Directory.CreateDirectory(collision);
        var ro = Path.Combine(collision, "locked.txt");
        File.WriteAllText(ro, "old");
        File.SetAttributes(ro, File.GetAttributes(ro) | FileAttributes.ReadOnly);

        Run($"Invoke-BashMv {Q(src)} {Q(dst)}");

        Assert.True(File.Exists(Path.Combine(collision, "moved.txt")));
        Assert.False(File.Exists(ro));
        Assert.False(Directory.Exists(src));
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
    [InlineData("Invoke-BashCp --bogus a b")]
    [InlineData("Invoke-BashMv --bogus a b")]
    [InlineData("Invoke-BashRm --bogus a")]
    [InlineData("Invoke-BashMkdir --bogus d")]
    [InlineData("Invoke-BashRmdir --bogus d")]
    public void Mover_UnrecognizedFlag_ExitsTwo(string cmd)
    {
        var lines = Run($"{cmd} *> $null; $global:LASTEXITCODE");
        Assert.Equal("2", lines[^1]);
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
}
