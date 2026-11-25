using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The command-lookup hook of a background job's runspace (<see cref="BashJobRunspaces"/>): a command word that
/// resolved to a program is started through <c>Invoke-BashNative</c>, never by PowerShell itself.
/// <para>
/// <b>Why.</b> PowerShell's native-command processor gives the program a copy of the PROCESS environment, which is
/// the foreground's scalars. A job's scalars are its own (<see cref="ScalarOverlay"/>, section 3 of
/// <c>docs/specs/background-jobs.md</c>), so <c>x=a; { sleep 1; printenv x; } &amp; export x=b</c> printed b.
/// <c>Invoke-BashNative</c> starts it through <see cref="ChildStdio"/>, whose environment is built by
/// <see cref="BashVariableStore.ApplyTo"/> from the job's store. A <c>.cmd</c> / <c>.bat</c> keeps PowerShell's
/// launcher (its argument quoting is the only one cmd.exe parses right) and so the process environment.
/// </para>
/// <para>
/// A program file the OS cannot start as it is gets <see cref="ProgramFile"/>'s stand-in, as the host's own hook gives
/// it (<c>GetVerbFallbackGuard</c>). While the runspace runs no job (the module's import) nothing is replaced.
/// Compiled delegate: the hook runs on every command lookup.
/// </para>
/// </summary>
internal static class JobProgramLookup
{
    /// <summary>
    /// Set while <c>Invoke-BashNative</c> looks a name up itself: it needs the program, not its stand-in, and starts
    /// it through <see cref="ChildStdio"/> anyway.
    /// </summary>
    [ThreadStatic] private static bool t_raw;

    /// <summary>Whether a lookup in progress is <c>Invoke-BashNative</c>'s own (<see cref="Raw{T}"/>): hooks leave it alone.</summary>
    internal static bool IsRaw => t_raw;

    public static void Install(CommandInvocationIntrinsics invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        invoke.PostCommandLookupAction = static (_, e) =>
        {
            if (t_raw || e.Command is not ApplicationInfo app) return;
            if (ProgramFile.Plan(app.Path) is { } launch)
            {
                e.CommandScriptBlock = ScriptBlock.Create(launch.StandInScript(ProgramFile.Shown(e.CommandName, app.Path)));
                return;
            }
            if (ScalarOverlay.Current is null || InvokeBashNativeCommand.IsBatchFile(app.Path)) return;
            e.CommandScriptBlock = ScriptBlock.Create(StandIn(app.Path));
        };
    }

    /// <summary>
    /// The program at <paramref name="path"/> through <c>Invoke-BashNative</c>, with the arguments and the pipeline
    /// input the command word was given. Input is passed record by record (a steppable pipeline), so the program reads
    /// it while it is produced; without input the program keeps the inherited stdin, as PowerShell would give it.
    /// </summary>
    internal static string StandIn(string path)
    {
        var p = PsBash.Core.Parser.PsBuild.SingleQuote(path);
        const string argv = PsBash.Core.Parser.PsBuild.ArgvSplat;
        return "begin { " + PsBash.Core.Parser.PsBuild.ArgvLiterals
               + "; if ($MyInvocation.ExpectingInput) { $__psbjS = { Invoke-BashNative -- " + p
               + " " + argv + " }.GetSteppablePipeline(); $__psbjS.Begin($true) } }\n"
               + "process { if ($__psbjS) { $__psbjS.Process($_) } }\n"
               + "end { if ($__psbjS) { $__psbjS.End() } else { Invoke-BashNative -- " + p + " " + argv + " } }";
    }

    /// <summary><paramref name="lookup"/> with this hook off (the caller gets the program itself).</summary>
    internal static T Raw<T>(Func<T> lookup)
    {
        bool was = t_raw;
        t_raw = true;
        try { return lookup(); }
        finally { t_raw = was; }
    }
}
