using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// The text conventions of a glob pattern the transpiler hands to <c>ConvertTo-BashGlob</c>. Public because
/// emitted PowerShell reaches it as <c>[PsBash.Cmdlets.BashGlobText]::Escape(...)</c>.
/// </summary>
public static class BashGlobText
{
    /// <summary>
    /// Makes <paramref name="literal"/> inert inside a glob pattern: a quoted or escaped <c>* ? [ \</c> must
    /// match itself (<c>"*"</c>, <c>\*</c>, <c>"$x"</c> where x holds a star). The pattern dialect is bash's own:
    /// <c>\</c> escapes the next character and <c>/</c> separates components.
    /// </summary>
    public static string Escape(string? literal)
    {
        if (string.IsNullOrEmpty(literal)) return literal ?? string.Empty;
        if (literal.AsSpan().IndexOfAny("*?[\\") < 0) return literal;
        var sb = new StringBuilder(literal.Length + 4);
        foreach (char c in literal)
        {
            if (c is '*' or '?' or '[' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>bash <c>${v@Q}</c>. Emitted PowerShell reaches it as <c>[PsBash.Cmdlets.BashQuoteText]::ForReuse(...)</c>.</summary>
public static class BashQuoteText
{
    /// <summary>Scalar <c>${v@K}</c> / <c>${v@k}</c> (quoted value) and <c>${v@A}</c> (the assignment that recreates it); "" when unset.</summary>
    public static string Transform(char op, string name, object? psVar)
    {
        if (psVar is System.Collections.IDictionary or System.Collections.IList) return "";
        string? env = BashVariableStore.Get(name);
        if (env is null) return "";
        string q = ForReuse(env);
        if (op != 'A') return q;
        string attrs = Attributes(name, psVar);
        return (attrs.Length == 0 ? "" : "declare -" + attrs + " ") + name + "=" + q;
    }

    /// <summary>The <c>${v@a}</c> attribute letters: <c>a</c>/<c>A</c> for an array kind, then <c>i</c>, <c>r</c>, <c>x</c>.</summary>
    public static string Attributes(string name, object? value)
    {
        string kind = value is System.Collections.IDictionary ? "A" : value is System.Collections.IList ? "a" : "";
        return kind + BashShellState.AttributeLetters(name);
    }

    /// <summary>Single-quoted form (<c>'</c> written as <c>'\''</c>); a control character switches to <c>$'...'</c>.</summary>
    public static string ForReuse(string? value)
    {
        value ??= string.Empty;
        foreach (char c in value)
            if (c < ' ' || c == '\u007f') return InvokeBashPrintfCommand.ShellQuote(value);
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// Quote removal on a word read at run time (an assoc subscript named through <c>${!ref}</c>: <c>"al"e</c> is
    /// <c>ale</c>). Single quotes keep everything; inside double quotes a backslash escapes only <c>$ ` " \</c>;
    /// outside quotes a backslash escapes the next character. No expansion is done.
    /// </summary>
    public static string RemoveQuotes(string? word)
    {
        if (string.IsNullOrEmpty(word)) return string.Empty;
        var sb = new System.Text.StringBuilder(word.Length);
        char quote = '\0';
        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0'; else sb.Append(c);
            }
            else if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else if (c == '\\' && i + 1 < word.Length && word[i + 1] is '$' or '`' or '"' or '\\') sb.Append(word[++i]);
                else sb.Append(c);
            }
            else if (c is '\'' or '"') quote = c;
            else if (c == '\\' && i + 1 < word.Length) sb.Append(word[++i]);
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Pathname expansion (bash "Filename Expansion") of ONE word: the pure engine behind
/// <c>ConvertTo-BashGlob</c> and <c>ConvertTo-BashWords</c>.
///
/// <para>Rules (bash 5.2, <c>nullglob</c>/<c>dotglob</c> off by default): the pattern is split into
/// <c>/</c>-separated components; a component with a glob character is matched against the entries of the
/// directory reached so far (<c>*</c> <c>?</c> <c>[set]</c> with <c>!</c>/<c>^</c> negation, ranges and
/// <c>[:class:]</c>), a plain component is just appended. A name that starts with <c>.</c> is matched only
/// by a pattern component that starts with a literal <c>.</c> (unless <c>dotglob</c>), and <c>.</c> /
/// <c>..</c> are never matched by a glob. Matches within a directory are sorted in ordinal (byte) order,
/// depth first, so the result equals the C / C.UTF-8 collation of the oracle. Results keep the form the
/// pattern was written in: a relative pattern gives relative paths, an absolute one absolute paths, a
/// trailing <c>/</c> keeps only directories and is kept. No match yields an empty list (the caller keeps
/// the literal word, or applies <c>nullglob</c> / <c>failglob</c>).</para>
///
/// <para><c>globstar</c>: a <c>**</c> component matches zero or more directory levels (dot-directories only under
/// <c>dotglob</c>, symlinked directories are not followed); as the last component it matches every file and
/// directory below. Its results are sorted as a whole, like bash. <c>nocaseglob</c> compares case-insensitively
/// (ordinal lower-casing). Not implemented: collation order other than ordinal.</para>
/// </summary>
internal static class BashGlob
{
    /// <summary>True when <paramref name="pattern"/> has an unescaped <c>*</c>, <c>?</c> or a <c>[..]</c> class.</summary>
    public static bool HasPattern(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\') { i++; continue; }
            if (c is '*' or '?') return true;
            if (c is '+' or '@' or '!' && i + 1 < pattern.Length && pattern[i + 1] == '(') return true;
            if (c == '[' && i + 2 <= pattern.Length && pattern.IndexOf(']', i + 2) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>The literal text of a pattern with its backslash escapes removed (what an unmatched pattern prints as).</summary>
    public static string Unescape(string pattern)
    {
        if (pattern.IndexOf('\\') < 0) return pattern;
        var sb = new StringBuilder(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\' && i + 1 < pattern.Length) i++;
            sb.Append(pattern[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A directory separator at <c>s[i]</c>: an unescaped <c>/</c>, or on Windows a LITERAL backslash (the escaped pair
    /// <c>\\</c>, from a quoted or escaped one: <c>'C:\dir\'*.txt</c>). No Windows file name can hold a backslash, so
    /// it can only mean a separator; it used to be matched as a name character and the pattern never expanded.
    /// </summary>
    private static bool IsSeparatorAt(string s, int i, out int width)
    {
        if (s[i] == '/') { width = 1; return true; }
        if (OperatingSystem.IsWindows() && s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '\\') { width = 2; return true; }
        width = 0;
        return false;
    }

    /// <summary>Splits on separators (<see cref="IsSeparatorAt"/>); each part keeps the separator that followed it, so a match prints as typed.</summary>
    private static List<(string Comp, char Sep)> SplitComponents(string rest)
    {
        var parts = new List<(string, char)>();
        var cur = new StringBuilder();
        for (int i = 0; i < rest.Length; i++)
        {
            if (IsSeparatorAt(rest, i, out int w)) { parts.Add((cur.ToString(), rest[i])); cur.Clear(); i += w - 1; continue; }
            char c = rest[i];
            if (c == '\\' && i + 1 < rest.Length) { cur.Append(c).Append(rest[++i]); continue; }
            cur.Append(c);
        }
        parts.Add((cur.ToString(), '/'));
        return parts;
    }

    /// <summary>The index just past the run of separators starting at <paramref name="i"/>.</summary>
    private static int SkipSeparators(string s, int i)
    {
        while (i < s.Length && IsSeparatorAt(s, i, out int w)) i += w;
        return i;
    }

    /// <summary>Matches of <paramref name="pattern"/>, resolved against <paramref name="cwd"/>; empty when nothing matches.</summary>
    public static List<string> Expand(string pattern, string cwd, bool dotGlob, bool globStar = false, bool noCase = false)
    {
        var ignore = GlobIgnorePatterns();
        if (ignore is null) return ExpandCore(pattern, cwd, dotGlob, globStar, noCase);
        // GLOBIGNORE set (non-empty): dotfiles become matchable, `.` / `..` never are, and matches equal to an ignore pattern drop out.
        var all = ExpandCore(pattern, cwd, dotGlob: true, globStar, noCase);
        all.RemoveAll(m => { var b = m.TrimEnd('/'); b = b[(b.LastIndexOf('/') + 1)..]; return b is "." or ".." || ignore.Exists(re => re.IsMatch(m)); });
        return all;
    }

    private static List<System.Text.RegularExpressions.Regex>? GlobIgnorePatterns()
    {
        var v = BashVariableStore.Get("GLOBIGNORE");
        if (string.IsNullOrEmpty(v)) return null;
        var list = new List<System.Text.RegularExpressions.Regex>();
        var cur = new System.Text.StringBuilder();
        void Flush() { if (cur.Length > 0) list.Add(new System.Text.RegularExpressions.Regex(PsBash.Core.Parser.BashPattern.ToAnchoredRegex(cur.ToString(), honorQuotes: false), System.Text.RegularExpressions.RegexOptions.Singleline)); cur.Clear(); }
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length) { cur.Append(v[i]).Append(v[++i]); continue; }
            if (v[i] == ':') { Flush(); continue; }
            cur.Append(v[i]);
        }
        Flush();
        return list;
    }

    private static List<string> ExpandCore(string pattern, string cwd, bool dotGlob, bool globStar, bool noCase)
    {
        string prefix = "";
        string dir = cwd;
        string rest = pattern;

        if (pattern.Length > 0 && IsSeparatorAt(pattern, 0, out _))
        {
            prefix = pattern[0] == '/' ? "/" : "\\";
            dir = Path.GetPathRoot(cwd) is { Length: > 0 } root ? root : "/";
            rest = pattern[SkipSeparators(pattern, 0)..];
        }
        else if (OperatingSystem.IsWindows() && pattern.Length >= 3 && char.IsAsciiLetter(pattern[0])
                 && pattern[1] == ':' && IsSeparatorAt(pattern, 2, out _))
        {
            prefix = pattern[..2] + (pattern[2] == '/' ? "/" : "\\");
            dir = pattern[..2] + "\\";
            rest = pattern[SkipSeparators(pattern, 2)..];
        }

        var split = SplitComponents(rest);
        bool dirOnly = false;
        char trailingSep = '/';
        if (split.Count > 1 && split[^1].Comp.Length == 0)
        {
            dirOnly = true;
            split.RemoveAt(split.Count - 1);
            trailingSep = split[^1].Sep;
        }
        // `a//b`: the empty part goes, with the separator after it (the one before it stays).
        split.RemoveAll(p => p.Comp.Length == 0);
        if (split.Count == 0) return new List<string>();
        var comps = new List<string>(split.Count);
        foreach (var p in split) comps.Add(p.Comp);

        var frontier = new List<(string Pfx, string Fs)> { (prefix, dir) };
        for (int i = 0; i < comps.Count && frontier.Count > 0; i++)
        {
            bool last = i == comps.Count - 1;
            string comp = comps[i];
            string sep = last ? "" : split[i].Sep == '/' ? "/" : "\\";
            var next = new List<(string, string)>();
            foreach (var (pfx, fsDir) in frontier)
            {
                if (globStar && comp == "**")
                {
                    if (last)
                    {
                        if (i > 0) next.Add((pfx, fsDir)); // `a/**` lists `a/` itself first
                        AddDescendants(pfx, fsDir, dotGlob, includeFiles: true, next);
                    }
                    else { next.Add((pfx, fsDir)); AddDescendants(pfx, fsDir, dotGlob, includeFiles: false, next); }
                    continue;
                }
                if (!HasPattern(comp))
                {
                    string name = Unescape(comp);
                    string full = Path.Combine(fsDir, name);
                    bool isDir = SafeDirectoryExists(full);
                    if (!isDir && (!last || dirOnly || !File.Exists(full))) continue;
                    next.Add((pfx + name + sep, full));
                    continue;
                }

                foreach (var name in ListNames(fsDir))
                {
                    if (!MatchComponent(noCase ? comp.ToLowerInvariant() : comp, noCase ? name.ToLowerInvariant() : name, dotGlob)) continue;
                    string full = Path.Combine(fsDir, name);
                    if (!last || dirOnly)
                    {
                        if (!SafeDirectoryExists(full)) continue;
                    }
                    next.Add((pfx + name + sep, full));
                }
            }
            frontier = next;
        }

        var results = new List<string>(frontier.Count);
        foreach (var (pfx, _) in frontier) results.Add(dirOnly ? pfx + trailingSep : pfx);
        if (globStar)
        {
            results.Sort(StringComparer.Ordinal);
            // Two `**` components can reach the same path by several routes (`**/**/*.*`): list each once.
            for (int k = results.Count - 1; k > 0; k--)
                if (results[k] == results[k - 1]) results.RemoveAt(k);
        }
        return results;
    }

    /// <summary>Pre-order walk below <paramref name="fsDir"/> (sorted names): directories, plus files when asked.</summary>
    private static void AddDescendants(string pfx, string fsDir, bool dotGlob, bool includeFiles, List<(string, string)> sink)
    {
        foreach (var name in ListNames(fsDir))
        {
            if (!dotGlob && name.StartsWith('.')) continue;
            string full = Path.Combine(fsDir, name);
            bool isDir = SafeDirectoryExists(full) && !IsLink(full);
            if (!isDir && !includeFiles) continue;
            sink.Add((pfx + name + (includeFiles ? "" : "/"), full));
            if (isDir) AddDescendants(pfx + name + "/", full, dotGlob, includeFiles, sink);
        }
    }

    private static bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget is not null; } catch { return false; }
    }

    /// <summary>One path component against one name; an extglob component (<c>@(a|b)</c>, <c>!(*.h)</c>) goes through <see cref="BashPattern"/>.</summary>
    private static bool MatchComponent(string comp, string name, bool dotGlob)
    {
        if (!PsBash.Core.Parser.BashPattern.HasExtGlob(comp, honorQuotes: false)) return LsGlob.Match(comp, name, dotGlob);
        if (!dotGlob && name.Length > 0 && name[0] == '.' && !comp.StartsWith('.')) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(name, PsBash.Core.Parser.BashPattern.ToAnchoredRegex(comp, honorQuotes: false));
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    /// <summary>Entry names of a directory in ordinal order; empty when it cannot be read.</summary>
    private static List<string> ListNames(string dir)
    {
        var names = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                names.Add(Path.GetFileName(entry));
        }
        catch
        {
            // unreadable / vanished: no matches, like bash
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
