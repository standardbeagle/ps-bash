using System.Text;

namespace PsBash.Host.Shell;

/// <summary>
/// Bash-style history expansion ("bang commands") for the interactive REPL.
///
/// Runs on the typed line BEFORE alias expansion and transpilation (the same order bash
/// uses), against the in-session command list the REPL maintains. Supported forms:
///
/// <list type="bullet">
///   <item><c>!!</c> — the previous command.</item>
///   <item><c>!n</c> — command number <c>n</c> in this session (1-based).</item>
///   <item><c>!-n</c> — the n-th command back (<c>!-1</c> == <c>!!</c>).</item>
///   <item><c>!str</c> — the most recent command starting with <c>str</c>.</item>
///   <item><c>!?str?</c> — the most recent command containing <c>str</c>.</item>
///   <item><c>!$</c> / <c>!^</c> / <c>!*</c> — last / first / all argument words of the previous command.</item>
///   <item>A <c>:</c> word designator (<c>:$</c>, <c>:^</c>, <c>:*</c>, <c>:n</c>) after any event.</item>
///   <item><c>^old^new</c> — quick substitution on the previous command (first match only).</item>
/// </list>
///
/// Expansion is suppressed inside single quotes and after a backslash (bash semantics);
/// a <c>!</c> followed by whitespace, <c>=</c>, <c>(</c>, or end-of-line stays literal so the
/// pipeline-negation operator (<c>! cmd</c>) is untouched.
///
/// Scope is the common 80/20 of bash history expansion; ranged word designators
/// (<c>!!:2-4</c>) and modifiers (<c>:h</c>, <c>:t</c>, <c>:s</c>) are out of scope.
/// </summary>
internal static class HistoryExpander
{
    /// <summary>
    /// Cheap gate: does the line contain anything that could trigger expansion? Lets the REPL
    /// skip the full scan for the overwhelmingly common no-bang line.
    /// </summary>
    public static bool ContainsExpansion(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        if (line[0] == '^') return true;   // quick substitution only at line start
        return line.Contains('!');
    }

    /// <summary>
    /// Expand history references in <paramref name="line"/> against <paramref name="history"/>
    /// (oldest first; the last element is the most recent command). Returns the expanded line,
    /// or the input unchanged when there was nothing to expand. On a failed reference (no such
    /// event / substitution failed) returns <c>null</c> and sets <paramref name="error"/> to a
    /// bash-style message; the caller should print it and not execute.
    /// </summary>
    public static string? Expand(string line, IReadOnlyList<string> history, out string? error)
    {
        error = null;

        // Quick substitution: ^old^new[^]  — operates on the previous command only.
        if (line.Length > 0 && line[0] == '^')
            return ExpandQuickSubstitution(line, history, out error);

        if (!line.Contains('!'))
            return line;

        var sb = new StringBuilder(line.Length);
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '\\' && i + 1 < line.Length && !inSingle)
            {
                // Backslash escapes the next char (including a literal !); keep both, skip expansion.
                sb.Append(c).Append(line[i + 1]);
                i++;
                continue;
            }
            if (c == '\'' && !inDouble) { inSingle = !inSingle; sb.Append(c); continue; }
            if (c == '"' && !inSingle) { inDouble = !inDouble; sb.Append(c); continue; }

            if (c != '!' || inSingle)
            {
                sb.Append(c);
                continue;
            }

            // A '!' is only a history reference when the NEXT char actually starts an
            // event selector or word designator: another '!', '?', the '$'/'^'/'*'
            // shorthands, a '-'/digit event number, or a letter/digit for `!str`.
            // Anything else — whitespace, '=', '(', a closing quote, ')', ';', end of
            // line, … — is literal. Bash does NOT expand `!` before a closing quote, so
            // `echo "done!"` stays literal; testing only a blocklist let `!"` fall into
            // ExpandBang with an EMPTY event token, which ResolveEvent mis-read as `!!`
            // and spliced in the previous command.
            var next = i + 1 < line.Length ? line[i + 1] : '\0';
            var isSelectorStart = next is '!' or '?' or '$' or '^' or '*' or '-'
                                  || char.IsLetterOrDigit(next);
            if (!isSelectorStart)
            {
                sb.Append(c);
                continue;
            }

            var replacement = ExpandBang(line, ref i, history, out error);
            if (error != null)
                return null;
            sb.Append(replacement);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parse one bang reference starting at <paramref name="i"/> (which points at the '!') and
    /// return its expansion. On return, <paramref name="i"/> points at the last consumed char.
    /// </summary>
    private static string ExpandBang(string line, ref int i, IReadOnlyList<string> history, out string? error)
    {
        error = null;
        var start = i;       // the '!'
        i++;                 // first char after '!'

        // Shorthand word designators that reference the previous command: !$ !^ !*
        if (i < line.Length && (line[i] == '$' || line[i] == '^' || line[i] == '*'))
        {
            var desig = line[i];
            var prevCmd = ResolveEvent("!", history, out error);
            if (error != null) { Reset(ref i, start); return ""; }
            return ApplyWordDesignator(prevCmd, desig.ToString(), line, start, ref error, ref i);
        }

        // Event selector.
        string eventCmd;
        if (i < line.Length && line[i] == '!')
        {
            // i already points at the second '!' — that IS the last consumed char (contract).
            eventCmd = ResolveEvent("!", history, out error);
        }
        else if (i < line.Length && line[i] == '?')
        {
            // !?str?  — substring search, terminated by an optional closing '?'.
            i++;
            var qs = i;
            while (i < line.Length && line[i] != '?') i++;
            var needle = line[qs..i];
            if (i < line.Length && line[i] == '?') i++; // consume closing '?'
            i--; // ExpandBang contract: i points at last consumed char
            eventCmd = ResolveContains(needle, history, out error);
        }
        else
        {
            // !-n / !n / !str
            var es = i;
            if (i < line.Length && line[i] == '-') i++;
            while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '-' or '_' or '.' or '/'))
                i++;
            var token = line[es..i];
            // Defensive: an empty token here means '!' was not followed by a real event
            // selector. Keep it literal rather than falling through to ResolveEvent's
            // empty-token == `!!` path (the caller's positive guard normally prevents this).
            if (token.Length == 0)
            {
                Reset(ref i, start);
                return "!";
            }
            i--; // point at last consumed char of the event token
            eventCmd = ResolveEvent(token, history, out error);
        }

        if (error != null) { Reset(ref i, start); return ""; }

        // Optional word designator suffix: :$ :^ :* :n
        if (i + 1 < line.Length && line[i + 1] == ':')
        {
            var ds = i + 2;
            if (ds < line.Length)
            {
                var de = ds;
                while (de < line.Length && (char.IsLetterOrDigit(line[de]) || line[de] is '$' or '^' or '*'))
                    de++;
                var designator = line[ds..de];
                i = de - 1;
                return ApplyWordDesignator(eventCmd, designator, line, start, ref error, ref i);
            }
        }

        return eventCmd;
    }

    private static void Reset(ref int i, int start) => i = start;

    /// <summary>Resolve an event selector token (sans the leading '!') to a command string.</summary>
    private static string ResolveEvent(string token, IReadOnlyList<string> history, out string? error)
    {
        error = null;

        if (token is "!" or "")
        {
            if (history.Count == 0) { error = "!!: event not found"; return ""; }
            return history[^1];
        }

        if (token[0] == '-' && int.TryParse(token[1..], out var back) && back > 0)
        {
            if (back > history.Count) { error = $"!{token}: event not found"; return ""; }
            return history[^back];
        }

        if (int.TryParse(token, out var n))
        {
            // Absolute 1-based session index.
            if (n >= 1 && n <= history.Count) return history[n - 1];
            error = $"!{token}: event not found";
            return "";
        }

        // !str — most recent command starting with str.
        for (var k = history.Count - 1; k >= 0; k--)
        {
            if (history[k].StartsWith(token, StringComparison.Ordinal))
                return history[k];
        }
        error = $"!{token}: event not found";
        return "";
    }

    private static string ResolveContains(string needle, IReadOnlyList<string> history, out string? error)
    {
        error = null;
        for (var k = history.Count - 1; k >= 0; k--)
        {
            if (history[k].Contains(needle, StringComparison.Ordinal))
                return history[k];
        }
        error = $"!?{needle}?: event not found";
        return "";
    }

    /// <summary>Apply a word designator (<c>$</c>, <c>^</c>, <c>*</c>, or a number) to a command.</summary>
    private static string ApplyWordDesignator(
        string command, string designator, string line, int start, ref string? error, ref int i)
    {
        var words = SplitWords(command);
        if (words.Count == 0)
        {
            error = $"{line[start..(i + 1)]}: bad word specifier";
            i = start;
            return "";
        }

        switch (designator)
        {
            case "$": return words[^1];
            case "^": return words.Count > 1 ? words[1] : words[0];
            case "*": return words.Count > 1 ? string.Join(' ', words.Skip(1)) : "";
            default:
                if (int.TryParse(designator, out var w) && w >= 0 && w < words.Count)
                    return words[w];
                error = $"{line[start..(i + 1)]}: bad word specifier";
                i = start;
                return "";
        }
    }

    /// <summary>Quick substitution: <c>^old^new[^]</c> on the previous command.</summary>
    private static string? ExpandQuickSubstitution(string line, IReadOnlyList<string> history, out string? error)
    {
        error = null;
        if (history.Count == 0) { error = ":s: event not found"; return null; }

        var rest = line[1..];
        var sep = rest.IndexOf('^');
        if (sep < 0) { error = "bad substitution"; return null; }

        var oldText = rest[..sep];
        var after = rest[(sep + 1)..];
        var sep2 = after.IndexOf('^');
        var newText = sep2 < 0 ? after : after[..sep2];
        var trailing = sep2 < 0 ? "" : after[(sep2 + 1)..];

        var prev = history[^1];
        var idx = prev.IndexOf(oldText, StringComparison.Ordinal);
        if (oldText.Length == 0 || idx < 0)
        {
            error = $"{oldText}: substitution failed";
            return null;
        }

        return string.Concat(prev[..idx], newText, prev[(idx + oldText.Length)..], trailing);
    }

    /// <summary>Whitespace-split a command into words (the unit a word designator indexes).</summary>
    private static List<string> SplitWords(string command) =>
        command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
}
