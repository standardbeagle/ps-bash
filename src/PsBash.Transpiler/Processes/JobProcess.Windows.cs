using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime.Processes;

public sealed unsafe partial class JobProcess
{
    // Windows state. Guarded by _gate; handles are closed exactly once in DisposeWindows.
    private IntPtr _hProcess;
    private IntPtr _hJob;
    private ManualResetEvent? _exitEvent;
    private RegisteredWaitHandle? _exitWait;

    [SupportedOSPlatform("windows")]
    private static JobProcess StartWindows(JobProcessStart spec)
    {
        string commandLine = spec.RawArguments is { Length: > 0 } raw
            ? WindowsCommandLine.Build(spec.FileName, []) + " " + raw
            : WindowsCommandLine.Build(spec.FileName, spec.Arguments);
        var cmdLine = Marshal.StringToHGlobalUni(commandLine);
        var envBlock = BuildEnvironmentBlock(spec.Environment, spec.EnvironmentIsComplete);
        var nulName = Marshal.StringToHGlobalUni("NUL");
        var cwd = string.IsNullOrEmpty(spec.WorkingDirectory) ? IntPtr.Zero : Marshal.StringToHGlobalUni(spec.WorkingDirectory);
        IntPtr nul = InvalidHandle, stdinDup = IntPtr.Zero, attrList = IntPtr.Zero, handleArray = IntPtr.Zero, job = IntPtr.Zero;
        var pi = default(ProcessInformation);
        bool started = false;
        try
        {
            var stdio = spec.Stdio ?? default;
            IntPtr NullDevice()
            {
                if (nul != InvalidHandle) return nul;
                var sa = new SecurityAttributes { nLength = sizeof(SecurityAttributes), bInheritHandle = 1 };
                nul = CreateFileW(nulName, GenericRead | GenericWrite, FileShareRead | FileShareWrite, &sa, OpenExisting, 0, IntPtr.Zero);
                if (nul == InvalidHandle) throw Failure("CreateFileW(NUL)");
                return nul;
            }

            IntPtr childIn;
            if (stdio.Stdin is { } givenIn) childIn = givenIn;
            else if (stdio.InheritStdin)
            {
                // An inheritable duplicate of this process's stdin; the null device when it has none.
                var current = GetCurrentProcess();
                IntPtr std = GetStdHandle(StdInputHandle);
                if (std != IntPtr.Zero && std != InvalidHandle
                    && DuplicateHandle(current, std, current, out stdinDup, 0, 1, DuplicateSameAccess) != 0)
                    childIn = stdinDup;
                else
                {
                    stdinDup = IntPtr.Zero;
                    childIn = NullDevice();
                }
            }
            else childIn = NullDevice();
            IntPtr childOut = stdio.Stdout ?? NullDevice();
            IntPtr childErr = stdio.Stderr ?? NullDevice();

            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, &size); // sizing call; fails by design
            attrList = Marshal.AllocHGlobal((nint)size);
            if (InitializeProcThreadAttributeList(attrList, 1, 0, &size) == 0)
            {
                Marshal.FreeHGlobal(attrList);
                attrList = IntPtr.Zero;
                throw Failure("InitializeProcThreadAttributeList");
            }
            // The handle list names each handle once (stdout and stderr may be one handle: 2>&1).
            handleArray = Marshal.AllocHGlobal(IntPtr.Size * 3);
            int handleCount = 0;
            Marshal.WriteIntPtr(handleArray, IntPtr.Size * handleCount++, childIn);
            if (childOut != childIn) Marshal.WriteIntPtr(handleArray, IntPtr.Size * handleCount++, childOut);
            if (childErr != childIn && childErr != childOut) Marshal.WriteIntPtr(handleArray, IntPtr.Size * handleCount++, childErr);
            if (UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeHandleList, handleArray, (nuint)(IntPtr.Size * handleCount), IntPtr.Zero, IntPtr.Zero) == 0)
                throw Failure("UpdateProcThreadAttribute(HANDLE_LIST)");

            var si = new StartupInfoEx { lpAttributeList = attrList };
            si.StartupInfo.cb = sizeof(StartupInfoEx);
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdInput = childIn;
            si.StartupInfo.hStdOutput = childOut;
            si.StartupInfo.hStdError = childErr;

            // Job first, so a failure to contain the process never leaves one running.
            job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
            if (job == IntPtr.Zero) throw Failure("CreateJobObjectW");
            var limits = default(JobObjectExtendedLimitInformation);
            // Nothing escapes, except - when allowed - a process that explicitly asks to (CREATE_BREAKAWAY_FROM_JOB).
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose | (spec.AllowBreakaway ? JobObjectLimitBreakawayOk : 0);
            if (SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, &limits, (uint)sizeof(JobObjectExtendedLimitInformation)) == 0)
                throw Failure("SetInformationJobObject");

            uint flags = ExtendedStartupInfoPresent | CreateSuspended | CreateUnicodeEnvironment
                | (spec.CreateNoWindow ? CreateNoWindow : 0)
                | (spec.NewConsoleProcessGroup ? CreateNewProcessGroup : 0);
            fixed (char* env = envBlock)
            {
                if (CreateProcessW(IntPtr.Zero, cmdLine, IntPtr.Zero, IntPtr.Zero, 1, flags, (IntPtr)env, cwd, &si, &pi) == 0)
                    throw Failure($"CreateProcessW('{spec.FileName}')");
            }
            started = true;

            // Contain, then run. A process assigned while suspended cannot have spawned anything yet.
            if (AssignProcessToJobObject(job, pi.hProcess) == 0)
            {
                var err = Failure("AssignProcessToJobObject");
                TerminateProcess(pi.hProcess, 1);
                throw err;
            }
            if (ResumeThread(pi.hThread) == uint.MaxValue)
            {
                var err = Failure("ResumeThread");
                TerminateJobObject(job, 1);
                throw err;
            }

            // Identity from the pinned handle: pi.hProcess keeps the pid from being recycled.
            var identity = ProcessIdentity.Of(pi.dwProcessId); // unreadable: the handle still reports its status

            var result = new JobProcess(pi.dwProcessId, identity) { _hProcess = pi.hProcess, _hJob = job };
            result._exitEvent = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(pi.hProcess, ownsHandle: false) };
            result._exitWait = ThreadPool.RegisterWaitForSingleObject(result._exitEvent,
                static (state, _) => ((JobProcess)state!).PollExit(), result, Timeout.Infinite, executeOnlyOnce: true);

            pi.hProcess = IntPtr.Zero; // ownership moved to the JobProcess
            job = IntPtr.Zero;
            return result;
        }
        catch when (!started || job != IntPtr.Zero || pi.hProcess != IntPtr.Zero)
        {
            // Failed after CreateProcess: make sure nothing is left running.
            if (pi.hProcess != IntPtr.Zero) TerminateProcess(pi.hProcess, 1);
            throw;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (job != IntPtr.Zero) CloseHandle(job); // KILL_ON_JOB_CLOSE reaps anything assigned
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (nul != InvalidHandle) CloseHandle(nul);
            if (stdinDup != IntPtr.Zero) CloseHandle(stdinDup);
            if (cwd != IntPtr.Zero) Marshal.FreeHGlobal(cwd);
            Marshal.FreeHGlobal(cmdLine);
            Marshal.FreeHGlobal(nulName);
        }
    }

    /// <summary>The leader's exit status, once its handle is signalled. Caller holds <c>_gate</c>.</summary>
    [SupportedOSPlatform("windows")]
    private bool TryReadExitCodeWindows(out int code)
    {
        code = 0;
        if (_hProcess == IntPtr.Zero || WaitForSingleObject(_hProcess, 0) != 0) return false;
        uint raw = 0;
        GetExitCodeProcess(_hProcess, &raw);
        code = unchecked((int)raw);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private bool SignalWindows(JobSignal signal)
    {
        if (_hJob == IntPtr.Zero) return false;
        // The job also holds any descendant that outlived the leader, so a tree kill is meaningful after exit.
        return TerminateJobObject(_hJob, signal == JobSignal.Kill ? 137u : 143u) != 0;
    }

    [SupportedOSPlatform("windows")]
    private bool SuspendWindows(bool suspend)
    {
        if (_hJob == IntPtr.Zero) return false;
        bool any = false;
        foreach (var pid in TreePidsWindows())
        {
            var h = OpenProcess(ProcessSuspendResume, 0, (uint)pid);
            if (h == IntPtr.Zero) continue;
            try { any |= (suspend ? NtSuspendProcess(h) : NtResumeProcess(h)) == 0; }
            finally { CloseHandle(h); }
        }
        return any;
    }

    [SupportedOSPlatform("windows")]
    private void ReleaseWindows()
    {
        if (_hJob == IntPtr.Zero) return;
        var limits = default(JobObjectExtendedLimitInformation); // flags 0: closing the handle no longer kills
        SetInformationJobObject(_hJob, JobObjectExtendedLimitInformationClass, &limits, (uint)sizeof(JobObjectExtendedLimitInformation));
    }

    [SupportedOSPlatform("windows")]
    private void DisposeWindows()
    {
        // Keep the status readable after the handles are gone.
        if (!_hasExitCode && TryReadExitCodeWindows(out int code)) PublishExit(code);
        _exitWait?.Unregister(null);
        _exitEvent?.Dispose();
        if (_hJob != IntPtr.Zero) { CloseHandle(_hJob); _hJob = IntPtr.Zero; }
        if (_hProcess != IntPtr.Zero) { CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
    }

    /// <summary>Pids currently in the job (leader and live descendants). Caller holds <c>_gate</c>.</summary>
    [SupportedOSPlatform("windows")]
    private IReadOnlyList<int> TreePidsWindows()
    {
        if (_hJob == IntPtr.Zero) return [];
        const int capacity = 1024;
        int bytes = 8 + capacity * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (QueryInformationJobObject(_hJob, JobObjectBasicProcessIdList, (void*)buffer, (uint)bytes, null) == 0) return [];
            int count = Marshal.ReadInt32(buffer, 4);
            var pids = new List<int>(count);
            for (int i = 0; i < count; i++) pids.Add((int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size));
            return pids;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Sorted case-insensitively, double-NUL terminated, as CreateProcess documents for CREATE_UNICODE_ENVIRONMENT.</summary>
    private static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string?>? overrides, bool complete)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!complete)
            foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
                if (e.Key is string k && e.Value is string v) merged[k] = v;
        if (overrides is not null)
            foreach (var (k, v) in overrides)
            {
                if (v is null) merged.Remove(k);
                else merged[k] = v;
            }
        var sb = new StringBuilder();
        foreach (var (k, v) in merged) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return sb.ToString().ToCharArray();
    }

    private static HostUnavailableException Failure(string call) => Failure(call, Marshal.GetLastPInvokeError());

    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const int StartfUseStdHandles = 0x100;
    private const uint CreateNoWindow = 0x08000000, ExtendedStartupInfoPresent = 0x00080000, CreateSuspended = 0x4,
        CreateNewProcessGroup = 0x200, CreateUnicodeEnvironment = 0x400;
    private const uint JobObjectLimitKillOnJobClose = 0x2000, JobObjectLimitBreakawayOk = 0x800;
    private const int StdInputHandle = -10;
    private const uint DuplicateSameAccess = 0x2;
    private const int JobObjectBasicProcessIdList = 3, JobObjectExtendedLimitInformationClass = 9;
    private const uint ProcessSuspendResume = 0x0800;
    private static readonly IntPtr ProcThreadAttributeHandleList = new(0x20002);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(IntPtr name, uint access, uint share, SecurityAttributes* sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int InitializeProcThreadAttributeList(IntPtr list, int count, int flags, nuint* size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int CreateProcessW(IntPtr app, IntPtr cmdLine, IntPtr pa, IntPtr ta, int inherit, uint flags, IntPtr env, IntPtr cwd, StartupInfoEx* si, ProcessInformation* pi);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attrs, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int SetInformationJobObject(IntPtr job, int infoClass, void* info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int QueryInformationJobObject(IntPtr job, int infoClass, void* info, uint size, uint* returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetExitCodeProcess(IntPtr process, uint* code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, int inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, int inherit, uint options);
    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr process);
}

/// <summary>
/// A kill-on-close Job Object around a process someone else started (<c>Process.Start</c>/<c>WindowsHostSpawn</c>):
/// closing it, or this process dying, ends the whole tree. For hosts that cannot be started through
/// <see cref="JobProcess"/> yet (the IPC worker's private hosts).
/// </summary>
public sealed unsafe class WindowsProcessTree : IDisposable
{
    private IntPtr _job;

    private WindowsProcessTree(IntPtr job) => _job = job;

    /// <summary>Contain <paramref name="process"/> (and what it later starts). Null when the OS refuses (already in a non-nestable job).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static WindowsProcessTree? TryContain(System.Diagnostics.Process process)
    {
        var job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (job == IntPtr.Zero) return null;
        var limits = default(ExtendedLimits);
        limits.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (SetInformationJobObject(job, 9, &limits, (uint)sizeof(ExtendedLimits)) == 0
            || AssignProcessToJobObject(job, process.Handle) == 0)
        {
            CloseHandle(job);
            return null;
        }
        return new WindowsProcessTree(job);
    }

    public void Kill()
    {
        var job = _job;
        if (job != IntPtr.Zero) TerminateJobObject(job, 137);
    }

    public void Dispose()
    {
        var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
        if (job != IntPtr.Zero) CloseHandle(job);
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION, flattened: only LimitFlags is set; the rest stays zero.
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong a, b, c, d, e, f;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attrs, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int SetInformationJobObject(IntPtr job, int infoClass, void* info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CloseHandle(IntPtr handle);
}
