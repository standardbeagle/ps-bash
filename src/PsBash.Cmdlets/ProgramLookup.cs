using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// "Does this command word run a program?" — the one question every cmdlet asks before it treats a stage as a program
/// (its pipeline input as bytes: <c>ConvertTo-BashNativeStdin</c>; the shell's stdin as its fd 0:
/// <c>Enter-BashNativeStdin</c>; a merged or job-environment start: <c>Invoke-BashNative</c>'s by-name runs; the
/// shell's stand-in: <see cref="Plain"/>).
/// <para>
/// <b>Why one primitive.</b> A runspace's command-lookup hook may hand back a stand-in for a program
/// (<see cref="NativeRunner"/>: a background job's always, the shell's while a scalar holds an invalid byte).
/// <c>InvokeCommand.GetCommand(name) is ApplicationInfo</c> then saw a function, so a pipe into the program got text
/// records (each gained a newline: <c>printf ab | /usr/bin/od -c</c> showed <c>a b \n</c>) and a program in a stdin
/// scope read the host's fd 0 instead of the scope's. The lookup here is done with the stand-in off
/// (<see cref="NativeRunner.Raw{T}"/>). The <c>Get-Command</c> cmdlet runs no lookup hook, so a script's
/// <c>Get-Command</c> sees the program anyway. Guard: <c>Core.Tests/ProgramLookupGuardTests</c>.
/// </para>
/// <para>
/// A program file the OS cannot start as it is (a <c>#!</c> script on Windows, no execute bit) is NOT a program here:
/// it runs as <see cref="ProgramFile"/>'s stand-in, which takes its records unchanged, as before.
/// </para>
/// </summary>
public static class ProgramLookup
{
    /// <summary>The program <paramref name="name"/> runs as it is, or null (a function, alias, cmdlet, script, unknown).</summary>
    internal static ApplicationInfo? Find(PSCmdlet cmdlet, string name)
    {
        ArgumentNullException.ThrowIfNull(cmdlet);
        return Find(cmdlet.InvokeCommand, name);
    }

    private static ApplicationInfo? Find(CommandInvocationIntrinsics invoke, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            return NativeRunner.Raw(() => invoke.GetCommand(name, CommandTypes.All)) is ApplicationInfo app
                   && ProgramFile.Plan(app.Path) is null
                ? app
                : null;
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// For the shell's stand-in (<see cref="NativeRunner.ShellStandIn"/>): the program at <paramref name="path"/> when
    /// PowerShell's launcher may start it with <paramref name="argv"/> (nothing it gets needs bytes the launcher would
    /// turn into U+FFFD, <see cref="ProcessScalars.ChildNeedsExactBytes"/>); null when <c>Invoke-BashNative</c> must.
    /// </summary>
    public static ApplicationInfo? Plain(EngineIntrinsics context, string path, string[]? argv)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ScalarOverlay.Current is not null || ProcessScalars.ChildNeedsExactBytes(argv ?? [])) return null;
        return Find(context.InvokeCommand, path);
    }
}
