using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Starts a program that outlives the shell that started it (<c>nohup</c>): not killed when the host exits, deaf to
/// console close / Ctrl+C, holding none of the caller's pipes. The one place that knows how, per OS.
/// <para>Windows: <c>CreateProcessW</c> with DETACHED_PROCESS (no console), CREATE_NEW_PROCESS_GROUP, and
/// CREATE_BREAKAWAY_FROM_JOB (so the host's Job Object does not take it down; retried without when the job forbids
/// breakaway). It inherits EXACTLY three handles via PROC_THREAD_ATTRIBUTE_HANDLE_LIST - NUL as stdin and the log
/// file as stdout/stderr - because .NET's own Process.Start would copy every inheritable handle of the caller,
/// including the pipe a waiting agent is reading. Unix: <c>sh</c> ignoring SIGHUP, then <c>exec</c>.</para>
/// </summary>
public static class DetachedProcess
{
    /// <summary>
    /// Runs <paramref name="exe"/> with <paramref name="args"/> in <paramref name="workingDirectory"/>. stdin is empty;
    /// stdout goes to <paramref name="outPath"/> (appended unless <paramref name="append"/> is false), stderr to
    /// <paramref name="errPath"/> or, when null, the same file. Returns the OS process id.
    /// </summary>
    /// <param name="exe">Program to run (already resolved).</param>
    /// <param name="args">Its arguments, unquoted.</param>
    /// <param name="workingDirectory">Directory the program starts in.</param>
    /// <param name="outPath">File that receives stdout.</param>
    /// <param name="append">Append to <paramref name="outPath"/> instead of truncating it.</param>
    /// <param name="errPath">File that receives stderr; null shares <paramref name="outPath"/>.</param>
    public static int Spawn(string exe, IReadOnlyList<string> args, string workingDirectory, string outPath, bool append, string? errPath)
        => OperatingSystem.IsWindows()
            ? StartWindows(exe, args, workingDirectory, outPath, append, errPath)
            : StartUnix(exe, args, workingDirectory, outPath, append, errPath);

    private static int StartUnix(string exe, IReadOnlyList<string> args, string cwd, string outPath, bool append, string? errPath)
    {
        var psi = new ProcessStartInfo("/bin/sh") { WorkingDirectory = cwd, UseShellExecute = false };
        var op = append ? ">>" : ">";
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(string.IsNullOrEmpty(errPath)
            ? $"trap '' HUP; o=\"$OUT\"; unset OUT; exec \"$@\" </dev/null {op}\"$o\" 2>&1"
            : $"trap '' HUP; o=\"$OUT\"; e=\"$ERR\"; unset OUT ERR; exec \"$@\" </dev/null {op}\"$o\" 2>>\"$e\"");
        psi.ArgumentList.Add("nohup");
        psi.ArgumentList.Add(exe);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["OUT"] = outPath;
        if (!string.IsNullOrEmpty(errPath)) psi.Environment["ERR"] = errPath;
        BashVariableStore.ApplyTo(psi); // exports reach the child (Windows inherits the process environment directly)
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        return p.Id;
    }

    // ---- Windows ----
    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileAppendData = 0x4;
    private const uint ShareRW = 0x3, OpenExisting = 3, OpenAlways = 4, CreateAlways = 2;
    private const int StartfUseStdHandles = 0x100;
    private const uint DetachedProcessFlag = 0x8, NewProcessGroup = 0x200, BreakawayFromJob = 0x01000000, ExtendedStartupInfoPresent = 0x80000;
    private static readonly IntPtr ProcThreadAttributeHandleList = new(0x00020002);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, ref SecurityAttributes sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder commandLine, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string cwd, ref StartupInfoEx si, out ProcessInformation pi);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static int StartWindows(string exe, IReadOnlyList<string> args, string cwd, string outPath, bool append, string? errPath)
    {
        var cmd = new StringBuilder();
        AppendQuoted(cmd, exe);
        foreach (var a in args) { cmd.Append(' '); AppendQuoted(cmd, a); }

        IntPtr nul = InvalidHandle, outH = InvalidHandle, errH = InvalidHandle, attrList = IntPtr.Zero, handleArray = IntPtr.Zero;
        ProcessInformation pi = default;
        try
        {
            var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
            nul = CreateFileW("NUL", GenericRead | GenericWrite, ShareRW, ref sa, OpenExisting, 0, IntPtr.Zero);
            if (nul == InvalidHandle) throw Failure("open NUL");
            outH = CreateFileW(outPath, FileAppendData, ShareRW, ref sa, append ? OpenAlways : CreateAlways, 0, IntPtr.Zero);
            if (outH == InvalidHandle) throw Failure($"open '{outPath}'");
            if (!string.IsNullOrEmpty(errPath))
            {
                errH = CreateFileW(errPath, FileAppendData, ShareRW, ref sa, OpenAlways, 0, IntPtr.Zero);
                if (errH == InvalidHandle) throw Failure($"open '{errPath}'");
            }
            var stderrH = errH == InvalidHandle ? outH : errH;

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // sizing call; fails by design
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) { Marshal.FreeHGlobal(attrList); attrList = IntPtr.Zero; throw Failure("InitializeProcThreadAttributeList"); }

            // The list may not repeat a handle; stdout and stderr are usually the same file.
            var handles = stderrH == outH ? new[] { nul, outH } : new[] { nul, outH, stderrH };
            handleArray = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            for (int i = 0; i < handles.Length; i++) Marshal.WriteIntPtr(handleArray, i * IntPtr.Size, handles[i]);
            if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeHandleList, handleArray, new IntPtr(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                throw Failure("UpdateProcThreadAttribute");

            var si = new StartupInfoEx { lpAttributeList = attrList };
            si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdInput = nul; si.StartupInfo.hStdOutput = outH; si.StartupInfo.hStdError = stderrH;

            uint baseFlags = DetachedProcessFlag | NewProcessGroup | ExtendedStartupInfoPresent;
            // Breakaway fails with ACCESS_DENIED when the host's job forbids it: run inside the job rather than not at all.
            if (!CreateProcessW(null, new StringBuilder(cmd.ToString()), IntPtr.Zero, IntPtr.Zero, true, baseFlags | BreakawayFromJob, IntPtr.Zero, cwd, ref si, out pi)
                && !CreateProcessW(null, new StringBuilder(cmd.ToString()), IntPtr.Zero, IntPtr.Zero, true, baseFlags, IntPtr.Zero, cwd, ref si, out pi))
                throw Failure($"CreateProcess '{exe}'");
            return pi.dwProcessId;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (nul != InvalidHandle) CloseHandle(nul);
            if (outH != InvalidHandle) CloseHandle(outH);
            if (errH != InvalidHandle) CloseHandle(errH);
        }
    }

    private static InvalidOperationException Failure(string what) => new($"{what} failed: Win32 error {Marshal.GetLastWin32Error()}");

    /// <summary>CommandLineToArgvW-compatible quoting (the rules ProcessStartInfo.ArgumentList applies).</summary>
    private static void AppendQuoted(StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && arg.AsSpan().IndexOfAny(" \t\"") < 0) { sb.Append(arg); return; }
        sb.Append('"');
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }
}
