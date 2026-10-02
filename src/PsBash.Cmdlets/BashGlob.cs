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
/// <para>Not implemented: <c>**</c> (acts as <c>*</c>), extglob <c>+(a|b)</c>, <c>nocaseglob</c>, and
/// collation order other than ordinal.</para>
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

    /// <summary>Splits on unescaped <c>/</c>.</summary>
    private static List<string> SplitComponents(string rest)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            if (c == '\\' && i + 1 < rest.Length) { cur.Append(c).Append(rest[++i]); continue; }
            if (c == '/') { parts.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        parts.Add(cur.ToString());
        return parts;
    }

    /// <summary>Matches of <paramref name="pattern"/>, resolved against <paramref name="cwd"/>; empty when nothing matches.</summary>
    public static List<string> Expand(string pattern, string cwd, bool dotGlob)
    {
        string prefix = "";
        string dir = cwd;
        string rest = pattern;

        if (pattern.StartsWith('/'))
        {
            int n = 0;
            while (n < pattern.Length && pattern[n] == '/') n++;
            prefix = "/";
            dir = Path.GetPathRoot(cwd) is { Length: > 0 } root ? root : "/";
            rest = pattern[n..];
        }
        else if (OperatingSystem.IsWindows() && pattern.Length >= 3 && char.IsAsciiLetter(pattern[0])
                 && pattern[1] == ':' && pattern[2] == '/')
        {
            int n = 2;
            while (n < pattern.Length && pattern[n] == '/') n++;
            prefix = pattern[..3];
            dir = pattern[..2] + "\\";
            rest = pattern[n..];
        }

        var comps = SplitComponents(rest);
        bool dirOnly = false;
        if (comps.Count > 1 && comps[^1].Length == 0)
        {
            dirOnly = true;
            comps.RemoveAt(comps.Count - 1);
        }
        comps.RemoveAll(c => c.Length == 0);
        if (comps.Count == 0) return new List<string>();

        var frontier = new List<(string Pfx, string Fs)> { (prefix, dir) };
        for (int i = 0; i < comps.Count && frontier.Count > 0; i++)
        {
            bool last = i == comps.Count - 1;
            string comp = comps[i];
            var next = new List<(string, string)>();
            foreach (var (pfx, fsDir) in frontier)
            {
                if (!HasPattern(comp))
                {
                    string name = Unescape(comp);
                    string full = Path.Combine(fsDir, name);
                    bool isDir = SafeDirectoryExists(full);
                    if (!isDir && (!last || dirOnly || !File.Exists(full))) continue;
                    next.Add((pfx + name + (last ? "" : "/"), full));
                    continue;
                }

                foreach (var name in ListNames(fsDir))
                {
                    if (!LsGlob.Match(comp, name, dotGlob)) continue;
                    string full = Path.Combine(fsDir, name);
                    if (!last || dirOnly)
                    {
                        if (!SafeDirectoryExists(full)) continue;
                    }
                    next.Add((pfx + name + (last ? "" : "/"), full));
                }
            }
            frontier = next;
        }

        var results = new List<string>(frontier.Count);
        foreach (var (pfx, _) in frontier) results.Add(dirOnly ? pfx + "/" : pfx);
        return results;
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
