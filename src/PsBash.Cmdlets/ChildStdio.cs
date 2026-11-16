using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// How a spawned child's stdin and stderr are wired. The one place a child's stdio shape is chosen; every spawn
/// goes through <see cref="ChildStdio.Start"/> (via <see cref="BashRuntime.RunChildProcess(ProcessStartInfo, System.TimeSpan?, string?, ChildStdioMode)"/>
/// and <see cref="BashRuntime.StartChildProcessStreaming"/>).
/// </summary>
/// <param name="MergeStderr">
/// stderr is merged into stdout (<c>2&gt;&amp;1</c>, <c>&amp;&gt;</c>, <c>|&amp;</c>, <c>&gt;f 2&gt;&amp;1</c>): the child gets ONE pipe as both
/// fd 1 and fd 2, so the order it wrote in is the order we read. Never two pipes read and interleaved afterwards.
/// </param>
/// <param name="InheritStdin">
/// The child's stdin is the process stdin (what a native program run by PowerShell gets when it has no pipeline
/// input: the launcher's forwarded stdin through <see cref="NativeStdinBridge"/>, or nothing) instead of a pipe the
/// caller writes and closes.
/// </param>
public readonly record struct ChildStdioMode(bool MergeStderr = false, bool InheritStdin = false)
{
    /// <summary>Separate stdout/stderr pipes, a stdin pipe: the default.</summary>
    public static ChildStdioMode Separate => default;

    /// <summary>One pipe for stdout and stderr, a stdin pipe.</summary>
    public static ChildStdioMode Merged => new(MergeStderr: true);
}

/// <summary>
/// A started child and the caller's ends of its stdio: <see cref="StandardInput"/> (null when it inherited the process
/// stdin), <see cref="StandardOutput"/>, and <see cref="StandardError"/> (null when stderr is merged into stdout).
/// </summary>
public sealed class SpawnedChild : IDisposable
{
    internal SpawnedChild(Process process, StreamWriter? stdin, StreamReader stdout, StreamReader? stderr)
    {
        Process = process;
        StandardInput = stdin;
        StandardOutput = stdout;
        StandardError = stderr;
    }

    public Process Process { get; }

    /// <summary>The child's stdin pipe (write end); null when the child inherited the process stdin.</summary>
    public StreamWriter? StandardInput { get; }

    /// <summary>The child's stdout — and, when <see cref="StderrMerged"/>, its stderr in write order.</summary>
    public StreamReader StandardOutput { get; }

    /// <summary>The child's stderr pipe; null when merged into <see cref="StandardOutput"/>.</summary>
    public StreamReader? StandardError { get; }

    public bool StderrMerged => StandardError is null;

    public void Dispose()
    {
        try { StandardInput?.Dispose(); } catch { /* broken pipe on the final flush */ }
        try { StandardOutput.Dispose(); } catch { }
        try { StandardError?.Dispose(); } catch { }
        try { Process.Dispose(); } catch { }
    }
}

/// <summary>
/// THE child-process spawn primitive. Under <see cref="ChildStdioMode.MergeStderr"/> the child's fd 1 and fd 2 are the
/// SAME OS pipe, so the merge happens in the kernel as the child writes and the order is preserved by construction:
/// <list type="bullet">
/// <item>Windows: <c>CreateProcessW</c> with <c>hStdOutput == hStdError</c> (the pipe's write end) and a
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> naming exactly the child's handles (.NET's <c>Process.Start</c>
/// cannot pass one handle for both).</item>
/// <item>Unix: <c>/bin/sh -c 'exec 2&gt;&amp;1; exec "$@"'</c> — <c>dup2(1, 2)</c> in the child, then the program
/// replaces the shell (same pid, so kill-tree and the exit status are the program's).</item>
/// </list>
/// Without the merge it is .NET's <c>Process.Start</c> with the requested redirections.
/// See <c>docs/specs/stdio-messaging.md</c> §4.
/// </summary>
public static class ChildStdio
{
    /// <summary>
    /// Start <paramref name="startInfo"/> (FileName + ArgumentList/Arguments, Environment, WorkingDirectory,
    /// CreateNoWindow are honored; its Redirect* flags are set here from <paramref name="mode"/>). Output is decoded
    /// byte-faithfully (<see cref="PsBash.Core.RawBytes.Encoding"/>) unless the caller chose an encoding.
    /// </summary>
    public static SpawnedChild Start(ProcessStartInfo startInfo, ChildStdioMode mode)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        BashVariableStore.ApplyTo(startInfo);
        startInfo.UseShellExecute = false;
        var outEncoding = startInfo.StandardOutputEncoding ?? PsBash.Core.RawBytes.Encoding;
        if (mode.MergeStderr)
        {
            return OperatingSystem.IsWindows()
                ? WindowsMerged.Start(startInfo, mode.InheritStdin, outEncoding)
                : StartUnixMerged(startInfo, mode.InheritStdin, outEncoding);
        }

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = !mode.InheritStdin;
        startInfo.StandardOutputEncoding = outEncoding;
        startInfo.StandardErrorEncoding ??= PsBash.Core.RawBytes.Encoding;
        var proc = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{startInfo.FileName}'.");
        return new SpawnedChild(proc, mode.InheritStdin ? null : proc.StandardInput, proc.StandardOutput, proc.StandardError);
    }

    /// <summary>The Unix merged form's shell program: fd 2 becomes fd 1, then the program replaces the shell.</summary>
    internal const string UnixMergeScript = "exec 2>&1; exec \"$@\"";

    private static SpawnedChild StartUnixMerged(ProcessStartInfo psi, bool inheritStdin, Encoding outEncoding)
    {
        var sh = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            WorkingDirectory = psi.WorkingDirectory,
            CreateNoWindow = psi.CreateNoWindow,
            RedirectStandardOutput = true,
            // The shell's own fd 2 is replaced by `exec 2>&1` before the program runs; nothing is ever written to it.
            RedirectStandardError = false,
            RedirectStandardInput = !inheritStdin,
            StandardOutputEncoding = outEncoding,
        };
        sh.ArgumentList.Add("-c");
        sh.ArgumentList.Add(UnixMergeScript);
        sh.ArgumentList.Add("sh");
        sh.ArgumentList.Add(psi.FileName);
        if (psi.ArgumentList.Count > 0)
            foreach (var a in psi.ArgumentList) sh.ArgumentList.Add(a);
        else if (!string.IsNullOrEmpty(psi.Arguments))
            throw new ArgumentException("A merged-stderr child takes its arguments as ArgumentList.", nameof(psi));
        BashVariableStore.ApplyTo(sh); // the materialization seam (BashVariableStoreGuardTests), before psi's env is copied
        sh.Environment.Clear();
        foreach (var kv in psi.Environment) sh.Environment[kv.Key] = kv.Value;
        var proc = Process.Start(sh)
            ?? throw new InvalidOperationException($"Failed to start process '{psi.FileName}'.");
        return new SpawnedChild(proc, inheritStdin ? null : proc.StandardInput, proc.StandardOutput, null);
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsMerged
    {
        public static SpawnedChild Start(ProcessStartInfo psi, bool inheritStdin, Encoding outEncoding)
        {
            var cmdLine = new StringBuilder();
            AppendQuoted(cmdLine, psi.FileName);
            if (psi.ArgumentList.Count > 0)
                foreach (var a in psi.ArgumentList) { cmdLine.Append(' '); AppendQuoted(cmdLine, a); }
            else if (!string.IsNullOrEmpty(psi.Arguments))
                cmdLine.Append(' ').Append(psi.Arguments);

            // Our ends are never inheritable (AnonymousPipeServerStream duplicates the server handle non-inheritable);
            // the child's ends are, and the handle list names exactly them.
            var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            AnonymousPipeServerStream? input = null;
            IntPtr stdinDup = IntPtr.Zero;
            IntPtr attrList = IntPtr.Zero, handleArray = IntPtr.Zero;
            var pi = default(ProcessInformation);
            bool ok = false;
            try
            {
                IntPtr childStdin;
                if (inheritStdin)
                {
                    stdinDup = InheritableStdin();
                    childStdin = stdinDup;
                }
                else
                {
                    input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                    childStdin = input.ClientSafePipeHandle.DangerousGetHandle();
                }
                IntPtr childOut = output.ClientSafePipeHandle.DangerousGetHandle();

                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // sizing call; fails by design
                attrList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                {
                    Marshal.FreeHGlobal(attrList);
                    attrList = IntPtr.Zero;
                    throw Failure("InitializeProcThreadAttributeList");
                }
                handleArray = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(handleArray, 0, childStdin);
                Marshal.WriteIntPtr(handleArray, IntPtr.Size, childOut);
                if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeHandleList, handleArray,
                        new IntPtr(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero))
                    throw Failure("UpdateProcThreadAttribute");

                var si = new StartupInfoEx { lpAttributeList = attrList };
                si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
                si.StartupInfo.dwFlags = StartfUseStdHandles;
                si.StartupInfo.hStdInput = childStdin;
                // THE merge: one handle for fd 1 and fd 2.
                si.StartupInfo.hStdOutput = childOut;
                si.StartupInfo.hStdError = childOut;

                uint flags = CreateSuspended | ExtendedStartupInfoPresent | CreateUnicodeEnvironment
                             | (psi.CreateNoWindow ? CreateNoWindowFlag : 0);
                string? cwd = string.IsNullOrEmpty(psi.WorkingDirectory) ? null : psi.WorkingDirectory;
                // Suspended until we hold a Process for it: a child that exits at once must still be observable.
                if (!CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, true, flags,
                        EnvironmentBlock(psi), cwd, ref si, out pi))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                        $"An error occurred trying to start process '{psi.FileName}'");

                Process proc;
                try
                {
                    proc = Process.GetProcessById(pi.dwProcessId);
                    _ = proc.Handle; // our own handle, opened while pi.hProcess pins the process object
                }
                catch
                {
                    TerminateProcess(pi.hProcess, 1);
                    throw;
                }
                ResumeThread(pi.hThread);

                // The child holds its ends now; drop ours so EOF arrives when it (and its descendants) close them.
                output.DisposeLocalCopyOfClientHandle();
                input?.DisposeLocalCopyOfClientHandle();
                var stdinWriter = input is null ? null : new StreamWriter(input, PsBash.Core.RawBytes.Encoding) { AutoFlush = true };
                var reader = new StreamReader(output, outEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
                ok = true;
                return new SpawnedChild(proc, stdinWriter, reader, null);
            }
            finally
            {
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
                if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
                if (stdinDup != IntPtr.Zero) CloseHandle(stdinDup);
                if (!ok)
                {
                    output.Dispose();
                    input?.Dispose();
                }
            }
        }

        /// <summary>An inheritable duplicate of the process stdin (NUL when there is none), for the child to inherit.</summary>
        private static IntPtr InheritableStdin()
        {
            var current = GetCurrentProcess();
            IntPtr std = GetStdHandle(StdInputHandle);
            if (std != IntPtr.Zero && std != InvalidHandle
                && DuplicateHandle(current, std, current, out var dup, 0, true, DuplicateSameAccess))
                return dup;
            var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
            var nul = CreateFileW("NUL", GenericRead, 0x3, ref sa, OpenExisting, 0, IntPtr.Zero);
            if (nul == InvalidHandle) throw Failure("open NUL");
            return nul;
        }

        /// <summary>The Unicode environment block CreateProcessW takes: sorted name=value pairs, NUL-separated.</summary>
        private static StringBuilder EnvironmentBlock(ProcessStartInfo psi)
        {
            var keys = new List<string>(psi.Environment.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (var k in keys)
            {
                var v = psi.Environment[k];
                if (v is null) continue;
                sb.Append(k).Append('=').Append(v).Append('\0');
            }
            sb.Append('\0');
            return sb;
        }

        private static System.ComponentModel.Win32Exception Failure(string what)
            => new(Marshal.GetLastWin32Error(), what + " failed");

        private static readonly IntPtr InvalidHandle = new(-1);
        private const int StdInputHandle = -10;
        private const uint GenericRead = 0x80000000, OpenExisting = 3, DuplicateSameAccess = 0x2;
        private const int StartfUseStdHandles = 0x100;
        private const uint CreateSuspended = 0x4, CreateUnicodeEnvironment = 0x400, CreateNoWindowFlag = 0x08000000,
            ExtendedStartupInfoPresent = 0x80000;
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
            StringBuilder env, string? cwd, ref StartupInfoEx si, out ProcessInformation pi);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int which);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
            uint access, bool inherit, uint options);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);
    }

    /// <summary>CommandLineToArgvW-compatible quoting (the rules ProcessStartInfo.ArgumentList applies).</summary>
    internal static void AppendQuoted(StringBuilder sb, string arg)
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
