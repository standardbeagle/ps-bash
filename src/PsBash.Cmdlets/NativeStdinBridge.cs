using System.IO.Pipes;
using System.Management.Automation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PsBash.Core;
using PsBash.Core.Platform;

namespace PsBash.Cmdlets;

/// <summary>
/// Gives ONE native program the shell's shared stdin (<c>$global:__BashStdIn</c>: the launcher's forwarded stdin, or a
/// compound command's) as its REAL stdin, the way bash hands a child fd 0.
/// <para>
/// <b>Why not <c>feed | prog</c>.</b> PowerShell starts a native program as soon as it is invoked, but a
/// pipeline ends only when its UPSTREAM does, and the upstream (the feed) cannot know the program exited. Under
/// a stdin that never ends — an agent shell tool keeps the launcher's stdin open and silent — every native
/// command therefore hung, even <c>git --version</c>, which never reads stdin at all.
/// </para>
/// <para>
/// <b>How.</b> A <see cref="StdinCursor"/>'s input goes through the cursor's ONE pipe (<see cref="CursorPipe"/>), which
/// the program holds while it runs: the shell and every program reading that stdin (a background job's too) read the
/// same bytes from one position, and what nobody read goes back in front of the cursor when the last of them is done
/// (docs/specs/stdio-messaging.md §4.7). <see cref="Enter"/> makes the pipe's read end the PROCESS stdin (Windows:
/// <c>SetStdHandle</c>; Unix: <c>dup2</c> onto fd 0): PowerShell starts a native program that has no pipeline input with
/// the process stdin. <see cref="ForChild"/> hands the read end to a program ps-bash starts itself. A plain
/// <c>Queue&lt;object&gt;</c> (an eager compound stdin, never shared with a job) gets a pipe of its own per program, with
/// the same put-back.
/// </para>
/// <para>
/// The process stdin is process-global: only the foreground swaps it (the host runs one command at a time, its
/// execution lease), nesting is refused (<see cref="Enter"/> returns null while a bridge is active), and code with its
/// own scalars (a background job, <c>env</c>'s command) never swaps it.
/// </para>
/// </summary>
public sealed class NativeStdinBridge
{
    private const int PipeBufferSize = 64 * 1024;
    private static readonly object Gate = new();
    private static NativeStdinBridge? _active;

    private readonly StdinSwap _swap;
    // A cursor's input: this program's hold on the cursor's pipe.
    private readonly CursorPipe? _pipe;
    // A plain queue's input: a pipe of this program's own, its pump and the queue it puts back into.
    private readonly Queue<object>? _queue;
    private readonly AnonymousPipeServerStream? _writer;
    private readonly CancellationTokenSource? _cancel;
    private readonly Thread? _pump;
    private bool _exited;

    private NativeStdinBridge(CursorPipe pipe, StdinSwap swap)
    {
        _pipe = pipe;
        _swap = swap;
    }

    private NativeStdinBridge(Queue<object> queue, AnonymousPipeServerStream writer, StdinSwap swap)
    {
        _queue = queue;
        _writer = writer;
        _swap = swap;
        _cancel = new CancellationTokenSource();
        _pump = new Thread(Pump) { IsBackground = true, Name = "ps-bash native stdin pump" };
    }

    /// <summary>
    /// Make <paramref name="stdin"/> (a <see cref="StdinCursor"/> or <c>Queue&lt;object&gt;</c>) the PROCESS stdin for the
    /// foreground program about to run. Null — and the program runs with the stdin it would have had anyway — when another
    /// bridge is active, the stdin is unsupported, or the OS refuses the pipe.
    /// <para>
    /// Only the foreground swaps the PROCESS stdin. Code with its own scalars (<see cref="ScalarOverlay.Current"/>: an
    /// in-process background job, <c>env</c>'s command) runs beside the foreground: a swap from there gave the
    /// foreground's next program the job's stdin (<c>/usr/bin/sleep 3 &amp; sleep 1; cat</c> read the job's empty pipe,
    /// not the launcher's input), and refused the foreground its own bridge while it lasted. Such a program gets the
    /// pipe as its own fd 0 (<see cref="ForChild"/>, which <c>Invoke-BashNative</c> uses).
    /// </para>
    /// </summary>
    public static NativeStdinBridge? Enter(object? stdin)
    {
        if (!Bridgeable(stdin) || ScalarOverlay.Current is not null) return null;
        lock (Gate)
        {
            if (_active is not null) return null;
            var bridge = stdin is StdinCursor cursor
                ? OnCursorPipe(cursor, StdinSwap.Install)
                : OwnPipe((Queue<object>)stdin!, HandleInheritability.Inheritable, StdinSwap.Install);
            _active = bridge;
            return bridge;
        }
    }

    /// <summary>
    /// The stdin of ONE program ps-bash starts itself: <see cref="ChildStdin"/> becomes its fd 0 and the process stdin is
    /// not touched. <see cref="Exit"/> after the child exited. Null when the stdin is unsupported, the OS refuses the
    /// pipe, or it is a queue the foreground's <see cref="Enter"/> already made the process stdin.
    /// </summary>
    public static NativeStdinBridge? ForChild(object? stdin)
    {
        if (!Bridgeable(stdin)) return null;
        if (stdin is StdinCursor cursor) return OnCursorPipe(cursor, static _ => new NoSwap());
        var queue = (Queue<object>)stdin!;
        lock (Gate)
            if (_active is { } active && ReferenceEquals(active._queue, queue)) return null;
        return OwnPipe(queue, OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None,
            static _ => new NoSwap());
    }

    /// <summary>
    /// THE stdin of a program ps-bash starts itself with no input piped to it (<c>Invoke-BashNative</c>; <c>env</c>,
    /// <c>command</c>, <c>time</c>, <c>find -exec</c> through <c>RunMerged</c>): the shell's stdin (<see cref="ForChild"/>);
    /// without one that can be bridged, in a background job end of input (bash: <c>/dev/null</c>; the process stdin is
    /// the foreground's), else the process stdin (a terminal, or the foreground's own bridge of a queue).
    /// <see cref="Exit"/> the bridge after the child exited. Guard: Core.Tests <c>StdinOwnershipGuardTests</c>.
    /// </summary>
    internal static ChildStdioMode ProgramStdin(PSCmdlet cmdlet, bool mergeStderr, out NativeStdinBridge? bridge)
    {
        bridge = ForChild(SharedStdin.Get(cmdlet.SessionState));
        bool inherit = bridge is null && !BashShellState.InBackgroundJob(cmdlet.SessionState);
        return new ChildStdioMode(MergeStderr: mergeStderr, InheritStdin: inherit, Stdin: bridge?.ChildStdin);
    }

    /// <summary>
    /// <paramref name="stdin"/> for a PROCESS that is not a child ps-bash waits on in place (a job's own host, a
    /// <c>nohup</c> child): the read end of its pipe, held (<see cref="ForChild"/>) until the lease is disposed once that
    /// process is done. The process gets the handle itself (a spawn's fd 0, or <see cref="PipeHandoff"/> into one already
    /// running). Null when <paramref name="stdin"/> cannot be bridged.
    /// </summary>
    internal static PipeHandoff.Lease? HoldFor(object? stdin)
        => ForChild(stdin) is { } bridge ? new PipeHandoff.Lease(bridge.ChildStdin, bridge.Exit) : null;

    /// <summary>The read end a <see cref="ForChild"/> bridge's child gets as its stdin.</summary>
    public SafePipeHandle ChildStdin => _pipe?.ReadEnd ?? _writer!.ClientSafePipeHandle;

    // An enumerator-backed cursor blocks in MoveNext whatever the token says, so no pump on it could ever be stopped.
    private static bool Bridgeable(object? stdin) => stdin is Queue<object> or StdinCursor { CanCancelWait: true };

    private static NativeStdinBridge? OnCursorPipe(StdinCursor cursor, Func<SafePipeHandle, StdinSwap> swap)
    {
        if (CursorPipe.Hold(cursor) is not { } pipe) return null;
        try { return new NativeStdinBridge(pipe, swap(pipe.ReadEnd)); }
        catch (Exception)
        {
            pipe.Release();
            return null;
        }
    }

    private static NativeStdinBridge? OwnPipe(Queue<object> queue, HandleInheritability readEnd, Func<SafePipeHandle, StdinSwap> swap)
    {
        AnonymousPipeServerStream? writer = null;
        try
        {
            // The server end is the WRITE end and is never inheritable.
            writer = new AnonymousPipeServerStream(PipeDirection.Out, readEnd, PipeBufferSize);
            var bridge = new NativeStdinBridge(queue, writer, swap(writer.ClientSafePipeHandle));
            bridge._pump!.Start();
            return bridge;
        }
        catch (Exception)
        {
            writer?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Undo <see cref="Enter"/> / <see cref="ForChild"/> once the program has exited: restore the process stdin and
    /// release the pipe (what nobody read goes back in front of the shared stdin). Idempotent.
    /// </summary>
    public void Exit()
    {
        lock (Gate)
        {
            if (_exited) return;
            _exited = true;
            if (ReferenceEquals(_active, this)) _active = null;
        }

        _swap.Restore();
        if (_pipe is not null)
        {
            _pipe.Release();
            return;
        }

        _cancel!.Cancel();
        // Drain the pipe until EOF while the pump winds down: the pump may be blocked writing into a full pipe
        // (the program stopped reading), and only reading unblocks it. EOF arrives when the pump closes the
        // write end (its finally) — every other holder of the pipe only has the read end.
        var unread = new MemoryStream();
        using (var reader = new AnonymousPipeClientStream(PipeDirection.In, _writer!.ClientSafePipeHandle))
        {
            var buffer = new byte[16 * 1024];
            int n;
            try
            {
                while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                    unread.Write(buffer, 0, n);
            }
            catch (IOException)
            {
                // Broken pipe: nothing more to read.
            }
        }
        _pump!.Join();
        _writer.Dispose();
        _cancel.Dispose();

        if (unread.Length == 0) return;
        var records = Records(RawBytes.GetString(unread.GetBuffer().AsSpan(0, (int)unread.Length)));
        var rest = _queue!.ToArray();
        _queue.Clear();
        foreach (var r in records) _queue.Enqueue(r);
        foreach (var r in rest) _queue.Enqueue(r);
    }

    private void Pump()
    {
        var ct = _cancel!.Token;
        try
        {
            while (!ct.IsCancellationRequested && _queue!.TryDequeue(out var record))
            {
                var bytes = RecordBytes(record);
                if (bytes.Length > 0)
                {
                    _writer!.Write(bytes, 0, bytes.Length);
                    _writer.Flush();
                }
            }
        }
        catch (IOException)
        {
            // The read end is gone.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            // End of input (or Exit): close the write end so a reader sees EOF and Exit's drain completes.
            try { _writer!.Dispose(); } catch { /* already closed */ }
        }
    }

    /// <summary>A record's exact bytes (<see cref="BashRuntime.RecordBytes"/>) as the child's stdin bytes.</summary>
    internal static byte[] RecordBytes(object? record) => RawBytes.GetBytes(BashRuntime.RecordBytes(record));

    /// <summary>Stdin text as records again: lines; an unterminated last piece stays unterminated.</summary>
    internal static List<object> Records(string text)
    {
        var records = new List<object>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            records.Add(text.Substring(start, i - start));
            start = i + 1;
        }
        if (start < text.Length) records.Add(BashRuntime.TextRecord(text.Substring(start), unterminated: true));
        return records;
    }

    /// <summary>The process-stdin swap: the pipe's read end becomes stdin until <see cref="Restore"/>.</summary>
    private abstract class StdinSwap
    {
        internal static StdinSwap Install(SafePipeHandle readEnd) =>
            OperatingSystem.IsWindows() ? new WindowsSwap(readEnd) : new UnixSwap(readEnd);

        internal abstract void Restore();
    }

    /// <summary><see cref="ForChild"/>: the child gets the read end itself; the process stdin stays.</summary>
    private sealed class NoSwap : StdinSwap
    {
        internal override void Restore() { }
    }

    private sealed class WindowsSwap : StdinSwap
    {
        private const int StdInputHandle = -10;
        private readonly IntPtr _saved;
        private bool _restored;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr handle);

        internal WindowsSwap(SafePipeHandle readEnd)
        {
            _saved = GetStdHandle(StdInputHandle);
            if (!SetStdHandle(StdInputHandle, readEnd.DangerousGetHandle()))
                throw new IOException("SetStdHandle failed: " + Marshal.GetLastWin32Error());
        }

        internal override void Restore()
        {
            if (_restored) return;
            _restored = true;
            SetStdHandle(StdInputHandle, _saved);
        }
    }

    private sealed class UnixSwap : StdinSwap
    {
        private readonly int _saved;
        private bool _restored;

        [DllImport("libc", SetLastError = true)]
        private static extern int dup(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int dup2(int fd, int fd2);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        internal UnixSwap(SafePipeHandle readEnd)
        {
            _saved = dup(0);
            if (_saved < 0) throw new IOException("dup(0) failed: " + Marshal.GetLastWin32Error());
            // dup2 clears close-on-exec on fd 0, so a child inherits the read end as its stdin.
            if (dup2((int)readEnd.DangerousGetHandle(), 0) < 0)
            {
                close(_saved);
                throw new IOException("dup2 failed: " + Marshal.GetLastWin32Error());
            }
        }

        internal override void Restore()
        {
            if (_restored) return;
            _restored = true;
            dup2(_saved, 0);
            close(_saved);
        }
    }
}

/// <summary>
/// <c>Enter-BashNativeStdin NAME</c>: when NAME resolves to an application and the shell has a shared stdin, give
/// the native program about to run that stdin as its process stdin (<see cref="NativeStdinBridge"/>); returns the
/// bridge for <c>Exit-BashNativeStdin</c>, or nothing. Emitted by the transpiler (<c>PsBuild.NativeStdinScope</c>).
/// </summary>
[Cmdlet(VerbsCommon.Enter, "BashNativeStdin")]
[OutputType(typeof(NativeStdinBridge))]
public sealed class EnterBashNativeStdinCommand : BashCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Name { get; set; } = "";

    protected override void EndProcessing()
    {
        var stdin = SharedStdin.Get(SessionState);
        if (stdin is null) return;

        // Only a real program: a function or alias of the same name (which PowerShell would run instead) reads
        // its stdin through the pipeline, and must not have it pumped away underneath it.
        if (!ResolvesToApplication(this, Name)) return;

        var bridge = NativeStdinBridge.Enter(stdin);
        if (bridge is not null) WriteObject(bridge);
    }

    /// <summary>
    /// True when <paramref name="name"/> runs a real program (not a function, alias, cmdlet or script), also while a
    /// lookup hook stands in for it (<see cref="ProgramLookup"/>).
    /// </summary>
    internal static bool ResolvesToApplication(PSCmdlet cmdlet, string name)
    {
        // A question, not a use: the host's hash table must not count it (the command itself runs right after).
        using var probe = PsBash.Core.CommandHashTable.Probe();
        return ProgramLookup.Find(cmdlet, name) is not null;
    }
}

/// <summary><c>Exit-BashNativeStdin $bridge</c>: undo <c>Enter-BashNativeStdin</c> (a null bridge is a no-op).</summary>
[Cmdlet(VerbsCommon.Exit, "BashNativeStdin")]
public sealed class ExitBashNativeStdinCommand : BashCmdlet
{
    [Parameter(Position = 0)]
    [AllowNull]
    public object? Bridge { get; set; }

    protected override void EndProcessing()
    {
        if (Bridge is null) return;
        var bridge = PSObject.AsPSObject(Bridge).BaseObject as NativeStdinBridge;
        bridge?.Exit();
    }
}
