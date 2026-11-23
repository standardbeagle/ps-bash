namespace PsBash.Core.Platform;

/// <summary>
/// The home directory of a login name: what bash's <c>~user</c> tilde prefix expands to (getpwnam's <c>pw_dir</c>).
/// Read from the passwd database file; null when the name has no entry (bash then leaves <c>~user</c> as written) and
/// always null on Windows, which has no passwd database (docs/specs/intentional-differences.md, <c>~user</c>).
/// <para>
/// The parsed file is kept only while its write time and length are unchanged, so an account added or changed in a
/// long-lived host is seen on the next lookup (a miss is never remembered on its own).
/// </para>
/// <para>
/// Limit: only <c>/etc/passwd</c> is read, so an account that exists only in a network directory (LDAP, sssd, macOS
/// Directory Services) is not found and its <c>~user</c> stays literal.
/// </para>
/// </summary>
public static class UserHome
{
    /// <summary>The passwd database file every supported Unix keeps.</summary>
    private const string PasswdFile = "/etc/passwd";

    /// <summary>The homes of one version of the passwd file, identified by its write time and length.</summary>
    private sealed record Snapshot(DateTime WriteTimeUtc, long Length, Dictionary<string, string> Homes);

    private static volatile Snapshot? s_snapshot;

    /// <summary>The home directory of <paramref name="user"/>, or null (no entry, or Windows).</summary>
    public static string? Lookup(string user)
    {
        if (OperatingSystem.IsWindows() || user.Length == 0) return null;
        try
        {
            var info = new FileInfo(PasswdFile);
            if (!info.Exists) return null;
            var snapshot = s_snapshot;
            if (snapshot is null || snapshot.WriteTimeUtc != info.LastWriteTimeUtc || snapshot.Length != info.Length)
                s_snapshot = snapshot = new Snapshot(info.LastWriteTimeUtc, info.Length, Parse(File.ReadLines(PasswdFile)));
            return snapshot.Homes.TryGetValue(user, out var home) ? home : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The home field (6th) of <paramref name="user"/>'s line in passwd text (test seam); the first entry wins,
    /// as with getpwnam.</summary>
    public static string? FromPasswd(IEnumerable<string> lines, string user)
        => Parse(lines).TryGetValue(user, out var home) ? home : null;

    private static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var homes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var fields = line.Split(':');
            if (fields.Length >= 7) homes.TryAdd(fields[0], fields[5]);
        }
        return homes;
    }
}
