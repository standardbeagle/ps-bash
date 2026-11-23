using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// Word splitting + pathname expansion of an UNQUOTED expansion result (<c>$(cmd)</c>, <c>`cmd`</c>,
/// <c>$x</c> in a <c>for</c> list), emitted by the transpiler as
/// <c>@(ConvertTo-BashWords -Text &lt;value&gt;)</c>.
///
/// <para>bash expands an unquoted substitution to TEXT, then splits that text into words on
/// <c>$IFS</c>, then expands every word that contains a glob character against the filesystem
/// (nullglob off: a pattern with no match stays as the literal word). The emitter used to hand the
/// capture array straight to PowerShell, which splits on LINES only, so
/// <c>for f in $(echo a b c)</c> iterated once over <c>a b c</c>.</para>
///
/// <para>Output is the list of words (an empty/whitespace-only value yields NO word, so the loop runs
/// zero times and a command operand is elided).</para>
/// </summary>
[Cmdlet(VerbsData.ConvertTo, "BashWords")]
[OutputType(typeof(string))]
public sealed class ConvertToBashWordsCommand : BashCmdlet
{
    /// <summary>One unquoted expansion's value: the word is that one <see cref="BashWordFields.Split"/> segment.</summary>
    [Parameter(Position = 0, ValueFromPipeline = true)]
    [AllowNull]
    [AllowEmptyString]
    public string? Text { get; set; }

    /// <summary>The word as segments, kind then value (see <see cref="BashWordFields"/>): the general form.</summary>
    [Parameter] [AllowNull] [AllowEmptyCollection] public object?[]? Parts { get; set; }

    /// <summary>With <see cref="Fields"/>: literal text glued before the expansion (<c>=$*=</c>, <c>a$x</c>).</summary>
    [Parameter] [AllowNull] [AllowEmptyString] public string? Prefix { get; set; }

    /// <summary>With <see cref="Fields"/>: literal text glued after the expansion.</summary>
    [Parameter] [AllowNull] [AllowEmptyString] public string? Suffix { get; set; }

    /// <summary>An unquoted list expansion's values (every positional parameter): one <see cref="BashWordFields.SplitList"/> segment.</summary>
    [Parameter] [AllowNull] [AllowEmptyCollection] public object?[]? Fields { get; set; }

    /// <summary>The prefix has quoted text, so it anchors a field even when empty (<c>""$x</c>).</summary>
    [Parameter] public SwitchParameter PrefixQuoted { get; set; }

    /// <summary>The suffix has quoted text (<c>$x""</c>).</summary>
    [Parameter] public SwitchParameter SuffixQuoted { get; set; }

    protected override void ProcessRecord()
    {
        var ifs = BashVariableStore.Get("IFS");
        bool extglob = InvokeBashShoptCommand.IsEnabled("extglob");
        System.Collections.IList parts = Parts
            ?? (Fields is not null || Prefix is not null || Suffix is not null
                ? BashWordSplitter.AffixParts(Prefix ?? "", Suffix ?? "", (System.Collections.IList?)Fields ?? Array.Empty<object?>(), PrefixQuoted.IsPresent, SuffixQuoted.IsPresent)
                : new object?[] { BashWordFields.Split, Text });
        bool noGlob = false, noGlobRead = false;
        foreach (var field in BashWordFields.Expand(parts, ifs, extglob))
        {
            if (field.Pattern is null) { WriteObject(field.Text); continue; }
            if (!noGlobRead) { noGlob = ConvertToBashGlobCommand.IsNoGlob(SessionState); noGlobRead = true; }
            if (noGlob) { WriteObject(field.Text); continue; }
            foreach (var expanded in ExpandGlob(field.Pattern, field.Text))
                WriteObject(expanded);
        }
    }

    /// <summary>
    /// Pathname expansion of one field (the shared <see cref="BashGlob"/> engine, so a field globs exactly like a literal
    /// pattern: relative matches, hidden files only for a <c>.</c> pattern, sorted; no match keeps the field's text,
    /// <c>nullglob</c> / <c>failglob</c> honoured). A Windows drive path written with backslashes is read as separators,
    /// not escapes.
    /// </summary>
    private IEnumerable<string> ExpandGlob(string pattern, string text)
    {
        if (OperatingSystem.IsWindows() && pattern.Length >= 3 && char.IsAsciiLetter(pattern[0]) && pattern[1] == ':' && pattern[2] == '\\')
            pattern = BashPath.ToShell(pattern);
        if (!BashGlob.HasPattern(pattern)) return new[] { text };

        string cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
        var matches = BashGlob.Expand(pattern, cwd, InvokeBashShoptCommand.IsEnabled("dotglob"),
            InvokeBashShoptCommand.IsEnabled("globstar"), InvokeBashShoptCommand.IsEnabled("nocaseglob"),
            skipDots: InvokeBashShoptCommand.IsEnabled("globskipdots"), extGlob: InvokeBashShoptCommand.IsEnabled("extglob"));
        if (matches.Count > 0) return matches;

        if (InvokeBashShoptCommand.IsEnabled("failglob")) ConvertToBashGlobCommand.FailGlob(this, text);
        return InvokeBashShoptCommand.IsEnabled("nullglob") ? Array.Empty<string>() : new[] { text };
    }
}

/// <summary>Pure word-splitting rules (bash "Word Splitting"), kept separate from the cmdlet for tests.</summary>
internal static class BashWordSplitter
{
    private static bool IsIfsWhitespace(char c) => c is ' ' or '\t' or '\n';

    /// <summary>
    /// Splits <paramref name="text"/> on <paramref name="ifs"/> (null = unset = space, tab, newline).
    /// IFS whitespace folds and is trimmed at both ends; every non-whitespace IFS character is its own
    /// delimiter and keeps empty fields between neighbours (<c>IFS=: ; a::b</c> = a, "", b) but a single
    /// trailing delimiter does not add a field. An empty IFS splits nothing.
    /// </summary>
    public static List<string> Split(string? text, string? ifs)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text)) return words;
        ifs ??= " \t\n";
        if (ifs.Length == 0)
        {
            words.Add(text);
            return words;
        }

        bool IsWs(char c) => ifs.IndexOf(c) >= 0 && IsIfsWhitespace(c);
        bool IsNonWs(char c) => ifs.IndexOf(c) >= 0 && !IsIfsWhitespace(c);

        int i = 0, n = text.Length;
        while (i < n && IsWs(text[i])) i++;
        while (i < n)
        {
            int start = i;
            while (i < n && ifs.IndexOf(text[i]) < 0) i++;
            words.Add(text.Substring(start, i - start));
            if (i >= n) break;

            if (IsWs(text[i]))
            {
                while (i < n && IsWs(text[i])) i++;
                if (i < n && IsNonWs(text[i]))
                {
                    i++;
                    while (i < n && IsWs(text[i])) i++;
                }
            }
            else
            {
                i++;
                while (i < n && IsWs(text[i])) i++;
            }
        }
        return words;
    }

    /// <summary>
    /// An unquoted <c>$*</c> / <c>$@</c> / <c>$x</c>, alone or with text glued on (<c>=$*=</c>, <c>a$x</c>). bash 5.2:
    /// the values (every positional parameter, or the one variable) are joined with the first IFS character (space
    /// when IFS is unset), the prefix and suffix glued on, and the result split on IFS. <c>IFS=x; set -- "" ""</c>:
    /// <c>$*</c> is one empty field, <c>=$*=</c> is <c>=</c> <c>=</c>; <c>set -- a "" b</c>: <c>$*</c> is a, "", b.
    /// An empty IFS splits nothing: each value is its own field (text glued to the first and last) and empty fields
    /// are dropped. No values at all is the prefix and suffix alone (<c>=$*=</c> is <c>==</c>).
    /// <para>The prefix and suffix are literal text: they are never split (<c>IFS=:; x=c; a:b$x</c> is <c>a:bc</c>). A
    /// quoted affix (<paramref name="prefixQuoted"/> / <paramref name="suffixQuoted"/>) anchors a field even when it is
    /// empty, as bash's quoted null does: A='  abc  ', <c>""$A</c> is <c>""</c> <c>abc</c> and <c>$A""</c> is <c>abc</c> <c>""</c>.</para>
    /// <para>The <see cref="BashWordFields"/> segments [prefix, list, suffix]; the fields are their text.</para>
    /// </summary>
    public static List<string> AffixFields(string prefix, string suffix, IReadOnlyList<string?> values, string? ifs,
        bool prefixQuoted = false, bool suffixQuoted = false)
    {
        var list = new object?[values.Count];
        for (int i = 0; i < values.Count; i++) list[i] = values[i];
        var fields = BashWordFields.Expand(AffixParts(prefix, suffix, list, prefixQuoted, suffixQuoted), ifs, extglob: false);
        var words = new List<string>(fields.Count);
        foreach (var f in fields) words.Add(f.Text);
        return words;
    }

    /// <summary>The segments of <see cref="AffixFields"/>: prefix (pattern text), a quoted-null anchor when the prefix is
    /// quoted, the list, the suffix's anchor, the suffix.</summary>
    public static object?[] AffixParts(string prefix, string suffix, System.Collections.IList values, bool prefixQuoted, bool suffixQuoted)
        => new object?[]
        {
            BashWordFields.Pattern, prefix,
            prefixQuoted ? BashWordFields.Quoted : BashWordFields.Pattern, "",
            BashWordFields.SplitList, values,
            suffixQuoted ? BashWordFields.Quoted : BashWordFields.Pattern, "",
            BashWordFields.Pattern, suffix,
        };

    /// <summary>A pattern character, or with <paramref name="extglob"/> an extglob group (<c>${undef:-@(foo|bar).py}</c> globs).</summary>
    public static bool HasGlobChars(string word, bool extglob = false)
    {
        if (word.AsSpan().IndexOfAny('*', '?', '[') >= 0) return true;
        if (!extglob) return false;
        for (int i = 0; i + 1 < word.Length; i++)
            if (word[i] is '@' or '+' or '!' && word[i + 1] == '(') return true;
        return false;
    }

    /// <summary>
    /// A match as bash prints it: for a relative pattern, the path relative to the working directory
    /// with forward slashes; an absolute pattern keeps its absolute form.
    /// </summary>
    public static string RelativizeMatch(string pattern, string match, string cwd)
    {
        bool rooted = pattern.StartsWith('/') || pattern.StartsWith('\\') || pattern.StartsWith('~')
            || (pattern.Length >= 2 && pattern[1] == ':');
        if (rooted) return match;

        string prefix = cwd.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return match.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? BashPath.ToShell(match.Substring(prefix.Length))
            : match;
    }
}
