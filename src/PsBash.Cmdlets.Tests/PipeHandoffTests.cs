using System.IO.Pipes;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PsBash.Core;
using PsBash.Core.Platform;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The shell's stdin pipe handed to a process already running (<see cref="PipeHandoff"/>: a background job's own host)
/// and adopted there as its stdin (<see cref="BackgroundJobStdin.Adopt"/>). The receiver here is this same process, which
/// takes the same OS path (Windows <c>DuplicateHandle</c> into a pid, Linux a <c>/proc/&lt;pid&gt;/fd</c> open). ps-bash's
/// own surface below the shell; the behavior against bash is Differential <c>JobStdinDifferentialTests</c> and Shell.Tests
/// <c>JobStdinEndToEndTests</c> (qa-rubric Directive 1).
/// </summary>
public class PipeHandoffTests
{
    private static void SkipWhereThereIsNoHandoff()
        => Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "macOS has no pipe handoff (a known gap)");

    private static AnonymousPipeServerStream Writer()
        => new(PipeDirection.Out, OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None);

    private static string ReadToEnd(SafePipeHandle handle)
    {
        using var reader = new AnonymousPipeClientStream(PipeDirection.In, handle);
        using var text = new StreamReader(reader, Encoding.UTF8);
        return text.ReadToEnd();
    }

    [SkippableFact]
    public void Offer_ThenAccept_ReadsTheSamePipe()
    {
        SkipWhereThereIsNoHandoff();
        using var writer = Writer();
        var token = PipeHandoff.Offer(writer.ClientSafePipeHandle, Environment.ProcessId);
        Assert.NotNull(token);
        var mine = PipeHandoff.Accept(token);
        Assert.NotNull(mine);
        Assert.NotEqual(writer.ClientSafePipeHandle.DangerousGetHandle(), mine!.DangerousGetHandle());
        writer.Write("a\nb\n"u8);
        writer.Dispose();
        Assert.Equal("a\nb\n", ReadToEnd(mine));
    }

    // The input ended before the receiver opened the pipe (`echo in | { cd / && cat & wait; }`: the shell's pump closed its
    // writer at once). A FIFO open waits for a writer, so a plain open of /proc/<pid>/fd/<n> never returned.
    [SkippableFact]
    public void Accept_AfterTheWriterClosed_DoesNotWait_AndReadsWhatIsLeft()
    {
        SkipWhereThereIsNoHandoff();
        var writer = Writer();
        using var readEnd = writer.ClientSafePipeHandle; // exposed: disposing the writer leaves it open
        writer.Write("in\n"u8);
        var token = PipeHandoff.Offer(readEnd, Environment.ProcessId);
        Assert.NotNull(token);
        writer.Dispose();

        var accept = Task.Run(() => PipeHandoff.Accept(token));
        Assert.True(accept.Wait(TimeSpan.FromSeconds(30)), "Accept waited for a writer that is gone");
        using var mine = accept.Result;
        Assert.NotNull(mine);
        Assert.Equal("in\n", ReadToEnd(mine!));
    }

    [Fact]
    public void Accept_ATokenForAnotherOs_OrNoToken_IsNothing()
    {
        Assert.Null(PipeHandoff.Accept(null));
        Assert.Null(PipeHandoff.Accept(""));
        Assert.Null(PipeHandoff.Accept("x:1"));
        Assert.Null(PipeHandoff.Accept("w:abc"));
        Assert.Null(PipeHandoff.Accept("p:1"));
        Assert.Null(PipeHandoff.Accept(OperatingSystem.IsWindows() ? "p:1:2" : "w:5"));
        // A job whose pipe cannot be had reads end of input.
        Assert.False(BackgroundJobStdin.Adopt("x:1").TryDequeue(out _));
    }

    [Fact]
    public void Lease_LetsGoOnce()
    {
        int released = 0;
        using var writer = Writer();
        var lease = new PipeHandoff.Lease(writer.ClientSafePipeHandle, () => released++);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, released);
    }

    // REGRESSION (gap 1): `seq 3 | { cd / && read a & wait; read b; }` gave b=1: the job's own host read none of the
    // compound's stdin. The job adopts the shell's pipe, reads ONE line of it, and the shell gets the rest once the job is
    // done with it.
    [SkippableFact]
    public void AJobsAdoptedPipe_TakesOneLine_AndTheShellGetsTheRest()
    {
        SkipWhereThereIsNoHandoff();
        var shell = new StdinCursor();
        foreach (var line in new[] { "1", "2", "3" }) shell.Enqueue(line);
        var lease = NativeStdinBridge.HoldFor(shell);
        Assert.NotNull(lease);
        var token = lease!.OfferTo(Environment.ProcessId);
        Assert.NotNull(token);

        var job = BackgroundJobStdin.Adopt(token!);
        Assert.True(job.TryDequeue(out var a));
        Assert.Equal("1", BashRuntime.GetBashText(a));
        // A program the job starts gets the adopted pipe itself, not a pipe of its own pumped from it.
        var program = NativeStdinBridge.ForChild(job);
        var second = NativeStdinBridge.ForChild(job);
        Assert.NotNull(program);
        Assert.NotNull(second);
        Assert.Same(program!.ChildStdin, second!.ChildStdin);
        second.Exit();
        program.Exit();
        // The job reads on after its programs are gone: the adopted pipe never tears down.
        Assert.True(job.TryDequeue(out var b));
        Assert.Equal("2", BashRuntime.GetBashText(b));
        job.Close();

        lease.Dispose();
        var rest = new List<string>();
        while (shell.TryDequeue(out var r)) rest.Add(BashRuntime.GetBashText(r));
        Assert.Equal(["3"], rest);
    }
}
