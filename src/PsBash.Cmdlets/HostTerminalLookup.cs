using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The command-lookup hook behind <see cref="HostTerminal"/>: in a runspace whose host registered a terminal, a command
/// word that resolved to a program runs through <c>Invoke-BashNative -HostTerminal</c>, which starts it on that terminal
/// when it writes straight to the host (<see cref="WritesToHost"/>) and reads no pipeline input - otherwise exactly as
/// PowerShell would have. Bash-mode and PowerShell-mode lines alike: both reach a program through this lookup, and so do
/// <c>env X=1 htop</c>, <c>command vim</c> and <c>time make</c>, which run their program by name.
/// <para>
/// The hook runs any lookup action installed before it FIRST (a host's own - BeagleTerm's session-environment binder
/// swaps a program for a launcher of it - and the order of installation then does not matter), and touches only an
/// <see cref="ApplicationInfo"/> that is still one. A runspace with no terminal registered is never touched.
/// </para>
/// </summary>
public static class HostTerminalLookup
{
    private static readonly ConcurrentDictionary<string, ScriptBlock> s_standIns = new(StringComparer.Ordinal);

    /// <summary>Installs the hook on <paramref name="invoke"/> (<c>$ExecutionContext.InvokeCommand</c>). Idempotent per hook chain.</summary>
    public static void Install(CommandInvocationIntrinsics invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        var previous = invoke.PostCommandLookupAction;
        if (previous?.Target is Marker) return;
        var marker = new Marker(previous);
        invoke.PostCommandLookupAction = marker.Run;
    }

    private sealed class Marker(EventHandler<CommandLookupEventArgs>? previous)
    {
        public void Run(object? sender, CommandLookupEventArgs e)
        {
            previous?.Invoke(sender, e);
            if (JobProgramLookup.IsRaw || e.CommandScriptBlock is not null || e.Command is not ApplicationInfo app) return;
            if (ProgramFile.Plan(app.Path) is not null) return;
            // A session that holds secrets starts its programs here too: PowerShell's native-command processor copies the
            // process environment, which never has them, so `python app.py` would not get its granted key (ShellSecrets).
            // A batch file in any runspace: that processor passes its arguments to cmd.exe unescaped (BatchFile).
            if (HostTerminal.For(Runspace.DefaultRunspace) is null && PsBash.Core.ShellSecrets.Current is null
                && !InvokeBashNativeCommand.IsBatchFile(app.Path)) return;
            e.CommandScriptBlock = s_standIns.GetOrAdd(app.Path, static path => ScriptBlock.Create(StandIn(path)));
        }
    }

    /// <summary>The program at <paramref name="path"/> through <c>Invoke-BashNative</c> (<see cref="PsBash.Core.Parser.PsBuild.NativeProgramStandIn"/>).</summary>
    internal static string StandIn(string path) => PsBash.Core.Parser.PsBuild.NativeProgramStandIn(path);

    /// <summary>
    /// Runs the program at <paramref name="path"/> on the host's terminal and sets <c>$LASTEXITCODE</c>, when
    /// <paramref name="cmdlet"/> - the command that runs it - writes straight to the host and reads no pipeline input,
    /// and the runspace's host registered a terminal that takes it. False, having done nothing, otherwise. A stop of
    /// <paramref name="cmdlet"/> (Ctrl+C) is passed to the host, which ends the program.
    /// </summary>
    internal static bool TryRun(PSCmdlet cmdlet, string shownName, string path, IReadOnlyList<string> args)
    {
        if (cmdlet.MyInvocation.ExpectingInput) return false;
        if (HostTerminal.For(Runspace.DefaultRunspace) is not { } terminal) return false;
        if (!WritesToHost(cmdlet)) return false;

        var psi = InvokeBashNativeCommand.ProgramStartInfo(cmdlet, path, args);
        BashVariableStore.ApplyTo(psi);
        var environment = new Dictionary<string, string?>(psi.Environment.Count,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var kv in psi.Environment) environment[kv.Key] = kv.Value;
        var launch = new HostTerminalLaunch(path, args.ToArray(),
            string.IsNullOrEmpty(psi.WorkingDirectory) ? null : psi.WorkingDirectory, environment);

        // Stopping is set before StopProcessing runs, on any thread; the host only needs to hear of it promptly.
        using var stop = new CancellationTokenSource();
        using var watch = new Timer(_ => { if (cmdlet.Stopping) { try { stop.Cancel(); } catch (ObjectDisposedException) { } } },
            null, 100, 100);
        int? status;
        try { status = terminal.Run(launch, stop.Token); }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex) && ex is not OperationCanceledException)
        {
            // The host could not start it: bash's "cannot execute", 126, as a failed spawn reports.
            FileSystemHelpers.WriteBashError(cmdlet, ShellDiagnostic.Prefix(cmdlet.SessionState) + $"{shownName}: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(cmdlet, 126);
            return true;
        }
        if (status is not int code) return false;
        if (!cmdlet.Stopping) FileSystemHelpers.SetLastExitCode(cmdlet, code);
        return true;
    }

    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// Whether <paramref name="cmdlet"/>'s output goes straight to the host - the pipeline the host invoked hands its
    /// output to the host's own writer - rather than into a variable, a subexpression, or a next command. That is the
    /// PowerShell meaning of "stdout is the terminal". False when it cannot be told (a PowerShell build that hides it).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "Reflecting over the untrimmed SMA internals in the PowerShell host runspace (as StderrMerge " +
            "does); a missing member is null, which reads as 'cannot tell'.")]
    public static bool WritesToHost(PSCmdlet cmdlet)
    {
        ArgumentNullException.ThrowIfNull(cmdlet);
        // Resolved once (as StderrMerge does): this runs for every program a terminal-hosting runspace starts.
        if (s_pipeMembers.Value is not { } m || cmdlet.CommandRuntime is not { } runtime || !m.Runtime.IsInstanceOfType(runtime))
            return false;
        try
        {
            var pipe = m.OutputPipe.GetValue(runtime);
            return pipe is not null && m.ExternalWriter.GetValue(pipe) is not null && m.Downstream.GetValue(pipe) is null;
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or ArgumentException)
        {
            return false;
        }
    }

    private sealed record PipeMembers(Type Runtime, PropertyInfo OutputPipe, FieldInfo ExternalWriter, FieldInfo Downstream);

    private static readonly Lazy<PipeMembers?> s_pipeMembers = new(ResolvePipeMembers);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2026",
        Justification = "The SMA internals are looked up by name in the untrimmed PowerShell host runspace, as StderrMerge does.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "Reflecting over members of the untrimmed SMA types above; a missing member is null = cannot tell.")]
    private static PipeMembers? ResolvePipeMembers()
    {
        try
        {
            var sma = typeof(PSCmdlet).Assembly;
            var runtime = sma.GetType("System.Management.Automation.MshCommandRuntime");
            var pipe = sma.GetType("System.Management.Automation.Internal.Pipe");
            var output = runtime?.GetProperty("OutputPipe", Any);
            var external = pipe?.GetField("_externalWriter", Any);
            var downstream = pipe?.GetField("_downstreamCmdlet", Any);
            return runtime is null || output is null || external is null || downstream is null
                ? null
                : new PipeMembers(runtime, output, external, downstream);
        }
        catch (Exception ex) when (ex is MemberAccessException or AmbiguousMatchException)
        {
            return null;
        }
    }

    /// <summary>
    /// Marks the command running <paramref name="context"/>'s current script as failed, so <c>$?</c> reads false after it:
    /// the stand-in is a script block, and a script block whose program failed is otherwise a success to PowerShell.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "Reflecting over the untrimmed SMA internals in the PowerShell host runspace (as StderrMerge " +
            "does); a missing member is null, which reads as 'cannot tell'.")]
    public static void FailCommand(EngineIntrinsics context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var ctx = context.GetType().GetField("_context", Any)?.GetValue(context);
            var processor = ctx?.GetType().GetProperty("CurrentCommandProcessor", Any)?.GetValue(ctx);
            var runtime = processor?.GetType().GetProperty("CommandRuntime", Any)?.GetValue(processor);
            var pipeline = runtime?.GetType().GetProperty("PipelineProcessor", Any)?.GetValue(runtime);
            pipeline?.GetType().GetProperty("ExecutionFailed", Any)?.SetValue(pipeline, true);
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or AmbiguousMatchException)
        {
            // $? stays true; $LASTEXITCODE still says what happened.
        }
    }
}
