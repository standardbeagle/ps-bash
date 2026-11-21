using System.Globalization;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// How long a framed-IPC host waits with no connection before it exits (docs/specs/host-lifecycle-contract.md). A
/// launcher that wants a shorter window for one host it starts hands it <see cref="Arg"/> on that host's command line;
/// <see cref="EnvVar"/> is the user's setting for every host. The launcher once lowered the window by setting
/// <see cref="EnvVar"/> in its own process around the spawn: the process environment is shared, so a host or child
/// another thread started meanwhile inherited 60 s, and the restore raced (guard ProcessEnvironmentWriteGuardTests).
/// </summary>
public static class HostIdleWindow
{
    /// <summary>The host argument (<c>--idle-secs=N</c>) that sets this host's window; it wins over <see cref="EnvVar"/>.</summary>
    public const string Arg = "--idle-secs";

    /// <summary>The environment variable every host reads when no <see cref="Arg"/> was given.</summary>
    public const string EnvVar = "PSBASH_HOST_IDLE_SECS";

    /// <summary>The window when neither is set.</summary>
    public const int DefaultSecs = 600;

    /// <summary>A nested (spill-slot) daemon's window: it only serves repeated nested calls, and nothing reaps it.</summary>
    public const int NestedSecs = 60;

    /// <summary>
    /// The host's window from its <see cref="Arg"/> value (null when absent) and <see cref="EnvVar"/> value: the first
    /// positive integer of the two, else <see cref="DefaultSecs"/>.
    /// </summary>
    public static TimeSpan Resolve(string? argValue, string? envValue)
        => TimeSpan.FromSeconds(PositiveSecs(argValue) ?? PositiveSecs(envValue) ?? DefaultSecs);

    /// <summary>
    /// The <see cref="Arg"/> a launcher passes to a nested daemon it starts: <see cref="NestedSecs"/>, or the user's
    /// <see cref="EnvVar"/> (<paramref name="envValue"/>) when that is already shorter.
    /// </summary>
    public static string NestedHostArgument(string? envValue)
    {
        int secs = PositiveSecs(envValue) is int user && user < NestedSecs ? user : NestedSecs;
        return Arg + "=" + secs.ToString(CultureInfo.InvariantCulture);
    }

    private static int? PositiveSecs(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : null;
}
