using System.Runtime.InteropServices;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Names the processes that hold a file open, via the Windows Restart Manager. Used only to make a sharing
/// violation ("being used by another process") actionable: on a CI runner an <c>rm -rf</c> that fails
/// for seconds is either an antivirus scan, the test harness, or a handle ps-bash itself never closed -
/// and the bare IOException message cannot tell them apart.
/// </summary>
internal static class WindowsFileLockers
{
    private const int MaxFiles = 64;

    /// <summary>
    /// "name (pid N), name (pid M)" for the processes holding <paramref name="path"/> - or, for a directory,
    /// any file under it - open; empty when none can be found or the API is unavailable (never throws).
    /// </summary>
    public static string Describe(string path)
    {
        if (!OperatingSystem.IsWindows()) return "";
        string holders = "";
        try { holders = DescribeCore(path); } catch { }
        string locked = "";
        try { locked = LockedEntries(path); } catch { }
        string cwds = "";
        try { cwds = ProcessesWithWorkingDirectoryUnder(path); } catch { }
        return string.Join("; ", new[] { holders, locked, cwds }.Where(s => s.Length > 0));
    }

    /// <summary>
    /// "name (pid N, cwd X)" for each OTHER process whose working directory is <paramref name="path"/> or inside it.
    /// A live process's cwd makes a directory undeletable on Windows yet is invisible to the Restart Manager.
    /// Reads each process's PEB (x64); a process we cannot open is skipped.
    /// </summary>
    private static string ProcessesWithWorkingDirectoryUnder(string path)
    {
        if (!Directory.Exists(path) || IntPtr.Size != 8) return "";
        var root = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var found = new List<string>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId || p.Id == 0 || p.Id == 4) continue;
                string? cwd = TryReadWorkingDirectory(p.Id);
                if (cwd is null) continue;
                var c = cwd.TrimEnd('\\') + "\\";
                if (c.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    string name; try { name = p.ProcessName; } catch { name = "?"; }
                    found.Add($"{name} (pid {p.Id}, cwd {cwd})");
                    if (found.Count >= 8) break;
                }
            }
        }
        return found.Count == 0 ? "" : "working directory of: " + string.Join(", ", found);
    }

    private static string? TryReadWorkingDirectory(int pid)
    {
        var h = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return null;
            var buf = new byte[8];
            // PEB.ProcessParameters @ +0x20; RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath (UNICODE_STRING) @ +0x38.
            if (!ReadProcessMemory(h, pbi.PebBaseAddress + 0x20, buf, 8, out _)) return null;
            var parameters = (IntPtr)BitConverter.ToInt64(buf, 0);
            var us = new byte[16];
            if (!ReadProcessMemory(h, parameters + 0x38, us, 16, out _)) return null;
            int length = BitConverter.ToUInt16(us, 0);
            var bufferPtr = (IntPtr)BitConverter.ToInt64(us, 8);
            if (length == 0 || length > 32000) return null;
            var chars = new byte[length];
            if (!ReadProcessMemory(h, bufferPtr, chars, length, out _)) return null;
            return Encoding.Unicode.GetString(chars);
        }
        finally { CloseHandle(h); }
    }

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    /// <summary>
    /// Which entries refuse an exclusive open (a handle without FILE_SHARE_* is open on them). The Restart Manager
    /// often cannot say who (access denied), and it never sees a process's working directory - so when a directory
    /// has NO locked file the likely holder is a directory handle or another process's cwd, and the message says so.
    /// </summary>
    private static string LockedEntries(string path)
    {
        if (File.Exists(path)) return IsLocked(path) ? "file is open elsewhere" : "";
        if (!Directory.Exists(path)) return "";
        var locked = new List<string>();
        int seen = 0;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            if (++seen > MaxFiles) break;
            if (IsLocked(f)) locked.Add(Path.GetRelativePath(path, f));
            if (locked.Count >= 5) break;
        }
        return locked.Count > 0
            ? "locked files: " + string.Join(", ", locked)
            : "no file is locked (a directory handle, or a process using it as its working directory)";
    }

    private static bool IsLocked(string file)
    {
        try { using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private const int ErrorMoreData = 234;
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// The Restart Manager refuses to enumerate holders when one of them is a process this caller may not open -
    /// in practice an antivirus scanner (Defender's MsMpEng) that opened the freshly written file. Worth saying:
    /// it separates "an external scanner holds it" from "ps-bash leaked a handle".
    /// </summary>
    internal const string ProtectedHolder = "a protected process (Restart Manager: access denied - typically antivirus scanning the file)";

    private static string DescribeCore(string path)
    {
        var files = new List<string>();
        if (Directory.Exists(path))
        {
            files.Add(path);
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                files.Add(f);
                if (files.Count >= MaxFiles) break;
            }
        }
        else if (File.Exists(path)) files.Add(path);
        if (files.Count == 0) return "";

        var key = new StringBuilder(33);
        if (RmStartSession(out var session, 0, key) != 0) return "";
        try
        {
            if (RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, null, 0, null) != 0) return "";
            uint needed = 0, count = 0, reasons;
            int rc = RmGetList(session, out needed, ref count, null, out reasons);
            if (rc == ErrorAccessDenied) return ProtectedHolder;
            if (rc != ErrorMoreData && rc != 0) return "";
            if (needed == 0) return "";
            var info = new RM_PROCESS_INFO[needed];
            count = needed;
            int rc2 = RmGetList(session, out needed, ref count, info, out reasons);
            if (rc2 == ErrorAccessDenied) return ProtectedHolder;
            if (rc2 != 0) return "";
            var names = new List<string>();
            for (int i = 0; i < count; i++)
                names.Add($"{info[i].strAppName} (pid {info[i].Process.dwProcessId})");
            return string.Join(", ", names);
        }
        finally { RmEndSession(session); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public uint dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint dwSessionHandle, uint nFiles, string[]? rgsFileNames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);
}
