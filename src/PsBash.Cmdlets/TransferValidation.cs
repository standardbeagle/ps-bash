using System.Runtime.InteropServices;

namespace PsBash.Cmdlets;

/// <summary>
/// Pre-mutation validation shared by <c>cp</c> and <c>mv</c>. Every method only
/// INSPECTS the filesystem; the caller runs it before the first write/delete so
/// a refused operation leaves the tree untouched (the old mv deleted its own
/// source, and cp -rf deleted destination-only files, before noticing).
///
/// Rules follow GNU coreutils (oracle-checked):
/// <list type="bullet">
/// <item>Several sources need an existing directory destination.</item>
/// <item>Source and target must not be the same file.</item>
/// <item>A directory must not be copied/moved into itself or a subdirectory.</item>
/// <item>Directory over non-directory and non-directory over directory are refused.</item>
/// <item>mv only: a directory may replace an EMPTY directory, never a non-empty one.
/// cp merges instead, so a non-empty target directory is fine there.</item>
/// </list>
/// </summary>
internal static class TransferValidation
{
    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Error when <paramref name="sourceCount"/> sources cannot share <paramref name="destAbs"/>;
    /// null when the shape is valid. Several sources need an existing directory.</summary>
    public static string? CheckOperandShape(string cmd, int sourceCount, string destRaw, string destAbs)
    {
        if (sourceCount <= 1 || Directory.Exists(destAbs)) return null;
        var why = File.Exists(destAbs) ? "Not a directory" : "No such file or directory";
        return $"{cmd}: target '{destRaw}': {why}";
    }

    /// <summary>The path <paramref name="src"/> lands at: inside <paramref name="destAbs"/> when that
    /// is an existing directory (keeping the source's basename), else <paramref name="destAbs"/> itself.</summary>
    public static string ResolveTarget(string src, string destAbs, bool destIsDir) =>
        destIsDir
            ? Path.Combine(destAbs, Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            : destAbs;

    /// <summary>True when both paths name the same location once normalized.</summary>
    public static bool IsSamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    /// <summary>True when the paths differ only by letter case on a case-insensitive filesystem
    /// (<c>mv foo Foo</c> on Windows/macOS): that is a legitimate rename, not "same file".</summary>
    public static bool IsCaseOnlyRename(string src, string target) =>
        PathComparison == StringComparison.OrdinalIgnoreCase
        && IsSamePath(src, target)
        && !string.Equals(Normalize(src), Normalize(target), StringComparison.Ordinal);

    /// <summary>True when <paramref name="child"/> lies strictly inside <paramref name="parent"/>.</summary>
    public static bool IsStrictlyInside(string child, string parent)
    {
        var c = Normalize(child);
        var p = Normalize(parent);
        return c.Length > p.Length
            && c.StartsWith(p, PathComparison)
            && (c[p.Length] == Path.DirectorySeparatorChar || p.EndsWith(Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Identity checks that apply before any skip logic: same file, or a directory into itself.
    /// Returns the GNU-style diagnostic, or null when the pair is acceptable.
    /// </summary>
    public static string? CheckIdentity(string cmd, string src, bool srcIsDir, string target)
    {
        if (IsSamePath(src, target) && !(cmd == "mv" && IsCaseOnlyRename(src, target)))
            return $"{cmd}: '{src}' and '{target}' are the same file";
        if (srcIsDir && IsStrictlyInside(target, src))
            return cmd == "mv"
                ? $"mv: cannot move '{src}' to a subdirectory of itself, '{target}'"
                : $"cp: cannot copy a directory, '{src}', into itself, '{target}'";
        return null;
    }

    /// <summary>
    /// Type/occupancy conflicts against an EXISTING target. Returns a diagnostic or null.
    /// <paramref name="replaceEmptyDirOnly"/> is true for mv (a directory may only replace
    /// an empty one) and false for cp (directories merge).
    /// </summary>
    public static string? CheckOccupancy(string cmd, string src, bool srcIsDir, string target, bool replaceEmptyDirOnly)
    {
        bool targetIsDir = Directory.Exists(target);
        bool targetIsFile = !targetIsDir && File.Exists(target);
        if (!targetIsDir && !targetIsFile) return null;

        if (srcIsDir && targetIsFile)
            return $"{cmd}: cannot overwrite non-directory '{target}' with directory '{src}'";
        if (!srcIsDir && targetIsDir)
            return $"{cmd}: cannot overwrite directory '{target}' with non-directory";
        if (srcIsDir && targetIsDir && replaceEmptyDirOnly && !IsEmptyDirectory(target))
            return $"{cmd}: cannot overwrite '{target}': Directory not empty";
        return null;
    }

    public static bool IsEmptyDirectory(string dir)
    {
        using var e = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
        return !e.MoveNext();
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }
}
