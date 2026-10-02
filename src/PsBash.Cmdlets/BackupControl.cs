namespace PsBash.Cmdlets;

/// <summary>GNU backup types (gnulib <c>backupfile.c</c>).</summary>
public enum BackupKind
{
    /// <summary>No backup is made.</summary>
    None,

    /// <summary><c>NAME~</c> (suffix from <c>-S</c> / <c>SIMPLE_BACKUP_SUFFIX</c>).</summary>
    Simple,

    /// <summary><c>NAME.~N~</c>, N one above the highest existing.</summary>
    Numbered,

    /// <summary>Numbered when a numbered backup of NAME already exists, simple otherwise.</summary>
    Existing,
}

/// <summary>
/// The backup options shared by <c>cp</c>, <c>mv</c> and <c>ln</c> (<c>-b</c>, <c>--backup[=CONTROL]</c>,
/// <c>-S</c>/<c>--suffix</c>, the <c>VERSION_CONTROL</c> and <c>SIMPLE_BACKUP_SUFFIX</c> environment).
/// Oracle: coreutils 9.4. <c>-b</c> takes no argument; <c>--backup</c> takes an optional attached one;
/// <c>-S</c> alone ALSO turns backups on (the backup type then comes from the environment). A control
/// word is matched like GNU <c>XARGMATCH</c> (<see cref="GnuArgMatch"/>), an empty word means "use the
/// environment", and a bad <c>VERSION_CONTROL</c> is an error worded for <c>$VERSION_CONTROL</c>.
/// </summary>
internal static class BackupControl
{
    private static readonly (string Name, BackupKind Value)[] Words =
    {
        ("none", BackupKind.None), ("off", BackupKind.None),
        ("simple", BackupKind.Simple), ("never", BackupKind.Simple),
        ("existing", BackupKind.Existing), ("nil", BackupKind.Existing),
        ("numbered", BackupKind.Numbered), ("t", BackupKind.Numbered),
    };

    private const string ValidBlock =
        "  - 'none', 'off'\n  - 'simple', 'never'\n  - 'existing', 'nil'\n  - 'numbered', 't'";

    /// <summary>Resolves the backup type of one command line. <paramref name="control"/> is the
    /// <c>--backup=CONTROL</c> word (null for a bare <c>-b</c>/<c>--backup</c>/<c>-S</c>).</summary>
    public static bool TryResolve(string command, string? control, Func<string, string?> getenv,
        out BackupKind kind, out string? error)
    {
        error = null;
        if (!string.IsNullOrEmpty(control))
            return GnuArgMatch.TryMatch(command, "backup", control, Words, ValidBlock, out kind, out error, "backup type");

        var env = getenv("VERSION_CONTROL");
        if (string.IsNullOrEmpty(env)) { kind = BackupKind.Existing; return true; }
        return GnuArgMatch.TryMatch(command, "backup", env, Words, ValidBlock, out kind, out error, "$VERSION_CONTROL");
    }

    /// <summary>The simple-backup suffix: <c>-S</c>, else <c>SIMPLE_BACKUP_SUFFIX</c>, else <c>~</c>.</summary>
    public static string Suffix(string? option, Func<string, string?> getenv)
    {
        if (!string.IsNullOrEmpty(option)) return option;
        var env = getenv("SIMPLE_BACKUP_SUFFIX");
        return string.IsNullOrEmpty(env) ? "~" : env;
    }

    /// <summary>
    /// The text to append to <paramref name="path"/> to name its backup (<c>~</c>, <c>.~3~</c>), or null
    /// when no backup is made. <paramref name="listSiblings"/> returns the file names in the directory
    /// (a seam so the numbering is testable without a disk).
    /// </summary>
    public static string? SuffixFor(string path, BackupKind kind, string simpleSuffix, Func<string, IEnumerable<string>> listSiblings)
    {
        if (kind == BackupKind.None) return null;
        if (kind == BackupKind.Simple) return simpleSuffix;

        int highest = HighestNumber(path, listSiblings);
        if (kind == BackupKind.Existing && highest == 0) return simpleSuffix;
        return $".~{highest + 1}~";
    }

    /// <summary>Highest N among the existing <c>NAME.~N~</c> siblings (0 when none).</summary>
    internal static int HighestNumber(string path, Func<string, IEnumerable<string>> listSiblings)
    {
        var name = Path.GetFileName(path);
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        int highest = 0;
        var prefix = name + ".~";
        foreach (var sibling in listSiblings(dir))
        {
            if (!sibling.StartsWith(prefix, StringComparison.Ordinal) || !sibling.EndsWith('~')
                || sibling.Length <= prefix.Length + 1) continue;
            var digits = sibling.Substring(prefix.Length, sibling.Length - prefix.Length - 1);
            if (digits.All(char.IsAsciiDigit) && int.TryParse(digits, out var n) && n > highest) highest = n;
        }
        return highest;
    }

    /// <summary>The real-filesystem sibling lister.</summary>
    public static IEnumerable<string> ListDirectory(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Moves the existing <paramref name="path"/> to its backup name and returns the suffix used (null
    /// when the type makes none). Throws on I/O failure; the caller reports it.
    /// </summary>
    public static string? MakeBackup(string path, BackupKind kind, string simpleSuffix)
    {
        var suffix = SuffixFor(path, kind, simpleSuffix, ListDirectory);
        if (suffix is null) return null;
        var backup = path + suffix;
        if (Directory.Exists(backup)) FileSystemHelpers.DeleteDirectoryForce(backup);
        else if (File.Exists(backup)) FileSystemHelpers.DeleteFileForce(backup);
        if (Directory.Exists(path)) Directory.Move(path, backup);
        else File.Move(path, backup);
        return suffix;
    }
}
