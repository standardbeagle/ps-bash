using System.Text.RegularExpressions;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// A differential script must mean the same on every file system the suite runs on (the cassette is recorded on Linux and
/// replayed on Windows and macOS too). <see cref="AssertOracle.EqualAsync"/> refuses, before anything runs, a script that
/// <list type="bullet">
/// <item>names two files that differ only in case (<c>2&gt;E</c> and <c>2&gt;e</c>): two files on the Linux oracle, ONE file
/// on Windows and macOS, so <c>{ echo y &gt;&amp;5 2&gt;e; } 2&gt;E; [ -e e ]</c> found the outer E and reported "e-made" on
/// the s24u Windows gate, which read as ps-bash performing a redirect after a failed one;</item>
/// <item>makes a file whose name Windows cannot hold (<c>touch 'x(a|b)y'</c>: <c>| : * ? " &lt; &gt;</c>).</item>
/// </list>
/// Guard: <c>ScriptPortabilityTests</c>.
/// </summary>
internal static class ScriptPortability
{
    // One shell word that names a file: a plain name, or a quoted one (quotes removed by Unquote).
    private const string Word = @"(?:'[^'\n]*'|""[^""$`\\\n]*""|[A-Za-z0-9_.][A-Za-z0-9_.\-]*)";

    // A redirection target (`>f`, `2>>f`, `<f`, `<>f`, `>|f`), not a dup (`>&2`), here-doc (`<<`), here-string or
    // process substitution (`<(`), and not inside a longer operator.
    private static readonly Regex RedirectTarget = new(
        @"(?<![<>&\w$])\d*(?:>>|>\||<>|>|<)(?![<>&(])[ \t]*(?<n>" + Word + ")", RegexOptions.CultureInvariant);

    // A test of a file (`[ -e e ]`, `[[ -f F ]]`, `test -s x`).
    private static readonly Regex FileTest = new(
        @"(?:\[\[?|\btest)[ \t]+-[efsdrwxL][ \t]+(?<n>" + Word + @")(?=[ \t;\]]|$)", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // The operands of a command that makes files (`touch a B`, `mkdir -p d`), up to the first word that is not a name.
    private static readonly Regex Maker = new(
        @"\b(?:touch|mkdir)(?:[ \t]+-p)?(?<args>(?:[ \t]+" + Word + @"(?=[ \t;&|)\n]|$))+)", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex MakerArg = new(Word, RegexOptions.CultureInvariant);

    /// <summary>Characters a Windows file name cannot hold (besides the separators and NUL).</summary>
    private static readonly char[] NotInAWindowsName = ['|', ':', '*', '?', '"', '<', '>'];

    private static string Unquote(string word)
        => word.Length >= 2 && (word[0] == '\'' || word[0] == '"') && word[^1] == word[0] ? word[1..^1] : word;

    /// <summary>The first pair of file names in <paramref name="script"/> that differ only in case, or null.</summary>
    public static (string A, string B)? CaseCollision(string script)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in FileNames(script))
        {
            if (seen.TryGetValue(name, out var first))
            {
                if (!string.Equals(first, name, StringComparison.Ordinal)) return (first, name);
            }
            else seen[name] = name;
        }
        return null;
    }

    /// <summary>The first file name in <paramref name="script"/> that Windows cannot hold, or null.</summary>
    public static string? NotAWindowsName(string script)
    {
        foreach (var name in FileNames(script))
            foreach (var part in name.Split('/'))
                if (part.IndexOfAny(NotInAWindowsName) >= 0) return name;
        return null;
    }

    private static IEnumerable<string> FileNames(string script)
    {
        // An operator or command name inside quotes is text (`printf '<%s>' "<$@>"`), not a redirect or a command.
        var quoted = QuotedPositions(script);
        foreach (Match m in RedirectTarget.Matches(script))
            if (!quoted[m.Index]) yield return Unquote(m.Groups["n"].Value);
        foreach (Match m in FileTest.Matches(script))
            if (!quoted[m.Index]) yield return Unquote(m.Groups["n"].Value);
        foreach (Match m in Maker.Matches(script))
        {
            if (quoted[m.Index]) continue;
            foreach (Match a in MakerArg.Matches(m.Groups["args"].Value))
                if (a.Value != "-p") yield return Unquote(a.Value);
        }
    }

    /// <summary>For each char of <paramref name="script"/>: true when it sits inside '…' or "…" (or is escaped).</summary>
    private static bool[] QuotedPositions(string script)
    {
        var quoted = new bool[script.Length + 1];
        char open = '\0';
        for (int i = 0; i < script.Length; i++)
        {
            char c = script[i];
            if (open == '\0')
            {
                if (c == '\\' && i + 1 < script.Length) { quoted[++i] = true; continue; }
                if (c == '\'' || c == '"') open = c;
                continue;
            }
            quoted[i] = true;
            if (open == '"' && c == '\\' && i + 1 < script.Length) { quoted[++i] = true; continue; }
            if (c == open) { open = '\0'; quoted[i] = false; }
        }
        return quoted;
    }

    /// <summary>The reason <paramref name="script"/> cannot be a differential row, or null.</summary>
    public static string? Refusal(string script)
    {
        if (CaseCollision(script) is { } pair)
            return $"the script names files `{pair.A}` and `{pair.B}`, which differ only in case: two files on the Linux "
                   + "oracle, one on Windows and macOS (a different script there). Rename one.";
        if (NotAWindowsName(script) is { } bad)
            return $"the script makes a file `{bad}`, which Windows cannot name (| : * ? \" < >). Use a portable name.";
        return null;
    }
}
