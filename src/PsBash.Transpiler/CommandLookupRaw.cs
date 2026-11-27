namespace PsBash.Core;

/// <summary>
/// Set while <c>Invoke-BashNative</c> looks a command name up itself: it needs the program, not the stand-in a
/// command-lookup hook would give it (the host's <c>GetVerbFallbackGuard</c>, a job's <c>JobProgramLookup</c>, an
/// embedding's <c>HostTerminalLookup</c>), and starts the program itself anyway. Every hook that replaces a program
/// leaves such a lookup alone: the stand-in calls <c>Invoke-BashNative</c>, so a hook that answered its lookup with the
/// stand-in again started the cmdlet over until the call depth overflowed (a <c>.cmd</c> by command word in the host).
/// Spec <c>program-files.md</c> §Batch files.
/// </summary>
public static class CommandLookupRaw
{
    [ThreadStatic] private static bool t_raw;

    /// <summary>Whether a lookup in progress is <c>Invoke-BashNative</c>'s own (<see cref="Run{T}"/>): hooks leave it alone.</summary>
    public static bool Active => t_raw;

    /// <summary><paramref name="lookup"/> with every program-replacing hook off (the caller gets the program itself).</summary>
    public static T Run<T>(Func<T> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        bool was = t_raw;
        t_raw = true;
        try { return lookup(); }
        finally { t_raw = was; }
    }
}
