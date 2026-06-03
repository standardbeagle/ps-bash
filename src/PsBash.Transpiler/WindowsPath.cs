namespace PsBash.Core;

/// <summary>
/// Central Windows path mapper shared by the transpiler (<c>PsEmitter</c>) and
/// the runtime cmdlets (<c>PsBash.Cmdlets</c>) so the many path shapes an LLM or
/// a unix-trained user might type all resolve to one native Windows path.
/// Lives in the leaf <c>PsBash.Transpiler</c> assembly because that is the only
/// project both the emitter and the cmdlets reference (see the csproj comments) —
/// keeping the mapping rules in ONE place avoids the two assemblies drifting.
/// <para>
/// Recognized absolute shapes (drive letter is case-insensitive; <c>/</c> or
/// <c>\</c> separators are accepted and canonicalized to <c>\</c>):
/// </para>
/// <list type="bullet">
///   <item><c>C:\Users\x</c> / <c>C:/Users/x</c> / <c>c:/users/x</c> — native Windows (any sep/case)</item>
///   <item><c>/c/Users/x</c> / <c>/c</c> — MSYS / git-bash drive path</item>
///   <item><c>/mnt/c/Users/x</c> / <c>/mnt/c</c> — WSL drive path</item>
/// </list>
/// <para>
/// Pure string logic: AOT-safe, no I/O, no environment reads. Callers decide
/// WHEN to apply it — the emitter gates on <c>PSBASH_UNIX_PATHS</c> (opt-in for
/// wrappers), the runtime gates on actually running under Windows. Anything that
/// is NOT a recognized absolute Windows/unix drive path — a relative path, a UNC
/// path (<c>\\server\share</c> / <c>//server/share</c>), or a bare POSIX path
/// like <c>/usr/bin</c> / <c>/etc/hosts</c> — is returned UNCHANGED so we never
/// rewrite a path that is not ours to rewrite.
/// </para>
/// </summary>
public static class WindowsPath
{
    private static bool IsAsciiLetter(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>
    /// Try to map a <b>unix-style drive path</b> (<c>/c/...</c>, <c>/c</c>,
    /// <c>/mnt/c/...</c>, <c>/mnt/c</c>) to a canonical Windows drive path
    /// (<c>C:\...</c>). Returns <c>true</c> and sets <paramref name="result"/>
    /// when the input matched such a form; otherwise returns <c>false</c> and
    /// leaves <paramref name="result"/> equal to the input.
    /// <para>
    /// This is the narrow, low-risk transform: the matched shapes (<c>/X/</c>
    /// and <c>/mnt/X/</c>) are unambiguous and are never valid native Windows
    /// paths, so it is safe to apply even to general command operands. Native
    /// <c>X:</c> paths are intentionally NOT handled here (a token such as
    /// <c>a:b</c> is a colon-bearing string, not a drive path) — use
    /// <see cref="Normalize"/> for inputs already known to be file paths.
    /// </para>
    /// </summary>
    public static bool TryMapUnixDrivePath(string? path, out string result)
    {
        result = path ?? string.Empty;
        if (string.IsNullOrEmpty(path) || path[0] != '/')
            return false;

        // WSL: /mnt/<drive>[/...]
        const string mnt = "/mnt/";
        if (path.Length >= mnt.Length + 1
            && path.StartsWith(mnt, System.StringComparison.Ordinal)
            && IsAsciiLetter(path[mnt.Length])
            && (path.Length == mnt.Length + 1 || path[mnt.Length + 1] == '/'))
        {
            result = BuildDriveRoot(path[mnt.Length], path.Substring(mnt.Length + 1));
            return true;
        }

        // MSYS / git-bash: /<drive>[/...]
        if (path.Length >= 2 && IsAsciiLetter(path[1])
            && (path.Length == 2 || path[2] == '/'))
        {
            result = BuildDriveRoot(path[1], path.Substring(2));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Full canonicalization for an input <b>already known to be a file path</b>
    /// (a redirect target, a <c>cd</c> target, a resolved command operand). Maps
    /// unix drive paths via <see cref="TryMapUnixDrivePath"/>, and additionally
    /// canonicalizes a native drive path (<c>c:/users</c> → <c>C:\users</c>:
    /// uppercase drive letter, <c>/</c> → <c>\</c>). Relative paths, UNC paths,
    /// and bare POSIX paths are returned unchanged.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path ?? string.Empty;

        if (TryMapUnixDrivePath(path, out var mapped))
            return mapped;

        // Native drive: X:... (X:\foo, X:/foo, c:foo). The drive-relative form
        // (X:foo, no separator) is preserved — only case + separators change.
        if (path.Length >= 2 && IsAsciiLetter(path[0]) && path[1] == ':')
            return char.ToUpperInvariant(path[0]) + ":" + path.Substring(2).Replace('/', '\\');

        return path;
    }

    /// <summary>
    /// Build <c>X:\rest</c> from a drive letter and the remainder of a unix drive
    /// path (which begins with the post-drive segment, e.g. <c>/Users/x</c> or the
    /// empty string for a bare drive root). Separators are canonicalized to
    /// <c>\</c> and the single leading separator is folded into the <c>X:\</c>.
    /// </summary>
    private static string BuildDriveRoot(char driveLetter, string remainder)
    {
        var rest = remainder.Replace('/', '\\');
        if (rest.Length > 0 && rest[0] == '\\')
            rest = rest.Substring(1);
        return char.ToUpperInvariant(driveLetter) + ":\\" + rest;
    }
}
