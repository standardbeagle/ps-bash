using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PsBash.Core.Runtime.Processes;

namespace PsBash.Cmdlets;

/// <summary>
/// Starts a program that outlives the shell that started it (<c>nohup</c>): not killed when the host exits, deaf to
/// console close / Ctrl+C, holding none of the caller's pipes. The one place that knows how, per OS.
/// <para>Windows: <c>CreateProcessW</c> with DETACHED_PROCESS (no console), CREATE_NEW_PROCESS_GROUP, and
/// CREATE_BREAKAWAY_FROM_JOB (so the host's Job Object does not take it down; retried without when the job forbids
/// breakaway). It inherits EXACTLY its three handles via PROC_THREAD_ATTRIBUTE_HANDLE_LIST - its stdin (NUL, or the
/// shell's stdin pipe) and the log file as stdout/stderr - because .NET's own Process.Start would copy every inheritable
/// handle of the caller, including the pipe a waiting agent is reading. Unix: <c>sh</c> ignoring SIGHUP, then
/// <c>exec</c>, started by <see cref="JobProcess"/> (its own process group, released at once) so its fd 0 is exactly
/// the stdin given: the null device, or the shell's stdin pipe.</para>
/// <para>stdin: GNU nohup leaves a stdin that is not a terminal alone, so CMD reads the shell's stdin where it has one
/// (<c>echo in | { nohup cat &gt; o &amp; wait; }</c> writes in): the caller passes the read end of the cursor's pipe
/// (<see cref="NativeStdinBridge.HoldFor"/>) and holds it until the child exits.</para>
/// <para>A redirect file is opened for WRITING (GENERIC_WRITE) and an append moves to its end, as cmd.exe's <c>&gt;&gt;</c>
/// does, never with FILE_APPEND_DATA alone: the MSYS2/Cygwin runtime (Git for Windows' ls.exe, sh.exe) derives an
/// inherited handle's open mode from its granted access, reads append-only as not writable, and wrote nothing
/// (<c>nohup ls /x &gt; f 2&gt;&amp;1</c> left f empty on the GitHub Windows runner, v0.15.1 gate).</para>
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
    /// <param name="errAppend">Append to <paramref name="errPath"/> (<c>2&gt;&gt;</c>) instead of truncating it (<c>2&gt;</c>).</param>
    public static int Spawn(string exe, IReadOnlyList<string> args, string workingDirectory, string outPath, bool append, string? errPath, bool errAppend = false)
    {
        using var child = SpawnChild(exe, args, workingDirectory, outPath, append, errPath, errAppend);
        return child.Pid;
    }

    /// <summary>As <see cref="Spawn"/>, keeping hold of the process so a foreground <c>nohup CMD</c> can wait for its
    /// status (<see cref="DetachedChild.Wait"/>). Disposing the result lets go of it; the process keeps running.</summary>
    public static DetachedChild SpawnChild(string exe, IReadOnlyList<string> args, string workingDirectory, string outPath, bool append, string? errPath, bool errAppend = false)
        => SpawnChild(exe, args, workingDirectory, outPath, append, errPath, errAppend, stdin: null);

    /// <summary>As above, with <paramref name="stdin"/> (a pipe's read end the caller holds until the child exits) as the
    /// child's stdin instead of the null device.</summary>
    public static DetachedChild SpawnChild(string exe, IReadOnlyList<string> args, string workingDirectory, string outPath, bool append, string? errPath, bool errAppend,
        SafeHandle? stdin)
    {
        bool added = false;
        try
        {
            stdin?.DangerousAddRef(ref added);
            IntPtr? stdinHandle = added ? stdin!.DangerousGetHandle() : null;
            return OperatingSystem.IsWindows()
                ? StartWindows(exe, args, workingDirectory, outPath, append, errPath, errAppend, stdinHandle)
                : StartUnix(exe, args, workingDirectory, outPath, append, errPath, errAppend, stdinHandle);
        }
        finally
        {
            if (added) stdin!.DangerousRelease();
        }
    }

    private static DetachedChild StartUnix(string exe, IReadOnlyList<string> args, string cwd, string outPath, bool append, string? errPath, bool errAppend,
        IntPtr? stdin)
    {
        var psi = new ProcessStartInfo(PsBash.Core.Platform.SystemTools.PosixShell) { WorkingDirectory = cwd, UseShellExecute = false };
        var op = append ? ">>" : ">";
        psi.ArgumentList.Add("-c");
        // fd 0 is set by the spawn: the null device, or the shell's stdin pipe.
        psi.ArgumentList.Add(string.IsNullOrEmpty(errPath)
            ? $"trap '' HUP; o=\"$OUT\"; unset OUT; exec \"$@\" {op}\"$o\" 2>&1"
            : $"trap '' HUP; o=\"$OUT\"; e=\"$ERR\"; unset OUT ERR; exec \"$@\" {op}\"$o\" 2{(errAppend ? ">>" : ">")}\"$e\"");
        psi.ArgumentList.Add("nohup");
        psi.ArgumentList.Add(exe);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["OUT"] = outPath;
        if (!string.IsNullOrEmpty(errPath)) psi.Environment["ERR"] = errPath;
        BashVariableStore.ApplyTo(psi); // the exported variables only
        var environment = new Dictionary<string, string?>(psi.Environment.Count, StringComparer.Ordinal);
        foreach (var kv in psi.Environment) environment[kv.Key] = kv.Value;
        var job = JobProcess.Start(new JobProcessStart(psi.FileName, [.. psi.ArgumentList], environment)
        {
            EnvironmentIsComplete = true,
            WorkingDirectory = cwd,
            OwnProcessGroup = true,
            Stdio = new JobStdio(stdin, null, null),
        });
        job.Release(); // started not to die with this shell
        return new DetachedChild(job.Pid, job, IntPtr.Zero);
    }

    // ---- Windows ----
    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint ShareRW = 0x3, OpenExisting = 3, OpenAlways = 4, CreateAlways = 2, FileEnd = 2;
    private const int StartfUseStdHandles = 0x100;
    private const uint DetachedProcessFlag = 0x8, NewProcessGroup = 0x200, BreakawayFromJob = 0x01000000, ExtendedStartupInfoPresent = 0x80000, UnicodeEnvironment = 0x400;
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
    private static extern bool SetFilePointerEx(IntPtr file, long distance, IntPtr newPosition, uint moveMethod);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    /// <summary>A started detached process, held only to learn its status: <see cref="Wait"/> never kills it.</summary>
    public sealed class DetachedChild : IDisposable
    {
        private readonly JobProcess? _unix;
        private IntPtr _process;

        internal DetachedChild(int pid, JobProcess? unix, IntPtr process) { Pid = pid; _unix = unix; _process = process; }

        /// <summary>The OS process id (<c>$!</c>).</summary>
        public int Pid { get; }

        /// <summary>
        /// Waits for the process to exit and returns its status, or null once <paramref name="stop"/> is signalled first
        /// (Ctrl+C, a dropped client). The process is NOT stopped then: it was started not to die with this shell.
        /// </summary>
        public int? Wait(CancellationToken stop)
        {
            const int Slice = 100;
            while (!stop.IsCancellationRequested)
            {
                if (_unix is not null)
                {
                    if (_unix.WaitForExit(Slice, stop)) return _unix.ExitCode;
                }
                else if (_process != IntPtr.Zero)
                {
                    if (WaitForSingleObject(_process, Slice) == 0)
                        return GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : 1;
                }
                else return null;
            }
            return null;
        }

        public void Dispose()
        {
            _unix?.Dispose();
            if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
        }
    }
    private static DetachedChild StartWindows(string exe, IReadOnlyList<string> args, string cwd, string outPath, bool append, string? errPath, bool errAppend,
        IntPtr? stdin)
    {
        var cmd = new StringBuilder();
        AppendQuoted(cmd, exe);
        foreach (var a in args) { cmd.Append(' '); AppendQuoted(cmd, a); }

        IntPtr nul = InvalidHandle, outH = InvalidHandle, errH = InvalidHandle, attrList = IntPtr.Zero, handleArray = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        ProcessInformation pi = default;
        try
        {
            // The exported variables only (the one child-environment primitive), not the process environment.
            char[] env = PsBash.Core.ShellExports.WindowsEnvironmentBlock();
            envBlock = Marshal.AllocHGlobal(env.Length * sizeof(char));
            Marshal.Copy(env, 0, envBlock, env.Length);
            var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
            if (stdin is null)
            {
                nul = CreateFileW("NUL", GenericRead | GenericWrite, ShareRW, ref sa, OpenExisting, 0, IntPtr.Zero);
                if (nul == InvalidHandle) throw Failure("open NUL");
            }
            // The shell's stdin pipe (inheritable: CursorPipe's read end), else NUL.
            var stdinH = stdin ?? nul;
            outH = OpenRedirectTarget(outPath, append, ref sa);
            if (!string.IsNullOrEmpty(errPath)) errH = OpenRedirectTarget(errPath, errAppend, ref sa);
            var stderrH = errH == InvalidHandle ? outH : errH;

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // sizing call; fails by design
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) { Marshal.FreeHGlobal(attrList); attrList = IntPtr.Zero; throw Failure("InitializeProcThreadAttributeList"); }

            // The list may not repeat a handle; stdout and stderr are usually the same file.
            var handles = stderrH == outH ? new[] { stdinH, outH } : new[] { stdinH, outH, stderrH };
            handleArray = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            for (int i = 0; i < handles.Length; i++) Marshal.WriteIntPtr(handleArray, i * IntPtr.Size, handles[i]);
            if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeHandleList, handleArray, new IntPtr(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                throw Failure("UpdateProcThreadAttribute");

            var si = new StartupInfoEx { lpAttributeList = attrList };
            si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdInput = stdinH; si.StartupInfo.hStdOutput = outH; si.StartupInfo.hStdError = stderrH;

            uint baseFlags = DetachedProcessFlag | NewProcessGroup | ExtendedStartupInfoPresent | UnicodeEnvironment;
            // Breakaway fails with ACCESS_DENIED when the host's job forbids it: run inside the job rather than not at all.
            if (!CreateProcessW(null, new StringBuilder(cmd.ToString()), IntPtr.Zero, IntPtr.Zero, true, baseFlags | BreakawayFromJob, envBlock, cwd, ref si, out pi)
                && !CreateProcessW(null, new StringBuilder(cmd.ToString()), IntPtr.Zero, IntPtr.Zero, true, baseFlags, envBlock, cwd, ref si, out pi))
                throw Failure($"CreateProcess '{exe}'");
            var child = new DetachedChild(pi.dwProcessId, null, pi.hProcess);
            pi.hProcess = IntPtr.Zero; // the child owns it now
            return child;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            if (nul != InvalidHandle) CloseHandle(nul);
            if (outH != InvalidHandle) CloseHandle(outH);
            if (errH != InvalidHandle) CloseHandle(errH);
        }
    }

    // A child's redirect file: writable (not FILE_APPEND_DATA alone, which MSYS2 programs cannot write to), truncated
    // (`>`) or positioned at its end (`>>`); the child inherits the handle and its position.
    private static IntPtr OpenRedirectTarget(string path, bool append, ref SecurityAttributes sa)
    {
        var h = CreateFileW(path, GenericWrite, ShareRW, ref sa, append ? OpenAlways : CreateAlways, 0, IntPtr.Zero);
        if (h == InvalidHandle) throw Failure($"open '{path}'");
        if (append && !SetFilePointerEx(h, 0, IntPtr.Zero, FileEnd)) { var e = Failure($"seek '{path}'"); CloseHandle(h); throw e; }
        return h;
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
