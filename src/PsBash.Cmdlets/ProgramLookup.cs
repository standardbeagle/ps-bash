using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// "Does this command word run a program?" — the one question every cmdlet asks before it treats a stage as a program
/// (its pipeline input as bytes: <c>ConvertTo-BashNativeStdin</c>; the shell's stdin as its fd 0:
/// <c>Enter-BashNativeStdin</c>; a merged or job-environment start: <c>Invoke-BashNative</c>'s by-name runs).
/// <para>
/// <b>Why one primitive.</b> A runspace's command-lookup hook may hand back a stand-in for a program
/// (<see cref="NativeRunner.StandIn"/>: a background job's always, the shell's while an exported scalar holds an
/// invalid byte). A plain <c>GetCommand(name) is ApplicationInfo</c> then saw a function, so a pipe into the program
/// got text records (each gained a newline: <c>printf ab | /usr/bin/od -c</c> showed <c>a b \n</c>) and a program in a
/// stdin scope read the host's fd 0 instead of the scope's. The lookup here is done with the stand-in off
/// (<see cref="NativeRunner.Raw{T}"/>). Guard: <c>Core.Tests/ProgramLookupGuardTests</c>.
/// </para>
/// <para>
/// A program file the OS cannot start as it is (a <c>#!</c> script on Windows, no execute bit) is NOT a program here:
/// it runs as <see cref="ProgramFile"/>'s stand-in, which takes its records unchanged, as before.
/// </para>
/// </summary>
internal static class ProgramLookup
{
    /// <summary>The program <paramref name="name"/> runs as it is, or null (a function, alias, cmdlet, script, unknown).</summary>
    public static ApplicationInfo? Find(PSCmdlet cmdlet, string name)
    {
        ArgumentNullException.ThrowIfNull(cmdlet);
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            return NativeRunner.Raw(() => cmdlet.InvokeCommand.GetCommand(name, CommandTypes.All)) is ApplicationInfo app
                   && ProgramFile.Plan(app.Path) is null
                ? app
                : null;
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            return null;
        }
    }
}
