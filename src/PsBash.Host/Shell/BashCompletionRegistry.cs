using System.Text;

namespace PsBash.Host.Shell;

/// <summary>
/// Bash programmable completion (<c>complete</c> / <c>compgen</c>), Tier 1 = static word lists
/// (<c>-W</c>). The interactive shell intercepts <c>complete</c> lines to populate this registry
/// (see <see cref="TryApplyCompleteCommand"/>); <see cref="CompletionEngine"/> consults it when
/// completing an argument of a registered command (<see cref="GetCandidates"/>).
/// </summary>
/// <remarks>
/// Tier 2 (function-based completion via <c>complete -F func</c>, with <c>COMP_WORDS</c> /
/// <c>COMPREPLY</c>) is deliberately out of scope: a <c>-F</c> spec registers with no Tier-1
/// candidates rather than running the function. The registry is shell-process-global state
/// (like <see cref="AliasExpander.Aliases"/>) so the prompt-side editor and the completion engine
/// share it without plumbing it through every call. <see cref="Clear"/> exists for test isolation.
/// </remarks>
internal static class BashCompletionRegistry
{
    /// <summary>A registered completion spec. Tier 1 holds a static word list (from <c>-W</c>).</summary>
    internal sealed record Spec(IReadOnlyList<string> Words);

    private static readonly Dictionary<string, Spec> _specs = new(StringComparer.Ordinal);

    /// <summary>True when a <c>complete</c> spec is registered for <paramref name="command"/>.</summary>
    public static bool HasSpec(string command) => _specs.ContainsKey(command);

    /// <summary>
    /// Candidates for completing an argument of <paramref name="command"/>, filtered to those that
    /// start with <paramref name="token"/> (case-sensitive, matching bash). Empty when no spec is
    /// registered or none match.
    /// </summary>
    public static IReadOnlyList<string> GetCandidates(string command, string token)
    {
        if (!_specs.TryGetValue(command, out var spec))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var w in spec.Words)
        {
            if (w.StartsWith(token, StringComparison.Ordinal))
            {
                result.Add(w);
            }
        }

        return result;
    }

    /// <summary>
    /// If <paramref name="input"/> is a <c>complete</c> command, apply it and return true; otherwise
    /// return false (the caller transpiles/executes the line as usual). Supported Tier-1 forms:
    /// <list type="bullet">
    /// <item><c>complete -W '&lt;words&gt;' NAME...</c> — register a static word list.</item>
    /// <item><c>complete -r [NAME...]</c> — remove the named specs, or all when no NAME given.</item>
    /// <item><c>complete</c> / <c>complete -p</c> — print registered specs.</item>
    /// </list>
    /// Other option forms (notably <c>-F func</c>) are accepted but register no Tier-1 candidates.
    /// </summary>
    public static bool TryApplyCompleteCommand(string input)
    {
        var tokens = Tokenize(input);
        if (tokens.Count == 0 || tokens[0] != "complete")
        {
            return false;
        }

        // Bare `complete` or `complete -p`: print the registered specs (bash format).
        if (tokens.Count == 1 || (tokens.Count == 2 && tokens[1] == "-p"))
        {
            PrintSpecs();
            return true;
        }

        string? wordList = null;
        var remove = false;
        var names = new List<string>();

        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            switch (t)
            {
                case "-W":
                    if (++i < tokens.Count) { wordList = tokens[i]; }
                    break;
                case "-r":
                    remove = true;
                    break;
                // Options that take a value we don't act on — skip the value too so it is not
                // mistaken for the command NAME. (-F function-based completion is Tier 2: accepted
                // but it contributes no Tier-1 word list.)
                case "-F":
                case "-C":
                case "-A":
                case "-G":
                case "-P":
                case "-S":
                case "-X":
                    i++;
                    break;
                default:
                    // Bare options (-o, -D, -E, …) we ignore; non-option tokens are command names.
                    if (!t.StartsWith('-'))
                    {
                        names.Add(t);
                    }

                    break;
            }
        }

        if (remove)
        {
            if (names.Count == 0)
            {
                _specs.Clear(); // bash: `complete -r` with no name removes all completion specs.
            }
            else
            {
                foreach (var n in names)
                {
                    _specs.Remove(n);
                }
            }

            return true;
        }

        if (names.Count > 0)
        {
            var words = wordList is null
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : wordList.Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var spec = new Spec(words);
            foreach (var n in names)
            {
                _specs[n] = spec;
            }
        }

        return true;
    }

    /// <summary>Drop all registered specs. For test isolation (the registry is process-global).</summary>
    internal static void Clear() => _specs.Clear();

    private static void PrintSpecs()
    {
        foreach (var kvp in _specs)
        {
            var words = string.Join(' ', kvp.Value.Words);
            Console.WriteLine(words.Length > 0
                ? $"complete -W '{words}' {kvp.Key}"
                : $"complete {kvp.Key}");
        }
    }

    /// <summary>
    /// Whitespace-split <paramref name="input"/> respecting single/double quotes, stripping the
    /// surrounding quotes from each token. Quoted whitespace stays inside one token, so
    /// <c>complete -W 'a b c' svc</c> yields tokens <c>complete</c>, <c>-W</c>, <c>a b c</c>,
    /// <c>svc</c>.
    /// </summary>
    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i]))
            {
                i++;
            }

            if (i >= input.Length)
            {
                break;
            }

            var sb = new StringBuilder();
            while (i < input.Length && !char.IsWhiteSpace(input[i]))
            {
                var c = input[i];
                if (c is '\'' or '"')
                {
                    var quote = c;
                    i++;
                    while (i < input.Length && input[i] != quote)
                    {
                        sb.Append(input[i]);
                        i++;
                    }

                    if (i < input.Length)
                    {
                        i++; // consume closing quote
                    }
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            tokens.Add(sb.ToString());
        }

        return tokens;
    }
}
