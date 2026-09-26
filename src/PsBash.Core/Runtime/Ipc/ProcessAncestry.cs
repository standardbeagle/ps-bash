using System.Runtime.InteropServices;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// AOT-safe process-ancestry lookup, used by <see cref="IpcTransportFactory"/> to
/// derive the automatic per-session daemon key. The session anchor is the nearest
/// <b>stable</b> shell/agent ancestor of a one-shot <c>ps-bash -c</c> launcher —
/// not its immediate parent, which may be a fresh short-lived shim (the Claude
/// Code Bash tool spawns a new parent per call). Repeated invocations from one
/// logical session therefore climb to the same anchor → same endpoint → warm-pool
/// reuse; independent shells/agents resolve distinct anchors (load spreads, no
/// cross-session contention). The anchor's process start time is folded into the
/// key so a recycled PID cannot attach an unrelated invocation to a stale daemon.
/// </summary>
/// <remarks>
/// <para>All platform paths use <c>[LibraryImport]</c> (source-generated,
/// trim/AOT-safe — the same pattern as <c>Pty/UnixPtyAdapter</c> and
/// <c>Pty/TerminalMode</c>). On failure a lookup returns <c>null</c> and the
/// caller falls back to the per-user canonical endpoint, so a stat/syscall
/// failure degrades to the old shared daemon rather than breaking resolution.</para>
/// </remarks>
internal static partial class ProcessAncestry
{
    /// <summary>
    /// A process's pid paired with its start time (UTC ticks). The start time makes
    /// the identity unique over time: two different processes that reuse one PID
    /// still get distinct identities.
    /// </summary>
    internal readonly record struct ProcessIdentity(int Pid, long StartTimeUtcTicks);

    /// <summary>How far up the ancestry chain the anchor walk may climb.</summary>
    private const int MaxAncestorDepth = 10;

    /// <summary>
    /// Executable names (without extension, case-insensitive) that identify a
    /// session anchor: interactive shells and agent runtimes that persist for the
    /// whole logical session. A launcher whose immediate parent is one of these
    /// uses that parent; otherwise the walk climbs past transient shims.
    /// </summary>
    private static readonly HashSet<string> SessionHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "dash", "zsh", "ksh", "fish",
        "pwsh", "powershell", "cmd",
        "node", "deno", "bun",
        "python", "python3", "pythonw",
        "opencode", "claude", "claude-code",
    };

    /// <summary>
    /// Executable names that terminate the anchor walk: shared service/build hosts
    /// that are not per-session owners. Recognizing these prevents the walk from
    /// collapsing every session onto a machine-wide process (e.g. the .NET build
    /// server that launched an agent).
    /// </summary>
    private static readonly HashSet<string> SharedHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "java", "explorer", "services", "svchost", "launchd", "systemd", "init",
    };

    /// <summary>
    /// Resolve the stable session anchor for the current process, or <c>null</c>
    /// when no ancestor is reachable (caller then uses the per-user endpoint).
    /// </summary>
    public static ProcessIdentity? FindSessionAnchor()
        => SelectSessionAnchor(Environment.ProcessId, GetParentProcessIdByPid, TryGetProcessInfo);

    /// <summary>
    /// Pure anchor-selection algorithm (test seam: synthetic ancestry chains).
    /// Climbs from the launcher's parent while ancestors are recognized session
    /// hosts, stopping at a shared host (use the last recognized ancestor) or when
    /// the chain ends (use the immediate parent). Returns the immediate parent when
    /// nothing is recognized, so distinct sessions still diverge.
    /// </summary>
    internal static ProcessIdentity? SelectSessionAnchor(
        int launcherPid,
        Func<int, int?> parentOfPid,
        Func<int, (string? Name, long StartTicks)?> infoOfPid)
    {
        var immediate = parentOfPid(launcherPid);
        if (immediate is null or <= 0) return null;

        ProcessIdentity? anchor = IdentityOf(immediate.Value, infoOfPid);
        if (anchor is null) return null;

        var currentPid = immediate.Value;
        string? currentName = infoOfPid(currentPid)?.Name;
        for (int depth = 0; depth < MaxAncestorDepth; depth++)
        {
            if (currentName is null || !SessionHostNames.Contains(StripExe(currentName)))
                break;

            var parentPid = parentOfPid(currentPid);
            if (parentPid is null or <= 0) break;

            var parentInfo = infoOfPid(parentPid.Value);
            if (parentInfo is null) break;

            var parentName = StripExe(parentInfo.Value.Name ?? "");
            // A shared host is the session boundary: keep the last recognized
            // session host (the current one), do not climb into the shared host.
            if (SharedHostNames.Contains(parentName)) break;
            // A non-recognized ancestor is not a session host either — stop and keep
            // the last recognized one.
            if (!SessionHostNames.Contains(parentName)) break;

            var parentIdentity = IdentityOf(parentPid.Value, infoOfPid);
            if (parentIdentity is null) break;

            anchor = parentIdentity;
            currentPid = parentPid.Value;
            currentName = parentInfo.Value.Name;
        }

        return anchor;
    }

    private static ProcessIdentity? IdentityOf(
        int pid, Func<int, (string? Name, long StartTicks)?> infoOfPid)
    {
        var info = infoOfPid(pid);
        return info is null ? null : new ProcessIdentity(pid, info.Value.StartTicks);
    }

    private static string StripExe(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>
    /// Real ancestry lookup: Windows via <c>NtQueryInformationProcess</c> on the
    /// target's handle; Linux via <c>/proc/{pid}/stat</c>. Returns <c>null</c> when
    /// unavailable (macOS without /proc, no permission, process gone).
    /// </summary>
    private static int? GetParentProcessIdByPid(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                var parent = GetParentViaNt(p.Handle);
                return parent is > 0 ? parent : null;
            }
            if (OperatingSystem.IsLinux())
                return ParseProcStatPpid(ReadProcStat(pid));
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Real process info lookup: name (without extension) and start time in UTC
    /// ticks. Returns <c>null</c> when the process is gone or unreadable.
    /// </summary>
    private static (string? Name, long StartTicks)? TryGetProcessInfo(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return (p.ProcessName, p.StartTime.ToUniversalTime().Ticks);
        }
        catch { return null; }
    }

    private static int? ParseProcStatPpid(string statText)
    {
        // Field 2 (comm) is parenthesized and may itself contain spaces/parens;
        // locate the LAST ')' then read state and ppid after it.
        int lastParen = statText.LastIndexOf(')');
        if (lastParen < 0 || lastParen + 2 >= statText.Length) return null;
        var fields = statText[(lastParen + 2)..].Split(' ');
        return fields.Length >= 2 && int.TryParse(fields[1], out var ppid) && ppid > 0 ? ppid : null;
    }

    private static string ReadProcStat(int pid)
    {
        using var stream = new FileStream(
            $"/proc/{pid}/stat", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> buffer = stackalloc byte[4096];
        int read = stream.Read(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer[..read]);
    }

    private static int? GetParentViaNt(IntPtr handle)
    {
        var pbi = default(ProcessBasicInformation);
        int status = NtQueryInformationProcess(
            handle, ProcessBasicInformationClass, ref pbi,
            Marshal.SizeOf<ProcessBasicInformation>(), out _);
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
}
