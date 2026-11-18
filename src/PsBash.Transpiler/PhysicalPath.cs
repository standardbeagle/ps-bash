namespace PsBash.Core;

/// <summary>
/// A path with every symlinked component resolved, as realpath(3) and getcwd(3) report it. <c>cd</c> uses it to tell
/// whether the logical directory it went by still names the process cwd (<c>BashRuntime.CdBase</c>): on macOS getcwd
/// answers <c>/private/var/…</c> for a directory reached as <c>/var/…</c>. The test assembly PsBash.Testing keeps a copy
/// (it takes no repo references by design); keep the two in step.
/// </summary>
public static class PhysicalPath
{
    /// <summary>Resolves each existing component in turn (a missing tail is kept as typed).</summary>
    public static string Of(string path) => Of(path, depth: 0);

    // A link's target is a path as written: it can run through further links, so it is resolved again.
    // 40 = Linux's MAXSYMLINKS; a loop stops there unresolved.
    private static string Of(string path, int depth)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var segment in full[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = depth < 40 ? Of(target.FullName, depth + 1) : target.FullName;
            }
            catch (IOException) { /* not a link / unreadable: keep the component */ }
            catch (UnauthorizedAccessException) { }
        }
        return current.Length > root.Length ? current.TrimEnd(Path.DirectorySeparatorChar) : current;
    }
}
