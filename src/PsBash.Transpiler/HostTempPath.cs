namespace PsBash.Core;

/// <summary>
/// The temp directory for ps-bash's OWN scratch files (autoload index, process-substitution files, less pages),
/// fixed when the host starts. A script may assign <c>tmp=x.txt</c>; on Windows environment names are
/// case-insensitive, so that IS <c>TMP</c>, and <see cref="Path.GetTempPath"/> read at command time then points
/// into the script's cwd: the first unknown command created a directory named <c>x.txt</c> there and the script's
/// <c>cmd &gt;&amp;$tmp</c> failed with "Is a directory". User-visible temp (mktemp, <c>/tmp</c>) is not this.
/// The same holds for <c>XDG_RUNTIME_DIR</c>, which places the host's sockets and sidecars: <c>kill</c> run by a script
/// that exported another one still finds the hosts (<see cref="XdgRuntimeDir"/>).
/// Shared through AppContext so the psm1 and the runspace setup script read the same value.
/// </summary>
public static class HostTempPath
{
    public const string AppContextKey = "PsBash.HostTempPath";
    public const string XdgAppContextKey = "PsBash.HostXdgRuntimeDir";

    /// <summary>Records the current temp directory and XDG_RUNTIME_DIR, once per process. Call before any script runs.</summary>
    public static void Capture()
    {
        if (AppContext.GetData(AppContextKey) is not string)
            AppContext.SetData(AppContextKey, Path.GetTempPath());
        if (AppContext.GetData(XdgAppContextKey) is not string)
            AppContext.SetData(XdgAppContextKey, Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "");
    }

    /// <summary>The captured directory; <see cref="Path.GetTempPath"/> when nothing captured it (module used outside the host).</summary>
    public static string Get() => AppContext.GetData(AppContextKey) as string ?? Path.GetTempPath();

    /// <summary>The captured XDG_RUNTIME_DIR (null when unset then); the live variable when nothing captured it.</summary>
    public static string? XdgRuntimeDir()
        => AppContext.GetData(XdgAppContextKey) is string captured
            ? (captured.Length == 0 ? null : captured)
            : Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
}
