using System.Collections;
using System.Diagnostics;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>Invoke-BashNative -- NAME ARGS...</c>: runs a native-candidate command whose stderr bash merges into its stdout
/// (<c>git x 2&gt;&amp;1</c>, <c>make &amp;&gt;log</c>, <c>cc x.c |&amp; less</c>, <c>{ git a; git b; } 2&gt;&amp;1</c>). The emitter puts
/// it in front of such a command (<c>PsEmitter.NativeMergeRunner</c>).
/// <para>
/// <b>Why.</b> PowerShell runs a program with two pipes, one for stdout and one for stderr, and merges them on its
/// side; lines the program wrote to fd 1 and fd 2 in turn come out in whatever order PowerShell's two readers saw
/// them. When <see cref="StderrMerge.JoinsStdout"/> holds and NAME is a program, this cmdlet starts it itself with ONE
/// pipe for fd 1 and fd 2 (<see cref="ChildStdio"/>), streams its output, feeds it the pipeline input (or lets it
/// inherit the process stdin, as PowerShell would), and sets <c>$LASTEXITCODE</c>.
/// </para>
/// <para>
/// Otherwise — NAME is a function, alias, cmdlet or script, the stage downstream splits stderr off again
/// (<c>2&gt;&amp;1 &gt;/dev/null</c>), or a Windows batch file (whose argument quoting only PowerShell's own launcher gets
/// right) — the command runs exactly as without this cmdlet: as a steppable pipeline of <c>&amp; NAME ARGS</c>.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashNative")]
public sealed class InvokeBashNativeCommand : BashCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Name { get; set; } = "";

    [Parameter(Position = 1, ValueFromRemainingArguments = true)]
    [AllowNull]
    [AllowEmptyString]
    [AllowEmptyCollection]
    public object?[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    [AllowNull]
    public PSObject? InputObject { get; set; }

    private StreamingChildProcess? _child;
    private SteppablePipeline? _fallback;
    private bool _finished;

    protected override void BeginProcessing()
    {
        // The one lookup of NAME (the host's command-lookup hook counts it as a use, as running it would).
        CommandInfo? info;
        // A job runspace's hook would hand back the stand-in that calls this cmdlet (JobProgramLookup): look past it.
        try { info = JobProgramLookup.Raw(() => InvokeCommand.GetCommand(Name, CommandTypes.All)); }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex)) { info = null; }

        var args = FlattenArguments(Arguments);
        // A program file the OS cannot start as it is runs as ProgramFile's stand-in (the host's lookup hook already
        // gave one; a runspace without the hook, Pester's, did not).
        object? standIn = info is ApplicationInfo file && PsBash.Core.ProgramFile.Plan(file.Path) is { } launch
            ? ScriptBlock.Create(launch.StandInScript(PsBash.Core.ProgramFile.Shown(Name, file.Path)))
            : null;
        // In a background job a program is always started here: PowerShell would give it the process environment,
        // the foreground's scalars, not the job's (ScalarOverlay, background-jobs.md section 3).
        bool merge = StderrMerge.JoinsStdout(this);
        if (standIn is null && info is ApplicationInfo app && !IsBatchFile(app.Path)
            && (merge || PsBash.Core.ScalarOverlay.Current is not null))
        {
            StartProgram(app.Path, args, merge);
            return;
        }

        // A CommandInfo runs without a second lookup; an unknown name goes through PowerShell's lookup, so the
        // host's command-not-found handling reports it exactly as before.
        object target = standIn ?? (object?)info ?? Name;
        // Not found: the status is 127 from here on, before the message is written (`nosuchcmd 2>&1 | head -1` stops
        // this stage once head has the message, and PIPESTATUS reads the status it had: InvokeBashPipeStageCommand).
        if (info is null && standIn is null) FileSystemHelpers.SetLastExitCode(this, 127);
        _fallback = ScriptBlock.Create("param($__psbash_cmd, $__psbash_args) & $__psbash_cmd @__psbash_args")
            .GetSteppablePipeline(CommandOrigin.Internal, new object[] { target, args.ToArray<object>() });
        _fallback.Begin(this);
    }

    protected override void ProcessRecord()
    {
        if (_fallback is not null)
        {
            // Without upstream input the command runs once with none (a program keeps the inherited stdin).
            if (MyInvocation.ExpectingInput) _fallback.Process(InputObject);
            else _fallback.Process();
            return;
        }
        if (InputObject is null || _child is not { StdinOpen: true }) return;
        var bytes = InputObject.BaseObject is byte[] raw ? raw : NativeStdinBridge.RecordBytes(InputObject);
        if (!_child.WriteStdin(this, bytes))
        {
            // The program stopped reading: bash's producer dies of SIGPIPE.
            Finish();
            UpstreamStop.Throw(this);
            return;
        }
        _child.PumpAvailable(this);
    }

    protected override void EndProcessing()
    {
        if (_fallback is not null)
        {
            // No AdoptLastExitCode: PowerShell publishes the program's status after this returns, and it stands as a
            // status set by no cmdlet (BashCmdlet); reading it here took the stage ahead's.
            _fallback.End();
            return;
        }
        Finish();
    }

    protected override void StopProcessing()
    {
        _child?.Kill();
        _fallback?.Dispose();
    }

    protected override void DisposeResources()
    {
        _child?.Dispose();
        _fallback?.Dispose();
    }

    private void StartProgram(string path, List<string> args, bool merge)
    {
        var psi = ProgramStartInfo(this, path, args);
        try
        {
            // Piped input is fed to it; otherwise it reads the process stdin, as a program PowerShell starts does.
            // Not merged, its stderr goes through this cmdlet's error stream (StreamingChildProcess).
            bool piped = MyInvocation.ExpectingInput;
            _child = BashRuntime.StartChildProcessStreaming(psi, keepStdinOpen: piped,
                new ChildStdioMode(MergeStderr: merge, InheritStdin: !piped));
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            // Found but not runnable (bash: "cannot execute", 126).
            FileSystemHelpers.WriteBashError(this, ShellDiagnostic.Prefix(SessionState) + $"{Name}: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 126);
        }
    }

    private void Finish()
    {
        if (_child is null || _finished) return;
        _finished = true;
        int exitCode = _child.Complete(this);
        if (Stopping) return;
        FileSystemHelpers.SetLastExitCode(this, exitCode);
    }

    /// <summary>
    /// For a cmdlet that runs a command by name (<c>env</c>, <c>xargs</c>, <c>find -exec</c>, <c>command</c>): the program
    /// path to run through <see cref="RunMerged"/> when the cmdlet's stderr goes down the pipeline
    /// (<see cref="StderrMerge.Redirected"/>: merged, or split off to a file or <c>/dev/null</c>) or the cmdlet runs in a
    /// background job (its environment is the job's scalars, <see cref="JobProgramLookup"/>), and
    /// <paramref name="name"/> is a program the OS starts as it is; null otherwise (run it by name as before, where the
    /// lookup hook gives a <c>#!</c> script or a document <see cref="PsBash.Core.ProgramFile"/>'s stand-in).
    /// </summary>
    internal static string? MergedProgramPath(PSCmdlet cmdlet, string name)
    {
        // In a background job always: PowerShell would start it with the process environment, not the job's scalars.
        if (string.IsNullOrEmpty(name) || (!StderrMerge.Redirected(cmdlet) && PsBash.Core.ScalarOverlay.Current is null)) return null;
        try
        {
            return JobProgramLookup.Raw(() => cmdlet.InvokeCommand.GetCommand(name, CommandTypes.All)) is ApplicationInfo app && !IsBatchFile(app.Path)
                   && PsBash.Core.ProgramFile.Plan(app.Path) is null
                ? app.Path
                : null;
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Run program <paramref name="path"/> with its output streamed through <paramref name="cmdlet"/>, and return its exit
    /// status: ONE pipe for its stdout and stderr when <see cref="StderrMerge.JoinsStdout"/> (<see cref="ChildStdio"/>),
    /// else its stderr through the cmdlet's error stream, where the redirect downstream picks it up. Its stdin is <paramref name="input"/>'s
    /// records when given, else the process stdin when <paramref name="inheritStdin"/>, else empty (xargs:
    /// <c>/dev/null</c>). <paramref name="workDir"/> defaults to the session's location. A program that cannot be started
    /// throws, as running it through PowerShell would.
    /// </summary>
    internal static int RunMerged(PSCmdlet cmdlet, string path, IEnumerable<string> args, bool inheritStdin,
        string? workDir = null, IEnumerable<object?>? input = null)
    {
        var psi = ProgramStartInfo(cmdlet, path, args);
        if (workDir is not null) psi.WorkingDirectory = workDir;
        using var child = BashRuntime.StartChildProcessStreaming(psi, keepStdinOpen: input is not null,
            new ChildStdioMode(MergeStderr: StderrMerge.JoinsStdout(cmdlet), InheritStdin: input is null && inheritStdin));
        if (input is not null)
        {
            foreach (var record in input)
            {
                if (record is null) continue;
                var pso = PSObject.AsPSObject(record);
                var bytes = pso.BaseObject is byte[] raw ? raw : NativeStdinBridge.RecordBytes(pso);
                if (!child.WriteStdin(cmdlet, bytes)) break; // it stopped reading: the rest is dropped
            }
        }
        return child.Complete(cmdlet);
    }

    private static ProcessStartInfo ProgramStartInfo(PSCmdlet cmdlet, string path, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo { FileName = path, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // The session's location, not the process cwd (one host process may run several sessions).
        try
        {
            var loc = cmdlet.SessionState.Path.CurrentFileSystemLocation.ProviderPath;
            if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc)) psi.WorkingDirectory = loc;
        }
        catch { /* not a filesystem location: inherit */ }
        return psi;
    }

    /// <summary>The arguments as a program receives them: arrays spread, each value as text.</summary>
    internal static List<string> FlattenArguments(object?[]? arguments)
    {
        var list = new List<string>();
        if (arguments is null) return list;
        foreach (var a in arguments) Add(list, a);
        return list;

        static void Add(List<string> list, object? value)
        {
            var v = value is PSObject pso ? pso.BaseObject : value;
            switch (v)
            {
                case null:
                    return;
                case string s:
                    list.Add(s);
                    return;
                case IEnumerable items when v is not IDictionary:
                    foreach (var item in items) Add(list, item);
                    return;
                default:
                    list.Add(LanguagePrimitives.ConvertTo<string>(value));
                    return;
            }
        }
    }

    internal static bool IsBatchFile(string path) =>
        OperatingSystem.IsWindows()
        && (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
}
