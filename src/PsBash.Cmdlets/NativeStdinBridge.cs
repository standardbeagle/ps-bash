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
/// The process stdin is process-global; the host runs one command at a time (its exec gate), and nesting is
/// refused (<see cref="Enter"/> returns null while a bridge is active), so only one program owns it. The write
/// end is never inheritable, or the program would never see EOF.
/// </para>
/// </summary>
public sealed class NativeStdinBridge
{
    private const int PipeBufferSize = 64 * 1024;
    private static readonly object Gate = new();
    private static NativeStdinBridge? _active;

    private readonly object _stdin;
    private readonly AnonymousPipeServerStream _writer;
    private readonly StdinSwap _swap;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread _pump;
    private bool _exited;

    private NativeStdinBridge(object stdin, AnonymousPipeServerStream writer, StdinSwap swap)
    {
        _stdin = stdin;
        _writer = writer;
        _swap = swap;
        _pump = new Thread(Pump) { IsBackground = true, Name = "ps-bash native stdin pump" };
    }

    /// <summary>
    /// Install the bridge for <paramref name="stdin"/> (a <see cref="StdinCursor"/> or <c>Queue&lt;object&gt;</c>).
    /// Null — and the program runs with the stdin it would have had anyway — when another bridge is active, the
    /// stdin is unsupported, or the OS refuses the pipe.
    /// </summary>
    public static NativeStdinBridge? Enter(object? stdin)
    {
        if (stdin is not (StdinCursor or Queue<object>)) return null;
        // An enumerator-backed cursor blocks in MoveNext whatever the token says, so Exit could never stop a
        // pump waiting on it.
        if (stdin is StdinCursor { CanCancelWait: false }) return null;
        lock (Gate)
        {
            if (_active is not null) return null;
            AnonymousPipeServerStream? writer = null;
            try
            {
                // The server end is the WRITE end and is never inheritable; the client (read) end is.
                writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable, PipeBufferSize);
                var swap = StdinSwap.Install(writer.ClientSafePipeHandle);
                var bridge = new NativeStdinBridge(stdin, writer, swap);
                _active = bridge;
                bridge._pump.Start();
                return bridge;
            }
            catch (Exception)
            {
                writer?.Dispose();
                return null;
            }
        }
    }

    /// <summary>
    /// Undo <see cref="Enter"/> once the program has exited: restore the process stdin, stop the pump and put
    /// every unread byte back at the front of the shared stdin. Idempotent.
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

    /// <summary>A record's exact bytes: its text plus the line terminator it carries (none when unterminated).</summary>
    internal static byte[] RecordBytes(object? record)
    {
        var text = BashRuntime.GetBashText(record);
        if (!text.EndsWith('\n') && !BashRuntime.IsUnterminated(record)) text += "\n";
        return RawBytes.GetBytes(text);
    }

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
public sealed class EnterBashNativeStdinCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Name { get; set; } = "";

    protected override void EndProcessing()
    {
        var stdin = SharedStdin.Get(SessionState);
        if (stdin is null) return;

        // Only a real program: a function or alias of the same name (which PowerShell would run instead) reads
        // its stdin through the pipeline, and must not have it pumped away underneath it.
        CommandInfo? resolved;
        try { resolved = InvokeCommand.GetCommand(Name, CommandTypes.All); }
        catch { return; }
        if (resolved is not ApplicationInfo) return;

        var bridge = NativeStdinBridge.Enter(stdin);
        if (bridge is not null) WriteObject(bridge);
    }
}

/// <summary><c>Exit-BashNativeStdin $bridge</c>: undo <c>Enter-BashNativeStdin</c> (a null bridge is a no-op).</summary>
[Cmdlet(VerbsCommon.Exit, "BashNativeStdin")]
public sealed class ExitBashNativeStdinCommand : PSCmdlet
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
