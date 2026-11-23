using System.IO.Pipes;
using System.Management.Automation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// Runs ONE native program with the shell's shared stdin (<c>$global:__BashStdIn</c>: the launcher's forwarded
/// stdin, or a compound command's) as its REAL process stdin, the way bash hands a child fd 0.
/// <para>
/// <b>Why not <c>feed | prog</c>.</b> PowerShell starts a native program as soon as it is invoked, but a
/// pipeline ends only when its UPSTREAM does, and the upstream (the feed) cannot know the program exited. Under
/// a stdin that never ends — an agent shell tool keeps the launcher's stdin open and silent — every native
/// command therefore hung, even <c>git --version</c>, which never reads stdin at all.
/// </para>
/// <para>
/// <b>How.</b> <see cref="Enter"/> creates an anonymous OS pipe, makes its read end the PROCESS stdin
/// (Windows: <c>SetStdHandle</c>; Unix: <c>dup2</c> onto fd 0) — PowerShell starts a native program that has no
/// pipeline input with the process stdin, so the program inherits the pipe — and starts a pump thread that
/// moves records from the shared stdin into the pipe (their exact bytes, <see cref="RawBytes"/>), closing the
/// write end at end of input so the program sees EOF. <see cref="Exit"/> (after the program exited) restores the
/// process stdin, cancels the pump (its wait on a silent stdin is cancellable, <see cref="IStdinRecordSource"/>),
/// reads back every byte the program left in the pipe and puts it back at the FRONT of the shared stdin, so the
/// next command sees exactly what bash would: the stream minus what the program consumed.
/// </para>
/// <para>
/// The process stdin is process-global; the host runs one command at a time (its execution lease), and nesting is
/// refused (<see cref="Enter"/> returns null while a bridge is active), so only one program owns it. The write
/// end is never inheritable, or the program would never see EOF.
/// </para>
/// </summary>
public sealed class NativeStdinBridge
{
    private const int PipeBufferSize = 64 * 1024;
    private static readonly object Gate = new();
    private static NativeStdinBridge? _active;
    // The one pipe of each stdin a background job shares with the shell (StdinCursor.SharedWithAJob), under Gate.
    private static readonly Dictionary<StdinCursor, NativeStdinBridge> SharedPipes = new(ReferenceEqualityComparer.Instance);

    private readonly object _stdin;
    private readonly AnonymousPipeServerStream _writer;
    private readonly StdinSwap _swap;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread _pump;
    // A foreground program's swap onto a shared pipe (SharedPipes) holds that pipe instead of owning one.
    private readonly NativeStdinBridge? _pipe;
    private int _refs = 1;
    private bool _exited;

    private NativeStdinBridge(object stdin, AnonymousPipeServerStream writer, StdinSwap swap)
    {
        _stdin = stdin;
        _writer = writer;
        _swap = swap;
        _pump = new Thread(Pump) { IsBackground = true, Name = "ps-bash native stdin pump" };
    }

    private NativeStdinBridge(NativeStdinBridge pipe, StdinSwap swap)
    {
        _stdin = pipe._stdin;
        _writer = pipe._writer;
        _swap = swap;
        _pump = pipe._pump;
        _pipe = pipe;
    }

    /// <summary>
    /// Install the bridge for <paramref name="stdin"/> (a <see cref="StdinCursor"/> or <c>Queue&lt;object&gt;</c>).
    /// Null — and the program runs with the stdin it would have had anyway — when another bridge is active, the
    /// stdin is unsupported, or the OS refuses the pipe.
    /// <para>
    /// Only the foreground swaps the PROCESS stdin. Code with its own scalars (<see cref="ScalarOverlay.Current"/>: an
    /// in-process background job, <c>env</c>'s command) runs beside the foreground: a swap from there gave the
    /// foreground's next program the job's stdin (<c>/usr/bin/sleep 3 &amp; sleep 1; cat</c> read the job's empty pipe,
    /// not the launcher's input), and refused the foreground its own bridge while it lasted. Such a program gets its
    /// stdin through a pipe of its own (<see cref="ForChild"/>, which <c>Invoke-BashNative</c> uses).
    /// </para>
    /// </summary>
    public static NativeStdinBridge? Enter(object? stdin)
    {
        if (!Bridgeable(stdin) || ScalarOverlay.Current is not null) return null;
        lock (Gate)
        {
            if (_active is not null) return null;
            NativeStdinBridge? bridge;
            if (stdin is StdinCursor { SharedWithAJob: true } shared)
            {
                // The process stdin becomes the pipe the job's programs read too.
                if (SharedPipe(shared) is not { } pipe) return null;
                try { bridge = new NativeStdinBridge(pipe, StdinSwap.Install(pipe.ChildStdin)); }
                catch (Exception)
                {
                    pipe.Release();
                    return null;
                }
            }
            else
            {
                bridge = Open(stdin!, HandleInheritability.Inheritable, StdinSwap.Install);
                if (bridge is null) return null;
                bridge._pump.Start();
            }
            _active = bridge;
            return bridge;
        }
    }

    /// <summary>
    /// A bridge for ONE child that gets <see cref="ChildStdin"/> as its fd 0 (the process stdin is not touched): the
    /// stdin of a program started by ps-bash itself outside the foreground. <see cref="Exit"/> after the child exited
    /// puts back what it did not read, as for <see cref="Enter"/>. Null when the stdin is unsupported or the OS refuses
    /// the pipe.
    /// <para>
    /// A stdin a background job shares with the shell (<see cref="StdinCursor.SharedWithAJob"/>) has ONE pipe, which
    /// every program reading it gets (a job's, through this; the foreground's, through <see cref="Enter"/>), the way
    /// bash's readers share one file description: a pump per program would move the input into the pipe of one that
    /// may never read it (<c>printf 'hi\n' | { sleep 3 &amp; sleep 1; cat; }</c> prints <c>hi</c>), and keeping the
    /// process stdin instead gave <c>echo hi | { cat &amp; wait; }</c> nothing. The unread rest goes back when the
    /// last program is done.
    /// </para>
    /// </summary>
    public static NativeStdinBridge? ForChild(object? stdin)
    {
        if (!Bridgeable(stdin)) return null;
        if (stdin is StdinCursor { SharedWithAJob: true } shared)
            lock (Gate) return SharedPipe(shared);
        var bridge = Open(stdin!, ChildReadEnd, static _ => new NoSwap());
        bridge?._pump.Start();
        return bridge;
    }

    // The read end goes to a child by an explicit handle (ChildStdio): inheritable only where the spawn needs it so
    // (Windows' handle list); POSIX dup2s it onto fd 0.
    private static HandleInheritability ChildReadEnd
        => OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None;

    // Under Gate: the shared pipe of the cursor, one more holder; null when the OS refuses the pipe.
    private static NativeStdinBridge? SharedPipe(StdinCursor cursor)
    {
        if (SharedPipes.TryGetValue(cursor, out var pipe) && !pipe._exited)
        {
            pipe._refs++;
            return pipe;
        }
        pipe = Open(cursor, ChildReadEnd, static _ => new NoSwap());
        if (pipe is null) return null;
        SharedPipes[cursor] = pipe;
        pipe._pump.Start();
        return pipe;
    }

    /// <summary>The read end a <see cref="ForChild"/> bridge's child gets as its stdin.</summary>
    public SafePipeHandle ChildStdin => _writer.ClientSafePipeHandle;

    // An enumerator-backed cursor blocks in MoveNext whatever the token says, so Exit could never stop a pump waiting on it.
    private static bool Bridgeable(object? stdin)
        => stdin is (StdinCursor or Queue<object>) && stdin is not StdinCursor { CanCancelWait: false };

    private static NativeStdinBridge? Open(object stdin, HandleInheritability readEnd, Func<SafePipeHandle, StdinSwap> swap)
    {
        AnonymousPipeServerStream? writer = null;
        try
        {
            // The server end is the WRITE end and is never inheritable.
            writer = new AnonymousPipeServerStream(PipeDirection.Out, readEnd, PipeBufferSize);
            return new NativeStdinBridge(stdin, writer, swap(writer.ClientSafePipeHandle));
        }
        catch (Exception)
        {
            writer?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Undo <see cref="Enter"/> once the program has exited: restore the process stdin, stop the pump and put
    /// every unread byte back at the front of the shared stdin. Each holder exits once (a shared pipe ends with its
    /// last holder); a further call is a no-op.
    /// </summary>
    public void Exit() => Release();

    private void Release()
    {
        lock (Gate)
        {
            if (_exited || --_refs > 0) return;
            _exited = true;
            if (ReferenceEquals(_active, this)) _active = null;
            if (_stdin is StdinCursor cursor && SharedPipes.TryGetValue(cursor, out var shared) && ReferenceEquals(shared, this))
                SharedPipes.Remove(cursor);
        }

        _swap.Restore();
        if (_pipe is not null)
        {
            _cancel.Dispose();
            _pipe.Release();
            return;
        }
        _cancel.Cancel();

        // Drain the pipe until EOF while the pump winds down: the pump may be blocked writing into a full pipe
        // (the program stopped reading), and only reading unblocks it. EOF arrives when the pump closes the
        // write end (its finally) — every other holder of the pipe only has the read end.
        var unread = new MemoryStream();
        using (var reader = new AnonymousPipeClientStream(PipeDirection.In, _writer.ClientSafePipeHandle))
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
        _pump.Join();
        _writer.Dispose();
        _cancel.Dispose();

        if (unread.Length > 0) PutBack(RawBytes.GetString(unread.GetBuffer().AsSpan(0, (int)unread.Length)));
    }

    private void Pump()
    {
        var ct = _cancel.Token;
        try
        {
            while (TryNext(ct, out var record))
            {
                var bytes = RecordBytes(record);
                if (bytes.Length > 0)
                {
                    _writer.Write(bytes, 0, bytes.Length);
                    _writer.Flush();
                }
                if (ct.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Exit: the program is gone and the stdin was silent.
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
            try { _writer.Dispose(); } catch { /* already closed */ }
        }
    }

    private bool TryNext(CancellationToken ct, out object? record)
    {
        record = null;
        switch (_stdin)
        {
            case StdinCursor cursor:
                return cursor.TryDequeue(ct, out record);
            case Queue<object> queue:
                ct.ThrowIfCancellationRequested();
                return queue.TryDequeue(out record);
            default:
                return false;
        }
    }

    /// <summary>A record's exact bytes (<see cref="BashRuntime.RecordBytes"/>) as the child's stdin bytes.</summary>
    internal static byte[] RecordBytes(object? record) => RawBytes.GetBytes(BashRuntime.RecordBytes(record));

    /// <summary>The unread text as records again (lines; an unterminated last piece stays unterminated), in front.</summary>
    private void PutBack(string text)
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

        switch (_stdin)
        {
            case StdinCursor cursor:
                cursor.PushFront(records);
                break;
            case Queue<object> queue:
                var rest = queue.ToArray();
                queue.Clear();
                foreach (var r in records) queue.Enqueue(r);
                foreach (var r in rest) queue.Enqueue(r);
                break;
        }
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
