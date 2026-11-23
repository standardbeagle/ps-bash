using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace PsBash.Cmdlets;

/// <summary>Read by the statement-boundary poll the emitter puts after each statement of a script that can set a trap.</summary>
public static class ShellSignalPoll
{
    /// <summary>A signal sent to this shell waits for its trap (<see cref="PsBash.Core.ShellSignals"/>).</summary>
    public static bool Pending => PsBash.Core.ShellSignals.Pending;
}

/// <summary>
/// A signal that has reached the shell running in this runspace: run its trap, ignore it, or take its default action
/// (end the script with 128+N). One implementation for <c>kill $$</c> (the shell signalling itself) and for a signal
/// another shell sent (<see cref="InvokeBashPendingSignalsCommand"/>), so both behave alike.
/// </summary>
internal static class ShellSignalTraps
{
    /// <summary>The runspace the calling pipeline runs in (<see cref="Guid.Empty"/> outside one).</summary>
    internal static Guid CurrentRunspace => Runspace.DefaultRunspace?.InstanceId ?? Guid.Empty;

    /// <summary>The <c>trap</c> action for signal number <paramref name="signal"/>: null when there is none, "" when it is ignored.</summary>
    internal static string? ActionFor(SessionState session, int signal)
    {
        var dict = (session.PSVariable.Get("global:BashTrapHandlers")
                    ?? session.PSVariable.Get("BashTrapHandlers"))?.Value as System.Collections.IDictionary;
        if (dict is null) return null;
        string number = signal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? bare = InvokeBashTrapCommand.NormalizeSignal(number);
        foreach (var key in new[] { bare, bare is null ? null : "SIG" + bare, number })
            if (key is not null && dict.Contains(key) && dict[key] is string s) return s;
        return null;
    }

    /// <summary>
    /// Run the trap for <paramref name="signal"/> in <paramref name="cmdlet"/>'s shell. True when the shell has a
    /// disposition for it (the action ran, or an empty action ignored it); false when it has none, and the caller applies
    /// the default (<see cref="IgnoredByDefault"/> or <see cref="Terminate"/>).
    /// </summary>
    internal static bool RunTrap(PSCmdlet cmdlet, int signal)
    {
        var action = ActionFor(cmdlet.SessionState, signal);
        if (action is null) return false;
        if (action.Length == 0) return true;
        string name = InvokeBashTrapCommand.NormalizeSignal(signal.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? "TERM";
        foreach (var obj in cmdlet.InvokeCommand.InvokeScript(InvokeBashTrapCommand.TranspileAction(action, name)))
            cmdlet.WriteObject(obj);
        return true;
    }

    /// <summary>No trap: the shell keeps running for these signals (<see cref="PsBash.Core.ShellSignals.IgnoredByDefault"/>).</summary>
    internal static bool IgnoredByDefault(int signal) => signal != 9 && PsBash.Core.ShellSignals.IgnoredByDefault(signal);

    /// <summary>
    /// No trap, and the code <paramref name="cmdlet"/> runs keeps running: <see cref="IgnoredByDefault"/>, and in a background
    /// job also SIGINT and SIGQUIT, which bash's non-interactive shell ignores in an asynchronous command
    /// (<c>{ kill -INT $BASHPID; } &amp; wait $!</c> is 0).
    /// </summary>
    internal static bool IgnoredHere(PSCmdlet cmdlet, int signal)
        => IgnoredByDefault(signal)
           || ((signal == PsBash.Core.Platform.OsSignals.SIGINT || signal == PsBash.Core.Platform.OsSignals.SIGQUIT) && InBackgroundJob(cmdlet));

    /// <summary>
    /// The script ends as if killed by <paramref name="signal"/>: status 128+N. SetShouldExit records it on the host (the
    /// channel bash <c>exit</c> uses); the pipeline stop ends the script, running its finally blocks, and the worker
    /// reports that status. Never returns. <paramref name="innermostSubshell"/>: the signal named the running subshell's own
    /// <c>$BASHPID</c>, so only that subshell (or background job) ends.
    /// </summary>
    internal static Exception Terminate(PSCmdlet cmdlet, int signal, bool innermostSubshell = false)
    {
        int status = 128 + signal;
        // `( kill -TERM $BASHPID )`: the subshell signalled itself, and only it ends, with 128+N (bash's subshell is a
        // child process).
        return EndShell(cmdlet, status, innermostSubshell, signal);
    }

    /// <summary>
    /// The shell ends with <paramref name="status"/> as bash <c>exit</c> ends it, from a cmdlet: the innermost
    /// <c>( )</c> / <c>$( )</c> / pipe-stage subshell when one is open (a fatal error there ends only that child), else a
    /// background job, else the script. Never returns. Used for a fatal shell error raised inside a cmdlet (an array
    /// subscript error under <c>bash -c</c>, <see cref="FileSystemHelpers.MarkExpansionFailed(PSCmdlet, bool)"/>).
    /// </summary>
    internal static Exception EndShell(PSCmdlet cmdlet, int status) => EndShell(cmdlet, status, innermostSubshell: true, signal: 0);

    private static Exception EndShell(PSCmdlet cmdlet, int status, bool innermostSubshell, int signal)
    {
        FileSystemHelpers.SetLastExitCode(cmdlet, status);
        bool inJob = InBackgroundJob(cmdlet);
        // Thrown the way PsBuild.DynamicExit throws a subshell's `exit`, for its scope (PsBuild.ShellStateGuard,
        // `$__psbash_ss`) to catch. Not at a job's own top level: there $BASHPID is the job's (BashJobRunspaces.NewJob),
        // and the job ends below.
        if (innermostSubshell && cmdlet.SessionState.PSVariable.Get(SubshellScopeVariable) is not null
            && !(inJob && Equals(Unwrap(cmdlet.SessionState.PSVariable.GetValue("global:" + BashRuntime.SubshellPidVariable)), BashRuntime.BashPid(cmdlet.SessionState))))
            throw new BashSubshellExitException(status);
        if (inJob)
        {
            // A background job ends the way its `exit` does: the job's wrapper keeps the output it made so far and
            // records the status, and `wait` reports the signal (JobSignalVariable, read by the wrapper's finally).
            // A pipeline stop there surfaced in `wait` as EndInvoke's "pipeline has been stopped" text, status 1,
            // and a non-live job's output was lost (docs/specs/background-jobs.md section 3).
            if (signal != 0) cmdlet.SessionState.PSVariable.Set("global:" + JobSignalVariable, signal);
            cmdlet.InvokeCommand.InvokeScript("exit " + status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        cmdlet.Host.SetShouldExit(status);
        throw new PipelineStoppedException();
    }

    /// <summary>The job-runspace global holding the signal that ended the job (Invoke-BashBackground's wrapper reads it).</summary>
    public const string JobSignalVariable = "__psbashJobSignal";

    // The variable an open subshell scope holds its saved state in (PsBuild.ShellStateGuard / DynamicExit).
    private const string SubshellScopeVariable = "__psbash_ss";

    private static object? Unwrap(object? v) => v is PSObject pso ? pso.BaseObject : v;

    private static bool InBackgroundJob(PSCmdlet cmdlet) => BashShellState.InBackgroundJob(cmdlet.SessionState);
}

/// <summary>
/// <c>Invoke-BashPendingSignals</c>: the statement-boundary poll's body (<c>PsBuild.SignalTrapPoll</c>). Takes the signals
/// another shell sent to this one since the last statement (<see cref="PsBash.Core.ShellSignals"/>) and, for each, runs its
/// trap, ignores it, or ends the script with 128+N. A trap leaves <c>$?</c> as it found it, as bash's does. Not a bash
/// command: only emitted code calls it.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPendingSignals")]
public sealed class InvokeBashPendingSignalsCommand : BashCmdlet
{
    // A trap action is itself a list of statements, and may poll: a signal that arrives while a trap runs waits for it.
    [ThreadStatic] private static bool _running;

    protected override void EndProcessing()
    {
        if (_running) return;
        var signals = PsBash.Core.ShellSignals.TakePending(ShellSignalTraps.CurrentRunspace);
        if (signals.Length == 0) return;
        object? status = SessionState.PSVariable.GetValue("global:LASTEXITCODE");
        _running = true;
        try
        {
            foreach (int signal in signals)
            {
                if (ShellSignalTraps.RunTrap(this, signal)) continue;
                if (ShellSignalTraps.IgnoredByDefault(signal)) continue;
                throw ShellSignalTraps.Terminate(this, signal);
            }
        }
        finally { _running = false; }
        FileSystemHelpers.RestoreShellStatus(this, status); // the status from before the traps, not this cmdlet's own
    }
}
