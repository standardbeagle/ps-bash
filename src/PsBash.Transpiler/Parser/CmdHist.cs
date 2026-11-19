using System.Text;

namespace PsBash.Core.Parser;

/// <summary>
/// bash's <c>cmdhist</c>: the one history entry a command read over several input lines becomes. Used for every
/// history entry ps-bash records: a piped <c>bash -i</c> session (PsBash.Shell PipedInteractive) and a script's
/// lines under <c>set -o history</c> (<see cref="PsEmitter"/> reader report, docs/specs/environment-model.md section 10).
/// </summary>
public static class CmdHist
{
    /// <summary>The history entry of <paramref name="text"/>, one command's input lines joined by <c>\n</c>.</summary>
    public static string Entry(string text) =>
        text.IndexOf('\n') < 0 ? text : Entry(text.Split('\n'));

    /// <summary>The history entry bash stores for a multi-line command (bash 5.2 oracle, <c>history</c>): the lines
    /// joined with <c>; </c>, or a space after a line that opens a compound command or ends in an operator; a
    /// backslash-newline is removed. A command holding a here-document or a quoted newline keeps its newlines.</summary>
    public static string Entry(IReadOnlyList<string> lines)
    {
        if (lines.Count == 1) return lines[0];
        var q = new LineQuotes();
        var heredocs = new Queue<(string, bool)>();
        for (int i = 0; i < lines.Count - 1; i++)
        {
            q.Scan(lines[i], heredocs);
            if (q.Open || heredocs.Count > 0) return string.Join('\n', lines);
        }
        var sb = new StringBuilder();
        var logical = new StringBuilder();   // the previous input line, its backslash-newlines joined
        bool continued = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = i == 0 || continued ? lines[i] : lines[i].Trim();
            if (i > 0 && !continued)
            {
                if (line.Length == 0) continue;
                var prev = logical.ToString().TrimEnd();
                bool opens = prev.Length == 0 || prev.EndsWith(';') || prev.EndsWith('{') || prev.EndsWith('(') || prev.EndsWith('|')
                    || prev.EndsWith("&&", StringComparison.Ordinal)
                    || EndsWithWord(prev, "do") || EndsWithWord(prev, "then") || EndsWithWord(prev, "else")
                    // `case x in` / `for x in`: `in` as a keyword, not `echo in`.
                    || (EndsWithWord(prev, "in") && (prev.TrimStart().StartsWith("case ", StringComparison.Ordinal) || prev.TrimStart().StartsWith("for ", StringComparison.Ordinal)));
                sb.Append(opens ? " " : "; ");
                logical.Clear();
            }
            continued = i < lines.Count - 1 && EndsWithContinuation(line);
            var part = continued ? line.AsSpan(0, line.Length - 1) : line.AsSpan();
            sb.Append(part);
            logical.Append(part);
        }
        return sb.ToString();
    }

    /// <summary>Whether <paramref name="line"/> ends in an unescaped backslash (a backslash-newline joins the next line).</summary>
    public static bool EndsWithContinuation(string line)
    {
        int n = 0;
        for (int i = line.Length - 1; i >= 0 && line[i] == '\\'; i--) n++;
        return n % 2 == 1;
    }

    private static bool EndsWithWord(string s, string word) =>
        s.EndsWith(word, StringComparison.Ordinal) && (s.Length == word.Length || char.IsWhiteSpace(s[^(word.Length + 1)]) || s[^(word.Length + 1)] == ';');

    /// <summary>Quote state carried across the lines of one command; also queues each here-document delimiter.</summary>
    public sealed class LineQuotes
    {
        private bool _single, _double;

        /// <summary>Whether a single or double quote is still open at the end of the lines scanned so far.</summary>
        public bool Open => _single || _double;

        /// <summary>Scans one input line, carrying the quote state and queueing each here-document's delimiter.</summary>
        public void Scan(string line, Queue<(string, bool)> heredocs)
        {
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (_single) { if (c == '\'') _single = false; continue; }
                if (c == '\\') { i++; continue; }
                if (_double) { if (c == '"') _double = false; continue; }
                switch (c)
                {
                    case '\'': _single = true; break;
                    case '"': _double = true; break;
                    case '#' when i == 0 || char.IsWhiteSpace(line[i - 1]) || line[i - 1] is ';' or '&' or '|' or '(':
                        return;
                    case '<' when i + 1 < line.Length && line[i + 1] == '<' && (i + 2 >= line.Length || line[i + 2] != '<'):
                        i = ReadHeredocWord(line, i + 2, heredocs);
                        break;
                }
            }
        }

        /// <summary>Reads the delimiter after <c>&lt;&lt;</c> / <c>&lt;&lt;-</c>; returns the index of its last char.</summary>
        private static int ReadHeredocWord(string line, int i, Queue<(string, bool)> heredocs)
        {
            bool strip = i < line.Length && line[i] == '-';
            if (strip) i++;
            while (i < line.Length && line[i] is ' ' or '\t') i++;
            var word = new StringBuilder();
            int start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] is not (';' or '&' or '|' or '<' or '>' or '(' or ')'))
            {
                char c = line[i];
                if (c is '\'' or '"')
                {
                    int close = line.IndexOf(c, i + 1);
                    if (close < 0) close = line.Length;
                    word.Append(line, i + 1, Math.Max(0, close - i - 1));
                    i = close + 1;
                    continue;
                }
                if (c == '\\' && i + 1 < line.Length) { word.Append(line[i + 1]); i += 2; continue; }
                word.Append(c);
                i++;
            }
            // `$((1 << 2))` is a shift, not a here-document: a delimiter never starts with a digit here.
            if (word.Length > 0 && !char.IsAsciiDigit(line[start])) heredocs.Enqueue((word.ToString(), strip));
            return i - 1;
        }
    }
}
