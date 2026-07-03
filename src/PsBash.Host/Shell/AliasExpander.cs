using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Host.Shell;

/// <summary>
/// The interactive shell's bash alias table and expansion. Owns the alias dictionary,
/// handles <c>alias</c>/<c>unalias</c> definition lines (<see cref="ProcessAliasCommand"/>),
/// and rewrites the first word of each command in a line against the table
/// (<see cref="ExpandAliases"/>). Extracted from InteractiveShell so the alias logic is
/// findable in one place; the table is shared with tab completion via <see cref="Aliases"/>.
/// </summary>
internal static class AliasExpander
{
    /// <summary>The live alias table: name → expansion. Read by tab completion to resolve the command word.</summary>
    internal static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal);

    /// <summary>
    /// Handle an <c>alias</c>/<c>unalias</c> line: define (<c>alias n=v</c>), list (<c>alias</c>,
    /// <c>alias -p</c>), show one (<c>alias n</c>), or remove (<c>unalias n</c>, <c>unalias -a</c>).
    /// Returns "" when the line was an alias command (nothing more to run), else the input unchanged.
    /// </summary>
    public static string ProcessAliasCommand(string input)
    {
        var aliasMatch = Regex.Match(
            input, @"^alias\s+((?:[^=\\ ""']+|\\.|""[^""]*""|'[^']*')+)=((?:[^\\ ""']+|\\.|""[^""]*""|'[^']*')*)\s*$");
        if (aliasMatch.Success)
        {
            var name = aliasMatch.Groups[1].Value.Trim();
            var value = aliasMatch.Groups[2].Value.Trim();
            if ((value.StartsWith('"') && value.EndsWith('"')) ||
                (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                value = value[1..^1];
            }
            Aliases[name] = value;
            return "";
        }

        if (input == "alias" || Regex.IsMatch(input, @"^alias\s+-p\s*$"))
        {
            foreach (var kvp in Aliases)
                Console.WriteLine($"alias {kvp.Key}='{kvp.Value}'");
            return "";
        }

        var aliasShowMatch = Regex.Match(input, @"^alias\s+([^\s=]+)\s*$");
        if (aliasShowMatch.Success)
        {
            var name = aliasShowMatch.Groups[1].Value;
            if (Aliases.TryGetValue(name, out var val))
                Console.WriteLine($"alias {name}='{val}'");
            else
                Console.Error.WriteLine($"ps-bash: alias: {name}: not found");
            return "";
        }

        var unaliasMatch = Regex.Match(input, @"^unalias\s+(.+)\s*$");
        if (unaliasMatch.Success)
        {
            var names = unaliasMatch.Groups[1].Value;
            if (names.Trim() == "-a")
            {
                Aliases.Clear();
            }
            else
            {
                foreach (var name in names.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Aliases.Remove(name))
                    {
                        Console.Error.WriteLine($"ps-bash: unalias: {name}: not found");
                    }
                }
            }
            return "";
        }

        return input;
    }

    /// <summary>
    /// Single forward pass: expand the first word of each command (after a separator) against the
    /// alias table, preserving quotes, escapes, and the separators (<c>| || ; &amp; &amp;&amp; ( &lt; &gt;</c>).
    /// </summary>
    public static string ExpandAliases(string input)
    {
        if (Aliases.Count == 0)
            return input;

        var sb = new StringBuilder();
        int pos = 0;
        // Bash expands an alias ONLY in command position — the first word, or the
        // first word after a command separator (; | || && & ( or newline). A word in
        // ARGUMENT position must never be alias-expanded: `alias ll='ls -la'` then
        // `echo ll` must run `echo ll`, not `echo ls -la`. A redirect target after
        // < / > is not a command either. Track it as we walk the separators.
        bool atCommandStart = true;

        while (pos < input.Length)
        {
            // Skip leading whitespace
            while (pos < input.Length && char.IsWhiteSpace(input[pos]))
                sb.Append(input[pos++]);

            if (pos >= input.Length)
                break;

            // Extract the next word
            int start = pos;
            bool quoted = false;
            char quoteChar = '\0';

            while (pos < input.Length)
            {
                char c = input[pos];
                if (quoted)
                {
                    if (c == quoteChar) quoted = false;
                    pos++;
                }
                else if (c == '"' || c == '\'')
                {
                    quoted = true;
                    quoteChar = c;
                    pos++;
                }
                else if (c == '\\')
                {
                    pos += 2;
                }
                else if (char.IsWhiteSpace(c) || c == ';' || c == '|' || c == '(' || c == '<' || c == '>')
                {
                    break;
                }
                else if (c == '&')
                {
                    if (pos + 1 < input.Length && input[pos + 1] == '&')
                        break;
                    break;
                }
                else
                {
                    pos++;
                }
            }

            var word = input[start..pos];

            if (atCommandStart && Aliases.TryGetValue(word, out var expansion))
                sb.Append(expansion);
            else
                sb.Append(word);

            // Copy separator until next word, deciding whether the NEXT word is a
            // command. Only a command separator (; | || && & () re-arms expansion;
            // plain whitespace (argument) and a redirect target (< >) leave it off.
            atCommandStart = false;
            while (pos < input.Length)
            {
                char c = input[pos];
                if (c == '&' && pos + 1 < input.Length && input[pos + 1] == '&')
                {
                    sb.Append("&&");
                    pos += 2;
                    atCommandStart = true;
                    break;
                }
                if (c == '&')
                {
                    sb.Append('&');
                    pos++;
                    atCommandStart = true;
                    break;
                }
                if (c == '|')
                {
                    if (pos + 1 < input.Length && input[pos + 1] == '|')
                    {
                        sb.Append("||");
                        pos += 2;
                        atCommandStart = true;
                        break;
                    }
                    sb.Append('|');
                    pos++;
                    atCommandStart = true;
                    break;
                }
                if (c == ';')
                {
                    sb.Append(';');
                    pos++;
                    atCommandStart = true;
                    break;
                }
                if (c == '(')
                {
                    sb.Append(c);
                    pos++;
                    atCommandStart = true;
                    break;
                }
                if (c == '<' || c == '>')
                {
                    // Redirect operator: the following word is a filename, not a command.
                    sb.Append(c);
                    pos++;
                    break;
                }
                if (char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                    pos++;
                    continue;
                }
                break;
            }
        }

        return sb.ToString();
    }
}
