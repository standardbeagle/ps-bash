using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// What bash reports for a command word that resolved to nothing runnable (the host's CommandNotFoundAction).
/// A bare name was searched on PATH: <c>NAME: command not found</c> (127). A word with a slash names a file, which
/// bash execs directly, so the report is the exec error: <c>No such file or directory</c> (127), <c>Is a
/// directory</c> (126), or <c>File name too long</c> (126, ENAMETOOLONG) when a component past an existing
/// directory is longer than NAME_MAX (255 bytes) or the whole word reaches PATH_MAX (4096). Every such word was
/// "command not found" (127), so <c>./0123…</c> (oils command_ "Name too long") had the wrong status.
/// </summary>
public static class CommandNotFoundReport
{
    /// <summary>Linux NAME_MAX: the longest file name component, in bytes.</summary>
    public const int NameMax = 255;

    /// <summary>Linux PATH_MAX: a path of this many bytes (with its NUL) is too long.</summary>
    public const int PathMax = 4096;

    /// <summary>
    /// The message without its source prefix (<c>NAME: REASON</c>; the caller adds <see cref="ShellDiagnostic"/>'s) and status for <paramref name="name"/>. <paramref name="directoryExists"/> answers for a prefix of the word as typed
    /// (relative to the shell's current directory).
    /// </summary>
    public static (string Message, int Status) Describe(string name, Func<string, bool> directoryExists)
    {
        if (name.IndexOfAny(['/', '\\']) < 0) return (NotFoundMessage(name), 127);
        if (RawBytes.GetByteCount(name) >= PathMax) return ($"{name}: File name too long", 126);
        var parts = name.Split('/', '\\');
        string prefix = "";
        for (int i = 0; i < parts.Length; i++)
        {
            if (RawBytes.GetByteCount(parts[i]) > NameMax) return ($"{name}: File name too long", 126);
            prefix = i == 0 ? parts[0] : prefix + "/" + parts[i];
            // Resolution stops at the first missing directory: `./nodir/LONG/x` is ENOENT, not ENAMETOOLONG.
            if (i < parts.Length - 1 && prefix.Length > 0 && !SafeExists(directoryExists, prefix)) break;
        }
        if (parts[^1].Length > 0 && SafeExists(directoryExists, name)) return ($"{name}: Is a directory", 126);
        return ($"{name}: No such file or directory", 127);
    }

    /// <summary>
    /// bash's <c>NAME: command not found</c> (no source prefix). A name with a character that does not print (a
    /// newline, a CR, an invalid byte) is shown in the <c>$'...'</c> form, as bash does (<c>$'a\nb': command not
    /// found</c>), so the message is one line that says which word was not found.
    /// </summary>
    public static string NotFoundMessage(string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] < ' ' || name[i] == '\u007f' || RawBytes.IsMarkerAt(name, i))
                return InvokeBashPrintfCommand.ShellQuote(name) + ": command not found";
        }
        return name + ": command not found";
    }

    /// <summary>
    /// <see cref="Describe(string, Func{string, bool})"/> against the file system, relative paths resolved under
    /// <paramref name="cwd"/> (the PowerShell location, not the process directory). Returns <c>[message, status]</c>
    /// for the PowerShell caller.
    /// </summary>
    public static object[] DescribeIn(string name, string cwd)
    {
        var (message, status) = Describe(name, p =>
        {
            string full = Path.IsPathRooted(p) ? p : Path.Combine(cwd, p);
            return Directory.Exists(full);
        });
        return [message, status];
    }

    private static bool SafeExists(Func<string, bool> directoryExists, string path)
    {
        try { return directoryExists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
