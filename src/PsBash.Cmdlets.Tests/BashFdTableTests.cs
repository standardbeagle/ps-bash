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

    // `{ exec >log; echo e >&2; } 2>&1`: fd 2 is copied from fd 1 BEFORE the body runs (dup2), so the exec moves fd 1
    // only and e still goes where fd 1 was. fd 2 followed the exec into log (the merge read fd 1 at write time).
    [Fact]
    public void RegisterMerge_CopiesFd2BeforeTheBody_AnExecInsideMovesFd1Only()
    {
        var passed = new System.Text.StringBuilder();
        var (o, e) = BashFdTable.RegisterMerge(s => passed.Append(s));
        Assert.Null(BashFdTable.Open(1, F("log"), ">"));
        Assert.Equal(0, BashFdTable.Write(2, "err\n"));
        Assert.Equal(0, BashFdTable.Write(1, "out\n"));
        Assert.True(BashFdTable.Moved(o, out var to) && to is not null);
        Assert.False(BashFdTable.Moved(e, out _));
        BashFdTable.Unregister(e);
        BashFdTable.Unregister(o);
        // bash: the exec inside the group stays in force after it (fd 1 is still log).
        Assert.Equal(0, BashFdTable.Write(1, "after\n"));
        Assert.Equal(2, BashFdTable.Write(2, "x\n"));
        BashFdTable.CloseAll();
        Assert.Equal("err\n", passed.ToString());
        Assert.Equal("out\nafter\n", File.ReadAllText(F("log")));
    }

    // The same through the stages the emitter writes for `{ exec 3>&1; exec >log; echo out; echo err >&2; echo t >&3; } 2>&1`
    // (PsBuild.MergeStderrIntoStdout, StdoutToStderrStage): the command's stderr is the stage's output, and so is what
    // it writes to a copy of the merge's fd 1 made before the exec; its stdout is the file the exec opened.
    [Fact]
    public void MergeStage_ExecInsideTheBody_StderrStillPassesOn()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"& {{ Invoke-BashExecFd 3 '>&' 1; Invoke-BashExecFd 1 '>' '{Q(F("log"))}'; Invoke-BashEcho out; "
            + "Invoke-BashEcho err | Write-BashHostStderr; Invoke-BashEcho t | Write-BashFd 3; Invoke-BashExecFd 3 '>&' '-' } 2>&1 "
            + "| Invoke-BashRedirect -MergeErrors; Invoke-BashExecFd 1 '>&' '-'").AssertSuccess();
        Assert.Equal(["err", "t"], r.Lines);
        Assert.Equal("out\n", File.ReadAllText(F("log")));
    }

    // What reaches the host while a stage is registered came out of that stage (or a pipe stage after it), which routed
    // it already: it goes where the descriptor was below the first stage, not where an exec inside moved it. Before, it
    // went to Low[1], so `cmd 2>&1 >f` put the merged stderr into f and `{ exec >g; ...; } 2>&1` re-routed into g.
    [Fact]
    public void Divert_WhileAStageIsRegistered_UsesTheDescriptorBelowIt()
    {
        var passed = new System.Text.StringBuilder();
        var (o, e) = BashFdTable.RegisterMerge(s => passed.Append(s));
        Assert.Null(BashFdTable.Open(1, F("g"), ">"));
        // The host's fd 1 is the original terminal below the merge: not diverted anywhere.
        Assert.False(PsBash.Core.Stdio.ShellStdio.TryDivert(1, "host\n"));
        BashFdTable.Unregister(e);
        BashFdTable.Unregister(o);
        BashFdTable.CloseAll();
        Assert.Equal("", File.ReadAllText(F("g")));
    }

    // `fx() { exec >log; }; fx; sed p log`: a reader of the shell's own output file reads it as it was when opened. Each
    // line written as it was made landed after what the reader had left, and the read never ended.
    [Fact]
    public void OpenRead_TheShellsStdoutFile_EndsAtItsLengthWhenOpened()
    {
        File.WriteAllText(F("log"), "a\n");
        Assert.Null(BashFdTable.Open(1, F("log"), ">>"));
        using (var s = BashFileSystem.OpenRead(F("log")))
        {
            using (var w = new FileStream(F("log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                w.Write("b\n"u8);
            Assert.Equal(2, s.Length);
            using var reader = new StreamReader(s);
            Assert.Equal("a\n", reader.ReadToEnd());
        }
        // Another file is read to its end.
        File.WriteAllText(F("other"), "x\n");
        using (var s = BashFileSystem.OpenRead(F("other")))
        {
            using (var w = new FileStream(F("other"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                w.Write("y\n"u8);
            using var reader = new StreamReader(s);
            Assert.Equal("x\ny\n", reader.ReadToEnd());
        }
    }

    // GNU grep refuses an input that is also its output (status 2), unless it writes nothing per line (-q, -c, -l, -L)
    // or at most one match (-m 1).
    [Fact]
    public void Grep_OfTheFileTheShellsStdoutNames_IsRefusedUnlessQuiet()
    {
        File.WriteAllText(F("log"), "a\n");
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"Invoke-BashExecFd 1 '>>' '{Q(F("log"))}'; Invoke-BashGrep a '{Q(F("log"))}'");
        r.AssertFailed(2, "input file is also the output");
        Assert.Empty(r.Lines);

        var q = CmdResult.Run(_fixture.AcquireFresh(),
            $"Invoke-BashExecFd 1 '>>' '{Q(F("log"))}'; Invoke-BashGrep -q a '{Q(F("log"))}'");
        q.AssertSuccess();
    }

    // `cat < log` after `exec >log`: the stdin cat reads is the file (RegularStdin carries its path). GNU: "cat: -: input
    // file is output file", status 1.
    [Fact]
    public void Cat_OfAStdinThatIsTheShellsStdoutFile_IsRefused()
    {
        File.WriteAllText(F("log"), "a\n");
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"Invoke-BashExecFd 1 '>>' '{Q(F("log"))}'; Invoke-BashCat '{Q(F("log"))}' -RawStdinBytes | Invoke-BashCat");
        r.AssertFailed(1, "cat: -: input file is output file");
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
