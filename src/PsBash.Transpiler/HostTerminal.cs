using System.Runtime.CompilerServices;

namespace PsBash.Core;

/// <summary>
/// A program ps-bash would start on the host's terminal: what to run, where, and with which environment.
/// </summary>
/// <param name="Path">The resolved program file.</param>
/// <param name="Arguments">Its argv after the program name, one string each, exactly as a program receives them.</param>
/// <param name="WorkingDirectory">The session's location; null to inherit.</param>
/// <param name="Environment">The COMPLETE environment the program gets (process environment, the session's exports, a
/// job's scalars); a null value is a variable the program must not have.</param>
public sealed record HostTerminalLaunch(
    string Path,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);

/// <summary>
/// A terminal the HOST owns that a program may run on, for a host whose PowerShell has none of its own: an embedding
/// that runs the session in-process (a GUI, a daemon) gives every program PIPES, so one that needs a terminal - an
/// editor, a pager, <c>htop</c>, <c>git commit</c>'s editor, <c>clear</c> - fails ("TERM environment variable not set")
/// or hangs. See <c>docs/specs/host-terminal.md</c>.
/// </summary>
public interface IHostTerminal
{
    /// <summary>
    /// Runs <paramref name="launch"/> on the host's terminal - the program's stdin, stdout and stderr ARE the terminal,
    /// and it is that terminal's foreground job - and returns its exit status once it ends (128+N for a death by signal
    /// N), or null when the host will not run it there, and ps-bash starts it on pipes as before. Blocks until then.
    /// <paramref name="stop"/> is cancelled when the user stops the command; the host then ends the program and returns.
    /// </summary>
    int? Run(HostTerminalLaunch launch, CancellationToken stop);
}

/// <summary>
/// Which runspace has which <see cref="IHostTerminal"/>. A program goes to the terminal only when it would write
/// straight to the host's output and read no pipeline input: <c>vim x</c>, the <c>htop</c> in <c>clear &amp;&amp; htop</c>,
/// the editor <c>git commit</c> starts, every program of a loop that writes to the screen. One whose output is captured
/// (<c>$(git log)</c>, <c>$x = git branch</c>), piped (<c>ls | less</c> - for <c>less</c> only <c>ls</c> is captured),
/// or redirected keeps its pipes, as it would in bash, where it is not on the terminal either.
/// <para>
/// Registering installs nothing: the host also calls <c>[PsBash.Cmdlets.HostTerminalLookup]::Install(...)</c> once
/// in the runspace, after importing the module, so that a program PowerShell itself resolves runs through
/// <c>Invoke-BashNative</c>, which asks here.
/// </para>
/// </summary>
public static class HostTerminal
{
    // Keyed by the runspace object (PsBash.Transpiler has no reference to System.Management.Automation). Weak: a
    // closed runspace takes its registration with it.
    private static readonly ConditionalWeakTable<object, IHostTerminal> s_terminals = new();

    /// <summary>Gives <paramref name="runspace"/> the terminal <paramref name="terminal"/>; replaces any it had.</summary>
    public static void Register(object runspace, IHostTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(runspace);
        ArgumentNullException.ThrowIfNull(terminal);
        s_terminals.AddOrUpdate(runspace, terminal);
    }

    /// <summary>Takes <paramref name="runspace"/>'s terminal away: its programs run on pipes again.</summary>
    public static void Unregister(object runspace)
    {
        ArgumentNullException.ThrowIfNull(runspace);
        s_terminals.Remove(runspace);
    }

    /// <summary>The terminal of <paramref name="runspace"/>, if its host gave it one.</summary>
    public static IHostTerminal? For(object? runspace)
        => runspace is not null && s_terminals.TryGetValue(runspace, out var terminal) ? terminal : null;
}
