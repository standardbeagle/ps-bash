namespace PsBash.Core;

/// <summary>
/// The temp directory for ps-bash's OWN scratch files (autoload index, process-substitution files, less pages),
/// fixed when the host starts. A script may assign <c>tmp=x.txt</c>; on Windows environment names are
/// case-insensitive, so that IS <c>TMP</c>, and <see cref="Path.GetTempPath"/> read at command time then points
/// into the script's cwd: the first unknown command created a directory named <c>x.txt</c> there and the script's
/// <c>cmd &gt;&amp;$tmp</c> failed with "Is a directory". User-visible temp (mktemp, <c>/tmp</c>) is not this.
/// Shared through AppContext so the psm1 and the runspace setup script read the same value.
/// </summary>
public static class HostTempPath
{
    public const string AppContextKey = "PsBash.HostTempPath";

    /// <summary>Records the current temp directory, once per process. Call before any script runs.</summary>
    public static void Capture()
    {
        if (AppContext.GetData(AppContextKey) is not string)
            AppContext.SetData(AppContextKey, Path.GetTempPath());
    }

    /// <summary>The captured directory; <see cref="Path.GetTempPath"/> when nothing captured it (module used outside the host).</summary>
    public static string Get() => AppContext.GetData(AppContextKey) as string ?? Path.GetTempPath();
}
