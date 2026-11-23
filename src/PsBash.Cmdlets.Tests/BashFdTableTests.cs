using PsBash.Core;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>xUnit collection for classes that use the descriptor table of code with no runspace (the test thread's).</summary>
[CollectionDefinition(Name)]
public sealed class FdTableCollection
{
    public const string Name = "fd-table";
}

/// <summary>
/// The shell's descriptor table (<see cref="BashFdTable"/>, docs/specs/shell-fds-and-bash-source.md "The descriptor
/// table"). Hand-written: the table is ps-bash's model of bash's per-process descriptors (bash has no API for it); the
/// bash-visible behavior is <c>FdTableDifferentialTests</c>.
/// </summary>
[Collection(FdTableCollection.Name)]
public class BashFdTableTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public BashFdTableTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), $"psb-fdt-{Guid.NewGuid():N}".Substring(0, 20));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        BashFdTable.CloseAll();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string F(string name) => Path.Combine(_dir, name);

    private static string Q(string s) => s.Replace("'", "''");

    // `exec 3>f1; exec 4>&3; exec 3>f2`: fd 4 is a COPY of what 3 named then (dup2), not a name for "fd 3".
    [Fact]
    public void Dup_CopiesTheEntry_ALaterChangeOfTheSourceDoesNotMoveIt()
    {
        Assert.Null(BashFdTable.Open(3, F("f1"), ">"));
        Assert.Null(BashFdTable.Dup(4, 3));
        Assert.Null(BashFdTable.Open(3, F("f2"), ">"));
        Assert.Equal(0, BashFdTable.Write(4, "a\n"));
        Assert.Equal(0, BashFdTable.Write(3, "b\n"));
        BashFdTable.CloseAll();
        Assert.Equal("a\n", File.ReadAllText(F("f1")));
        Assert.Equal("b\n", File.ReadAllText(F("f2")));
    }

    // `cmd 5>f2` after `exec 5>f1`: the command's own fd 5 is put back afterwards (it was closed, losing f1).
    [Fact]
    public void SaveFds_RestoreFds_PutsBackWhatTheDescriptorNamedBefore()
    {
        Assert.Null(BashFdTable.Open(5, F("f1"), ">"));
        var saved = BashFdTable.SaveFds([5, 6]);
        Assert.Null(BashFdTable.Open(5, F("f2"), ">"));
        Assert.Null(BashFdTable.Open(6, F("f3"), ">"));
        BashFdTable.RestoreFds(saved);
        Assert.Equal(0, BashFdTable.Write(5, "c\n"));
        Assert.False(BashFdTable.IsOpen(6));
        BashFdTable.CloseAll();
        Assert.Equal("c\n", File.ReadAllText(F("f1")));
        Assert.Equal("", File.ReadAllText(F("f2")));
    }

    // A closed fd 1 fails a write; an fd 1 / 2 with no entry is the shell's original stream (the caller writes it).
    [Fact]
    public void Write_ClosedStdoutThrows_AnOriginalOneIsTheCallers()
    {
        Assert.Equal(1, BashFdTable.Write(1, "x\n"));
        Assert.Equal(2, BashFdTable.Write(2, "x\n"));
        Assert.True(BashFdTable.Close(1));
        Assert.Throws<InvalidOperationException>(() => BashFdTable.Write(1, "x\n"));
        Assert.Throws<InvalidOperationException>(() => BashFdTable.Write(7, "x\n"));
    }

    // `( exec >&-; echo x )`: a write that reaches the shell's closed fd 1 is counted on the writing thread, so the
    // builtin that wrote it fails (bash: `echo: write error: Bad file descriptor`, status 1). It was dropped silently.
    [Fact]
    public void Divert_AWriteToAClosedShellStdout_IsCountedForTheWriter()
    {
        Assert.True(BashFdTable.Close(1));
        int before = BashFdTable.ClosedStdoutWrites;
        Assert.True(PsBash.Core.Stdio.ShellStdio.TryDivert(1, ""));
        Assert.Equal(before, BashFdTable.ClosedStdoutWrites);
        Assert.True(PsBash.Core.Stdio.ShellStdio.TryDivert(1, "x\n"));
        Assert.Equal(before + 1, BashFdTable.ClosedStdoutWrites);
        Assert.True(PsBash.Core.Stdio.ShellStdio.TryDivert(2, "e\n") is false);
        BashFdTable.CloseAll();
        Assert.False(PsBash.Core.Stdio.ShellStdio.TryDivert(1, "x\n"));
    }

    // `fx() { exec >log; }; fx; cat log`: cat's output is the file it reads. It read what it wrote and never ended (the
    // exec used to end with the function, so cat wrote the terminal); GNU cat refuses it, status 1.
    [Fact]
    public void Cat_OfTheFileTheShellsStdoutNames_IsRefused()
    {
        File.WriteAllText(F("log"), "a\n");
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"Invoke-BashExecFd 1 '>>' '{Q(F("log"))}'; Invoke-BashCat '{Q(F("log"))}'");
        r.AssertFailed(1, "input file is output file");
        Assert.Empty(r.Lines);
    }

    // One table per shell: a descriptor this (runspace-less) code opened is not open in a runspace's shell. The table was
    // process-wide, so an in-process job's or a parallel runspace's redirection stage took over the other's fd 1.
    [Fact]
    public void Table_IsPerShell_ARunspaceDoesNotSeeAnotherShellsDescriptor()
    {
        Assert.Null(BashFdTable.Open(9, F("mine"), ">"));
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashEcho x | Write-BashFd 9");
        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("9: Bad file descriptor", r.Stderr);
        BashFdTable.CloseAll();
        Assert.Equal("", File.ReadAllText(F("mine")));
    }

    // A redirection stage is fd 1 of the command under it: `{ exec 3>&1; } >f; echo x >&3` writes x to f.
    [Fact]
    public void RedirectStage_IsFd1OfItsCommand_ACopyMadeInsideOutlivesIt()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"& {{ Invoke-BashExecFd 3 '>&' 1 }} | Invoke-BashRedirect -Path '{Q(F("f"))}'; "
            + "Invoke-BashEcho x | Write-BashFd 3; Invoke-BashExecFd 3 '>&' '-'").AssertSuccess();
        Assert.Empty(r.Lines);
        Assert.Equal("x\n", File.ReadAllText(F("f")));
    }

    // `x=$(exec >f; echo a)`: an exec inside a capture moves what the capture would have received.
    [Fact]
    public void Capture_IsFd1OfItsCommand_AnExecInsideMovesTheOutput()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"$v = & {{ Invoke-BashExecFd 1 '>' '{Q(F("g"))}'; Invoke-BashEcho a }} | ConvertTo-BashCapture; \"[$v]\"").AssertSuccess();
        Assert.Equal("[]", r.Lines[^1]);
        Assert.Equal("a\n", File.ReadAllText(F("g")));
    }
}
