using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PsBash.Shell;

/// <summary>
/// Windows Job Object watchdog. When ps-bash attaches itself to a kill-on-close Job
/// Object, the OS guarantees that every descendant process (e.g., the pwsh worker)
/// dies if ps-bash dies. Combined with a parent-PID poller, this gives us
/// Erlang-style "die when the parent dies" behavior on Windows without relying on
/// stdin-EOF detection alone.
///
/// No-op on non-Windows platforms (Linux/macOS use process groups and SIGHUP via
/// the shell, which already covers this case).
/// </summary>
internal static class JobObjectWatchdog
{
    // Must live for the process lifetime so the kernel handle is not GC'd and
    // the Job Object is not closed prematurely. Closing the handle triggers the
    // KILL_ON_JOB_CLOSE semantics immediately.
    private static IntPtr _jobHandle = IntPtr.Zero;
    private static Task? _parentWatcher;
    private static CancellationTokenSource? _parentWatcherCts;

    /// <summary>
    /// Attach the current process to a new Job Object configured with
    /// KILL_ON_JOB_CLOSE so all descendants die when ps-bash exits.
    /// Returns true if attached (Windows), false otherwise.
    /// </summary>
    public static bool AttachCurrentProcess()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (_jobHandle != IntPtr.Zero) return true; // already attached

        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return false;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags =
                JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE |
                JOB_OBJECT_LIMIT_BREAKAWAY_OK |
                JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;

            int len = Marshal.SizeOf(info);
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len))
                {
                    CloseHandle(job);
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }

            if (!AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
            {
                // If the current process is already in a job that disallows
                // nesting/breakaway, this can fail. Not fatal — we still benefit
                // from the parent-PID poller.
                CloseHandle(job);
                return false;
            }

            _jobHandle = job;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start a background poller (200 ms) that exits this process if the given
    /// parent PID disappears. Safe to call on any platform; no-op if parentPid
    /// is zero/negative or already watching.
    /// </summary>
    public static void StartParentDeathWatcher(int parentPid, int exitCode = 0)
    {
        if (parentPid <= 0) return;
        if (_parentWatcher is not null) return;

        _parentWatcherCts = new CancellationTokenSource();
        var token = _parentWatcherCts.Token;

        _parentWatcher = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var p = Process.GetProcessById(parentPid);
                    if (p.HasExited)
                    {
                        Environment.Exit(exitCode);
                    }
                }
                catch (ArgumentException)
                {
                    // Process no longer exists.
                    Environment.Exit(exitCode);
                }
                catch (InvalidOperationException)
                {
                    Environment.Exit(exitCode);
                }
                catch
                {
                    // transient query failure — keep polling
                }

                try { await Task.Delay(200, token); }
                catch (TaskCanceledException) { return; }
            }
        }, token);
    }

    /// <summary>
    /// Returns the parent process id of the current process on Windows,
    /// or 0 if unavailable.
    /// </summary>
    public static int GetCurrentParentProcessId()
        => GetParentProcessId(Process.GetCurrentProcess().Handle);

    /// <summary>
    /// Returns the parent process id of the process owning <paramref name="processHandle"/>,
    /// or 0 if unavailable. Windows-only.
    /// </summary>
    public static int GetParentProcessId(IntPtr processHandle)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(
                processHandle,
                0, // ProcessBasicInformation
                ref pbi,
                Marshal.SizeOf(pbi),
                out _);
            if (status != 0) return 0;
            return (int)pbi.InheritedFromUniqueProcessId.ToInt64();
        }
        catch { return 0; }
    }

    // ------------------------- P/Invoke -------------------------

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x0800;
    private const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x1000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);
}
