using System.Runtime.InteropServices;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// AOT-safe parent-process-id lookup, used by <see cref="IpcTransportFactory"/> to
/// derive a per-session daemon endpoint. The parent of a one-shot <c>ps-bash -c</c>
/// launcher is the process that invoked it (an interactive shell, a script, or an
/// agent runner); repeated invocations from the same parent therefore resolve to
/// the same endpoint (warm-pool reuse) while independent shells/agents get distinct
/// endpoints (load spreads, no cross-session contention).
/// </summary>
/// <remarks>
/// <para>Both platform paths use <c>[LibraryImport]</c> (source-generated,
/// trim/AOT-safe — the same pattern as <c>Pty/UnixPtyAdapter</c> and
/// <c>Pty/TerminalMode</c>). On failure the lookup returns <c>null</c> and the
/// caller falls back to the per-user canonical endpoint (today's behavior), so a
/// stat/syscall failure degrades to the old shared daemon rather than breaking
/// resolution.</para>
/// </remarks>
internal static partial class ProcessAncestry
{
    /// <summary>
    /// The parent process id of the current process, or <c>null</c> if it cannot be
    /// determined. Never throws.
    /// </summary>
    public static int? GetParentProcessId()
    {
        try
        {
            return OperatingSystem.IsWindows() ? GetParentWindows() : GetParentPosix();
        }
        catch
        {
            return null;
        }
    }

    private static int? GetParentPosix()
    {
        var ppid = getppid();
        return ppid > 0 ? ppid : null;
    }

    private static int? GetParentWindows()
    {
        // PROCESS_BASIC_INFORMATION.InheritedFromUniqueProcessId holds the parent PID.
        // (IntPtr)(-1) is the current-process pseudo-handle, so no OpenProcess /
        // handle lifetime to manage.
        var pbi = default(ProcessBasicInformation);
        int status = NtQueryInformationProcess(
            (IntPtr)(-1), ProcessBasicInformationClass, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0) return null;
        long parent = (long)pbi.InheritedFromUniqueProcessId;
        return parent > 0 && parent <= int.MaxValue ? (int)parent : null;
    }

    private const int ProcessBasicInformationClass = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public UIntPtr UniqueProcessId;
        public UIntPtr InheritedFromUniqueProcessId;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [LibraryImport("libc")]
    private static partial int getppid();
}
