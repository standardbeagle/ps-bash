using System.Text;

namespace PsBash.Core;

/// <summary>
/// Bash-style history expansion ("bang commands"): the ONE expander, used by the interactive REPL
/// (<c>Host/Shell/InteractiveShell.cs</c>, on the typed line BEFORE alias expansion and transpilation, the
/// same order bash uses) and by <c>history -p</c> (<c>Cmdlets/InvokeBashHistoryCommand.cs</c>), each against
/// its own command list. Supported forms:
///
/// <list type="bullet">
///   <item><c>!!</c> — the previous command.</item>
///   <item><c>!n</c> — command number <c>n</c> in this session (1-based).</item>
///   <item><c>!-n</c> — the n-th command back (<c>!-1</c> == <c>!!</c>).</item>
///   <item><c>!str</c> — the most recent command starting with <c>str</c>.</item>
///   <item><c>!?str?</c> — the most recent command containing <c>str</c>.</item>
///   <item><c>!$</c> / <c>!^</c> / <c>!*</c> — last / first / all argument words of the previous command.</item>
///   <item>A <c>:</c> word designator (<c>:$</c>, <c>:^</c>, <c>:*</c>, <c>:n</c>) after any event.</item>
///   <item>Substitution modifiers after an event or designator, chained (<c>:s/old/new/:s/a/b/</c>): <c>:s</c> (first
///   match; any delimiter, the last one optional; <c>&amp;</c> in <c>new</c> is <c>old</c>), <c>:&amp;</c> (repeat the last),
///   <c>g</c>/<c>a</c> in front (every match). See <see cref="HistorySubstitution"/>.</item>
///   <item><c>^old^new</c> — quick substitution on the previous command (first match only).</item>
/// </list>
///
/// Expansion is suppressed inside single quotes and after a backslash (bash semantics);
/// a <c>!</c> followed by whitespace, <c>=</c>, <c>(</c>, or end-of-line stays literal so the
/// pipeline-negation operator (<c>! cmd</c>) is untouched.
///
/// Scope is the common 80/20 of bash history expansion; ranged word designators
/// (<c>!!:2-4</c>), <c>:G</c> and the other modifiers (<c>:h</c>, <c>:t</c>, <c>:p</c>) are out of scope.
/// </summary>
public static class HistoryExpander
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
    /// <param name="line">The typed line (or one <c>history -p</c> word).</param>
    /// <param name="history">The command list, oldest first.</param>
    /// <param name="error">The bash-style message of a failed reference, else null.</param>
    /// <param name="lastSubstitution">The last substitution made (bash keeps one per session, for <c>:&amp;</c> and an
    /// empty <c>old</c>); updated here. Null: a fresh one for this line.</param>
    public static string? Expand(string line, IReadOnlyList<string> history, out string? error, HistorySubstitution? lastSubstitution = null)
    {
        error = null;
        var last = lastSubstitution ?? new HistorySubstitution();

        // Quick substitution: ^old^new[^]  — operates on the previous command only.
        if (line.Length > 0 && line[0] == '^')
            return ExpandQuickSubstitution(line, history, last, out error);

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

            var replacement = ExpandBang(line, ref i, history, last, out error);
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
    private static string ExpandBang(string line, ref int i, IReadOnlyList<string> history, HistorySubstitution last, out string? error)
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
            var word = ApplyWordDesignator(prevCmd, desig.ToString(), line, start, ref error, ref i);
            return error != null ? "" : ApplyModifiers(word, line, start, ref i, last, out error);
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
        if (i + 1 < line.Length && line[i + 1] == ':' && i + 2 < line.Length && !IsModifierAt(line, i + 2))
        {
            var ds = i + 2;
            if (ds < line.Length)
            {
                var de = ds;
                while (de < line.Length && (char.IsLetterOrDigit(line[de]) || line[de] is '$' or '^' or '*'))
                    de++;
                var designator = line[ds..de];
                i = de - 1;
                eventCmd = ApplyWordDesignator(eventCmd, designator, line, start, ref error, ref i);
                if (error != null) return "";
            }
        }

        return ApplyModifiers(eventCmd, line, start, ref i, last, out error);
    }

    /// <summary>Does a substitution modifier start at <paramref name="at"/> (after a <c>:</c>): <c>s</c>, <c>&amp;</c>, or
    /// either behind <c>g</c> / <c>a</c>?</summary>
    private static bool IsModifierAt(string line, int at) =>
        line[at] is 's' or '&'
        || line[at] is 'g' or 'a' && at + 1 < line.Length && line[at + 1] is 's' or '&';

    /// <summary>
    /// bash's substitution modifiers right after the event (<paramref name="i"/> = its last char), applied in turn
    /// (<c>!e:s/e/E/:s/c/C/</c>): <c>:s/old/new/</c> replaces the first <c>old</c>, <c>:&amp;</c> repeats the last
    /// substitution, a <c>g</c> (or <c>a</c>) in front replaces every one (<c>:gs/e/E/</c>, <c>:g&amp;</c>). The delimiter
    /// is any character, the last one optional; a backslash quotes it. In <c>new</c> a <c>&amp;</c> is <c>old</c>
    /// (<c>\&amp;</c> a plain &amp;). An empty <c>old</c> is the last one. <c>:s</c> with no delimiter does nothing (bash).
    /// No <c>old</c> in the text, or nothing to repeat, fails the expansion. Other modifiers are left as text.
    /// </summary>
    private static string ApplyModifiers(string text, string line, int start, ref int i, HistorySubstitution last, out string? error)
    {
        error = null;
        while (i + 2 < line.Length && line[i + 1] == ':' && IsModifierAt(line, i + 2))
        {
            int m = i + 2;
            bool global = line[m] is 'g' or 'a';
            if (global) m++;
            string? lhs, rhs;
            int end;
            if (line[m] == '&')
            {
                (lhs, rhs, end) = (last.Lhs, last.Rhs, m);
            }
            else if (m + 1 >= line.Length)
            {
                i = m;   // `!!:s` with no delimiter: bash ignores it
                continue;
            }
            else
            {
                char delim = line[m + 1];
                int p = m + 2;
                lhs = ReadSubstPart(line, ref p, delim);
                rhs = "";
                if (p < line.Length) { p++; rhs = ReadSubstPart(line, ref p, delim); }
                end = p < line.Length ? p : line.Length - 1;   // the closing delimiter, or the end of the line
                if (lhs.Length == 0) lhs = last.Lhs;
                if (lhs is not null) rhs = ReplaceAmpersands(rhs, lhs);
            }
            if (lhs is null || rhs is null)
            {
                error = $"{line[start..(end + 1)]}: no previous substitution";
                i = start;
                return "";
            }
            last.Lhs = lhs;
            last.Rhs = rhs;
            int idx = text.IndexOf(lhs, StringComparison.Ordinal);
            if (idx < 0)
            {
                error = $"{line[start..(end + 1)]}: substitution failed";
                i = start;
                return "";
            }
            text = global
                ? text.Replace(lhs, rhs, StringComparison.Ordinal)
                : string.Concat(text.AsSpan(0, idx), rhs, text.AsSpan(idx + lhs.Length));
            i = end;
        }
        return text;
    }

    /// <summary>One side of <c>:s/old/new/</c> from <paramref name="p"/> up to <paramref name="delim"/> (or the end), a
    /// backslash before the delimiter removed; <paramref name="p"/> ends at the delimiter.</summary>
    private static string ReadSubstPart(string line, ref int p, char delim)
    {
        var sb = new StringBuilder();
        for (; p < line.Length && line[p] != delim; p++)
        {
            if (line[p] == '\\' && p + 1 < line.Length && line[p + 1] == delim) p++;
            sb.Append(line[p]);
        }
        return sb.ToString();
    }

    /// <summary>A replacement's <c>&amp;</c> is the matched text; <c>\&amp;</c> is a plain <c>&amp;</c>.</summary>
    private static string ReplaceAmpersands(string rhs, string lhs)
    {
        if (!rhs.Contains('&')) return rhs;
        var sb = new StringBuilder(rhs.Length + lhs.Length);
        for (int k = 0; k < rhs.Length; k++)
        {
            if (rhs[k] == '\\' && k + 1 < rhs.Length && rhs[k + 1] == '&') { sb.Append('&'); k++; }
            else if (rhs[k] == '&') sb.Append(lhs);
            else sb.Append(rhs[k]);
        }
        return sb.ToString();
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
    private static string? ExpandQuickSubstitution(string line, IReadOnlyList<string> history, HistorySubstitution last, out string? error)
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

        (last.Lhs, last.Rhs) = (oldText, newText);
        return string.Concat(prev[..idx], newText, prev[(idx + oldText.Length)..], trailing);
    }

    /// <summary>Whitespace-split a command into words (the unit a word designator indexes).</summary>
    private static List<string> SplitWords(string command) =>
        command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
}

/// <summary>
/// The last substitution a history expansion made (<c>:s/old/new/</c>, <c>^old^new</c>), which <c>:&amp;</c> and an empty
/// <c>old</c> reuse. bash keeps one for the whole session, so a caller keeps one across its lines (<c>history -p</c>:
/// <c>'!!:s/e/E/' '!!:&amp;'</c>).
/// </summary>
public sealed class HistorySubstitution
{
    /// <summary>The text replaced.</summary>
    public string? Lhs { get; set; }

    /// <summary>The replacement, its <c>&amp;</c> already the replaced text.</summary>
    public string? Rhs { get; set; }
}
