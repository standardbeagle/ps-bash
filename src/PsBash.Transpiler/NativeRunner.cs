using PsBash.Core.Parser;

namespace PsBash.Core;

/// <summary>
/// A command word that resolved to a program, started by <c>Invoke-BashNative</c> (through <c>ChildStdio</c>) instead of
/// PowerShell's native-command processor. A runspace's command-lookup hook hands back <see cref="StandIn"/> for it:
/// a background job's always (its environment is the job's scalars, <c>JobProgramLookup</c>), the shell's while a
/// scalar a program would get holds a value the process environment cannot (<see cref="ProcessScalars.KeepsAnyExported"/>).
/// <para>
/// <b>Why the shell's.</b> PowerShell gives a program a copy of the process environment and marshals its argv with
/// plain UTF-8; on Linux both turn an escaped-byte marker (<see cref="RawBytes"/>) into U+FFFD, so
/// <c>export E=$'\xff'; sh -c 'printf %s "$E"'</c> wrote <c>ef bf bd</c> where bash writes <c>ff</c>.
/// <c>ChildStdio</c> builds the environment from the shell's exact values (<see cref="ShellExports.RemoveUnexported"/>)
/// and marshals both with <see cref="RawBytes"/>.
/// </para>
/// </summary>
public static class NativeRunner
{
    /// <summary>
    /// Set while <c>Invoke-BashNative</c> (or a cmdlet that runs a program by name) looks a name up itself: it needs
    /// the program, not the stand-in that would call it again.
    /// </summary>
    [ThreadStatic] private static bool t_raw;

    /// <summary>True inside <see cref="Raw{T}"/>: a lookup hook hands back the program itself.</summary>
    public static bool LookingUpRaw => t_raw;

    /// <summary><paramref name="lookup"/> with the stand-in off (the caller gets the program itself).</summary>
    public static T Raw<T>(Func<T> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        bool was = t_raw;
        t_raw = true;
        try { return lookup(); }
        finally { t_raw = was; }
    }

    /// <summary>
    /// The program at <paramref name="path"/> through <c>Invoke-BashNative</c>, with the arguments and the pipeline
    /// input the command word was given. Input is passed record by record (a steppable pipeline), so the program reads
    /// it while it is produced; without input the program keeps the inherited stdin, as PowerShell would give it.
    /// </summary>
    public static string StandIn(string path)
    {
        var p = PsBuild.SingleQuote(path);
        const string argv = PsBuild.ArgvSplat;
        return "begin { " + PsBuild.ArgvLiterals
               + "; if ($MyInvocation.ExpectingInput) { $__psbjS = { Invoke-BashNative -- " + p
               + " " + argv + " }.GetSteppablePipeline(); $__psbjS.Begin($true) } }\n"
               + "process { if ($__psbjS) { $__psbjS.Process($_) } }\n"
               + "end { if ($__psbjS) { $__psbjS.End() } else { Invoke-BashNative -- " + p + " " + argv + " } }";
    }
}
