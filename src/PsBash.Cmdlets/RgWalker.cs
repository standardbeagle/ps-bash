namespace PsBash.Cmdlets;

/// <summary>
/// rg's directory walk: lazy, deterministic (files of a directory in ordinal order, then its sub-directories), with the
/// default prune set (<see cref="BashFileSystem.DefaultPrunedDirectories"/>) and hidden-entry filter applied BEFORE
/// descent, plus <c>--max-depth</c>, <c>-L/--follow</c> (symlink / junction loops are cut by a visited-set) and a
/// per-file filter (globs / types). Symbolic links are skipped unless following. Paths come out as
/// <c>rootDisplay + separator + relative</c> — "as typed", like ripgrep — or just the relative path when the root was
/// implicit (no operand).
/// </summary>
internal static class RgWalker
{
    internal sealed class Options
    {
        internal bool IncludeIgnored, IncludeHidden, Follow;
        internal int MaxDepth = -1;
        internal Func<string, bool>? Accept;
    }

    private static bool IsLink(FileSystemInfo info)
    {
        try { return info.LinkTarget is not null; }
        catch { return false; }
    }

    internal static IEnumerable<(string Abs, string Display)> Walk(string rootAbs, string rootDisplay, Options o)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Dir, string Display, int Depth)>();
        stack.Push((rootAbs, rootDisplay, 0));
        if (o.Follow) visited.Add(Canonical(rootAbs));

        while (stack.Count > 0)
        {
            var (dir, display, depth) = stack.Pop();

            string[] files;
            try { files = Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal).ToArray(); }
            catch { files = Array.Empty<string>(); }
            if (o.MaxDepth < 0 || depth + 1 <= o.MaxDepth)
            {
                foreach (var f in files)
                {
                    string name = Path.GetFileName(f);
                    if (!o.IncludeHidden && name.StartsWith('.')) continue;
                    if (!o.Follow && IsLink(new FileInfo(f))) continue;
                    if (o.Accept is { } accept && !accept(f)) continue;
                    yield return (f, Join(display, name));
                }
            }

            if (o.MaxDepth >= 0 && depth + 1 >= o.MaxDepth) continue;

            string[] subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir).OrderDescending(StringComparer.Ordinal).ToArray(); }
            catch { subdirs = Array.Empty<string>(); }
            foreach (var sd in subdirs)
            {
                string name = Path.GetFileName(sd);
                if (!o.IncludeHidden && name.StartsWith('.')) continue;
                if (!o.IncludeIgnored && BashFileSystem.DefaultPrunedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (IsLink(new DirectoryInfo(sd)))
                {
                    if (!o.Follow) continue;
                    if (!visited.Add(Canonical(sd))) continue;   // loop
                }
                stack.Push((sd, Join(display, name), depth + 1));
            }
        }
    }

    private static string Canonical(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return Path.GetFullPath(target?.FullName ?? info.FullName);
        }
        catch { return dir; }
    }

    private static string Join(string display, string name)
    {
        if (display.Length == 0) return name;
        char last = display[^1];
        return last == '/' || last == '\\' ? display + name : display + Path.DirectorySeparatorChar + name;
    }
}
