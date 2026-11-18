namespace PsBash.Testing;

/// <summary>
/// A path with every symlinked component resolved, as realpath(3) and getcwd(3) report it. A test that expects
/// such output builds the expectation from this, never from <c>Path.GetTempPath()</c> as typed: on macOS the temp
/// dir is <c>/var/folders/…</c> and <c>/var</c> is a link to <c>/private/var</c>, so the canonical answer starts
/// <c>/private/var</c>. REGRESSION (publish v0.15.0, macOS): eight readlink/realpath tests expected the typed
/// spelling while the commands answered correctly. Guard: <c>Core.Tests/PhysicalPathGuardTests</c>. The product copy is
/// <c>PsBash.Core.PhysicalPath</c> (this assembly takes no repo references by design); keep the two in step.
/// </summary>
public static class PhysicalPath
{
    /// <summary>Resolves each existing component in turn (a missing tail is kept as typed).</summary>
    public static string Of(string path) => Of(path, depth: 0);

    // A link's target is a path as written: it can run through further links (a link made to /var/folders/x on
    // macOS), so it is resolved again. 40 = Linux's MAXSYMLINKS; a loop stops there unresolved.
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
