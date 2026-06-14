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
            if (mNum.Success) { signalName = ResolveSignal(mNum.Groups[1].Value); i++; continue; }

            // Named-signal short form (-KILL, -SIGINT, ...) — before the pid scan.
            var mNamed = NamedSignal.Match(a);
            if (mNamed.Success) { signalName = ResolveSignal(a.Substring(1)); i++; continue; }

            var mLong = LongSignal.Match(a);
            if (mLong.Success) { signalName = ResolveSignal(mLong.Groups[1].Value); i++; continue; }

            var mPid = PidSpec.Match(a);
            if (mPid.Success) { pids.Add(int.Parse(mPid.Groups[1].Value)); i++; continue; }

            if (int.TryParse(a, out var pidVal)) pids.Add(pidVal);
            i++;
        }

        if (pids.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this,
                "kill: usage: kill [-s sigspec | -n signum | -sigspec] pid | jobspec ... or kill -l [sigspec]");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        bool existenceOnly = signalName == "SIG0";
        bool hadError = false;
        foreach (var procId in pids)
        {
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

        FileSystemHelpers.SetLastExitCode(this, hadError ? 1 : 0);
    }
}
