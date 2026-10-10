using System.Diagnostics;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// mv / cp against files another process has open (<see cref="FileTransfer"/>). Linux rename(2) and cp
/// never care who has a file open; Windows does. Each case is what GNU does on Linux, unless Windows makes
/// it impossible — then the error must say so at once and name the process. The test process holding a
/// handle IS another process: the cmdlets run in the fixture's pwsh. Windows-only by nature (no sharing
/// modes elsewhere).
/// </summary>
public class FileTransferTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;
    private readonly List<IDisposable> _held = new();
    private Process? _program;

    public FileTransferTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-xfer-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        foreach (var h in _held) h.Dispose();
        if (_program is { HasExited: false }) { _program.Kill(); _program.WaitForExit(5000); }
        _program?.Dispose();
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private string P(string rel) => Path.GetFullPath(Path.Combine(_tmp, rel));

    private void Write(string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), content);
    }

    private string Read(string rel) => File.ReadAllText(P(rel));

    private void Hold(string rel, FileShare share) =>
        _held.Add(new FileStream(P(rel), FileMode.Open, FileAccess.Read, share));

    /// <summary>Runs a copy of ping.exe from <paramref name="rel"/> for a minute: a program whose image is mapped.</summary>
    private void RunProgram(string rel)
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), P(rel));
        _program = Process.Start(new ProcessStartInfo(P(rel), "-n 60 127.0.0.1")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        _program.StandardOutput.ReadLine();   // started: the image is mapped
    }

    private static string Pid => $"pid {Environment.ProcessId}";

    // ───────────── replace what Windows will not overwrite in place ─────────────

    [SkippableFact]
    public void Mv_OntoARunningProgram_ReplacesIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing");
        RunProgram("app.exe");
        Write("new.exe", "new build");

        InTmp("Invoke-BashMv new.exe app.exe").AssertSuccess();

        Assert.Equal("new build", Read("app.exe"));
        Assert.False(File.Exists(P("new.exe")));
        // The old image cannot be deleted while it runs: it may only remain as a HIDDEN leftover.
        foreach (var f in Directory.GetFiles(_tmp).Where(f => !f.EndsWith("app.exe", StringComparison.Ordinal)))
            Assert.True((File.GetAttributes(f) & FileAttributes.Hidden) != 0, $"visible leftover {f}");
    }

    [SkippableFact]
    public void Mv_LeftoverOfAReplacedProgram_IsSweptOnceItExits()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing");
        RunProgram("app.exe");
        Write("v2.exe", "v2");
        InTmp("Invoke-BashMv v2.exe app.exe").AssertSuccess();
        _program!.Kill();
        _program.WaitForExit(5000);

        Write("v3.exe", "v3");
        InTmp("Invoke-BashMv v3.exe app.exe").AssertSuccess();

        Assert.Equal(new[] { "app.exe" }, Directory.GetFiles(_tmp).Select(Path.GetFileName));
        Assert.Equal("v3", Read("app.exe"));
    }

    [SkippableFact]
    public void Mv_OntoAReadOnlyFile_ReplacesIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows read-only attribute");
        Write("a", "new"); Write("b", "old");
        File.SetAttributes(P("b"), FileAttributes.ReadOnly);

        InTmp("Invoke-BashMv a b").AssertSuccess();

        Assert.Equal("new", Read("b"));
        Assert.False(File.Exists(P("a")));
    }

    [SkippableFact]
    public void Cp_OntoAFileOpenWithoutWriteSharing_ReplacesIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing");
        Write("a", "new"); Write("b", "old");
        Hold("b", FileShare.Read | FileShare.Delete);   // a reader that refuses writers, not renames

        InTmp("Invoke-BashCp a b").AssertSuccess();

        Assert.Equal("new", Read("b"));
    }

    [SkippableFact]
    public void Cp_OntoAReadOnlyFile_WithoutForce_IsStillPermissionDenied()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows read-only attribute");
        Write("a", "new"); Write("b", "old");
        File.SetAttributes(P("b"), FileAttributes.ReadOnly);

        InTmp("Invoke-BashCp a b").AssertFailed(1, "cp: cannot copy 'a' to 'b': Permission denied");
        File.SetAttributes(P("b"), FileAttributes.Normal);
        Assert.Equal("old", Read("b"));
    }

    // ───────────── hard limits: fail at once, name the process, change nothing ─────────────

    [SkippableFact]
    public void Mv_SourceHeldOpen_FailsNamingTheProcess_AndChangesNothing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing");
        Write("a", "A");
        Hold("a", FileShare.Read);   // a writer-style handle: no delete sharing, so no rename

        var r = InTmp("Invoke-BashMv a b");

        r.AssertFailed(1, "mv: cannot move 'a' to 'b': Device or resource busy (open in ");
        Assert.Contains(Pid, r.Stderr);
        Assert.Equal("A", Read("a"));
        Assert.False(File.Exists(P("b")));
    }

    [SkippableFact]
    public void Mv_DestinationHeldOpen_FailsNamingIt_AndKeepsBoth()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing");
        Write("a", "new"); Write("b", "old");
        Hold("b", FileShare.Read);

        var r = InTmp("Invoke-BashMv a b");

        r.AssertFailed(1, "Device or resource busy");
        Assert.Contains(Pid, r.Stderr);
        Assert.Equal("new", Read("a"));
        Assert.Equal("old", Read("b"));
        Assert.Equal(new[] { "a", "b" }, Directory.GetFiles(_tmp).Select(Path.GetFileName).Order());
    }

    [SkippableFact]
    public void Mv_DirectoryWithAHeldFile_FailsNamingThatFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing");
        Write("d/sub/inner.txt", "x");
        Hold("d/sub/inner.txt", FileShare.ReadWrite);

        var r = InTmp("Invoke-BashMv d e");

        r.AssertFailed(1, "mv: cannot move 'd' to 'e': Device or resource busy ('d/sub/inner.txt' open in ");
        Assert.Contains(Pid, r.Stderr);
        Assert.True(Directory.Exists(P("d/sub")));
        Assert.False(Directory.Exists(P("e")));
    }

    [SkippableFact]
    public void Mv_DirectoryOntoEmptyDir_FailingMove_PutsTheEmptyDirBack()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing");
        Write("d/inner.txt", "x");
        Directory.CreateDirectory(P("e"));
        Hold("d/inner.txt", FileShare.ReadWrite);

        InTmp("Invoke-BashMv '-T' d e").AssertFailed(1, "Device or resource busy");

        Assert.True(Directory.Exists(P("e")));
        Assert.Equal(new[] { "d", "e" }, Directory.GetFileSystemEntries(_tmp).Select(Path.GetFileName).Order());
    }

    [SkippableFact]
    public void Cp_OntoAFileHeldWithoutDeleteSharing_FailsNamingTheProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing");
        Write("a", "new"); Write("b", "old");
        Hold("b", FileShare.Read);   // refuses writers AND renames: nothing can replace it

        var r = InTmp("Invoke-BashCp a b");

        r.AssertFailed(1, "cp: cannot copy 'a' to 'b': Device or resource busy");
        Assert.Contains(Pid, r.Stderr);
        Assert.Equal("old", Read("b"));
    }

    // ───────────── a directory across volumes (one volume here: the seam directly) ─────────────

    [SkippableFact]
    public void AcrossVolumes_CopiesTheTreeWithTimes_ThenRemovesTheSource()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing");
        Write("src/a.txt", "A");
        Write("src/sub/b.txt", "B");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(P("src/sub/b.txt"), stamp);

        FileTransfer.MoveDirectoryAcrossVolumes(P("src"), P("dst"));

        Assert.False(Directory.Exists(P("src")));
        Assert.Equal("A", Read("dst/a.txt"));
        Assert.Equal("B", Read("dst/sub/b.txt"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(P("dst/sub/b.txt")));
    }

    [SkippableFact]
    public void AcrossVolumes_AHeldFile_IsReportedBeforeAnythingIsCopied()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file sharing");
        Write("src/a.txt", "A");
        Write("src/held.txt", "H");
        Hold("src/held.txt", FileShare.ReadWrite);

        var ex = Assert.Throws<FileTransfer.FileBusyException>(
            () => FileTransfer.MoveDirectoryAcrossVolumes(P("src"), P("dst")));

        Assert.Equal(P("src/held.txt"), ex.Path);
        Assert.False(Directory.Exists(P("dst")));
        Assert.Equal("Device or resource busy ('src/held.txt' open in testhost.exe, " + Pid + ")",
            FileTransfer.Reason(ex, P("src"), "src").Replace(Process.GetCurrentProcess().ProcessName + ".exe", "testhost.exe"));
    }

    // ───────────── pure ─────────────

    [Theory]
    [InlineData("MsMpEng.exe", true)]
    [InlineData("searchprotocolhost", true)]
    [InlineData("beagle-term.exe", false)]
    [InlineData("pwsh.exe", false)]
    public void IsTransient_OnlyScannersAreWaitedOut(string name, bool expected)
        => Assert.Equal(expected, FileTransfer.IsTransient(name));
}
