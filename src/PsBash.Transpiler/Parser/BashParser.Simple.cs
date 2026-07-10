using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// Simple-command parsing for <see cref="BashParser"/>: the word/redirect/
/// heredoc/env-prefix loop, here-document body capture, assignments, and
/// redirect parsing. Split from the parser spine in BashParser.cs.
/// </summary>
public sealed partial class BashParser
{
    private Command ParseSimpleCommand()
    {
        // Check for "export" keyword followed by assignment words or bare variable names.
        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "export")
        {
            int saved = _pos;
            Advance(); // consume "export"

            if (Peek().Kind == BashTokenKind.AssignmentWord || Peek().Kind == BashTokenKind.Word)
            {
                // Bash: `export` accepts an interleaved mix of NAME=VAL and bare NAME args
                // in any order (e.g. `eval $(printf 'export A=1\nexport B=2\n')` field-splits
                // to `export A=1 export B=2`). Consume both kinds in a single loop so a bare
                // Word in the middle does not silently drop subsequent assignments.
                var pairs = ImmutableArray.CreateBuilder<Assignment>();
                while (Peek().Kind == BashTokenKind.AssignmentWord || Peek().Kind == BashTokenKind.Word)
                {
                    if (Peek().Kind == BashTokenKind.AssignmentWord)
                    {
                        pairs.Add(ParseAssignmentWord());
                    }
                    else
                    {
                        var name = Peek().Value;
                        Advance();
                        pairs.Add(new Assignment(name, AssignOp.Equal, null));
                    }
                }
                if (pairs.Count > 0)
                    return new Command.ShAssignment(pairs.ToImmutable());
            }

            // Not followed by assignment or names — rewind and parse as normal command.
            _pos = saved;
        }

        // Check for "local" keyword followed by assignment words or bare variable names.
        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "local")
        {
            int saved = _pos;
            Advance(); // consume "local"

            if (Peek().Kind == BashTokenKind.AssignmentWord || Peek().Kind == BashTokenKind.Word)
            {
                // Same interleaving rule as `export` above.
                var pairs = ImmutableArray.CreateBuilder<Assignment>();
                while (Peek().Kind == BashTokenKind.AssignmentWord || Peek().Kind == BashTokenKind.Word)
                {
                    if (Peek().Kind == BashTokenKind.AssignmentWord)
                    {
                        pairs.Add(ParseAssignmentWordWithArray());
                    }
                    else
                    {
                        var name = Peek().Value;
                        Advance();
                        pairs.Add(new Assignment(name, AssignOp.Equal, null));
                    }
                }
                if (pairs.Count > 0)
                    return new Command.ShAssignment(pairs.ToImmutable(), IsLocal: true);
            }

            // Not followed by assignment or names — rewind and parse as normal command.
            _pos = saved;
        }

        // Collect leading assignment words (VAR=val ...).
        // Array assignments like arr=(a b c) are detected and consumed here.
        var assignmentPairs = ImmutableArray.CreateBuilder<Assignment>();
        var envPairs = ImmutableArray.CreateBuilder<EnvPair>();
        bool hasArrayAssignment = false;
        while (Peek().Kind == BashTokenKind.AssignmentWord)
        {
            var assign = ParseAssignmentWordWithArray();
            if (assign.ArrayValue is not null)
                hasArrayAssignment = true;
            assignmentPairs.Add(assign);
            envPairs.Add(new EnvPair(assign.Name, assign.Value));
        }

        // If we consumed array assignments, they must be bare assignments.
        if (hasArrayAssignment)
            return new Command.ShAssignment(assignmentPairs.ToImmutable());

        var words = ImmutableArray.CreateBuilder<CompoundWord>();
        var redirects = ImmutableArray.CreateBuilder<Redirect>();
        var hereDocs = ImmutableArray.CreateBuilder<HereDoc>();
        var pendingHeredocs = new List<(string Delimiter, bool Expand, bool StripTabs)>();

        while (true)
        {
            var kind = Peek().Kind;

            if (kind == BashTokenKind.Word)
            {
                // Named-fd redirect prefix `{name}` before a redirect (bash
                // allocates a free fd into $name). PowerShell has no dynamic fd
                // allocation, so drop the prefix and let the redirect parse as a
                // default-fd redirect (documented degrade). The lexer only emits a
                // `{name}` Word when a redirect immediately follows.
                if (IsNamedFdToken(Peek().Value)
                    && _pos + 1 < _tokens.Count
                    && (_tokens[_pos + 1].Kind == BashTokenKind.IoNumber
                        || IsRedirectOp(_tokens[_pos + 1].Kind)))
                {
                    Advance(); // drop {name}
                    continue;
                }

                // Stop at reserved words that delimit compound commands —
                // but ONLY when they appear in the position bash itself
                // treats as a keyword. Per bash man: reserved words are
                // recognized only as the first word of a simple command,
                // OR following another reserved word (e.g. `do ... done`).
                // Inside `echo done`, `done` is just an argument; without
                // this guard the parser would drop the argument and emit
                // `Invoke-BashEcho` with no operand, which silently hangs
                // because Invoke-BashEcho reads stdin when no args.
                if (words.Count == 0 && IsCompoundDelimiter(Peek().Value))
                    break;

                var token = Advance();
                var parts = DecomposeWord(token.Value);
                words.Add(new CompoundWord(parts));
            }
            else if (kind == BashTokenKind.AssignmentWord)
            {
                // After command words have started, treat assignment-like tokens as arguments.
                var token = Advance();
                var parts = DecomposeWord(token.Value);
                words.Add(new CompoundWord(parts));
            }
            else if (kind == BashTokenKind.Bang && words.Count > 0)
            {
                // A `!` AFTER the command word is a literal argument, not negation:
                // `find . ! -name x`, `test ! -f y`. Per bash, `!` is the negation
                // reserved word only at the start of a pipeline — ParsePipeline
                // already consumes a leading run of `!` before we get here, so any
                // `!` reaching this loop mid-command is an operand. Without this it
                // fell to the `else break`, ending the command and leaving
                // `! -name x` to be mis-parsed as a separate negated pipeline.
                var token = Advance();
                words.Add(new CompoundWord(DecomposeWord(token.Value)));
            }
            else if (kind == BashTokenKind.TLess)
            {
                // Here-string: <<< word — the word becomes the body directly.
                Advance(); // consume <<<
                var wordToken = Advance();
                string raw = wordToken.Value;
                // Strip surrounding quotes from the here-string word. Require at
                // least two chars so a lone quote (`<<< '`) — where StartsWith and
                // EndsWith both see the SAME single char — is not sliced to a
                // start-past-end range (raw[1..^1] on length 1 threw
                // ArgumentOutOfRangeException).
                bool expand = true;
                if (raw.Length >= 2
                    && ((raw.StartsWith('\'') && raw.EndsWith('\''))
                        || (raw.StartsWith('"') && raw.EndsWith('"'))))
                {
                    expand = raw[0] == '"';
                    raw = raw[1..^1];
                }
                // bash: a here-string supplies the word PLUS a trailing
                // newline to the command's stdin. `cat <<< "hello"` prints
                // "hello\n", not "hello". The emitter wraps the body in a
                // here-string @"..."@ which itself swallows the @-line
                // newlines, so we have to encode the trailing newline by
                // appending it to the body value here.
                if (!raw.EndsWith('\n'))
                    raw += "\n";
                hereDocs.Add(new HereDoc(raw, expand, StripTabs: false));
            }
            else if (kind is BashTokenKind.DLess or BashTokenKind.DLessDash)
            {
                bool stripTabs = kind == BashTokenKind.DLessDash;
                Advance(); // consume << or <<-
                var delimToken = Advance();
                // Quoted (`'EOF'` / `"EOF"`) or backslashed (`\EOF`) delimiters
                // suppress expansion; the shared helper is the single source the
                // lexer's body-skip also uses, so the two can't disagree on the
                // terminator.
                var (delimiter, expand) = BashLexer.ParseHeredocDelimiter(delimToken.Value);
                pendingHeredocs.Add((delimiter, expand, stripTabs));
            }
            else if (kind == BashTokenKind.IoNumber || IsRedirectOp(kind))
            {
                redirects.Add(ParseRedirect());
            }
            else
            {
                break;
            }
        }

        // If only assignments and no command words, it's a bare assignment.
        if (assignmentPairs.Count > 0 && words.Count == 0 && redirects.Count == 0
            && pendingHeredocs.Count == 0 && hereDocs.Count == 0)
            return new Command.ShAssignment(assignmentPairs.ToImmutable());

        // Collect heredoc bodies in order for all pending heredoc redirects.
        if (pendingHeredocs.Count > 0)
            CollectHereDocBodies(pendingHeredocs, hereDocs);

        return new Command.Simple(
            words.ToImmutable(),
            envPairs.ToImmutable(),
            redirects.ToImmutable(),
            hereDocs.ToImmutable());
    }

    /// <summary>
    /// Collect the bodies of all pending here-documents from RAW source, in order.
    /// </summary>
    /// <remarks>
    /// A heredoc body is raw, line-oriented text — bash matches the delimiter
    /// against whole physical lines with no quote/comment interpretation. The lexer
    /// has already skipped the bodies (it emits NO body tokens), so the current
    /// token is the command-line newline that both starts the body region and
    /// serves as the separator before whatever follows the heredoc. We therefore
    /// read the body text straight from source — peeling one body per pending
    /// delimiter from a single running offset, which is what lets STACKED heredocs
    /// (<c>cat &lt;&lt;A &lt;&lt;B</c>) resolve in order — and DO NOT advance the
    /// token cursor: leaving that newline in place lets <c>ParseList</c> see the
    /// separator and parse the command after the heredoc.
    /// </remarks>
    private void CollectHereDocBodies(
        List<(string Delimiter, bool Expand, bool StripTabs)> pending,
        ImmutableArray<HereDoc>.Builder hereDocs)
    {
        // The first body line begins just past the command-line newline (peeked,
        // not consumed). If the command ended at EOF instead, there is no body.
        int scan = Peek().Kind switch
        {
            BashTokenKind.Newline => NextLineStart(Peek().Position),
            BashTokenKind.Eof => _input.Length,
            _ => Peek().Position,
        };

        foreach (var (delimiter, expand, stripTabs) in pending)
        {
            var bodyLines = new List<string>();
            while (scan < _input.Length)
            {
                int nlPos = _input.IndexOf('\n', scan);
                int lineEnd = nlPos < 0 ? _input.Length : nlPos;
                // A folded "\r\n": exclude the trailing '\r' from the line text.
                int textEnd = lineEnd > scan && _input[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
                string line = _input.Substring(scan, textEnd - scan);

                // For <<- the delimiter line may have leading tabs.
                string trimmedLine = stripTabs ? line.TrimStart('\t') : line;
                bool isDelim = trimmedLine == delimiter;
                if (!isDelim) bodyLines.Add(stripTabs ? line.TrimStart('\t') : line);

                if (nlPos < 0) { scan = _input.Length; break; }
                scan = nlPos + 1;
                if (isDelim) break;
            }
            hereDocs.Add(new HereDoc(BuildHereDocBody(bodyLines), expand, stripTabs));
        }
        // Cursor intentionally left at the command-line newline (the separator).
    }

    // bash: each body line (including the final one) is terminated by a newline, so
    // join with \n and append a trailing \n for byte parity through cat / read. An
    // empty body ("cat <<EOF\nEOF") yields "" because there are no body lines.
    private static string BuildHereDocBody(List<string> bodyLines) =>
        bodyLines.Count == 0 ? string.Empty : string.Join("\n", bodyLines) + "\n";

    /// <summary>
    /// Given the Position of a normalized Newline token — which points at the
    /// '\n', or at the '\r' of a "\r\n" pair the lexer folded into one token —
    /// return the source offset of the first character of the following line.
    /// </summary>
    private int NextLineStart(int newlinePos)
    {
        int p = newlinePos;
        if (p < _input.Length && _input[p] == '\r')
        {
            p++;
            if (p < _input.Length && _input[p] == '\n')
                p++;
        }
        else if (p < _input.Length && _input[p] == '\n')
        {
            p++;
        }
        return p;
    }

    private Assignment ParseAssignmentWord()
    {
        var token = Advance();
        var (name, value, op) = SplitAssignmentWord(token.Value);
        return new Assignment(name, op, value);
    }

    /// <summary>
    /// Parse an assignment word, consuming a trailing array literal <c>(a b c)</c> if present.
    /// </summary>
    private Assignment ParseAssignmentWordWithArray()
    {
        var token = Advance();
        var (name, value, op) = SplitAssignmentWord(token.Value);

        // Array assignment: arr=(a b c) or arr+=('x') — the assignment word
        // ends at '=' / '+=' and the next token is LParen. A plain empty
        // assignment (`x=`) is different: it assigns an explicit empty string.
        if (value is not null && value.Parts.IsEmpty && Peek().Kind == BashTokenKind.LParen)
        {
            Advance(); // consume (
            var elements = ImmutableArray.CreateBuilder<CompoundWord>();
            while (Peek().Kind != BashTokenKind.RParen && Peek().Kind != BashTokenKind.Eof)
            {
                if (Peek().Kind == BashTokenKind.Word)
                {
                    var wordToken = Advance();
                    elements.Add(new CompoundWord(DecomposeWord(wordToken.Value)));
                }
                else
                {
                    break;
                }
            }
            if (Peek().Kind == BashTokenKind.RParen)
                Advance(); // consume )
            return new Assignment(name, op, null,
                new ArrayWord(elements.ToImmutable()));
        }

        return new Assignment(name, op, value);
    }

    private (string Name, CompoundWord? Value, AssignOp Op) SplitAssignmentWord(string raw)
    {
        int eqIndex = raw.IndexOf('=');
        bool isPlus = eqIndex > 0 && raw[eqIndex - 1] == '+';
        string name = isPlus ? raw[..(eqIndex - 1)] : raw[..eqIndex];
        string valueRaw = raw[(eqIndex + 1)..];
        var op = isPlus ? AssignOp.PlusEqual : AssignOp.Equal;

        if (valueRaw.Length == 0)
            return (name, new CompoundWord(ImmutableArray<WordPart>.Empty), op);

        // Assignment values expand a tilde at the start AND after each unquoted
        // ':' (PATH-style), unlike ordinary words — use the assignment-aware path.
        var parts = DecomposeAssignmentValue(valueRaw);
        return (name, new CompoundWord(parts), op);
    }

    private Redirect ParseRedirect()
    {
        int fd = -1;

        if (Peek().Kind == BashTokenKind.IoNumber)
        {
            var fdToken = Advance();
            // A pathological digit run (e.g. `999999999999>file`) overflows int.
            // Bash rejects an out-of-range fd; convert to a clean ParseException
            // rather than the raw OverflowException that violated the fuzz invariant.
            if (!int.TryParse(fdToken.Value, out fd))
                throw MakeError($"file descriptor '{fdToken.Value}' out of range", fdToken.Position, "redirect");
        }

        var opToken = Advance();
        string op = opToken.Kind switch
        {
            BashTokenKind.Great => ">",
            BashTokenKind.DGreat => ">>",
            BashTokenKind.Less => "<",
            BashTokenKind.GreatAnd => ">&",
            BashTokenKind.LessAnd => "<&",
            BashTokenKind.AmpGreat => "&>",
            BashTokenKind.AmpDGreat => "&>>",
            BashTokenKind.DLess => "<<",
            BashTokenKind.DLessDash => "<<-",
            _ => opToken.Value,
        };

        // Default fd: 0 for input redirects, 1 for output redirects.
        if (fd == -1)
        {
            fd = op[0] == '<' ? 0 : 1;
        }

        // Consume the target word. It MUST be a word — a redirect with no target
        // (`echo > ; ls`, `cat <` at a separator/EOF) is a syntax error in bash.
        // Without this guard Advance() swallowed the following `;`/newline/operator
        // token as the "target", silently merging two statements.
        if (Peek().Kind != BashTokenKind.Word)
            throw MakeError($"syntax error near unexpected token `{Peek().Value}'", Peek().Position, "redirect");
        var targetToken = Advance();
        var targetParts = DecomposeWord(targetToken.Value);
        var target = new CompoundWord(targetParts);

        return new Redirect(op, fd, target);
    }

    /// <summary>True for a <c>{varname}</c> named-fd prefix token (the lexer only
    /// emits this Word shape when a redirect immediately follows).</summary>
    private static bool IsNamedFdToken(string s)
    {
        if (s.Length < 3 || s[0] != '{' || s[^1] != '}')
            return false;
        string inner = s[1..^1];
        if (!(char.IsLetter(inner[0]) || inner[0] == '_'))
            return false;
        foreach (var ch in inner)
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                return false;
        return true;
    }

    private static bool IsRedirectOp(BashTokenKind kind) =>
        kind is BashTokenKind.Great or BashTokenKind.DGreat
            or BashTokenKind.Less or BashTokenKind.GreatAnd
            or BashTokenKind.LessAnd or BashTokenKind.AmpGreat
            or BashTokenKind.AmpDGreat or BashTokenKind.DLess
            or BashTokenKind.DLessDash;

    private static bool IsCompoundDelimiter(string word) =>
        word is "then" or "elif" or "else" or "fi"
            or "do" or "done" or "esac";
}
