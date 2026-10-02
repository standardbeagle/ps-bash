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

    protected override void ProcessRecord()
    {
        foreach (var word in BashWordSplitter.Split(Text, BashVariableStore.Get("IFS")))
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
        if (!BashGlob.HasPattern(pattern)) return new[] { word };

        string cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
        var matches = BashGlob.Expand(pattern, cwd, InvokeBashShoptCommand.IsEnabled("dotglob"));
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
