using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PsBash.Shell.Pty;

/// <summary>
/// Windows pseudo-console (ConPTY) adapter.
///
/// <para><b>Minimum OS:</b> Windows 10 version 1809 / build 17763 or later.
/// Older builds either lack <c>CreatePseudoConsole</c> entirely or expose an
/// API that drifted before stabilizing in 1809. <see cref="AllocateAsync"/>
/// throws <see cref="PlatformNotSupportedException"/> on lower builds.</para>
///
/// <para><b>Pipe layout:</b> ConPTY operates on two unidirectional pipes —
/// the launcher writes to <c>inputWriteSide</c> (which the console reads as
/// stdin for the attached child) and reads from <c>outputReadSide</c> (which
/// the console writes child stdout/stderr into). ConPTY owns the other ends
/// (<c>inputReadSide</c>, <c>outputWriteSide</c>): we hand those to
/// <c>CreatePseudoConsole</c> and never touch them again until Dispose,
/// where ConPTY closes them when <c>ClosePseudoConsole</c> is called.</para>
///
/// <para><b>Dispose order:</b> <c>ClosePseudoConsole</c> triggers the
/// console to flush, signal the attached child (if any), and close its
/// internal pipe ends. Closing our outward-facing pipe handles before
/// <c>ClosePseudoConsole</c> can deadlock the console thread; closing them
/// after is the documented order.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ConPtyAdapter : IPty
{
    private readonly AnonymousPipeServerStream _input;   // launcher writes; ConPTY reads as stdin
    private readonly AnonymousPipeServerStream _output;  // ConPTY writes; launcher reads as stdout
    private IntPtr _hpc;
    private bool _disposed;

    private ConPtyAdapter(IntPtr hpc, AnonymousPipeServerStream input, AnonymousPipeServerStream output)
    {
        _hpc = hpc;
        _input = input;
        _output = output;
    }

    public Stream Input => _input;
    public Stream Output => _output;
    public IntPtr SlaveHandle => _hpc;
    public int SlaveFileDescriptor => -1;

    public static ValueTask<IPty> AllocateAsync(short cols, short rows)
    {
        // Build 17763 = Win10 1809. Earlier builds may export CreatePseudoConsole
        // but the API drifted; we pin to the documented stable subset.
        if (Environment.OSVersion.Version.Build < 17763)
        {
            throw new PlatformNotSupportedException(
                $"ConPTY requires Windows 10 1809 / build 17763 or later; current build is {Environment.OSVersion.Version.Build}.");
        }

        // Anonymous pipes:
        //   _input  = SERVER (Out) -> launcher writes; CLIENT (In) handed to ConPTY for stdin
        //   _output = SERVER (In)  -> launcher reads;  CLIENT (Out) handed to ConPTY for stdout
        var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        AnonymousPipeServerStream? output = null;
        try
        {
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);

            // CreatePseudoConsole takes raw HANDLEs. The pipe streams keep
            // ownership of the server-side handles; ConPTY takes ownership of
            // the client-side handles on success and releases them on
            // ClosePseudoConsole.
            var inputClient = input.ClientSafePipeHandle;
            var outputClient = output.ClientSafePipeHandle;

            var size = new NativeMethods.COORD { X = cols, Y = rows };
            int hr = NativeMethods.CreatePseudoConsole(
                size,
                inputClient.DangerousGetHandle(),
                outputClient.DangerousGetHandle(),
                NativeMethods.PSEUDOCONSOLE_INHERIT_CURSOR,
                out IntPtr hpc);
            if (hr != 0)
            {
                throw new InvalidOperationException(
                    $"CreatePseudoConsole failed (hr=0x{hr:X8})");
            }

            // Per Microsoft sample: once ConPTY owns the client pipe handles,
            // dispose them on our side so the only references are inside ConPTY.
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();

            var adapter = new ConPtyAdapter(hpc, input, output);
            return ValueTask.FromResult<IPty>(adapter);
        }
        catch
        {
            input.Dispose();
            output?.Dispose();
            throw;
        }
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed || _hpc == IntPtr.Zero) return;
        var size = new NativeMethods.COORD { X = cols, Y = rows };
        int hr = NativeMethods.ResizePseudoConsole(_hpc, size);
        if (hr != 0)
        {
            throw new InvalidOperationException(
                $"ResizePseudoConsole failed (hr=0x{hr:X8})");
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose order is documented in the class summary: close ConPTY
        // FIRST so it can flush + signal the child, then close our pipe
        // handles. Closing the pipe handles first can deadlock the console
        // thread.
        var hpc = System.Threading.Interlocked.Exchange(ref _hpc, IntPtr.Zero);
        if (hpc != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(hpc);
        }

        try { _input.Dispose(); } catch { /* swallow — handles may already be gone */ }
        try { _output.Dispose(); } catch { /* swallow */ }
    }

    private static partial class NativeMethods
    {
        public const uint PSEUDOCONSOLE_INHERIT_CURSOR = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD
        {
            public short X;
            public short Y;
        }

        // CreatePseudoConsole / ResizePseudoConsole / ClosePseudoConsole live
        // in kernel32.dll starting in Win10 1809 (build 17763).
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial int CreatePseudoConsole(
            COORD size,
            IntPtr hInput,
            IntPtr hOutput,
            uint dwFlags,
            out IntPtr phPC);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial int ResizePseudoConsole(IntPtr hPC, COORD size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial void ClosePseudoConsole(IntPtr hPC);
    }
}
