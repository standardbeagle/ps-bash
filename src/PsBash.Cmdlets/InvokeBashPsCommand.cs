using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Runtime.InteropServices;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashPs</c> function
/// (REFACTOR-2 follow-on). Lists running processes, matching the GNU
/// coreutils / BSD <c>ps</c> surface as the psm1 oracle implemented it.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashPs</c>. Flag
/// surface preserved byte-for-byte:
/// <list type="bullet">
/// <item><c>aux</c> / <c>-aux</c> — BSD all-processes form.</item>
/// <item><c>-e</c> / <c>-A</c> — show all processes.</item>
/// <item><c>-f</c> — full-format output.</item>
/// <item><c>-u USER</c> — filter by user.</item>
/// <item><c>-p PID</c> — filter by single PID (oracle: single int, last wins).</item>
/// <item><c>--sort COL</c> / <c>--sort=-COL</c> — sort with optional descending.</item>
/// <item><c>-o COL,COL,...</c> — custom output columns.</item>
/// </list>
///
/// Cross-platform process enumeration:
/// <list type="bullet">
/// <item>Linux: walk <c>/proc/[pid]</c> directly (oracle: <c>Get-LinuxProcEntry</c>).</item>
/// <item>Windows / macOS: <c>System.Diagnostics.Process.GetProcesses()</c> plus
/// platform-specific batch metadata lookup (<c>Win32_Process</c> CIM on Windows,
/// <c>/bin/ps</c> on macOS) — same shape the oracle's
/// <c>Get-DotNetProcEntry</c> assembled.</item>
/// </list>
///
/// Output: typed <c>PsBash.PsEntry</c> PSObjects with the same property set the
/// oracle emitted (PID, PPID, User, CPU, Memory, MemoryMB, VSZ, RSS, TTY, Stat,
/// Start, Time, Command, CommandLine, ProcessName, WorkingSet, BashText).
/// BashText carries the format-mode rendered line (aux / default / custom).
///
/// Common-parameter collisions (declared as explicit params per the playbook
/// table — exact param-name match beats common-parameter prefix-match under
/// the PSCmdlet binder):
/// <list type="bullet">
/// <item><c>-e</c> — prefix-collides with <c>-ErrorAction</c> /
/// <c>-ErrorVariable</c>. Declared as <see cref="SwitchParameter"/>
/// <see cref="E"/>.</item>
/// <item><c>-A</c> — bare token <c>-A</c> case-folds to <c>-a</c> under the
/// case-insensitive binder, prefix-matching the catch-all
/// <see cref="Arguments"/>. Declared as <see cref="SwitchParameter"/>
/// <see cref="A"/>.</item>
/// <item><c>-p</c> — prefix-collides with <c>-PipelineVariable</c> /
/// <c>-ProgressAction</c>. Declared as nullable string <see cref="P"/>
/// (the oracle takes a single PID value).</item>
/// <item><c>-o</c> — prefix-collides with <c>-OutVariable</c> /
/// <c>-OutBuffer</c>. Declared as nullable string <see cref="O"/>.</item>
/// <item><c>-f</c> / <c>-u</c> / <c>--sort</c> / <c>aux</c> — no PowerShell
/// common-parameter prefix collision; stay in <see cref="Arguments"/> and are
/// decoded by the manual scan.</item>
/// </list>
///
/// Directive 12 (injection probe): the <c>-p</c> value is parsed via
/// <see cref="int.TryParse(string, out int)"/> — a non-integer (e.g.
/// <c>$(throw 'pwn')</c> arriving as a literal string) silently falls through
/// to "no filter match" with no output and no exception. No user-controlled
/// token is concatenated into a script body.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPs")]
[OutputType("PsBash.PsEntry")]
public sealed class InvokeBashPsCommand : PSCmdlet
{
    [Parameter] public SwitchParameter E { get; set; }
    [Parameter] public SwitchParameter A { get; set; }
    [Parameter] public string? P { get; set; }
    [Parameter] public string? O { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private static long s_totalMemBytes = 0;

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "ps"))
            {
                WriteObject(line);
            }
            return;
        }

        bool showAll = E.IsPresent || A.IsPresent;
        bool bsdAux = false;
        bool fullFormat = false;
        string? filterUser = null;
        int? filterPid = null;
        string? sortKey = null;
        bool sortDescending = false;
        string? customFormat = O;

        // Pre-bind value flags from explicit parameters.
        if (!string.IsNullOrEmpty(P) && int.TryParse(P, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var ppid))
        {
            filterPid = ppid;
        }

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "aux", StringComparison.Ordinal) ||
                string.Equals(arg, "-aux", StringComparison.Ordinal))
            {
                bsdAux = true;
                showAll = true;
                continue;
            }
            if (string.Equals(arg, "-e", StringComparison.Ordinal) ||
                string.Equals(arg, "-A", StringComparison.Ordinal))
            {
                showAll = true;
                continue;
            }
            if (string.Equals(arg, "-f", StringComparison.Ordinal))
            {
                fullFormat = true;
                continue;
            }
            if (string.Equals(arg, "-u", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                filterUser = args[++i];
                continue;
            }
            if (string.Equals(arg, "-p", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                var pv = args[++i];
                if (int.TryParse(pv, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var parsedPid))
                {
                    filterPid = parsedPid;
                }
                // else: silently drop (Directive 12 — non-integer falls through).
                continue;
            }
            if (string.Equals(arg, "-o", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                customFormat = args[++i];
                continue;
            }
            if (arg.StartsWith("--sort=", StringComparison.Ordinal))
            {
                var sk = arg.Substring(7);
                if (sk.StartsWith("-", StringComparison.Ordinal))
                {
                    sortDescending = true;
                    sk = sk.Substring(1);
                }
                sortKey = sk;
                continue;
            }
            if (string.Equals(arg, "--sort", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                var sk = args[++i];
                if (sk.StartsWith("-", StringComparison.Ordinal))
                {
                    sortDescending = true;
                    sk = sk.Substring(1);
                }
                sortKey = sk;
                continue;
            }
            // Unknown / unsupported flags: oracle silently drops them.
        }

        // Gather entries
        var entries = new List<PsEntry>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string? currentUser = TryRun("/usr/bin/id", "-un");
            string[] procDirs;
            try { procDirs = Directory.GetDirectories("/proc"); }
            catch { procDirs = Array.Empty<string>(); }

            foreach (var dir in procDirs)
            {
                var dirName = Path.GetFileName(dir);
                if (!int.TryParse(dirName, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var ddpid))
                    continue;
                if (filterPid.HasValue && ddpid != filterPid.Value) continue;

                var entry = GetLinuxProcEntry(dir, ddpid);
                if (entry == null) continue;

                if (!showAll && !bsdAux && !filterPid.HasValue && filterUser == null)
                {
                    if (fullFormat || customFormat != null)
                    {
                        if (entry.User != currentUser) continue;
                    }
                    else
                    {
                        if (entry.User != currentUser || entry.TTY == "?") continue;
                    }
                }
                if (filterUser != null && entry.User != filterUser) continue;
                entries.Add(entry);
            }
        }
        else
        {
            Process[] procs;
            try
            {
                procs = filterPid.HasValue
                    ? new[] { Process.GetProcessById(filterPid.Value) }
                    : Process.GetProcesses();
            }
            catch
            {
                procs = Array.Empty<Process>();
            }

            // Windows batch metadata. GetOwner() on each Win32_Process is the
            // slow part (per-process WMI RPC roundtrip — 5s+ for ~200 procs).
            // Skip the owner lookup unless the requested format actually
            // needs a user column: `aux`, `-f` (full), or a custom -o spec
            // mentioning user/ruser/euser.
            Dictionary<int, (string CommandLine, string User, int PPID)>? winLookup = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && procs.Length > 0)
            {
                bool needUser = bsdAux || fullFormat ||
                    (customFormat != null &&
                     (customFormat.Contains("user", StringComparison.OrdinalIgnoreCase) ||
                      customFormat.Contains("ruser", StringComparison.OrdinalIgnoreCase) ||
                      customFormat.Contains("euser", StringComparison.OrdinalIgnoreCase))) ||
                    filterUser != null;
                winLookup = BuildWindowsCimLookup(needUser);
            }
            // macOS batch metadata
            Dictionary<int, (string User, int PPID, string TTY)>? macLookup = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && procs.Length > 0)
            {
                macLookup = BuildMacPsLookup();
            }

            EnsureTotalMemBytes();
            string? currentUser = null;

            foreach (var p in procs)
            {
                PsEntry? entry;
                try
                {
                    entry = GetDotNetProcEntry(p, winLookup, macLookup);
                }
                catch
                {
                    continue;
                }
                if (entry == null) continue;

                if (!showAll && !bsdAux && !filterPid.HasValue && filterUser == null)
                {
                    if (currentUser == null)
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            currentUser = Environment.UserName;
                        }
                        else
                        {
                            currentUser = TryRun("/usr/bin/id", "-un");
                        }
                    }
                    if (!string.IsNullOrEmpty(currentUser) && entry.User != currentUser) continue;
                }
                if (filterUser != null && entry.User != filterUser) continue;
                entries.Add(entry);
            }
        }

        // Sort
        if (sortKey != null)
        {
            string propName = sortKey.ToLowerInvariant() switch
            {
                "pid" => "PID",
                "ppid" => "PPID",
                "cpu" or "%cpu" => "CPU",
                "mem" or "%mem" => "Memory",
                "rss" => "RSS",
                "vsz" => "VSZ",
                "user" => "User",
                "comm" => "ProcessName",
                "time" => "Time",
                _ => "PID",
            };
            IEnumerable<PsEntry> sorted = propName switch
            {
                "PID" => sortDescending ? entries.OrderByDescending(e => e.PID) : entries.OrderBy(e => e.PID),
                "PPID" => sortDescending ? entries.OrderByDescending(e => e.PPID) : entries.OrderBy(e => e.PPID),
                "CPU" => sortDescending ? entries.OrderByDescending(e => e.CPU) : entries.OrderBy(e => e.CPU),
                "Memory" => sortDescending ? entries.OrderByDescending(e => e.Memory) : entries.OrderBy(e => e.Memory),
                "RSS" => sortDescending ? entries.OrderByDescending(e => e.RSS) : entries.OrderBy(e => e.RSS),
                "VSZ" => sortDescending ? entries.OrderByDescending(e => e.VSZ) : entries.OrderBy(e => e.VSZ),
                "User" => sortDescending ? entries.OrderByDescending(e => e.User, StringComparer.Ordinal) : entries.OrderBy(e => e.User, StringComparer.Ordinal),
                "ProcessName" => sortDescending ? entries.OrderByDescending(e => e.ProcessName, StringComparer.Ordinal) : entries.OrderBy(e => e.ProcessName, StringComparer.Ordinal),
                "Time" => sortDescending ? entries.OrderByDescending(e => e.Time, StringComparer.Ordinal) : entries.OrderBy(e => e.Time, StringComparer.Ordinal),
                _ => entries,
            };
            entries = sorted.ToList();
        }

        string[]? columns = null;
        if (customFormat != null)
        {
            columns = customFormat.Split(',');
        }

        foreach (var entry in entries)
        {
            string bashText;
            if (columns != null)
            {
                bashText = FormatPsCustomLine(entry, columns);
            }
            else if (bsdAux || fullFormat)
            {
                bashText = FormatPsAuxLine(entry);
            }
            else
            {
                bashText = string.Format(CultureInfo.InvariantCulture,
                    "{0,7} {1,-7} {2,8} {3}", entry.PID, entry.TTY, entry.Time, entry.Command);
            }

            var pso = new PSObject();
            pso.TypeNames.Insert(0, "PsBash.PsEntry");
            pso.Properties.Add(new PSNoteProperty("PID", entry.PID));
            pso.Properties.Add(new PSNoteProperty("PPID", entry.PPID));
            pso.Properties.Add(new PSNoteProperty("User", entry.User));
            pso.Properties.Add(new PSNoteProperty("CPU", entry.CPU));
            pso.Properties.Add(new PSNoteProperty("Memory", entry.Memory));
            pso.Properties.Add(new PSNoteProperty("MemoryMB", entry.MemoryMB));
            pso.Properties.Add(new PSNoteProperty("VSZ", entry.VSZ));
            pso.Properties.Add(new PSNoteProperty("RSS", entry.RSS));
            pso.Properties.Add(new PSNoteProperty("TTY", entry.TTY));
            pso.Properties.Add(new PSNoteProperty("Stat", entry.Stat));
            pso.Properties.Add(new PSNoteProperty("Start", entry.Start));
            pso.Properties.Add(new PSNoteProperty("Time", entry.Time));
            pso.Properties.Add(new PSNoteProperty("Command", entry.Command));
            pso.Properties.Add(new PSNoteProperty("CommandLine", entry.CommandLine));
            pso.Properties.Add(new PSNoteProperty("ProcessName", entry.ProcessName));
            pso.Properties.Add(new PSNoteProperty("WorkingSet", entry.WorkingSet));
            pso.Properties.Add(new PSNoteProperty("BashText", bashText));
            WriteObject(pso);
        }
    }

    // ----- Per-process entry model -----
    private sealed class PsEntry
    {
        public int PID;
        public int PPID;
        public string User = "";
        public double CPU;
        public double Memory;
        public double MemoryMB;
        public long VSZ;
        public long RSS;
        public string TTY = "?";
        public string Stat = "S";
        public DateTime Start = DateTime.Now;
        public string Time = "0:00";
        public string Command = "";
        public string CommandLine = "";
        public string ProcessName = "";
        public long WorkingSet;
    }

    private static PsEntry? GetLinuxProcEntry(string procDir, int pid)
    {
        string statPath = Path.Combine(procDir, "stat");
        string statRaw;
        try { statRaw = File.ReadAllText(statPath); }
        catch { return null; }

        // PID (comm) state PPID ... — comm can contain spaces and parens
        var m = System.Text.RegularExpressions.Regex.Match(statRaw,
            @"^\d+\s+\((.+)\)\s+(\S+)\s+(\d+)\s+(.*)$");
        if (!m.Success) return null;

        var comm = m.Groups[1].Value;
        var state = m.Groups[2].Value;
        var ppid = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var restFields = m.Groups[4].Value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);

        int ttyNr = restFields.Length > 2 && int.TryParse(restFields[2], out var t) ? t : 0;
        long utime = restFields.Length > 9 && long.TryParse(restFields[9], out var u) ? u : 0;
        long stime = restFields.Length > 10 && long.TryParse(restFields[10], out var s) ? s : 0;
        long starttime = restFields.Length > 17 && long.TryParse(restFields[17], out var st) ? st : 0;
        long vsize = restFields.Length > 18 && long.TryParse(restFields[18], out var v) ? v : 0;
        long rssPages = restFields.Length > 19 && long.TryParse(restFields[19], out var r) ? r : 0;

        int uid = 0;
        try
        {
            foreach (var line in File.ReadAllLines(Path.Combine(procDir, "status")))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var parts = line.Substring(4).Trim().Split((char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                        int.TryParse(parts[0], out uid);
                    break;
                }
            }
        }
        catch { }

        string userName = uid.ToString(CultureInfo.InvariantCulture);
        var getent = TryRun("/usr/bin/getent", $"passwd {uid}");
        if (!string.IsNullOrEmpty(getent))
        {
            var first = getent.Split(':')[0];
            if (!string.IsNullOrEmpty(first)) userName = first;
        }

        string cmdline = "";
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(procDir, "cmdline"));
            if (bytes.Length > 0)
            {
                cmdline = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0').Replace('\0', ' ');
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(cmdline)) cmdline = "[" + comm + "]";

        string tty = "?";
        if (ttyNr != 0)
        {
            int major = (ttyNr >> 8) & 0xFF;
            int minor = ttyNr & 0xFF;
            tty = major switch
            {
                136 => $"pts/{minor}",
                4 => $"tty{minor}",
                _ => $"{major}/{minor}",
            };
        }

        const int clkTck = 100;
        double totalCpuSec = (utime + stime) / (double)clkTck;
        long cpuMin = (long)Math.Floor(totalCpuSec / 60);
        int cpuSec = (int)(totalCpuSec % 60);
        string cpuTime = $"{cpuMin}:{cpuSec:D2}";

        DateTimeOffset bootTime = DateTimeOffset.UtcNow;
        try
        {
            var uptimeStr = File.ReadAllText("/proc/uptime").Trim().Split(' ')[0];
            if (double.TryParse(uptimeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var uptimeSec))
            {
                bootTime = DateTimeOffset.UtcNow.AddSeconds(-uptimeSec);
            }
        }
        catch { }
        DateTime startDate = bootTime.AddSeconds(starttime / (double)clkTck).LocalDateTime;

        const int pageSize = 4096;
        long rssKB = rssPages * pageSize / 1024;
        long vszKB = vsize / 1024;

        long totalMemKB = 1;
        try
        {
            foreach (var ml in File.ReadAllLines("/proc/meminfo"))
            {
                if (ml.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    var num = new string(ml.Where(char.IsDigit).ToArray());
                    if (long.TryParse(num, out var tm)) totalMemKB = tm;
                    break;
                }
            }
        }
        catch { }
        double memPct = totalMemKB > 0
            ? Math.Round(rssKB / (double)totalMemKB * 100.0, 1) : 0.0;

        return new PsEntry
        {
            PID = pid,
            PPID = ppid,
            User = userName,
            CPU = 0.0,
            Memory = memPct,
            MemoryMB = Math.Round(rssKB / 1024.0, 1),
            VSZ = vszKB,
            RSS = rssKB,
            TTY = tty,
            Stat = state,
            Start = startDate,
            Time = cpuTime,
            Command = cmdline,
            CommandLine = cmdline,
            ProcessName = comm,
            WorkingSet = rssKB * 1024,
        };
    }

    private void EnsureTotalMemBytes()
    {
        if (s_totalMemBytes > 0) return;
        s_totalMemBytes = 1;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var r in InvokeCommand.InvokeScript(
                    "(Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue).TotalVisibleMemorySize"))
                {
                    if (r?.BaseObject != null &&
                        long.TryParse(r.BaseObject.ToString(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var kb))
                    {
                        s_totalMemBytes = kb * 1024;
                    }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var sysctl = TryRun("/usr/sbin/sysctl", "-n hw.memsize");
                if (!string.IsNullOrEmpty(sysctl) &&
                    long.TryParse(sysctl, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var tb))
                {
                    s_totalMemBytes = tb;
                }
            }
        }
        catch { }
    }

    private Dictionary<int, (string CommandLine, string User, int PPID)>? BuildWindowsCimLookup(bool needUser)
    {
        var dict = new Dictionary<int, (string, string, int)>();
        try
        {
            // GetOwner() is a per-process WMI RPC roundtrip — orders of
            // magnitude slower than the rest of the query. Only ask for it
            // when the caller's format actually needs a user column.
            string ownerExpr = needUser
                ? "$u=''; try { $u = $_.GetOwner().User } catch {}; "
                : "$u=''; ";
            var results = InvokeCommand.InvokeScript(
                "Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | " +
                "ForEach-Object { " + ownerExpr +
                "[PSCustomObject]@{ ProcessId=[int]$_.ProcessId; CommandLine=$_.CommandLine; " +
                "User=$u; PPID=$(if ($_.ParentProcessId) { [int]$_.ParentProcessId } else { 0 }) } }");
            foreach (var r in results)
            {
                if (r == null) continue;
                var pidVal = r.Properties["ProcessId"]?.Value;
                if (pidVal == null) continue;
                int pid = Convert.ToInt32(pidVal, CultureInfo.InvariantCulture);
                string cl = r.Properties["CommandLine"]?.Value?.ToString() ?? "";
                string usr = r.Properties["User"]?.Value?.ToString() ?? "";
                int ppid = Convert.ToInt32(r.Properties["PPID"]?.Value ?? 0,
                    CultureInfo.InvariantCulture);
                dict[pid] = (cl, usr, ppid);
            }
        }
        catch { }
        return dict;
    }

    private static Dictionary<int, (string User, int PPID, string TTY)>? BuildMacPsLookup()
    {
        var dict = new Dictionary<int, (string, int, string)>();
        try
        {
            var output = TryRun("/bin/ps", "-axo pid=,user=,ppid=,tty=");
            if (string.IsNullOrEmpty(output)) return dict;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                var parts = trimmed.Split((char[]?)null,
                    4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                if (!int.TryParse(parts[0], out var pid)) continue;
                int.TryParse(parts[2], out var ppid);
                string tty = parts[3] == "??" ? "?" : parts[3];
                dict[pid] = (parts[1], ppid, tty);
            }
        }
        catch { }
        return dict;
    }

    private static PsEntry? GetDotNetProcEntry(
        Process p,
        Dictionary<int, (string CommandLine, string User, int PPID)>? winLookup,
        Dictionary<int, (string User, int PPID, string TTY)>? macLookup)
    {
        string procName;
        int pid;
        try { procName = p.ProcessName; pid = p.Id; }
        catch { return null; }

        long ws = 0;
        try { ws = p.WorkingSet64; } catch { }
        long rssKB = ws / 1024;
        long vszKB = 0;
        try { vszKB = p.VirtualMemorySize64 / 1024; } catch { }

        double memPct = 0.0;
        if (s_totalMemBytes > 0)
        {
            memPct = Math.Round(ws / (double)s_totalMemBytes * 100.0, 1);
        }

        DateTime startDate = DateTime.Now;
        try { startDate = p.StartTime; } catch { }

        string cpuTime = "0:00";
        try
        {
            var totalSec = p.TotalProcessorTime.TotalSeconds;
            long cpuMin = (long)Math.Floor(totalSec / 60);
            int cpuSec = (int)(totalSec % 60);
            cpuTime = $"{cpuMin}:{cpuSec:D2}";
        }
        catch { }

        int ppid = 0;
        string userName = "";
        string tty = "?";
        string cmdline = "";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (winLookup != null && winLookup.TryGetValue(pid, out var info))
            {
                cmdline = info.CommandLine ?? "";
                userName = info.User ?? "";
                ppid = info.PPID;
            }
            if (string.IsNullOrEmpty(userName))
            {
                try
                {
                    if (p.SessionId == Process.GetCurrentProcess().SessionId)
                        userName = Environment.UserName;
                }
                catch { }
            }
            try
            {
                if (p.SessionId > 0) tty = $"con{p.SessionId}";
            }
            catch { }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (macLookup != null && macLookup.TryGetValue(pid, out var info))
            {
                userName = info.User;
                ppid = info.PPID;
                tty = info.TTY;
            }
        }

        if (string.IsNullOrEmpty(cmdline)) cmdline = procName;
        if (string.IsNullOrEmpty(userName)) userName = "?";

        string statStr = "S";
        try
        {
            if (!p.Responding && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                statStr = "D";
            else if (p.Threads.Count > 1) statStr = "Sl";
        }
        catch { }

        return new PsEntry
        {
            PID = pid,
            PPID = ppid,
            User = userName,
            CPU = 0.0,
            Memory = memPct,
            MemoryMB = Math.Round(rssKB / 1024.0, 1),
            VSZ = vszKB,
            RSS = rssKB,
            TTY = tty,
            Stat = statStr,
            Start = startDate,
            Time = cpuTime,
            Command = cmdline,
            CommandLine = cmdline,
            ProcessName = procName,
            WorkingSet = ws,
        };
    }

    private static string FormatPsAuxLine(PsEntry e)
    {
        string startStr = e.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        return string.Format(CultureInfo.InvariantCulture,
            "{0,-8} {1,7} {2,4:F1} {3,4:F1} {4,7} {5,6} {6,-7} {7,-4} {8,5} {9,8} {10}",
            e.User, e.PID, e.CPU, e.Memory, e.VSZ, e.RSS, e.TTY, e.Stat, startStr, e.Time, e.Command);
    }

    private static string FormatPsCustomLine(PsEntry e, string[] columns)
    {
        var parts = new List<string>();
        foreach (var col in columns)
        {
            switch (col.Trim().ToLowerInvariant())
            {
                case "pid": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,7}", e.PID)); break;
                case "ppid": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,7}", e.PPID)); break;
                case "user": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,-8}", e.User)); break;
                case "%cpu":
                case "cpu": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,4:F1}", e.CPU)); break;
                case "%mem":
                case "mem": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,4:F1}", e.Memory)); break;
                case "vsz": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,7}", e.VSZ)); break;
                case "rss": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,6}", e.RSS)); break;
                case "tty": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,-7}", e.TTY)); break;
                case "stat": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,-4}", e.Stat)); break;
                case "start": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,5}",
                    e.Start.ToString("HH:mm", CultureInfo.InvariantCulture))); break;
                case "time": parts.Add(string.Format(CultureInfo.InvariantCulture, "{0,8}", e.Time)); break;
                case "command":
                case "cmd": parts.Add(e.Command); break;
                case "comm": parts.Add(e.ProcessName); break;
                case "args": parts.Add(e.CommandLine); break;
                default: parts.Add("?"); break;
            }
        }
        return string.Join(' ', parts);
    }

    private static string? TryRun(string fileName, string arguments)
    {
        try
        {
            if (!File.Exists(fileName)) return null;
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return output.TrimEnd('\r', '\n');
        }
        catch
        {
            return null;
        }
    }
}
