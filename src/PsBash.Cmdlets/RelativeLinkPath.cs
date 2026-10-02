namespace PsBash.Cmdlets;

/// <summary>
/// The text <c>ln -s -r</c> stores in a symbolic link (coreutils <c>ln.c</c> <c>convert_abs_rel</c>): the
/// target is canonicalized (every symbolic link resolved, a missing tail allowed), the directory that will
/// hold the link is canonicalized the same way, and the result is the relative path from the second to the
/// first. Pure apart from the link-reading seam, so the arithmetic is unit-tested without a disk.
/// </summary>
internal static class RelativeLinkPath
{
    /// <summary>GNU stops following links after a fixed number of hops (ELOOP); a loop leaves the path as reached.</summary>
    private const int MaxLinkHops = 40;

    /// <summary>Relative path (forward slashes) from <paramref name="linkDirectory"/> to <paramref name="target"/>,
    /// both given as full paths. <c>.</c> when they are the same directory. When no relative path exists (different
    /// roots on Windows) the canonical absolute target is returned.</summary>
    public static string Compute(string target, string linkDirectory, Func<string, string?> readLink)
    {
        var absTarget = Canonicalize(target, readLink);
        var absDir = Canonicalize(linkDirectory, readLink);
        var rel = Path.GetRelativePath(absDir, absTarget);
        return rel.Replace('\\', '/');
    }

    /// <summary>
    /// <c>canonicalize_filename_mode(CAN_MISSING)</c>: resolves <c>.</c>, <c>..</c> and every symbolic link of
    /// <paramref name="fullPath"/> physically (<c>..</c> applies AFTER a link in front of it was followed);
    /// components that do not exist are kept as they are.
    /// </summary>
    public static string Canonicalize(string fullPath, Func<string, string?> readLink)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        var pending = new LinkedList<string>(Split(fullPath.Substring(root.Length)));
        var current = root;
        int hops = 0;
        while (pending.First is { } node)
        {
            var part = node.Value;
            pending.RemoveFirst();
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                var parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                current = string.IsNullOrEmpty(parent) ? root : parent;
                continue;
            }

            var next = current.Length == 0 ? part : Path.Combine(current, part);
            var link = hops < MaxLinkHops ? readLink(next) : null;
            if (link is null) { current = next; continue; }

            hops++;
            var linkRoot = Path.GetPathRoot(link) ?? "";
            if (linkRoot.Length > 0)
            {
                current = linkRoot;
                link = link.Substring(linkRoot.Length);
            }
            var expanded = Split(link);
            for (int i = expanded.Length - 1; i >= 0; i--) pending.AddFirst(expanded[i]);
        }
        return current.Length == 0 ? "." : current;
    }

    private static string[] Split(string path) =>
        path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The real-filesystem link reader: the stored target text of a symbolic link or junction, else null.</summary>
    public static string? ReadLinkOnDisk(string path)
    {
        try
        {
            // GetAttributes sees a dangling link too (it reads the link itself, never its target).
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return null;
            return new FileInfo(path).LinkTarget;
        }
        catch
        {
            return null;
        }
    }
}
