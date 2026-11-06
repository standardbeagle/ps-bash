using System.Runtime.InteropServices;

namespace PsBash.Core;

/// <summary>
/// The one definition of "this file is a command a shell can run" and of the <c>$PATH</c> search built on it.
/// <para>
/// A command-name lookup is the same everywhere (ps-bash's interactive shell, <c>test -x</c>, traceroute, tab completion,
/// BeagleTerm's agent resolver): skip directories and files that are not runnable, ignore empty and malformed
/// <c>$PATH</c> entries, try the <c>PATHEXT</c> suffixes on Windows, and never throw. Each copy that re-derived this drifted:
/// BeagleTerm accepted the first file named <c>env</c> on <c>PATH</c> without checking its mode, so an empty,
/// non-executable <c>~/.local/bin/env</c> shadowed <c>/usr/bin/env</c> and every alias that used it failed with
/// "permission denied" (exit 13). The OS skips such a file and keeps searching; so does this.
/// </para>
/// <para>
/// Runnable means: a regular file (a directory is never a command) that, on Linux and macOS, has at least one execute
/// permission bit. Windows has no execute bit; there an existing file is runnable and the extension (PATHEXT) decides
/// which names are tried.
/// </para>
/// </summary>
public static class ExecutableLookup
{
    private static readonly string[] DefaultWindowsExtensions = [".EXE", ".CMD", ".BAT", ".COM"];

    /// <summary>
    /// True when <paramref name="path"/> is a regular file a shell could execute (see the type remarks). Never throws:
    /// an unreadable or malformed path is simply not runnable.
    /// </summary>
    /// <param name="path">A file path (absolute, or relative to the current directory).</param>
    public static bool IsRunnable(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;   // false for a directory too
            if (OperatingSystem.IsWindows()) return true;
            return (File.GetUnixFileMode(path)
                    & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The first runnable file for <paramref name="name"/> on <c>$PATH</c>, or null. A name containing a directory
    /// separator is a path, not searched (checked as-is, as the OS does).
    /// </summary>
    /// <param name="name">A bare command name such as <c>env</c> or <c>git</c> (or a path).</param>
    /// <param name="extraFirstDir">An optional directory searched before <c>$PATH</c> (the shell's working directory).</param>
    public static string? Find(string name, string? extraFirstDir = null)
        => Find(name, SearchDirectories(extraFirstDir));

    /// <summary>
    /// The first runnable file for <paramref name="name"/> in <paramref name="directories"/>, in order, or null.
    /// </summary>
    /// <param name="name">A bare command name (or a path, which is checked as-is).</param>
    /// <param name="directories">The directories to search, in priority order; empty or malformed entries are ignored.</param>
    public static string? Find(string name, IEnumerable<string> directories)
    {
        foreach (var hit in Enumerate(name, directories)) return hit;
        return null;
    }

    /// <summary>Every runnable file for <paramref name="name"/> on <c>$PATH</c>, in order (what <c>which -a</c> lists).</summary>
    /// <param name="name">A bare command name.</param>
    /// <param name="extraFirstDir">An optional directory searched before <c>$PATH</c>.</param>
    public static IReadOnlyList<string> FindAll(string name, string? extraFirstDir = null)
        => [.. Enumerate(name, SearchDirectories(extraFirstDir))];

    /// <summary>The directories of <c>$PATH</c> (empty entries dropped), preceded by <paramref name="extraFirstDir"/> when given.</summary>
    /// <param name="extraFirstDir">An optional directory placed first.</param>
    public static IEnumerable<string> SearchDirectories(string? extraFirstDir = null)
    {
        if (!string.IsNullOrEmpty(extraFirstDir)) yield return extraFirstDir;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return dir;
    }

    /// <summary>
    /// The names tried in each directory for <paramref name="name"/>: on Windows the PATHEXT suffixes then the exact name
    /// (so <c>git</c> finds <c>git.exe</c>, and <c>git.exe</c> itself still matches); elsewhere just the name.
    /// </summary>
    /// <param name="name">A bare command name.</param>
    public static IReadOnlyList<string> CandidateNames(string name)
    {
        if (!OperatingSystem.IsWindows()) return [name];
        var extensions = WindowsExtensions();
        var names = new List<string>(extensions.Count + 1);
        foreach (var ext in extensions) names.Add(name + ext);
        names.Add(name);
        return names;
    }

    private static IEnumerable<string> Enumerate(string name, IEnumerable<string> directories)
    {
        if (string.IsNullOrEmpty(name)) yield break;
        if (HasDirectorySeparator(name))
        {
            if (IsRunnable(name)) yield return name;
            yield break;
        }

        var candidates = CandidateNames(name);
        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var candidate in candidates)
            {
                string? full = null;
                try { full = Path.Combine(dir, candidate); }
                catch { /* malformed PATH entry */ }
                if (full is not null && IsRunnable(full)) yield return full;
            }
        }
    }

    private static bool HasDirectorySeparator(string name)
        => name.Contains('/') || (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && name.Contains('\\'));

    private static List<string> WindowsExtensions()
    {
        var raw = Environment.GetEnvironmentVariable("PATHEXT");
        var list = new List<string>();
        foreach (var ext in string.IsNullOrWhiteSpace(raw) ? DefaultWindowsExtensions : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!list.Contains(ext, StringComparer.OrdinalIgnoreCase)) list.Add(ext);
        return list;
    }
}
