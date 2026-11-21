using System.Text;
using PsBash.Core.Parser;

namespace PsBash.Shell;

/// <summary>
/// <c>bash -i</c> with its input piped (<c>echo 'cmd' | bash -i</c>, <c>bash -i &lt;&lt;EOF</c>): an interactive
/// shell that reads its commands from the pipe. bash reads one complete command at a time and, before each prompt,
/// runs <c>PROMPT_COMMAND</c>; it writes the prompt (and the echoed line) to stderr, adds each command to the
/// history before running it, expands <c>!!</c>, and saves the history to <c>$HISTFILE</c> at end of input.
/// <para>
/// <see cref="BuildScript"/> turns the input into one bash script that does the same: each command runs through
/// <c>eval</c> (so a syntax error or an arithmetic error ends only that command, and <c>shopt</c> / alias changes
/// apply to the commands after it), <c>$?</c> carries over from one command to the next and into
/// <c>PROMPT_COMMAND</c>, and the script exits with the last command's status.
/// </para>
/// <para>
/// History file: loaded at start only when <c>HISTFILE</c> came from the environment, and saved at end of input
/// unless it is still the launcher's default (<c>~/.bash_history</c>), so a piped session never reads or rewrites
/// the user's own history file unless asked to. An explicit <c>exit</c> ends the session without saving.
/// </para>
/// The REPL (line editor, persistent history) remains for a terminal, or with <c>PSBASH_INTERACTIVE_REPL=1</c>.
/// </summary>
internal static class PipedInteractive
{
    /// <summary>Splits piped input into complete commands, one per bash prompt: a command continues over the
    /// next lines while a quote, a here-document, a trailing backslash, or a compound command is still open.
    /// Blank lines are kept as empty commands (bash still prompts, and runs <c>PROMPT_COMMAND</c>, for them).</summary>
    internal static List<List<string>> SplitCommands(string input)
    {
        var result = new List<List<string>>();
        var lines = input.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var acc = new List<string>();
        var heredocs = new Queue<(string Delim, bool StripTabs)>();
        var quote = new CmdHist.LineQuotes();
        foreach (var line in lines)
        {
            acc.Add(line);
            if (heredocs.Count > 0)
            {
                var (delim, strip) = heredocs.Peek();
                if ((strip ? line.TrimStart('\t') : line) == delim) heredocs.Dequeue();
                if (heredocs.Count > 0) continue;
            }
            else
            {
                quote.Scan(line, heredocs);
                if (heredocs.Count > 0 || quote.Open || CmdHist.EndsWithContinuation(line)) continue;
            }
            if (!IsComplete(string.Join('\n', acc))) continue;
            result.Add(acc);
            acc = new List<string>();
            quote = new CmdHist.LineQuotes();
        }
        if (acc.Count > 0) result.Add(acc);
        return result;
    }

    /// <summary>True when the text parses, or fails with an error that more input cannot fix.</summary>
    internal static bool IsComplete(string text)
    {
        try
        {
            BashParser.Parse(text);
            return true;
        }
        catch (ParseException ex)
        {
            // An empty unexpected token is the end of input (`for i in 1; do` alone).
            var m = ex.Message;
            return !(m.Contains("(Eof)", StringComparison.Ordinal)
                     || m.Contains("unexpected token `'", StringComparison.Ordinal)
                     || m.Contains("end of input", StringComparison.OrdinalIgnoreCase)
                     || m.Contains("unexpected EOF", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>History expansion of one command against the session's earlier entries (most recent last):
    /// <c>!!</c>, <c>!-n</c>, <c>!$</c>, <c>!prefix</c>. Not inside single quotes, after a backslash, or for a
    /// <c>!</c> before a blank, <c>=</c>, <c>(</c> or the end (bash leaves those alone). Null with
    /// <paramref name="error"/> set when an event is not found.</summary>
    internal static string? ExpandHistory(string line, IReadOnlyList<string> history, out string? error)
    {
        error = null;
        if (!line.Contains('!')) return line;
        var sb = new StringBuilder(line.Length);
        bool single = false, dbl = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (single) { sb.Append(c); if (c == '\'') single = false; continue; }
            if (c == '\\' && i + 1 < line.Length) { sb.Append(c).Append(line[++i]); continue; }
            if (c == '\'' && !dbl) { single = true; sb.Append(c); continue; }
            if (c == '"') { dbl = !dbl; sb.Append(c); continue; }
            if (c != '!' || i + 1 >= line.Length) { sb.Append(c); continue; }
            char n = line[i + 1];
            if (char.IsWhiteSpace(n) || n is '=' or '(' || (dbl && n == '"') || (i > 0 && line[i - 1] is '$' or '{'))
            {
                sb.Append(c);
                continue;
            }
            string? previous = history.Count > 0 ? history[^1] : null;
            if (n == '!')
            {
                if (previous is null) { error = "!!: event not found"; return null; }
                sb.Append(previous); i++;
                continue;
            }
            if (n == '$')
            {
                if (previous is null) { error = "!$: event not found"; return null; }
                var words = previous.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                sb.Append(words.Length > 0 ? words[^1] : ""); i++;
                continue;
            }
            int j = i + 1;
            if (n == '-')
            {
                j++;
                while (j < line.Length && char.IsAsciiDigit(line[j])) j++;
                if (j == i + 2) { sb.Append(c); continue; }
                int back = int.Parse(line[(i + 2)..j], System.Globalization.CultureInfo.InvariantCulture);
                if (back < 1 || back > history.Count) { error = line[i..j] + ": event not found"; return null; }
                sb.Append(history[^back]); i = j - 1;
                continue;
            }
            if (char.IsAsciiDigit(n)) { sb.Append(c); continue; }
            while (j < line.Length && !char.IsWhiteSpace(line[j]) && line[j] is not (';' or '&' or '|' or '"' or '\'' or ':')) j++;
            var prefix = line[(i + 1)..j];
            var match = history.LastOrDefault(h => h.StartsWith(prefix, StringComparison.Ordinal));
            if (match is null) { error = "!" + prefix + ": event not found"; return null; }
            sb.Append(match); i = j - 1;
        }
        return sb.ToString();
    }

    private static string Sq(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>The bash script that replays <paramref name="input"/> as an interactive session (see the type).
    /// <paramref name="loadHistory"/>: read <c>$HISTFILE</c> first. <paramref name="noSaveFile"/>: a
    /// <c>$HISTFILE</c> value the session must not write (the launcher's default). <paramref name="rcFile"/>: the
    /// <c>--rcfile</c> sourced before the first prompt (<see cref="ShellArgs.RcSourceCommand"/>), so its <c>PS1</c>
    /// is the one the prompts show.</summary>
    internal static string BuildScript(string input, bool loadHistory, string? noSaveFile, string? rcFile = null)
    {
        var sb = new StringBuilder();
        // An interactive shell ($- has i, emacs/monitor/history on), marked before the rc runs so its
        // `case $- in *i*)` guard sees it, as the REPL and `-i -c` do.
        sb.Append("shopt -s ").Append(ShellArgs.InteractiveMark).Append('\n');
        if (rcFile is not null) sb.Append(ShellArgs.RcSourceCommand(rcFile)).Append('\n');
        sb.Append("set -o history\n");
        if (loadHistory) sb.Append("history -r 2>/dev/null\n");
        // The status is put back INSIDE the branch: the `[ -n … ]` test is a command, so before it $? would be its 0.
        const string promptCommand =
            "if [ -n \"${PROMPT_COMMAND-}\" ]; then (exit $__psbash_status); eval \"$PROMPT_COMMAND\"; fi\n";
        var session = new List<string>();
        foreach (var chunk in SplitCommands(input))
        {
            var lines = chunk;
            sb.Append("__psbash_status=$?\n").Append(promptCommand);
            for (int k = 0; k < lines.Count; k++)
                sb.Append("printf '%s%s\\n' \"${").Append(k == 0 ? "PS1" : "PS2").Append("@P}\" ").Append(Sq(lines[k])).Append(" >&2\n");
            var command = string.Join('\n', lines);
            if (command.Trim().Length == 0) { sb.Append("(exit $__psbash_status)\n"); continue; }

            var expanded = ExpandHistory(command, session, out var error);
            if (expanded is null)
            {
                sb.Append("printf '%s\\n' ").Append(Sq("bash: " + error)).Append(" >&2; (exit 1)\n");
                continue;
            }
            if (expanded != command)
            {
                sb.Append("printf '%s\\n' ").Append(Sq(expanded)).Append(" >&2\n");
                lines = expanded.Split('\n').ToList();
                command = expanded;
            }
            var entry = CmdHist.Entry(lines);
            session.Add(entry);
            sb.Append("history -s -- ").Append(Sq(entry)).Append('\n');
            sb.Append("__psbash_line=").Append(Sq(command)).Append('\n');
            sb.Append("(exit $__psbash_status); eval \"$__psbash_line\"\n");
        }
        sb.Append("__psbash_status=$?\n").Append(promptCommand);
        sb.Append("printf '%sexit\\n' \"${PS1@P}\" >&2\n");
        sb.Append("if [ -n \"${HISTFILE-}\" ]");
        if (!string.IsNullOrEmpty(noSaveFile)) sb.Append(" && [ \"$HISTFILE\" != ").Append(Sq(noSaveFile)).Append(" ]");
        sb.Append("; then history -w 2>/dev/null; fi\n");
        sb.Append("exit $__psbash_status\n");
        return sb.ToString();
    }
}
