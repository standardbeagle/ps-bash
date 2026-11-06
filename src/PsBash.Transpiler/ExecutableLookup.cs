namespace PsBash.Core;

/// <summary>
/// The ONE answer to "which file does this command name run?" — shared by the cmdlets (<c>test -x</c>),
/// the host, and embedders such as BeagleTerm, so none re-implements PATH search and drifts.
/// <para>
/// Mirrors <c>execvp</c> on Unix: a candidate must be a regular file with an execute bit, otherwise the
/// search continues down PATH. (A stray empty <c>~/.local/bin/env</c> once shadowed <c>/usr/bin/env</c>
/// in an embedder that took the first existing file; every spawn of it died with EACCES.) On Windows
/// the extension decides runnability, so a bare name is tried with each PATHEXT suffix.
/// </para>
/// </summary>
public static class ExecutableLookup
{
    private const UnixFileMode AnyExecute =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>True when <paramref name="path"/> is a file the OS may execute (Unix: any execute bit;
    /// Windows: the file exists — the extension is judged by the caller's suffix list). Directories are
    /// never executable files.</summary>
    public static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;
            return (File.GetUnixFileMode(path) & AnyExecute) != 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>First runnable <paramref name="command"/> in <paramref name="directories"/>, in order;
    /// null when none. Blank and malformed directory entries are skipped, never thrown on.</summary>
    public static string? Find(string command, IEnumerable<string> directories) =>
        FindAll(command, directories).FirstOrDefault();

    /// <summary>Every runnable match, PATH order (what <c>which -a</c> lists).</summary>
    public static IEnumerable<string> FindAll(string command, IEnumerable<string> directories)
    {
        if (string.IsNullOrEmpty(command)) yield break;
        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var hit in InDirectory(command, dir))
                yield return hit;
        }
    }

    private static IEnumerable<string> InDirectory(string command, string directory)
    {
        foreach (var suffix in Suffixes(command))
        {
            string candidate;
            try { candidate = Path.Combine(directory, command + suffix); }
            catch (ArgumentException) { yield break; }
            if (IsExecutableFile(candidate)) yield return candidate;
        }
    }

    /// <summary>Suffixes to try. Unix: none. Windows: a name WITHOUT an extension tries PATHEXT first
    /// (npm's extensionless sh script sits beside its <c>.cmd</c> shim and cannot be started), then the
    /// bare name; a name that has an extension is tried as given first.</summary>
    private static IEnumerable<string> Suffixes(string command)
    {
        if (!OperatingSystem.IsWindows()) { yield return ""; yield break; }
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (Path.HasExtension(command)) yield return "";
        foreach (var e in exts) yield return e;
        if (!Path.HasExtension(command)) yield return "";
    }
}
