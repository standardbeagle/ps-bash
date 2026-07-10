using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PsBash.Shell.Pty;

/// <summary>
/// Windows <see cref="PtySpawner"/> implementation using
/// <c>CreateProcessW</c> with a <c>STARTUPINFOEX</c> attribute list carrying
/// <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE_HANDLE</c>. The
/// <see cref="IPty.SlaveHandle"/> (HPCON) returned by
/// <c>CreatePseudoConsole</c> is the value passed to
/// <c>UpdateProcThreadAttribute</c>.
///
/// <para><c>ProcessStartInfo</c> intentionally does not expose
/// <c>STARTUPINFOEX</c> or the attribute list, so the Windows code path
/// drops directly to <c>kernel32!CreateProcessW</c>. Per Microsoft's
/// pseudoconsole sample, the launcher's stdio is <i>not</i> redirected on
/// this path — the ConPTY pair already provides the child's stdio.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPtySpawner : PtySpawner
{
    private readonly SafeProcessHandle _process;
    private readonly int _pid;
    private bool _disposed;

    private WindowsPtySpawner(SafeProcessHandle process, int pid)
    {
        _process = process;
        _pid = pid;
    }

    public override int Pid => _pid;

    public static PtySpawner SpawnInternal(
        string executablePath,
        IReadOnlyList<string> arguments,
        IPty pty,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (pty.SlaveHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                "Windows PTY spawn requires a non-zero IPty.SlaveHandle (HPCON).");

        // 1) Quote the command line per CommandLineToArgvW rules.
        string commandLine = BuildCommandLine(executablePath, arguments);

        // 2) Allocate the proc-thread attribute list and stash the HPCON.
        IntPtr attrList = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            nuint size = 0;
            // First call sizes the attribute list. It returns FALSE with
            // ERROR_INSUFFICIENT_BUFFER; that's expected.
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal((int)size);
            if (!NativeMethods.InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "InitializeProcThreadAttributeList failed");

            // Stash the HPCON for the child.
            if (!NativeMethods.UpdateProcThreadAttribute(
                    attrList,
                    0,
                    NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE_HANDLE,
                    pty.SlaveHandle,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "UpdateProcThreadAttribute(PSEUDOCONSOLE_HANDLE) failed");
            }

            // 3) Build STARTUPINFOEX. Do NOT set hStdInput/hStdOutput/hStdError
            //    or STARTF_USESTDHANDLES — ConPTY wires the child's stdio
            //    through the HPCON.
            var siEx = new NativeMethods.STARTUPINFOEX();
            siEx.StartupInfo.cb = (uint)Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();
            siEx.lpAttributeList = attrList;

            // 4) Build a merged environment block: current env + overrides.
            envBlock = BuildEnvironmentBlock(environment);

            var pi = new NativeMethods.PROCESS_INFORMATION();
            uint creationFlags =
                NativeMethods.EXTENDED_STARTUPINFO_PRESENT |
                NativeMethods.CREATE_UNICODE_ENVIRONMENT;

            var mutableCommandLine = new StringBuilder(commandLine);
            if (!NativeMethods.CreateProcessW(
                    lpApplicationName: null,
                    lpCommandLine: mutableCommandLine,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: creationFlags,
                    lpEnvironment: envBlock,
                    lpCurrentDirectory: null,
                    lpStartupInfo: ref siEx,
                    lpProcessInformation: out pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"CreateProcessW('{executablePath}') failed");
            }

            // hThread isn't needed by the launcher (we don't suspend/resume).
            NativeMethods.CloseHandle(pi.hThread);

            var processHandle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            return new WindowsPtySpawner(processHandle, (int)pi.dwProcessId);
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }
    }

    public override async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        // WaitForSingleObject is blocking; bounce to a Task so cancellation
        // remains responsive. WAIT_OBJECT_0 == 0.
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        _ = Task.Run(() =>
        {
            uint result = NativeMethods.WaitForSingleObject(_process, NativeMethods.INFINITE);
            if (result != 0)
            {
                tcs.TrySetException(new Win32Exception(Marshal.GetLastWin32Error(),
                    $"WaitForSingleObject returned 0x{result:X8}"));
                return;
            }
            if (!NativeMethods.GetExitCodeProcess(_process, out uint exit))
            {
                tcs.TrySetException(new Win32Exception(Marshal.GetLastWin32Error(),
                    "GetExitCodeProcess failed"));
                return;
            }
            tcs.TrySetResult(unchecked((int)exit));
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool ContainsAny(string s, string chars)
    {
        foreach (char c in s) if (chars.IndexOf(c) >= 0) return true;
        return false;
    }

    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        AppendArgvQuoted(sb, executablePath);
        foreach (var a in arguments)
        {
            sb.Append(' ');
            AppendArgvQuoted(sb, a);
        }
        return sb.ToString();
    }

    private static void AppendArgvQuoted(StringBuilder sb, string s)
    {
        // CommandLineToArgvW rules: backslashes are literal unless followed by
        // a quote (then they need to be doubled). The standard quoting trick:
        bool needsQuotes = s.Length == 0 || ContainsAny(s, " \t\"\n");
        if (!needsQuotes) { sb.Append(s); return; }

        sb.Append('"');
        int backslashes = 0;
        foreach (char c in s)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }
            if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            if (kv.Key is string k && kv.Value is string v) merged[k] = v;
        }
        if (overrides is not null)
        {
            foreach (var kv in overrides) merged[kv.Key] = kv.Value;
        }

        // Format: KEY=VAL\0KEY=VAL\0...\0\0  (UTF-16LE)
        var sb = new StringBuilder();
        foreach (var kv in merged)
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        sb.Append('\0');

        byte[] bytes = Encoding.Unicode.GetBytes(sb.ToString());
        IntPtr block = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, block, bytes.Length);
        return block;
    }

    private static partial class NativeMethods
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint INFINITE = 0xFFFFFFFF;

        // From processthreadsapi.h:
        //   #define ProcThreadAttributeValue(Number, Thread, Input, Additive) \
        //     (((Number) & 0xffff) | ((Thread != FALSE) ? 0x10000 : 0) |       \
        //      ((Input != FALSE) ? 0x20000 : 0) | ((Additive != FALSE) ? 0x40000 : 0))
        // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = ProcThreadAttributeValue(22, FALSE, TRUE, FALSE)
        //                                    = 22 | 0x20000 = 0x00020016
        public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE_HANDLE = (IntPtr)0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public uint cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref nuint lpSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr Attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [LibraryImport("kernel32.dll")]
        public static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateProcessW",
            CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(IntPtr hObject);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);
    }
}
