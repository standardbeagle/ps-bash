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

    private static readonly Regex NumSignal = new(@"^-(\d+)$", RegexOptions.Compiled);
    private static readonly Regex NamedSignal = new(@"^-(SIG)?([A-Za-z][A-Za-z0-9]*)$", RegexOptions.Compiled);
    private static readonly Regex LongSignal = new(@"^--signal=(.+)$", RegexOptions.Compiled);
    private static readonly Regex PidSpec = new(@"^-%?(\d+)$", RegexOptions.Compiled);

    private static string ResolveSignal(string token)
        => token.StartsWith("SIG", StringComparison.Ordinal) ? token
           : Signals.TryGetValue(token, out var name) ? name
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

        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];

            if (a == "-l" || a == "--list")
            {
                // Signal names ordered by signal number (psm1 parity).
                var names = Signals
                    .Where(kv => int.TryParse(kv.Key, out _))
                    .OrderBy(kv => int.Parse(kv.Key))
                    .Select(kv => kv.Value);
                foreach (var obj in BashRuntime.EmitBashLines(string.Join("\n", names) + "\n", "kill"))
                    WriteObject(obj);
                return;
            }
            if (a == "-s" || a == "--signal")
            {
                i++;
                if (i < args.Length) signalName = ResolveSignal(args[i]);
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
            i++;
        }

        if (pids.Count == 0 && jobSpecs.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this,
                "kill: usage: kill [-s sigspec | -n signum | -sigspec] pid | jobspec ... or kill -l [sigspec]");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        bool existenceOnly = signalName == "SIG0";
        bool hadError = false;
        bool signalSelf = false;
        int sigNum = SignalNumber(signalName ?? "SIGTERM");
        if (existenceOnly) sigNum = 0;
        // A background job of the in-process table (`$!` is a synthetic id, `%N` a jobspec), as `wait` resolves them.
        bool StopBgJob(string spec)
        {
            try
            {
                var r = InvokeCommand.InvokeScript("param($s,$n,$p) Stop-BashBgJob $s $n $p", spec, sigNum, existenceOnly);
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
                signalSelf = true;
                continue;
            }
            if (StopBgJob(procId.ToString(System.Globalization.CultureInfo.InvariantCulture))) continue;
            // A background job runs in its own host process: stop its whole tree and let `wait` report the signal.
            if (!existenceOnly && PsBash.Core.BackgroundJobTable.TrySignal(procId, SignalNumber(signalName ?? "SIGTERM")))
                continue;
            try
            {
                using var proc = Process.GetProcessById(procId);
                // GetProcessById throws if the PID is unknown; a process object that
                // has already exited also counts as "no such process".
                if (proc.HasExited) throw new ArgumentException("exited");
                if (!existenceOnly)
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

        if (signalSelf && RunSelfSignalTrap(signalName ?? "SIGTERM"))
            signalSelf = false;   // a trap handled it: bash runs the handler and the script carries on

        if (signalSelf)
        {
            // The script ends as if killed by the signal: status 128+N (137 for -9, 143 for TERM).
            // SetShouldExit records it on the host (the same channel bash `exit` uses); the pipeline
            // stop ends the script, running its finally blocks, and the worker reports that status.
            int status = 128 + SignalNumber(signalName ?? "SIGTERM");
            FileSystemHelpers.SetLastExitCode(this, status);
            Host.SetShouldExit(status);
            throw new PipelineStoppedException();
        }

        FileSystemHelpers.SetLastExitCode(this, hadError ? 1 : 0);
    }

    /// <summary>
    /// `kill -USR1 $$` / `kill -INT $$` with a `trap ... USR1` installed runs the trap in this shell instead of
    /// ending it. Returns true when a handler ran.
    /// </summary>
    private bool RunSelfSignalTrap(string signalName)
    {
        string bare = signalName.StartsWith("SIG", StringComparison.Ordinal) ? signalName.Substring(3) : signalName;
        var dict = (SessionState.PSVariable.Get("global:BashTrapHandlers")
                    ?? SessionState.PSVariable.Get("BashTrapHandlers"))?.Value as System.Collections.IDictionary;
        if (dict is null) return false;
        string? action = null;
        foreach (var key in new[] { bare, "SIG" + bare, SignalNumber(signalName).ToString(System.Globalization.CultureInfo.InvariantCulture) })
            if (dict.Contains(key) && dict[key] is string s) { action = s; break; }
        if (action is null) return false;
        if (action.Length == 0) return true;   // an empty action IGNORES the signal
        foreach (var obj in InvokeCommand.InvokeScript(InvokeBashTrapCommand.TranspileAction(action)))
            WriteObject(obj);
        FileSystemHelpers.SetLastExitCode(this, 0);
        return true;
    }

    /// <summary>The number of a resolved signal name (<c>SIGKILL</c> → 9); 15 (TERM) when unknown.</summary>
    private static int SignalNumber(string signalName)
    {
        foreach (var (key, value) in Signals)
            if (value == signalName && int.TryParse(key, out var n)) return n;
        return 15;
    }
}
