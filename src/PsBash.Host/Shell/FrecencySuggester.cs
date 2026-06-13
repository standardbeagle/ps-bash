namespace PsBash.Host.Shell;

/// <summary>
/// Inline-suggestion (ghost text) provider for the zoxide-style jump commands.
/// For a <c>cd</c> / <c>z</c> / <c>zi</c> line it returns the suffix that completes
/// the line to the highest-frecency directory, so the gray ghost previews where the
/// jump will land. Append-only (the editor appends the suffix), so it only fires
/// where appending is correct:
/// <list type="bullet">
/// <item>empty argument (<c>cd&#160;</c> / <c>z&#160;</c>): suggest the full path of the
/// top directory — accepting yields <c>cd /full/path</c> (z's path-passthrough cd's there).</item>
/// <item><c>z &lt;kw&gt;</c> / <c>zi &lt;kw&gt;</c>: complete the keyword to the matching
/// directory's final component — <c>z &lt;basename&gt;</c> still resolves through frecency.</item>
/// </list>
/// <c>cd &lt;partial&gt;</c> with a non-empty token is left to path/history completion (a bare
/// basename is not a guaranteed subdirectory of the cwd). Path-like tokens are skipped entirely.
/// </summary>
internal sealed class FrecencySuggester
{
    private readonly IFrecencyStore _store;

    public FrecencySuggester(IFrecencyStore store) => _store = store;

    public async Task<string?> SuggestSuffixAsync(string line)
    {
        if (!TrySplit(line, out var cmd, out var arg)) return null;
        if (arg.Contains(' ')) return null;                 // multi-keyword → Tab handles it
        if (ArgLooksLikePath(arg)) return null;             // literal path typing → base handles it

        var keywords = arg.Length == 0 ? Array.Empty<string>() : new[] { arg };
        IReadOnlyList<FrecencyMatch> matches;
        try { matches = await _store.QueryAsync(keywords, limit: 1).ConfigureAwait(false); }
        catch { return null; }
        if (matches.Count == 0) return null;

        var path = matches[0].Path;

        // Empty argument: append the full path (valid for cd and z alike).
        if (arg.Length == 0) return path;

        // Non-empty keyword: only z/zi complete to the matched basename (z resolves a
        // bare basename through frecency; cd would need it to be a literal subdir).
        if (cmd is "z" or "zi")
        {
            var basename = LastSegment(path);
            if (basename.Length > arg.Length && basename.StartsWith(arg, StringComparison.OrdinalIgnoreCase))
                return basename[arg.Length..];
        }
        return null;
    }

    // Split "<cmd> <arg-so-far>" where cmd is a jump command. The arg is everything
    // after the first whitespace run (possibly empty when the line ends in a space).
    private static bool TrySplit(string line, out string cmd, out string arg)
    {
        cmd = ""; arg = "";
        int sp = line.IndexOf(' ');
        if (sp < 0) return false;                            // no argument region yet
        cmd = line[..sp];
        if (cmd is not ("cd" or "z" or "zi")) return false;
        arg = line[(sp + 1)..].TrimStart();
        return true;
    }

    private static bool ArgLooksLikePath(string token)
        => token.Length > 0
           && (token.Contains('/') || token.Contains('\\') || token[0] == '~'
               || token == "." || token == ".."
               || (token.Length >= 2 && token[1] == ':'));

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        int slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }
}
