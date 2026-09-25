using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Windows host spawn that inherits EXACTLY one handle: the <c>NUL</c> device, bound
/// as the host's stdin, stdout and stderr. Nothing else from the launcher crosses.
/// </summary>
/// <remarks>
/// <para><b>Why not <see cref="Process.Start(ProcessStartInfo)"/>.</b> .NET always calls
/// <c>CreateProcess</c> with <c>bInheritHandles=TRUE</c>, so the child receives a copy of
/// EVERY inheritable handle the launcher holds — not just its redirect pipes. A launcher
/// started by a .NET parent (vstest's testhost, any C# tool) carries that parent's
/// inheritable stdout/stderr; a persisted <see cref="Lifetime.Daemon"/> host that inherits
/// them keeps the pipe open after everyone else exits, and the reader upstream never sees
/// EOF. That hung the full-suite <c>tman test</c> after its last project reported
/// (task 01M3AQ4TFDYASNFAFK2HB7WNSM). Clearing the inherit flag on the launcher's own
/// three std handles does not help: the stray copies sit at other handle values.
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> is the only way to name the complete set.</para>
/// <para><b>Why NUL for all three.</b> stdin reads EOF at once (a host must never block on
/// a stdin nobody feeds), writes are discarded without ever blocking, and no launcher-side
/// drain task is needed. <c>CREATE_NO_WINDOW</c> keeps the hidden-console behavior the
/// host had under <c>ProcessStartInfo.CreateNoWindow</c>, so console children it runs do
/// not pop windows.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsHostSpawn
{
    /// <summary>
    /// Starts <paramref name="exe"/> with <paramref name="args"/> and returns a
    /// <see cref="Process"/> for it. Throws <see cref="HostUnavailableException"/> when the
    /// spawn fails or the host exits before it can be observed.
    /// </summary>
    public static Process Start(string exe, IReadOnlyList<string> args)
    {
        var commandLine = BuildCommandLine(exe, args);

        var nulName = Marshal.StringToHGlobalUni("NUL");
        // CreateProcessW may write into lpCommandLine, so it must be a mutable buffer.
        var cmdLine = Marshal.StringToHGlobalUni(commandLine);
        IntPtr nul = InvalidHandle;
        IntPtr attrList = IntPtr.Zero;
        IntPtr handleArray = IntPtr.Zero;
        var pi = default(ProcessInformation);
        try
        {
            var sa = new SecurityAttributes
            {
                nLength = sizeof(SecurityAttributes),
                bInheritHandle = 1, // the handle list only accepts inheritable handles
            };
            nul = CreateFileW(nulName, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                &sa, OpenExisting, 0, IntPtr.Zero);
            if (nul == InvalidHandle)
                throw Win32Failure("CreateFileW(NUL)");

            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, &size); // sizing call; fails by design
            attrList = Marshal.AllocHGlobal((nint)size);
            if (InitializeProcThreadAttributeList(attrList, 1, 0, &size) == 0)
            {
                Marshal.FreeHGlobal(attrList);
                attrList = IntPtr.Zero;
                throw Win32Failure("InitializeProcThreadAttributeList");
            }

            // The attribute stores a POINTER to the list; it must stay valid until
            // CreateProcessW returns, so it lives in unmanaged memory.
            handleArray = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(handleArray, nul);
            if (UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeHandleList,
                    handleArray, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero) == 0)
                throw Win32Failure("UpdateProcThreadAttribute(HANDLE_LIST)");

            var si = new StartupInfoEx { lpAttributeList = attrList };
            si.StartupInfo.cb = sizeof(StartupInfoEx);
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdInput = nul;
            si.StartupInfo.hStdOutput = nul;
            si.StartupInfo.hStdError = nul;

            // lpApplicationName = NULL: CreateProcess resolves the command line's first
            // token (cwd, then PATH), so a relative or bare PSBASH_HOST override keeps
            // working exactly as it did under Process.Start.
            if (CreateProcessW(IntPtr.Zero, cmdLine, IntPtr.Zero, IntPtr.Zero, 1,
                    CreateNoWindow | ExtendedStartupInfoPresent, IntPtr.Zero, IntPtr.Zero,
                    &si, &pi) == 0)
                throw Win32Failure($"CreateProcessW('{exe}')");

            // Holding pi.hProcess keeps the process object (and its exit code) alive,
            // so the Process lookup below cannot race PID reuse. A host that already
            // exited is reported with its real exit code, like the Process.Start path.
            try
            {
                var proc = Process.GetProcessById(pi.dwProcessId);
                _ = proc.Handle; // open our own handle while pi.hProcess still pins the object
                return proc;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                uint code = 0;
                GetExitCodeProcess(pi.hProcess, &code);
                throw new HostUnavailableException(
                    $"ps-bash-host exited prematurely with code {unchecked((int)code)}.");
            }
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (nul != InvalidHandle) CloseHandle(nul);
            Marshal.FreeHGlobal(cmdLine);
            Marshal.FreeHGlobal(nulName);
        }
    }

    /// <summary>
    /// Builds a Windows command line that <c>CommandLineToArgvW</c> (and the .NET host)
    /// splits back into exactly <paramref name="exe"/> followed by <paramref name="args"/> —
    /// the same quoting rules <see cref="ProcessStartInfo.ArgumentList"/> applies.
    /// </summary>
    internal static string BuildCommandLine(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        AppendQuoted(sb, exe);
        foreach (var arg in args)
        {
            sb.Append(' ');
            AppendQuoted(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && arg.AsSpan().IndexOfAny(" \t\"") < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes are literal unless they precede a quote: then each one
            // must be doubled, and the quote itself escaped.
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        // Trailing backslashes precede the closing quote, so they double too.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    private static HostUnavailableException Win32Failure(string call)
        => new($"{call} failed: Win32 error {Marshal.GetLastPInvokeError()}.");

    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const int StartfUseStdHandles = 0x100;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private static readonly IntPtr ProcThreadAttributeHandleList = new(0x20002);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateFileW(IntPtr lpFileName, uint dwDesiredAccess, uint dwShareMode,
        SecurityAttributes* lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
        int dwFlags, nuint* lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, nuint cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CreateProcessW(IntPtr lpApplicationName, IntPtr lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, int bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, IntPtr lpCurrentDirectory, StartupInfoEx* lpStartupInfo,
        ProcessInformation* lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetExitCodeProcess(IntPtr hProcess, uint* lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(IntPtr hObject);
}
