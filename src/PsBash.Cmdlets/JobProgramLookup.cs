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

    public static void Install(CommandInvocationIntrinsics invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        invoke.PostCommandLookupAction = static (_, e) =>
        {
            if (NativeRunner.LookingUpRaw || e.Command is not ApplicationInfo app) return;
            if (ProgramFile.Plan(app.Path) is { } launch)
            {
                e.CommandScriptBlock = ScriptBlock.Create(launch.StandInScript(ProgramFile.Shown(e.CommandName, app.Path)));
                return;
            }
            if (ScalarOverlay.Current is null || InvokeBashNativeCommand.IsBatchFile(app.Path)) return;
            e.CommandScriptBlock = ScriptBlock.Create(NativeRunner.StandIn(app.Path));
        };
    }

    /// <summary><paramref name="lookup"/> with the stand-in off, in this hook and the shell's (<see cref="NativeRunner.Raw{T}"/>).</summary>
    internal static T Raw<T>(Func<T> lookup) => NativeRunner.Raw(lookup);
}
