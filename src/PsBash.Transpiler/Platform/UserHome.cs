using System.Collections.Concurrent;

namespace PsBash.Core.Platform;

/// <summary>
/// The home directory of a login name: what bash's <c>~user</c> tilde prefix expands to (getpwnam's <c>pw_dir</c>).
/// Read from the passwd database file; null when the name has no entry (bash then leaves <c>~user</c> as written) and
/// always null on Windows, which has no passwd database (docs/specs/intentional-differences.md, <c>~user</c>).
/// <para>
/// Limit: only <c>/etc/passwd</c> is read, so an account that exists only in a network directory (LDAP, sssd, macOS
/// Directory Services) is not found and its <c>~user</c> stays literal.
/// </para>
/// </summary>
public static class UserHome
{
    /// <summary>The passwd database file every supported Unix keeps.</summary>
    private const string PasswdFile = "/etc/passwd";

    private static readonly ConcurrentDictionary<string, string?> s_cache = new(StringComparer.Ordinal);

    /// <summary>The home directory of <paramref name="user"/>, or null (no entry, or Windows).</summary>
    public static string? Lookup(string user)
    {
        if (OperatingSystem.IsWindows() || user.Length == 0) return null;
        return s_cache.GetOrAdd(user, static name =>
        {
            try
            {
                return File.Exists(PasswdFile) ? FromPasswd(File.ReadLines(PasswdFile), name) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        });
    }

    /// <summary>The home field (6th) of <paramref name="user"/>'s line in passwd text (test seam); the first entry wins,
    /// as with getpwnam.</summary>
    public static string? FromPasswd(IEnumerable<string> lines, string user)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var fields = line.Split(':');
            if (fields.Length >= 7 && fields[0] == user) return fields[5];
        }
        return null;
    }
}
