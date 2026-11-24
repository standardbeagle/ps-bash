using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PsBash.Core.Platform;

/// <summary>
/// The one place ps-bash asks the OS about ANOTHER process (or its own creation mask): parent pid, zombie state,
/// process-group members, real uid, umask. Every caller that used to parse <c>/proc</c> privately goes through here,
/// so each question has an answer on every shipped OS instead of a silent "unknown" off Linux.
/// <list type="bullet">
/// <item>Linux: <c>/proc/{pid}/stat</c> (state, ppid, pgrp) and <c>/proc/{pid}/status</c> (<c>Uid:</c>, <c>Umask:</c>).</item>
/// <item>macOS: libproc — <c>proc_pidinfo(PROC_PIDTBSDINFO)</c> fills a <c>struct proc_bsdinfo</c>
/// (<see cref="BsdInfoSize"/> bytes; layout in <see cref="TryDecodeBsdInfo"/>), <c>proc_listpids(PROC_ALL_PIDS)</c>
/// enumerates. There is no <c>/proc</c>.</item>
/// <item>Windows: <c>NtQueryInformationProcess(ProcessBasicInformation)</c> for the parent; zombies, process groups,
/// uids and a creation mask do not exist there.</item>
/// </list>
/// Every lookup is best effort: a process that is gone, unreadable or unknown answers <c>null</c> / <c>false</c> /
/// empty, never an exception. The byte decoders are public so they are tested on any OS with hand-built buffers.
/// Spec: <c>docs/specs/background-jobs.md</c> §6 (Process information).
/// </summary>
public static partial class ProcessInfo
{
    /// <summary>One <c>/proc/{pid}/stat</c> record: the state letter, parent pid and process group.</summary>
    public readonly record struct ProcStat(char State, int ParentPid, int GroupId)
    {
        /// <summary>State <c>Z</c>: exited, not yet reaped.</summary>
        public bool IsZombie => State == 'Z';
    }

    /// <summary>The fields ps-bash reads from a macOS <c>struct proc_bsdinfo</c>.</summary>
    public readonly record struct BsdInfo(uint Status, int Pid, int ParentPid, int GroupId, uint RealUid)
    {
        /// <summary><c>pbi_status == SZOMB</c>: exited, not yet reaped.</summary>
        public bool IsZombie => Status == SZOMB;
    }

    /// <summary><c>sizeof(struct proc_bsdinfo)</c> (XNU <c>bsd/sys/proc_info.h</c>; same on arm64 and x86_64).</summary>
    public const int BsdInfoSize = 136;

    // struct proc_bsdinfo field offsets (XNU bsd/sys/proc_info.h):
    //   uint32 pbi_flags @0, pbi_status @4, pbi_xstatus @8, pbi_pid @12, pbi_ppid @16,
    //   uid pbi_uid @20, gid pbi_gid @24, uid pbi_ruid @28, gid pbi_rgid @32, pbi_svuid @36, pbi_svgid @40, rfu_1 @44,
    //   char pbi_comm[16] @48, char pbi_name[32] @64, uint32 pbi_nfiles @96, pbi_pgid @100, pbi_pjobc @104,
    //   e_tdev @108, e_tpgid @112, int32 pbi_nice @116, uint64 pbi_start_tvsec @120, pbi_start_tvusec @128 -> 136.
    private const int OffStatus = 4, OffPid = 12, OffPpid = 16, OffRuid = 28, OffPgid = 100;

    /// <summary><c>p_stat</c> value of a zombie (XNU <c>bsd/sys/proc.h</c>: SIDL 1, SRUN 2, SSLEEP 3, SSTOP 4, SZOMB 5).</summary>
    public const uint SZOMB = 5;

    private const int PROC_PIDTBSDINFO = 3;
    private const uint PROC_ALL_PIDS = 1;
    private const int StatMaxBytes = 4096;
    private const int StatusMaxBytes = 64 * 1024;

    // ------------------------------------------------------------------ queries

    /// <summary>Parent pid of <paramref name="pid"/>, or null when it is gone, unreadable, or has no parent.</summary>
    public static int? ParentPid(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var p = Process.GetProcessById(pid);
                return ParentPidOfHandle(p.Handle);
            }
            if (OperatingSystem.IsLinux())
                return TryReadProcStat(pid, out var stat) && stat.ParentPid > 0 ? stat.ParentPid : null;
            if (OperatingSystem.IsMacOS())
                return TryReadBsdInfo(pid, out var info) && info.ParentPid > 0 ? info.ParentPid : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or NotSupportedException or EntryPointNotFoundException or DllNotFoundException) { }
        return null;
    }

    /// <summary>
    /// Linux: when <paramref name="pid"/> started, in UTC ticks, from the kernel's own record — <c>btime</c> of
    /// <c>/proc/stat</c> (boot, whole seconds since the epoch) plus <c>starttime</c> of <c>/proc/{pid}/stat</c> (clock
    /// ticks after boot, <c>sysconf(_SC_CLK_TCK)</c> a second). Null off Linux, or when the process is gone or unreadable.
    /// <para>
    /// .NET's <c>Process.StartTime</c> adds that same <c>starttime</c> to a boot time each process derives for itself
    /// from two clocks read a moment apart, so two processes reading ONE live process disagree by up to ~1 ms. A
    /// start time recorded in one process and checked in another (a daemon's session anchor) then never matched, and
    /// every Linux daemon took its live session for gone a second after it started. These inputs are the same numbers
    /// in every process, so the sum is too.
    /// </para>
    /// </summary>
    public static long? LinuxStartTimeUtcTicks(int pid)
    {
        if (!OperatingSystem.IsLinux() || pid <= 0) return null;
        try
        {
            if (ReadAscii($"/proc/{pid}/stat", StatMaxBytes) is not { } stat
                || !TryParseProcStartTime(System.Text.Encoding.ASCII.GetBytes(stat), out ulong startClockTicks)) return null;
            if (ReadAscii("/proc/stat", StatusMaxBytes) is not { } system || ParseBootTimeSeconds(system) is not { } btime)
                return null;
            long hz = sysconf(SC_CLK_TCK);
            return hz > 0 ? StartTimeUtcTicks(btime, startClockTicks, hz) : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    /// <summary><c>_SC_CLK_TCK</c> for <c>sysconf(3)</c> on Linux (glibc and musl <c>bits/confname.h</c>).</summary>
    private const int SC_CLK_TCK = 2;

    [LibraryImport("libc")]
    private static partial long sysconf(int name);

    /// <summary>
    /// Parent pid of THIS process, or null when unknown. On Windows it reads its own handle (no process lookup by id,
    /// which enumerates every process): the launcher asks once per command for <c>$PPID</c>.
    /// </summary>
    public static int? OwnParentPid()
    {
        if (!OperatingSystem.IsWindows()) return ParentPid(Environment.ProcessId);
        using var self = Process.GetCurrentProcess();
        return ParentPidOfHandle(self.Handle);
    }

    /// <summary>
    /// This process's groups, bash's <c>GROUPS</c> array: the primary group first, then each group <c>getgroups(2)</c>
    /// returns (bash keeps a repeat of the primary). Windows has no numeric gids: each group SID of the process token,
    /// as its last sub-authority (the numeric id ps-bash gives a SID, as for <c>$UID</c> and <c>id</c>). Empty when unknown.
    /// </summary>
    public static List<long> GroupIds()
    {
        var ids = new List<long>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (identity.Groups is { } groups)
                    foreach (var g in groups)
                        if (g is System.Security.Principal.SecurityIdentifier sid
                            && long.TryParse(sid.Value.AsSpan(sid.Value.LastIndexOf('-') + 1), out long rid))
                            ids.Add(rid);
                return ids;
            }
            ids.Add(getgid());
            int n = getgroups(0, null);
            if (n <= 0) return ids;
            var list = new uint[n];
            n = getgroups(n, list);
            for (int i = 0; i < n; i++) ids.Add(list[i]);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException
                                       or System.Security.SecurityException or UnauthorizedAccessException) { }
        return ids;
    }

    [LibraryImport("libc")]
    private static partial uint getgid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getgroups(int size, [Out] uint[]? list);

    /// <summary>Windows: parent pid of the process behind <paramref name="processHandle"/>; null when unavailable.</summary>
    public static int? ParentPidOfHandle(IntPtr processHandle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var pbi = default(ProcessBasicInformation);
            int status = NtQueryInformationProcess(processHandle, ProcessBasicInformationClass, ref pbi,
                Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status != 0) return null;
            long parent = (long)pbi.InheritedFromUniqueProcessId;
            return parent > 0 && parent <= int.MaxValue ? (int)parent : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    /// <summary>True when <paramref name="pid"/> has exited but is not reaped yet. False when it is alive, gone or unknown,
    /// and always on Windows (no zombies).</summary>
    public static bool IsZombie(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            if (OperatingSystem.IsLinux()) return TryReadProcStat(pid, out var stat) && stat.IsZombie;
            if (OperatingSystem.IsMacOS()) return TryReadBsdInfo(pid, out var info) && info.IsZombie;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
        return false;
    }

    /// <summary>
    /// Live members of process group <paramref name="groupId"/> (zombies excluded: they run nothing and cannot be
    /// signalled into doing anything). Empty on Windows, which has no process groups, and when enumeration fails.
    /// </summary>
    public static List<int> GroupMembers(int groupId)
    {
        var members = new List<int>();
        if (groupId <= 0) return members;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var pid in LinuxPids())
                    if (TryReadProcStat(pid, out var stat) && !stat.IsZombie && stat.GroupId == groupId) members.Add(pid);
            }
            else if (OperatingSystem.IsMacOS())
            {
                foreach (var pid in MacPids())
                    if (TryReadBsdInfo(pid, out var info) && !info.IsZombie && info.GroupId == groupId) members.Add(pid);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException) { }
        return members;
    }

    /// <summary>Real uid of <paramref name="pid"/> (Linux <c>/proc/{pid}/status</c> <c>Uid:</c>, macOS <c>pbi_ruid</c>);
    /// false when unknown or on Windows.</summary>
    public static bool TryGetRealUid(int pid, out uint uid)
    {
        uid = 0;
        if (pid <= 0) return false;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var text = ReadAscii($"/proc/{pid}/status", StatusMaxBytes);
                if (text is null || ParseStatusRealUid(text) is not { } u) return false;
                uid = u;
                return true;
            }
            if (OperatingSystem.IsMacOS() && TryReadBsdInfo(pid, out var info))
            {
                uid = info.RealUid;
                return true;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
        return false;
    }

    private static int s_umask = -1;

    /// <summary>
    /// This process's file-creation mask as inherited at start (Windows has none: the conventional 022). Read once:
    /// ps-bash never calls <c>umask(2)</c> (the <c>umask</c> builtin keeps the shell's mask itself), so it cannot change.
    /// Linux reads <c>/proc/self/status</c> <c>Umask:</c>. macOS has no <c>getumask</c>, and the
    /// <c>umask(0)</c>/<c>umask(old)</c> read would briefly apply the wrong mask to files other threads create, so it
    /// creates a probe directory with mode 0777 and reads back what the kernel's mask left.
    /// </summary>
    public static int ProcessUmask()
    {
        var cached = Volatile.Read(ref s_umask);
        if (cached >= 0) return cached;
        var value = ReadProcessUmask();
        Volatile.Write(ref s_umask, value);
        return value;
    }

    private static int ReadProcessUmask()
    {
        if (OperatingSystem.IsWindows()) return DefaultUmask;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (ReadAscii("/proc/self/status", StatusMaxBytes) is { } text && ParseStatusUmask(text) is { } mask) return mask;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return ProbeUmask() ?? DefaultUmask;
    }

    private const int DefaultUmask = 0x12; // 0022

    /// <summary>The creation mask as the kernel applies it: mkdir a probe directory with mode 0777 and read back what is
    /// left. Race-free (it changes no process state), unlike a <c>umask(0)</c>/<c>umask(old)</c> pair. Null on Windows
    /// or when the temp directory is unusable.</summary>
    internal static int? ProbeUmask()
    {
        if (OperatingSystem.IsWindows()) return null;
        var root = Path.Combine(HostTempPath.Get(), "ps-bash");
        var probe = Path.Combine(root, $"umask-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(probe, (UnixFileMode)0x1FF); // mkdir(probe, 0777): the kernel applies the umask
            return ~(int)File.GetUnixFileMode(probe) & 0x1FF;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        finally
        {
            try { Directory.Delete(probe); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------ pure decoders (tested on every OS)

    /// <summary>
    /// Decodes <c>/proc/{pid}/stat</c>: <c>pid (comm) state ppid pgrp ...</c>. <c>comm</c> may itself hold spaces and
    /// parentheses, so the fields are read after the LAST <c>')'</c>.
    /// </summary>
    public static bool TryParseProcStat(ReadOnlySpan<byte> stat, out ProcStat result)
    {
        result = default;
        int close = stat.LastIndexOf((byte)')');
        if (close < 0) return false;
        var rest = stat[(close + 1)..];
        if (!NextField(ref rest, out var state) || state.Length != 1) return false;
        if (!NextField(ref rest, out var ppidField) || !TryParseInt(ppidField, out int ppid)) return false;
        if (!NextField(ref rest, out var pgrpField) || !TryParseInt(pgrpField, out int pgrp)) return false;
        result = new ProcStat((char)state[0], ppid, pgrp);
        return true;
    }

    /// <summary>
    /// <c>starttime</c> of a <c>/proc/{pid}/stat</c> record: field 22 (clock ticks after boot), the 20th field after the
    /// LAST <c>')'</c> that closes <c>comm</c>.
    /// </summary>
    public static bool TryParseProcStartTime(ReadOnlySpan<byte> stat, out ulong startClockTicks)
    {
        startClockTicks = 0;
        int close = stat.LastIndexOf((byte)')');
        if (close < 0) return false;
        var rest = stat[(close + 1)..];
        ReadOnlySpan<byte> field = default;
        for (int i = 0; i < 20; i++)
            if (!NextField(ref rest, out field)) return false;
        if (field.Length > 20) return false;
        ulong v = 0;
        foreach (var b in field)
        {
            if (b < (byte)'0' || b > (byte)'9') return false;
            v = v * 10 + (ulong)(b - '0');
        }
        startClockTicks = v;
        return true;
    }

    /// <summary>The <c>btime</c> line of <c>/proc/stat</c>: boot time, whole seconds since the Unix epoch; null when absent.</summary>
    public static long? ParseBootTimeSeconds(string procStatText)
    {
        foreach (var line in procStatText.Split('\n'))
            if (line.StartsWith("btime ", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(6).Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out long seconds))
                return seconds;
        return null;
    }

    /// <summary>Boot time plus a start offset in clock ticks, as UTC <see cref="DateTime"/> ticks. Integer math only, so
    /// equal inputs give an equal result in every process.</summary>
    public static long StartTimeUtcTicks(long bootTimeSeconds, ulong startClockTicks, long clockTicksPerSecond)
        => DateTime.UnixEpoch.Ticks
           + bootTimeSeconds * TimeSpan.TicksPerSecond
           + (long)(startClockTicks / (ulong)clockTicksPerSecond) * TimeSpan.TicksPerSecond
           + (long)(startClockTicks % (ulong)clockTicksPerSecond) * TimeSpan.TicksPerSecond / clockTicksPerSecond;

    /// <summary>
    /// Decodes a macOS <c>struct proc_bsdinfo</c> as <c>proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, buf, 136)</c> fills it
    /// (native little-endian; both shipped Apple architectures are little-endian). False on a short buffer.
    /// </summary>
    public static bool TryDecodeBsdInfo(ReadOnlySpan<byte> buffer, out BsdInfo info)
    {
        info = default;
        if (buffer.Length < BsdInfoSize) return false;
        info = new BsdInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[OffStatus..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[OffPid..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[OffPpid..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[OffPgid..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[OffRuid..]));
        return true;
    }

    /// <summary>The pids in a <c>proc_listpids</c> result of <paramref name="bytesReturned"/> bytes (zero entries skipped).</summary>
    public static List<int> DecodePidList(ReadOnlySpan<int> buffer, int bytesReturned)
    {
        int count = Math.Clamp(bytesReturned / sizeof(int), 0, buffer.Length);
        var pids = new List<int>(count);
        foreach (var pid in buffer[..count])
            if (pid > 0) pids.Add(pid);
        return pids;
    }

    /// <summary>The <c>Umask:</c> line of <c>/proc/{pid}/status</c> (octal), or null when absent (kernels before 4.7).</summary>
    public static int? ParseStatusUmask(string statusText)
    {
        var value = StatusField(statusText, "Umask:");
        if (value is null || value.Length == 0) return null;
        int mask = 0;
        foreach (var c in value)
        {
            if (c < '0' || c > '7') return null;
            mask = mask * 8 + (c - '0');
            if (mask > 0xFFF) return null;
        }
        return mask & 0x1FF;
    }

    /// <summary>The real uid (first number) of the <c>Uid:</c> line of <c>/proc/{pid}/status</c>, or null.</summary>
    public static uint? ParseStatusRealUid(string statusText)
    {
        var value = StatusField(statusText, "Uid:");
        if (value is null) return null;
        int end = 0;
        while (end < value.Length && value[end] is not (' ' or '\t')) end++;
        return uint.TryParse(value.AsSpan(0, end), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var uid) ? uid : null;
    }

    private static string? StatusField(string statusText, string key)
    {
        foreach (var line in statusText.Split('\n'))
            if (line.StartsWith(key, StringComparison.Ordinal))
                return line[key.Length..].Trim();
        return null;
    }

    private static bool NextField(ref ReadOnlySpan<byte> rest, out ReadOnlySpan<byte> field)
    {
        int start = 0;
        while (start < rest.Length && rest[start] == (byte)' ') start++;
        int end = start;
        while (end < rest.Length && rest[end] is not ((byte)' ' or (byte)'\n')) end++;
        field = rest[start..end];
        rest = rest[end..];
        return field.Length > 0;
    }

    private static bool TryParseInt(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        bool negative = digits.Length > 0 && digits[0] == (byte)'-';
        if (negative) digits = digits[1..];
        if (digits.Length == 0 || digits.Length > 10) return false;
        long v = 0;
        foreach (var b in digits)
        {
            if (b < (byte)'0' || b > (byte)'9') return false;
            v = v * 10 + (b - '0');
        }
        if (v > int.MaxValue) return false;
        value = negative ? -(int)v : (int)v;
        return true;
    }

    // ------------------------------------------------------------------ Linux

    private static bool TryReadProcStat(int pid, out ProcStat stat)
    {
        stat = default;
        Span<byte> buffer = stackalloc byte[StatMaxBytes];
        int read;
        try
        {
            using var stream = new FileStream($"/proc/{pid}/stat", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } // gone while we looked
        return TryParseProcStat(buffer[..read], out stat);
    }

    private static IEnumerable<int> LinuxPids()
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(dir), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int pid) && pid > 0)
                yield return pid;
    }

    private static string? ReadAscii(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            var buffer = new byte[maxBytes];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return System.Text.Encoding.ASCII.GetString(buffer, 0, read); // field names and numbers are ASCII
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------------ macOS

    private static unsafe bool TryReadBsdInfo(int pid, out BsdInfo info)
    {
        info = default;
        Span<byte> buffer = stackalloc byte[BsdInfoSize];
        int got;
        fixed (byte* p = buffer) got = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, p, BsdInfoSize);
        return got == BsdInfoSize && TryDecodeBsdInfo(buffer, out info);
    }

    private static unsafe List<int> MacPids()
    {
        // proc_listpids returns BYTES: with no buffer, an estimate of the size needed; with one, the bytes filled.
        int estimate = proc_listpids(PROC_ALL_PIDS, 0, null, 0);
        int capacity = Math.Max(estimate / sizeof(int), 256) + 64;
        for (int attempt = 0; attempt < 4; attempt++, capacity *= 2)
        {
            var buffer = new int[capacity];
            int bytes;
            fixed (int* p = buffer) bytes = proc_listpids(PROC_ALL_PIDS, 0, p, capacity * sizeof(int));
            if (bytes <= 0) return new List<int>();
            if (bytes < capacity * sizeof(int)) return DecodePidList(buffer, bytes); // not full: nothing was cut off
        }
        return new List<int>();
    }

    // libproc: the same library name the .NET runtime itself binds for proc_pidinfo on macOS.
    [LibraryImport("libproc", SetLastError = true)]
    private static unsafe partial int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int buffersize);

    [LibraryImport("libproc", SetLastError = true)]
    private static unsafe partial int proc_listpids(uint type, uint typeinfo, int* buffer, int buffersize);

    // ------------------------------------------------------------------ Windows

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
        IntPtr processHandle, int processInformationClass, ref ProcessBasicInformation processInformation,
        int processInformationLength, out int returnLength);
}
