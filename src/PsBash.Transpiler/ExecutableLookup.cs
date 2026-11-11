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

    /// <summary>The directories of $PATH (empty entries dropped), preceded by <paramref name="firstDir"/> when given
    /// (the shell's working directory, searched before PATH).</summary>
    public static IEnumerable<string> PathDirectories(string? firstDir = null)
    {
        if (!string.IsNullOrEmpty(firstDir)) yield return firstDir;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return dir;
    }

    /// <summary>
    /// A PATH value as bash scripts write it (<c>PATH=_tmp:$PATH</c>, <c>PATH="$HOME/bin:$PATH"</c>) in Windows form:
    /// each <c>:</c> that separates entries becomes <c>;</c>. A drive colon stays: one letter at the start of an
    /// entry followed by <c>\</c>, <c>/</c> or the end (<c>C:\x</c>, <c>C:/x</c>, <c>C:</c>). Without this,
    /// <c>_tmp:C:\Program Files\PowerShell\7</c> is ONE entry and hides both directories from command lookup.
    /// </summary>
    public static string ToWindowsPathList(string value)
    {
        if (value.IndexOf(':') < 0) return value;
        var sb = new System.Text.StringBuilder(value.Length);
        int entryStart = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == ';') { sb.Append(c); entryStart = i + 1; continue; }
            if (c == ':')
            {
                bool driveColon = i - entryStart == 1 && char.IsAsciiLetter(value[entryStart])
                    && (i + 1 == value.Length || value[i + 1] is '\\' or '/' or ';' or ':');
                if (!driveColon) { sb.Append(';'); entryStart = i + 1; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
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
