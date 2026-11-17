using System.Diagnostics;
using System.Management.Automation;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashKill</c>. Sends a
/// (best-effort) signal to processes by PID: terminates for the usual signals,
/// treats signal 0 as an existence probe only, and <c>-l</c> lists the known
/// signal names. Windows has no real POSIX signals, so every terminating signal
/// maps to <see cref="Process.Kill()"/> — matching the psm1 oracle, whose
/// Stop-Process calls also force-terminate. No flag collides with a PowerShell
/// common parameter, so all tokens arrive via <see cref="Arguments"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashKill")]
public sealed class InvokeBashKillCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    // Number/name -> canonical SIGxxx, mirroring the psm1 table.
    /// <summary>Linux: a terminated-but-unreaped child (state Z in /proc) is already gone for <c>kill -0</c>, as in bash.</summary>
    private static bool IsZombie(int pid)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var stat = System.IO.File.ReadAllBytes("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/stat");
            int close = Array.LastIndexOf(stat, (byte)')');
            return close >= 0 && close + 2 < stat.Length && stat[close + 2] == (byte)'Z';
        }
        catch (Exception) { return false; }
    }

    private static readonly Dictionary<string, string> Signals = new(StringComparer.Ordinal)
    {
        ["1"] = "SIGHUP", ["HUP"] = "SIGHUP",
        ["2"] = "SIGINT", ["INT"] = "SIGINT",
        ["3"] = "SIGQUIT", ["QUIT"] = "SIGQUIT",
        ["6"] = "SIGABRT", ["ABRT"] = "SIGABRT",
        ["9"] = "SIGKILL", ["KILL"] = "SIGKILL",
        ["10"] = "SIGUSR1", ["USR1"] = "SIGUSR1",
        ["12"] = "SIGUSR2", ["USR2"] = "SIGUSR2",
        ["14"] = "SIGALRM", ["ALRM"] = "SIGALRM",
        ["15"] = "SIGTERM", ["TERM"] = "SIGTERM",
        ["18"] = "SIGCONT", ["CONT"] = "SIGCONT",
        ["19"] = "SIGSTOP", ["STOP"] = "SIGSTOP",
        ["20"] = "SIGTSTP", ["TSTP"] = "SIGTSTP",
        ["28"] = "SIGWINCH", ["WINCH"] = "SIGWINCH",
    };

    /// <summary>
    /// Bash's signal names by number on Linux, the one table every <c>kill</c> form reads (list, <c>-l N</c>, <c>-s NAME</c>,
    /// <c>-N</c>): 1-31, then the real-time signals 34-64 as bash names them (<c>RTMIN</c>, <c>RTMIN+1</c> ... <c>RTMIN+15</c>,
    /// <c>RTMAX-14</c> ... <c>RTMAX</c>); 32 and 33 belong to the C library and have none.
    /// </summary>
    private static readonly string?[] LinuxSignalNames = BuildSignalNames();

    private static string?[] BuildSignalNames()
    {
        string?[] names = new string?[65];
        string[] standard =
        [
            "HUP", "INT", "QUIT", "ILL", "TRAP", "ABRT", "BUS", "FPE", "KILL", "USR1", "SEGV", "USR2", "PIPE",
            "ALRM", "TERM", "STKFLT", "CHLD", "CONT", "STOP", "TSTP", "TTIN", "TTOU", "URG", "XCPU", "XFSZ",
            "VTALRM", "PROF", "WINCH", "IO", "PWR", "SYS",
        ];
        standard.CopyTo(names, 1);
        for (int n = 34; n <= 64; n++)
            names[n] = n == 34 ? "RTMIN" : n <= 49 ? $"RTMIN+{n - 34}" : n < 64 ? $"RTMAX-{64 - n}" : "RTMAX";
        return names;
    }

    /// <summary>
    /// <c>kill -l</c> with no operand: bash's table, <c>%2d) SIGNAME</c> entries, a tab after each and a newline after every
    /// fifth (bash's display_signal_list), so a row ends <c>5) SIGTRAP</c> and a short last row ends with a tab.
    /// </summary>
    internal static string SignalList()
    {
        var sb = new System.Text.StringBuilder();
        int column = 0;
        for (int n = 1; n < LinuxSignalNames.Length; n++)
        {
            if (LinuxSignalNames[n] is not { } name) continue;
            sb.Append(n.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(2)).Append(") SIG").Append(name);
            if (++column < 5) sb.Append('\t');
            else { sb.Append('\n'); column = 0; }
        }
        if (column != 0) sb.Append('\n');
        return sb.ToString();
    }

    private static readonly Regex NumSignal = new(@"^-(\d+)$", RegexOptions.Compiled);
    private static readonly Regex NamedSignal = new(@"^-(SIG)?([A-Za-z][A-Za-z0-9]*)$", RegexOptions.Compiled);
    private static readonly Regex LongSignal = new(@"^--signal=(.+)$", RegexOptions.Compiled);
    private static readonly Regex PidSpec = new(@"^-%?(\d+)$", RegexOptions.Compiled);

    private static string ResolveSignal(string token)
        => token.StartsWith("SIG", StringComparison.Ordinal) ? token
           : Signals.TryGetValue(token, out var name) ? name
           : int.TryParse(token, out var n) && n > 0 && n < LinuxSignalNames.Length ? "SIG" + LinuxSignalNames[n]
           : "SIG" + token;

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "kill"))
                WriteObject(line);
            return;
        }

        string? signalName = null;
        var pids = new List<int>();
        var jobSpecs = new List<string>();   // %N / %% / %+ : routed to the in-process background job table
        bool badArg = false;

        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];

            if ((a == "-l" || a == "-L" || a == "--list") && i + 1 < args.Length)
            {
                // kill -l SIGSPEC...: a number (or 128+number) prints its name, a name prints its number.
                int bad = 0;
                for (int k = i + 1; k < args.Length; k++)
                {
                    string t = args[k];
                    string? shown = null;
                    if (int.TryParse(t, out var n) && n >= 0)
                    {
                        if (n > 128) n -= 128;
                        if (n == 0 && t == "0") shown = "EXIT";
                        else if (n >= 1 && n < LinuxSignalNames.Length) shown = LinuxSignalNames[n] ?? "";   // 32, 33: valid, no name, prints nothing
                    }
                    else
                    {
                        string nm = t.StartsWith("SIG", StringComparison.OrdinalIgnoreCase) ? t[3..] : t;
                        if (nm.Equals("EXIT", StringComparison.OrdinalIgnoreCase)) shown = "0";
                        else
                        {
                            int idx = Array.FindIndex(LinuxSignalNames, x => x != null && x.Equals(nm, StringComparison.OrdinalIgnoreCase));
                            if (idx > 0) shown = idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                    if (shown == null)
                    {
                        FileSystemHelpers.WriteBashError(this, $"kill: {t}: invalid signal specification");
                        bad = 1;
                    }
                    else if (shown.Length > 0) WriteObject(BashRuntime.NewBashObject(shown));
                }
                FileSystemHelpers.SetLastExitCode(this, bad);
                return;
            }
            if (a == "-l" || a == "-L" || a == "--list")
            {
                foreach (var obj in BashRuntime.EmitBashLines(SignalList(), "kill"))
                    WriteObject(obj);
                return;
            }
            if (a == "-s" || a == "-n" || a == "--signal")
            {
                i++;
                if (i < args.Length)
                {
                    string spec = args[i];
                    string nm = spec.StartsWith("SIG", StringComparison.OrdinalIgnoreCase) ? spec[3..] : spec;
                    bool known = int.TryParse(spec, out var sn)
                        ? sn >= 0 && sn <= 64
                        : Array.Exists(LinuxSignalNames, x => x != null && x.Equals(nm, StringComparison.OrdinalIgnoreCase))
                          || Regex.IsMatch(nm, @"^(RTMIN|RTMAX)(\+\d+|-\d+)?$", RegexOptions.IgnoreCase);
                    if (!known)
                    {
                        FileSystemHelpers.WriteBashError(this, $"kill: {spec}: invalid signal specification");
                        FileSystemHelpers.SetLastExitCode(this, 1);
                        return;
                    }
                    signalName = ResolveSignal(spec);
                }
                i++;
                continue;
            }
            var mNum = NumSignal.Match(a);
            if (mNum.Success && BashRuntime.ParseCountClamped(mNum.Groups[1].Value) > 64)
            {
                // No such signal (the highest real-time one is 64): nothing is signalled.
                FileSystemHelpers.WriteBashError(this, $"kill: {a[1..]}: invalid signal specification");
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
            if (mNum.Success) { signalName = ResolveSignal(mNum.Groups[1].Value); i++; continue; }

            // Named-signal short form (-KILL, -SIGINT, ...) — before the pid scan.
            var mNamed = NamedSignal.Match(a);
            if (mNamed.Success) { signalName = ResolveSignal(a.Substring(1)); i++; continue; }

            var mLong = LongSignal.Match(a);
            if (mLong.Success) { signalName = ResolveSignal(mLong.Groups[1].Value); i++; continue; }

            if (a.Length > 0 && a[0] == '%') { jobSpecs.Add(a); i++; continue; }

            var mPid = PidSpec.Match(a);
            if (mPid.Success) { pids.Add(BashRuntime.ParseCountClamped(mPid.Groups[1].Value)); i++; continue; }

            if (int.TryParse(a, out var pidVal)) pids.Add(pidVal);
            else if (a != "--")
            {
                // bash: a word that is neither a number nor a jobspec is reported and skipped (status 1).
                FileSystemHelpers.WriteBashError(this, $"kill: {a}: arguments must be process or job IDs");
                badArg = true;
            }
            i++;
        }

        if (pids.Count == 0 && jobSpecs.Count == 0 && badArg)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        if (pids.Count == 0 && jobSpecs.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this,
                "kill: usage: kill [-s sigspec | -n signum | -sigspec] pid | jobspec ... or kill -l [sigspec]");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        bool existenceOnly = signalName == "SIG0";
        bool hadError = badArg;
        bool signalSelf = false;
        int sigNum = SignalNumber(signalName ?? "SIGTERM");
        if (existenceOnly) sigNum = 0;
        // A job, another process or the shell without a trap for it ignores the signal: only check the target exists.
        bool probeOnly = existenceOnly || IgnoredByDefault(sigNum);
        // A background job of the in-process table (`$!` is a synthetic id, `%N` a jobspec), as `wait` resolves them.
        bool StopBgJob(string spec)
        {
            try
            {
                var r = InvokeCommand.InvokeScript("param($s,$n,$p) Stop-BashBgJob $s $n $p", spec, sigNum, probeOnly);
                return r.Count > 0 && r[0]?.BaseObject is true;
            }
            catch (CommandNotFoundException) { return false; }   // PsBash module not loaded: no job table
        }
        foreach (var spec in jobSpecs)
        {
            if (StopBgJob(spec)) continue;
            FileSystemHelpers.WriteBashError(this, $"kill: {spec}: no such job");
            hadError = true;
        }
        foreach (var procId in pids)
        {
            // `kill $$` signals the SHELL. Here the shell process is the shared ps-bash host, which
            // runs every command of the session: Process.Kill on it ended all of them (and the
            // daemon). bash's outcome — the current script dies of the signal — is applied to this
            // command instead, below, after the other pids are signalled.
            if (procId == Environment.ProcessId && !existenceOnly)
            {
                // A background job's runspace in this host (in-process job) signalling `$$`: the shell is the command
                // this host runs, not the job, so the signal goes to it as one another shell sends.
                if (PsBash.Core.ShellSignals.HasCommand && !PsBash.Core.ShellSignals.IsOwner(ShellSignalTraps.CurrentRunspace))
                {
                    PsBash.Core.ShellSignals.Deliver(sigNum);
                    continue;
                }
                signalSelf = true;
                continue;
            }
            // A subshell's synthetic $BASHPID (BashRuntime.SubshellPidVariable) is no process: the running subshell's
            // own id is the shell signalling itself, any other one (a subshell that has ended) names nothing.
            if (BashRuntime.IsSubshellPid(procId))
            {
                if (procId == BashRuntime.BashPid(SessionState)) { if (!existenceOnly) signalSelf = true; continue; }
                FileSystemHelpers.WriteBashError(this, $"kill: ({procId}) - No such process");
                hadError = true;
                continue;
            }
            if (StopBgJob(procId.ToString(System.Globalization.CultureInfo.InvariantCulture))) continue;
            // A background job runs in its own host process: stop its whole tree and let `wait` report the signal.
            if (!probeOnly && PsBash.Core.BackgroundJobTable.TrySignal(procId, SignalNumber(signalName ?? "SIGTERM")))
                continue;
            // Another ps-bash shell's `$$` (`sh -c "kill -USR1 $$"`: the nested shell runs in another host): the signal
            // goes to the command that host runs, as a message (its trap runs, or it ends with 128+N). The one route;
            // a guard test keeps every kill path on it. Ignored-by-default signals too: that shell may trap them.
            if (!existenceOnly && PsBash.Core.ShellSignals.SendToShell(procId, sigNum, out _))
                continue;
            try
            {
                using var proc = Process.GetProcessById(procId);
                // GetProcessById throws if the PID is unknown; a process object that
                // has already exited also counts as "no such process".
                if (proc.HasExited) throw new ArgumentException("exited");
                if (IsZombie(procId)) throw new ArgumentException("zombie");
                if (!probeOnly && IsShellHost(proc))
                {
                    // A ps-bash host the route above could not reach (no host route in this process, an older host
                    // that does not know the signal request, no readable sidecar): it serves every script of its
                    // session, so Process.Kill would end all of them. Say so instead of killing the host.
                    FileSystemHelpers.WriteBashError(this,
                        $"kill: ({procId}) - signal not delivered: a ps-bash shell host that could not be reached");
                    continue;
                }
                if (!probeOnly)
                    proc.Kill();
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                // psm1 parity: any failure (unknown PID, access denied, already
                // exited) is reported as "No such process".
                FileSystemHelpers.WriteBashError(this, $"kill: ({procId}) - No such process");
                hadError = true;
            }
        }

        // `kill -USR1 $$`: the shell signals itself. A trap runs here and the script carries on (an empty action ignores
        // the signal); with none, SIGCHLD/SIGURG/SIGWINCH are ignored and anything else ends the script with 128+N
        // (137 for -9, 143 for TERM). The same rules as a signal another shell sends (ShellSignalTraps).
        if (signalSelf && sigNum != 9 && ShellSignalTraps.RunTrap(this, sigNum))
            signalSelf = false;
        if (signalSelf && ShellSignalTraps.IgnoredByDefault(sigNum))
            signalSelf = false;
        if (signalSelf)
            throw ShellSignalTraps.Terminate(this, sigNum);

        FileSystemHelpers.SetLastExitCode(this, hadError ? 1 : 0);
    }

    /// <summary>A ps-bash host process (shared by a session's scripts). Background-job hosts are signalled before this.</summary>
    private static bool IsShellHost(Process proc)
    {
        try { return string.Equals(proc.ProcessName, "ps-bash-host", StringComparison.OrdinalIgnoreCase); }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>The number of a resolved signal name (<c>SIGKILL</c> → 9, <c>SIGURG</c> → 23); 15 (TERM) when unknown.</summary>
    internal static int SignalNumber(string signalName)
    {
        foreach (var (key, value) in Signals)
            if (value == signalName && int.TryParse(key, out var n)) return n;
        string bare = signalName.StartsWith("SIG", StringComparison.Ordinal) ? signalName[3..] : signalName;
        int idx = Array.FindIndex(LinuxSignalNames, x => x != null && x.Equals(bare, StringComparison.OrdinalIgnoreCase));
        return idx > 0 ? idx : 15;
    }

    /// <summary>SIGCHLD (17), SIGURG (23), SIGWINCH (28): the default action is to ignore them, so a process without a
    /// handler keeps running. Everything else ps-bash delivers terminates (Windows has no signals).</summary>
    internal static bool IgnoredByDefault(int sig) => sig is 17 or 23 or 28;
}
