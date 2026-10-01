using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// cp / mv refuse "the same file" when two DIFFERENT names lead to one file: a hard link, or a
/// symlink that resolves to the other operand. Expectations (messages, exit, what survives) come from
/// GNU coreutils 9.4 under <c>wsl bash</c>:
/// <list type="bullet">
/// <item><c>cp h1 h2</c> (hard links) and <c>cp y1 y2</c> (y2 -> y1): <c>'a' and 'b' are the same file</c>, exit 1; <c>-f</c> and <c>-u</c> do not help, <c>-n</c> silently skips (exit 0).</item>
/// <item><c>mv h1 h2</c> (hard links): same message, both names remain. <c>mv link target</c>: refused too. But <c>mv y1 y2</c> where y2 is a symlink TO y1 works: y2 is replaced by the moved file.</item>
/// <item>Copying/moving several hard links INTO A DIRECTORY is fine (different target names).</item>
/// </list>
/// Hard links are created with the OS primitive (Windows <c>CreateHardLink</c>, Unix <c>ln</c>); symlink
/// cases skip with a reason where the test process cannot create one (Windows without privilege).
/// </summary>
public class SameFileIdentityTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public SameFileIdentityTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-sam-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private string P(string rel) => Path.Combine(_tmp, rel);

    private void File1(string rel, string content = "one\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), content);
    }

    // ───────────── link creation helpers ─────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newName, string existing, IntPtr security);

    private bool HardLink(string existingRel, string newRel)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return CreateHardLinkW(P(newRel), P(existingRel), IntPtr.Zero);
            var psi = new ProcessStartInfo("ln") { RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add(P(existingRel));
            psi.ArgumentList.Add(P(newRel));
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private bool FileSymlink(string linkRel, string targetRel)
    {
        try { File.CreateSymbolicLink(P(linkRel), P(targetRel)); return true; }
        catch { return false; }
    }

    private void MustHardLink(string existingRel, string newRel) =>
        Assert.True(HardLink(existingRel, newRel), "test setup: could not create a hard link");

    private string Read(string rel) => File.ReadAllText(P(rel));

    // ───────────── cp: hard links ─────────────

    [Theory]
    [InlineData("h1 h2", "cp: 'h1' and 'h2' are the same file")]
    [InlineData("h2 h1", "cp: 'h2' and 'h1' are the same file")]
    [InlineData("h1 ./h2", "cp: 'h1' and './h2' are the same file")]
    [InlineData("'-f' h1 h2", "cp: 'h1' and 'h2' are the same file")]   // force does not make it legal
    [InlineData("'-u' h1 h2", "cp: 'h1' and 'h2' are the same file")]   // nor does update
    public void Cp_HardLinkedOperands_AreTheSameFile(string args, string message)
    {
        File1("h1");
        MustHardLink("h1", "h2");

        InTmp($"Invoke-BashCp {args}").AssertFailed(1, message);

        Assert.Equal("one\n", Read("h1"));
        Assert.Equal("one\n", Read("h2"));
    }

    [Fact]
    public void Cp_NoClobber_OnHardLinkedOperands_SilentlySkips()
    {
        File1("h1");
        MustHardLink("h1", "h2");
        // exit 0; the only stderr is coreutils 9.4's -n warning
        Assert.Equal(0, InTmp("Invoke-BashCp '-n' h1 h2").ExitCode);
    }

    [Fact]
    public void Cp_NoClobber_OnTheSamePath_SilentlySkips()
    {
        File1("a");
        Assert.Equal(0, InTmp("Invoke-BashCp '-n' a a").ExitCode);
    }

    [Fact]
    public void Cp_HardLinksIntoADirectory_AreFine()
    {
        File1("h1");
        MustHardLink("h1", "h2");
        Directory.CreateDirectory(P("d"));

        InTmp("Invoke-BashCp h1 h2 d").AssertSuccess();

        Assert.Equal("one\n", Read("d/h1"));
        Assert.Equal("one\n", Read("d/h2"));
    }

    [Fact]
    public void Cp_ToAHardLinkOfTheSourceInsideTheDestinationDirectory_IsRefused()
    {
        File1("a");
        Directory.CreateDirectory(P("d"));
        MustHardLink("a", "d/a");   // d/a IS a (same inode): `cp a d` would copy a onto itself

        InTmp("Invoke-BashCp a d").AssertFailed(1, "cp: 'a' and 'd/a' are the same file");
    }

    [Fact]
    public void Cp_DifferentFilesWithTheSameSizeAndTimestamp_AreNotMistakenForHardLinks()
    {
        File1("p", "same\n");
        File1("q", "same\n");
        var t = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(P("p"), t);
        File.SetLastWriteTimeUtc(P("q"), t);

        InTmp("Invoke-BashCp p q").AssertSuccess();
    }

    [Fact]
    public void Cp_RecursiveCopyOfATreeWithHardLinks_Works()
    {
        File1("t/a");
        MustHardLink("t/a", "t/b");
        InTmp("Invoke-BashCp -r t u").AssertSuccess();
        Assert.Equal("one\n", Read("u/a"));
        Assert.Equal("one\n", Read("u/b"));
    }

    // ───────────── mv: hard links ─────────────

    [Theory]
    [InlineData("h1 h2", "mv: 'h1' and 'h2' are the same file")]
    [InlineData("h2 h1", "mv: 'h2' and 'h1' are the same file")]
    [InlineData("'-f' h1 h2", "mv: 'h1' and 'h2' are the same file")]
    public void Mv_HardLinkedOperands_AreTheSameFile_AndBothNamesRemain(string args, string message)
    {
        File1("h1");
        MustHardLink("h1", "h2");

        InTmp($"Invoke-BashMv {args}").AssertFailed(1, message);

        Assert.True(File.Exists(P("h1")));
        Assert.True(File.Exists(P("h2")));
    }

    [Fact]
    public void Mv_HardLinksIntoADirectory_AreFine()
    {
        File1("r1");
        MustHardLink("r1", "r2");
        Directory.CreateDirectory(P("rd"));

        InTmp("Invoke-BashMv r1 r2 rd").AssertSuccess();

        Assert.True(File.Exists(P("rd/r1")));
        Assert.True(File.Exists(P("rd/r2")));
        Assert.False(File.Exists(P("r1")));
    }

    [SkippableFact]
    public void Mv_CaseOnlyRename_IsNotMistakenForTheSameFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "case-insensitive filesystems only");
        File1("foo");
        InTmp("Invoke-BashMv foo Foo").AssertSuccess();
        Assert.Contains("Foo", Directory.GetFiles(_tmp).Select(Path.GetFileName));
    }

    // ───────────── symlinks (skip where they cannot be created) ─────────────

    [SkippableFact]
    public void Cp_ASymlinkToTheSource_IsTheSameFile()
    {
        File1("y1");
        Skip.IfNot(FileSymlink("y2", "y1"), "cannot create a file symlink here");

        InTmp("Invoke-BashCp y1 y2").AssertFailed(1, "cp: 'y1' and 'y2' are the same file");
        InTmp("Invoke-BashCp y2 y1").AssertFailed(1, "cp: 'y2' and 'y1' are the same file");
        InTmp("Invoke-BashCp '-f' y1 y2").AssertFailed(1, "cp: 'y1' and 'y2' are the same file");
        Assert.Equal("one\n", Read("y1"));
    }

    [SkippableFact]
    public void Mv_ARealFileOntoASymlinkThatPointsAtIt_ReplacesTheSymlink()
    {
        File1("y1");
        Skip.IfNot(FileSymlink("y2", "y1"), "cannot create a file symlink here");

        InTmp("Invoke-BashMv y1 y2").AssertSuccess();

        Assert.False(File.Exists(P("y1")));
        Assert.True(File.Exists(P("y2")));
        Assert.Null(new FileInfo(P("y2")).LinkTarget);   // y2 is now the moved regular file
        Assert.Equal("one\n", Read("y2"));
    }

    [SkippableFact]
    public void Mv_ASymlinkOntoItsOwnTarget_IsTheSameFile()
    {
        File1("z1");
        Skip.IfNot(FileSymlink("z2", "z1"), "cannot create a file symlink here");

        InTmp("Invoke-BashMv z2 z1").AssertFailed(1, "mv: 'z2' and 'z1' are the same file");

        Assert.True(File.Exists(P("z1")));
        Assert.NotNull(new FileInfo(P("z2")).LinkTarget);
    }

    // ───────────── the pure helper ─────────────

    [Fact]
    public void FileIdentity_HardLinks_AreTheSameFile_InBothModes()
    {
        File1("h1");
        MustHardLink("h1", "h2");
        Assert.True(FileIdentity.SameFileFollowingLinks(P("h1"), P("h2")));
        Assert.True(FileIdentity.SameEntryNotFollowing(P("h1"), P("h2")));
    }

    [Fact]
    public void FileIdentity_UnrelatedFiles_AreNot()
    {
        File1("a");
        File1("b");
        Assert.False(FileIdentity.SameFileFollowingLinks(P("a"), P("b")));
        Assert.False(FileIdentity.SameEntryNotFollowing(P("a"), P("b")));
    }

    [Fact]
    public void FileIdentity_MissingPaths_AreNot()
    {
        File1("a");
        Assert.False(FileIdentity.SameFileFollowingLinks(P("a"), P("nosuch")));
        Assert.False(FileIdentity.SameEntryNotFollowing(P("nosuch"), P("a")));
        Assert.False(FileIdentity.SameFileFollowingLinks(P("nosuch1"), P("nosuch2")));
    }
}
