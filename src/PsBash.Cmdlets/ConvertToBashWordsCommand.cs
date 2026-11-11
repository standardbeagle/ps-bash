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

    protected override void ProcessRecord()
    {
        var ifs = BashVariableStore.Get("IFS");
        var words = Fields is not null || Prefix is not null || Suffix is not null
            ? BashWordSplitter.AffixFields(Prefix ?? "", Suffix ?? "",
                (Fields ?? Array.Empty<object?>()).Select(f => f is null ? null : (f is PSObject p ? p.BaseObject : f).ToString()).ToList(), ifs)
            : BashWordSplitter.Split(Text, ifs);
        foreach (var word in words)
        {
            if (!BashWordSplitter.HasGlobChars(word))
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
            InvokeBashShoptCommand.IsEnabled("globstar"), InvokeBashShoptCommand.IsEnabled("nocaseglob"));
        if (matches.Count > 0) return matches;

        if (InvokeBashShoptCommand.IsEnabled("failglob"))
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException("bash: no match: " + word),
                "NoGlobMatch", ErrorCategory.ObjectNotFound, word));
        }
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
    public static List<string> AffixFields(string prefix, string suffix, IReadOnlyList<string?> values, string? ifs)
    {
        if (ifs is { Length: 0 })
        {
            var fields = new List<string>(values.Count);
            foreach (var v in values) fields.Add(v ?? "");
            if (fields.Count == 0) fields.Add("");
            fields[0] = prefix + fields[0];
            fields[^1] = fields[^1] + suffix;
            fields.RemoveAll(f => f.Length == 0);
            return fields;
        }
        char sep = ifs is null ? ' ' : ifs[0];
        var sb = new System.Text.StringBuilder(prefix);
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(sep);
            sb.Append(values[i]);
        }
        sb.Append(suffix);
        return Split(sb.ToString(), ifs);
    }

    public static bool HasGlobChars(string word) => word.AsSpan().IndexOfAny('*', '?', '[') >= 0;

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
