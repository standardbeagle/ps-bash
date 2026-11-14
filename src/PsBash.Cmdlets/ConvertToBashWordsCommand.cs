using System.Management.Automation;

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
public sealed class ConvertToBashWordsCommand : PSCmdlet
{
    [Parameter(Position = 0, ValueFromPipeline = true)]
    [AllowNull]
    [AllowEmptyString]
    public string? Text { get; set; }

    /// <summary>With <see cref="Fields"/>: literal text glued before the expansion (<c>=$*=</c>, <c>a$x</c>).</summary>
    [Parameter] [AllowNull] [AllowEmptyString] public string? Prefix { get; set; }

    /// <summary>With <see cref="Fields"/>: literal text glued after the expansion.</summary>
    [Parameter] [AllowNull] [AllowEmptyString] public string? Suffix { get; set; }

    /// <summary>The expansion's values, one field source each (every positional parameter, or the one variable).</summary>
    [Parameter] [AllowNull] [AllowEmptyCollection] public object?[]? Fields { get; set; }

    /// <summary>The prefix has quoted text, so it anchors a field even when empty (<c>""$x</c>).</summary>
    [Parameter] public SwitchParameter PrefixQuoted { get; set; }

    /// <summary>The suffix has quoted text (<c>$x""</c>).</summary>
    [Parameter] public SwitchParameter SuffixQuoted { get; set; }

    protected override void ProcessRecord()
    {
        var ifs = BashVariableStore.Get("IFS");
        var words = Fields is not null || Prefix is not null || Suffix is not null
            ? BashWordSplitter.AffixFields(Prefix ?? "", Suffix ?? "",
                (Fields ?? Array.Empty<object?>()).Select(f => f is null ? null : (f is PSObject p ? p.BaseObject : f).ToString()).ToList(), ifs,
                PrefixQuoted.IsPresent, SuffixQuoted.IsPresent)
            : BashWordSplitter.Split(Text, ifs);
        bool extglob = InvokeBashShoptCommand.IsEnabled("extglob");
        foreach (var word in words)
        {
            if (!BashWordSplitter.HasGlobChars(word, extglob))
            {
                WriteObject(word);
                continue;
            }

            foreach (var expanded in ExpandGlob(word))
                WriteObject(expanded);
        }
    }

    /// <summary>
    /// Pathname expansion of one split word (the shared <see cref="BashGlob"/> engine, so a word from a
    /// substitution globs exactly like a literal pattern: relative matches, hidden files only for a
    /// <c>.</c> pattern, sorted; no match keeps the word, <c>nullglob</c> / <c>failglob</c> honoured).
    /// A Windows drive path written with backslashes is read as separators, not escapes.
    /// </summary>
    private IEnumerable<string> ExpandGlob(string word)
    {
        string pattern = OperatingSystem.IsWindows() && word.Length >= 3 && char.IsAsciiLetter(word[0])
            && word[1] == ':' && word[2] == '\\'
            ? word.Replace('\\', '/')
            : word;
        if (ConvertToBashGlobCommand.IsNoGlob(SessionState) || !BashGlob.HasPattern(pattern)) return new[] { word };

        string cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
        var matches = BashGlob.Expand(pattern, cwd, InvokeBashShoptCommand.IsEnabled("dotglob"),
            InvokeBashShoptCommand.IsEnabled("globstar"), InvokeBashShoptCommand.IsEnabled("nocaseglob"),
            skipDots: InvokeBashShoptCommand.IsEnabled("globskipdots"));
        if (matches.Count > 0) return matches;

        if (InvokeBashShoptCommand.IsEnabled("failglob")) ConvertToBashGlobCommand.FailGlob(this, word);
        return InvokeBashShoptCommand.IsEnabled("nullglob") ? Array.Empty<string>() : new[] { word };
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
    /// </summary>
    /// <para>The prefix and suffix are literal text: they are never split (<c>IFS=:; x=c; a:b$x</c> is <c>a:bc</c>). A
    /// quoted affix (<paramref name="prefixQuoted"/> / <paramref name="suffixQuoted"/>) anchors a field even when it is
    /// empty, as bash's quoted null does: A='  abc  ', <c>""$A</c> is <c>""</c> <c>abc</c> and <c>$A""</c> is <c>abc</c> <c>""</c>.</para>
    public static List<string> AffixFields(string prefix, string suffix, IReadOnlyList<string?> values, string? ifs,
        bool prefixQuoted = false, bool suffixQuoted = false)
    {
        if (ifs is { Length: 0 })
        {
            var fields = new List<string>(values.Count);
            foreach (var v in values) fields.Add(v ?? "");
            if (fields.Count == 0) fields.Add("");
            fields[0] = prefix + fields[0];
            fields[^1] = fields[^1] + suffix;
            bool keepFirst = prefixQuoted || (fields.Count == 1 && suffixQuoted), keepLast = suffixQuoted;
            for (int i = fields.Count - 1; i >= 0; i--)
                if (fields[i].Length == 0 && !(i == 0 && keepFirst) && !(i == fields.Count - 1 && keepLast)) fields.RemoveAt(i);
            return fields;
        }
        char sep = ifs is null ? ' ' : ifs[0];
        // Each character is tagged: 0 = expansion text (split on IFS), 1 = literal affix text, 2 = a quoted-null anchor
        // (kept in no field, but it makes the field it sits in exist).
        var sb = new System.Text.StringBuilder(prefix.Length + suffix.Length + 16);
        var kind = new List<byte>(prefix.Length + suffix.Length + 16);
        void Add(string text, byte k) { sb.Append(text); for (int j = 0; j < text.Length; j++) kind.Add(k); }
        Add(prefix, 1);
        if (prefixQuoted) { sb.Append('\0'); kind.Add(2); }
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) Add(sep.ToString(), 0);
            Add(values[i] ?? "", 0);
        }
        if (suffixQuoted) { sb.Append('\0'); kind.Add(2); }
        Add(suffix, 1);
        return SplitTagged(sb.ToString(), kind, ifs ?? " \t\n");
    }

    /// <summary><see cref="Split"/> where only characters tagged 0 can delimit and characters tagged 2 are dropped from
    /// the fields they anchor (see <see cref="AffixFields"/>).</summary>
    private static List<string> SplitTagged(string text, List<byte> kind, string ifs)
    {
        var words = new List<string>();
        bool IsWs(int k) => kind[k] == 0 && ifs.IndexOf(text[k]) >= 0 && IsIfsWhitespace(text[k]);
        bool IsNonWs(int k) => kind[k] == 0 && ifs.IndexOf(text[k]) >= 0 && !IsIfsWhitespace(text[k]);
        bool IsIfs(int k) => kind[k] == 0 && ifs.IndexOf(text[k]) >= 0;
        var field = new System.Text.StringBuilder();
        int i = 0, n = text.Length;
        while (i < n && IsWs(i)) i++;
        while (i < n)
        {
            field.Clear();
            while (i < n && !IsIfs(i))
            {
                if (kind[i] != 2) field.Append(text[i]);
                i++;
            }
            words.Add(field.ToString());
            if (i >= n) break;

            if (IsWs(i))
            {
                while (i < n && IsWs(i)) i++;
                if (i < n && IsNonWs(i))
                {
                    i++;
                    while (i < n && IsWs(i)) i++;
                }
            }
            else
            {
                i++;
                while (i < n && IsWs(i)) i++;
            }
        }
        return words;
    }

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
            ? match.Substring(prefix.Length).Replace('\\', '/')
            : match;
    }
}
