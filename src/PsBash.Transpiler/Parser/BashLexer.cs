using System.Collections.Frozen;

namespace PsBash.Core.Parser;

/// <summary>
/// Hand-rolled lexer for bash input. Produces a flat token list from a string.
/// Context-sensitive aspects (here-docs) are deferred to the parser.
/// </summary>
public static class BashLexer
{
    private static readonly FrozenSet<string> ReservedWords = FrozenSet.ToFrozenSet(
    [
        "if", "then", "else", "elif", "fi",
        "do", "done",
        "case", "esac",
        "while", "until", "for", "in",
        "function",
    ]);

    /// <summary>
    /// Tokenize the given bash input into a list of tokens.
    /// The final token is always <see cref="BashTokenKind.Eof"/>.
    /// </summary>
    public static List<BashToken> Tokenize(string input)
    {
        var tokens = new List<BashToken>();
        int pos = 0;
        int len = input.Length;

        // Heredoc bodies are RAW LINE TEXT, not shell tokens. We must not tokenize
        // them: a body line with an unbalanced quote/backtick/paren would otherwise
        // make a word scanner swallow the delimiter line and every following command
        // into one token. So we record each `<<`/`<<-` delimiter (with its token
        // index) as it is seen and, at the newline that ends the command line, skip
        // past the bodies in raw source — recording each body's exact span on its
        // delimiter token so the parser never has to recompute it.
        var pendingHeredocs = new List<(string Delim, bool StripTabs, bool Expand, int DelimTokenIndex)>();
        // The next word token is a heredoc delimiter: 0 = none, 1 = `<<`, 2 = `<<-`.
        int heredocDelimPending = 0;
        // Line of the `for` command whose header word list is being scanned (0 = none).
        int forLine = 0;
        // End offset of the `(( … ))` arithmetic header being scanned: a `<<` before it is a shift, not a heredoc.
        int arithEnd = 0;

        while (pos < len)
        {
            char c = input[pos];

            // Skip spaces and tabs (not newlines).
            if (c is ' ' or '\t')
            {
                pos++;
                continue;
            }

            // `(( i = 1 << 3 ))` / `for ((i = 1 << 3; …))`: arithmetic, so its `<<` is a shift. The parser slices these
            // headers from the raw text; only the heredoc detection below needs to know where they end.
            if (c == '(' && pos + 1 < len && input[pos + 1] == '(' && pos >= arithEnd && AtArithHeaderPosition(tokens)
                && ArithBodyClosesDoubled(input, pos + 2))
            {
                int end = ScanArithBody(input, pos + 2);
                if (end >= pos + 4 && input[end - 1] == ')' && input[end - 2] == ')') arithEnd = end;
            }

            // Comments: skip to end of line.
            if (c == '#')
            {
                while (pos < len && input[pos] != '\n')
                    pos++;
                continue;
            }

            // Newline.
            if (c == '\n')
            {
                tokens.Add(new BashToken(BashTokenKind.Newline, "\n", pos));
                pos++;
                heredocDelimPending = 0;
                if (pendingHeredocs.Count > 0)
                {
                    pos = SkipHeredocBodies(input, pos, pendingHeredocs, tokens);
                    pendingHeredocs.Clear();
                }
                continue;
            }

            // Carriage return (normalize \r\n to single newline token). A lone CR inside a line is an ordinary word
            // character in bash (`echo\rTEST` is one command name, not found); only CRLF / a final CR ends the line.
            if (c == '\r' && IsLineEndCr(input, pos))
            {
                int start = pos;
                pos++;
                if (pos < len && input[pos] == '\n')
                    pos++;
                tokens.Add(new BashToken(BashTokenKind.Newline, "\n", start));
                heredocDelimPending = 0;
                if (pendingHeredocs.Count > 0)
                {
                    pos = SkipHeredocBodies(input, pos, pendingHeredocs, tokens);
                    pendingHeredocs.Clear();
                }
                continue;
            }

            // Line continuation: a backslash immediately before a newline is removed
            // entirely (bash joins the two lines). ScanWord only consumes this mid-word,
            // so a `\<newline>` sitting BETWEEN tokens — e.g. an indented continued line
            // `cmd \<nl>  -l` — used to fall through to ScanWord, which ate the `\<nl>`
            // and then broke on the following space, emitting a spurious zero-length Word.
            if (c == '\\' && pos + 1 < len)
            {
                if (input[pos + 1] == '\n') { pos += 2; continue; }
                if (input[pos + 1] == '\r')
                {
                    pos += 2;
                    if (pos < len && input[pos] == '\n') pos++;
                    continue;
                }
            }

            // Process substitution: <(...) or >(...) — consume as a word.
            // Quote-aware so a ')' inside a string in the producer does not
            // close the region early.
            if (c is '<' or '>' && pos + 1 < len && input[pos + 1] == '(')
            {
                int psStart = pos;
                pos = ScanBalancedParens(input, pos + 2, 1); // skip <( or >(
                string psText = input[psStart..pos];
                tokens.Add(new BashToken(BashTokenKind.Word, psText, psStart));
                continue;
            }

            // Two- and three-character operators. Probe with direct char reads
            // (no per-position Substring allocation) and emit interned string
            // literals as token text (no per-token allocation). Behaviour mirrors
            // the previous Substring(pos,2) switch exactly, including which forms
            // reclassify a preceding IO-number.
            if (pos + 1 < len)
            {
                char c2 = input[pos + 1];
                char c3 = pos + 2 < len ? input[pos + 2] : '\0';

                // `<<` family: `<<<` (here-string) and `<<-` are checked before plain `<<`.
                if (c == '<' && c2 == '<' && pos >= arithEnd)
                {
                    if (c3 == '<')
                    {
                        // `3<<< w`: a here-string takes a descriptor prefix like every redirection (RedirectOperators).
                        TryReclassifyIoNumber(tokens, pos);
                        tokens.Add(new BashToken(BashTokenKind.TLess, "<<<", pos));
                        pos += 3;
                        continue;
                    }
                    if (c3 == '-')
                    {
                        TryReclassifyIoNumber(tokens, pos);
                        tokens.Add(new BashToken(BashTokenKind.DLessDash, "<<-", pos));
                        pos += 3;
                        heredocDelimPending = 2;
                        continue;
                    }
                    // This branch only ever emits DLess (the `<<<`/`<<-` cases above
                    // already returned), and DLess is always a redirect kind — so
                    // `IsRedirectKind(BashTokenKind.DLess)` here was a compile-time
                    // constant-true guard (unlike the dynamic `IsRedirectKind(op.Kind)`
                    // checks elsewhere in this method, which gate over several possible
                    // matched kinds). Reclassify unconditionally; this is the fd-redirect
                    // case (`2<<EOF`) with no whitespace before `<<`.
                    TryReclassifyIoNumber(tokens, pos);
                    tokens.Add(new BashToken(BashTokenKind.DLess, "<<", pos));
                    pos += 2;
                    heredocDelimPending = 1;
                    continue;
                }

                // `&>>` (append both stdout+stderr) before `&>` (redirect both) before
                // the single-char `&` — else `cmd &> f` would lex as background `&` + `> f`.
                if (c == '&' && c2 == '>')
                {
                    if (c3 == '>')
                    {
                        tokens.Add(new BashToken(BashTokenKind.AmpDGreat, "&>>", pos));
                        pos += 3;
                        continue;
                    }
                    tokens.Add(new BashToken(BashTokenKind.AmpGreat, "&>", pos));
                    pos += 2;
                    continue;
                }

                // `>|` (force-clobber): a Great token whose text ">|" tells the parser to mark the
                // redirect Force (it is a plain `>` except under noclobber).
                // `<>` (`exec 5<> f`): open for reading and writing. A Less token whose text is `<>`; the parser marks
                // the redirect ReadWrite.
                if (c == '<' && c2 == '>')
                {
                    TryReclassifyIoNumber(tokens, pos);
                    tokens.Add(new BashToken(BashTokenKind.Less, "<>", pos));
                    pos += 2;
                    continue;
                }
                if (c == '>' && c2 == '|')
                {
                    TryReclassifyIoNumber(tokens, pos);
                    tokens.Add(new BashToken(BashTokenKind.Great, ">|", pos));
                    pos += 2;
                    continue;
                }

                (BashTokenKind Kind, string Text)? two = (c, c2) switch
                {
                    ('&', '&') => (BashTokenKind.AndIf, "&&"),
                    ('|', '|') => (BashTokenKind.OrIf, "||"),
                    ('|', '&') => (BashTokenKind.PipeAmp, "|&"),
                    ('>', '>') => (BashTokenKind.DGreat, ">>"),
                    ('<', '&') => (BashTokenKind.LessAnd, "<&"),
                    ('>', '&') => (BashTokenKind.GreatAnd, ">&"),
                    _ => null,
                };

                if (two is { } op)
                {
                    if (IsRedirectKind(op.Kind))
                        TryReclassifyIoNumber(tokens, pos);

                    tokens.Add(new BashToken(op.Kind, op.Text, pos));
                    pos += 2;
                    continue;
                }
            }

            // Empty braces {} — literal word (find -exec, xargs -I{}, etc.)
            if (c == '{' && pos + 1 < len && input[pos + 1] == '}')
            {
                tokens.Add(new BashToken(BashTokenKind.Word, "{}", pos));
                pos += 2;
                continue;
            }

            // Brace expansion: {a,b,c} or {1..10} — treat as a word, not LBrace.
            if (c == '{' && IsBraceExpansion(input, pos))
            {
                int braceStart = pos;
                pos = ScanWord(input, pos);
                string braceText = input[braceStart..pos];
                tokens.Add(new BashToken(BashTokenKind.Word, braceText, braceStart));
                continue;
            }

            // `{` / `}` glued to more word text (`}_{a,b}`, `{x}_a`, `{1.3}`) are ordinary characters of a word:
            // they are reserved only as a standalone token.
            if ((c == '{' || c == '}') && pos + 1 < len && !IsBlankOrOperatorChar(input[pos + 1])
                && !(c == '{' && TryScanNamedFd(input, pos, out _)))
            {
                int glued = pos;
                pos = ScanWord(input, pos + 1);
                tokens.Add(new BashToken(BashTokenKind.Word, input[glued..pos], glued));
                continue;
            }

            // Named-fd redirect prefix: {name}>file / {name}<file (bash allocates
            // a free fd into $name). Emit {name} as a Word so it is NOT lexed as
            // LBrace — otherwise the parser tries to parse a brace group and throws
            // on the common `exec {fd}>file` idiom. The fd allocation has no
            // PowerShell equivalent; the parser drops the prefix (documented degrade).
            if (c == '{' && TryScanNamedFd(input, pos, out int nfdEnd))
            {
                tokens.Add(new BashToken(BashTokenKind.Word, input[pos..nfdEnd], pos));
                pos = nfdEnd;
                continue;
            }

            // `!(` is the extglob NEGATION in argument / pattern position (`[[ x == !(*.log) ]]`,
            // `case $f in !(*.o))`), but at command position `!(false)` is `!` + a subshell
            // (oracle). Skip the Bang operator there so the word scan takes the extglob whole.
            // Right after `[[`, `!(x)` is a pattern word only once extglob is on: bash parses each line after the one
            // before it ran, so without an earlier `shopt -s extglob` it is the test `! ( x )` ([[ !($empty) ]] is true).
            bool extGlobBang = c == '!' && pos + 1 < len && input[pos + 1] == '('
                && IsArgumentOrPatternPosition(tokens)
                && !(tokens[^1] is { Kind: BashTokenKind.Word, Value: "[[" } && input.AsSpan(0, pos).IndexOf("extglob", StringComparison.Ordinal) < 0);

            // Single-character operators. Interned literal token text avoids the
            // per-token c.ToString() allocation.
            (BashTokenKind Kind, string Text)? one = extGlobBang ? null : c switch
            {
                '|' => (BashTokenKind.Pipe, "|"),
                ';' => (BashTokenKind.Semi, ";"),
                '&' => (BashTokenKind.Amp, "&"),
                '(' => (BashTokenKind.LParen, "("),
                ')' => (BashTokenKind.RParen, ")"),
                '{' => (BashTokenKind.LBrace, "{"),
                '}' => (BashTokenKind.RBrace, "}"),
                '<' => (BashTokenKind.Less, "<"),
                '>' => (BashTokenKind.Great, ">"),
                '!' => (BashTokenKind.Bang, "!"),
                _ => null,
            };

            if (one is { } onef)
            {
                if (IsRedirectKind(onef.Kind))
                    TryReclassifyIoNumber(tokens, pos);

                tokens.Add(new BashToken(onef.Kind, onef.Text, pos));
                pos++;
                continue;
            }

            // Word (including quoted strings as part of a word).
            int wordStart = pos;
            pos = ScanWord(input, pos);
            // `a[5 + 1]=x` at command position: bash reads NAME[ to its matching ] as one word, blanks and all
            // (`a[5 + 3]` alone is then a command name, status 127; an unclosed `a[` is a syntax error).
            while ((pos >= len || input[pos] is ' ' or '\t' or '\n' or '\r') && IsCommandWordPosition(tokens, tokens.Count) && OpenSubscriptEnd(input, wordStart, pos) is var close && close != 0)
            {
                if (close < 0)
                {
                    var (eofLine, eofCol) = ParseException.ComputeLineCol(input, wordStart);
                    throw new ParseException("unexpected EOF while looking for matching `]'", eofLine, eofCol, "subscript");
                }
                pos = close < len && !IsBlankOrOperatorChar(input[close]) ? ScanWord(input, close) : close;
            }
            string value = input[wordStart..pos];

            // Defensive: never emit a zero-length Word. ScanWord can consume a
            // mid-word line continuation and then stop with no characters left
            // (e.g. a trailing `\<newline>`); an empty token would become a stray
            // empty operand/command word downstream. Force progress to avoid a hang.
            if (value.Length == 0)
            {
                pos++;
                continue;
            }

            // A `for` header's word list reports the line of the `for` command (bash), not of each
            // word on a continued line; the list ends at the first `;` or newline.
            if (tokens.Count > 0 && tokens[^1].Kind is BashTokenKind.Newline or BashTokenKind.Semi)
                forLine = 0;
            if (value == "for" && (tokens.Count == 0 || !(tokens[^1].Kind == BashTokenKind.Word && !IsReservedWord(tokens[^1].Value))))
                forLine = LineAt(input, wordStart);
            if (value.Contains("LINENO", StringComparison.Ordinal))
                value = SubstituteLineNumber(value, input, wordStart, forLine > 0 ? forLine : 0);
            BashTokenKind wordKind = ClassifyWord(value);
            tokens.Add(new BashToken(wordKind, value, wordStart));

            // The word immediately following `<<`/`<<-` is the heredoc delimiter.
            // Record it (with the strip-tabs flag from the operator and the index of
            // the delimiter token, which we stamp with the body span at the newline)
            // so the body skip knows which line ends each body.
            if (heredocDelimPending != 0)
            {
                var (delim, expand) = ParseHeredocDelimiter(value);
                pendingHeredocs.Add((delim, heredocDelimPending == 2, expand, tokens.Count - 1));
                heredocDelimPending = 0;
            }
        }

        tokens.Add(new BashToken(BashTokenKind.Eof, "", pos));
        return tokens;
    }

    /// <summary>
    /// Skip past one or more heredoc bodies in raw source, consuming each body up
    /// to and including its delimiter line, and stamp each body's exact source span
    /// onto its delimiter token. <paramref name="pos"/> is the offset of the first
    /// body line (just past the command-line newline); the return value is the
    /// offset where normal tokenizing resumes (the line after the last delimiter,
    /// or end of input if a delimiter never appears — bash's "here-document
    /// delimited by end-of-file"). Matches bash exactly: the delimiter is compared
    /// against whole physical lines with no quote or comment interpretation;
    /// <c>&lt;&lt;-</c> strips leading tabs before the comparison.
    ///
    /// The recorded span is <c>[bodyStart, delimLineStart)</c>: it covers the body
    /// lines (each including its trailing newline) and stops before the delimiter
    /// line, so the delimiter line's leading tabs are never part of the body. This
    /// is the single source of the body text; the parser no longer re-derives the
    /// span from its token cursor, which is what made a same-line
    /// <c>|</c>/<c>&amp;&amp;</c>/<c>;</c> tail land in the body.
    /// </summary>
    private static int SkipHeredocBodies(
        string input,
        int pos,
        List<(string Delim, bool StripTabs, bool Expand, int DelimTokenIndex)> pending,
        List<BashToken> tokens)
    {
        foreach (var (delim, stripTabs, expand, delimTokenIndex) in pending)
        {
            int bodyStart = pos;
            pos = SkipHeredocBody(input, pos, delim, stripTabs, expand, out int bodyEnd);
            tokens[delimTokenIndex] = tokens[delimTokenIndex] with
            {
                BodyStart = bodyStart,
                BodyEnd = bodyEnd,
            };
        }
        return pos;
    }

    /// <summary>
    /// Skip one heredoc body starting at <paramref name="pos"/> (the first body line): whole
    /// lines, no quote or comment interpretation, until a line equal to <paramref name="delim"/>
    /// (leading tabs stripped for <c>&lt;&lt;-</c>) or end of input. With an unquoted delimiter
    /// (<paramref name="joinContinuations"/>) a line is what bash reads as one: a backslash-newline
    /// joins the next physical line onto it (<see cref="JoinHereDocContinuations"/>), so <c>a\</c>
    /// then <c>EOF</c> is the body line <c>aEOF</c>, not the end. Returns the offset just past the
    /// delimiter line; <paramref name="bodyEnd"/> is where the delimiter line starts (the end of
    /// the body text).
    /// </summary>
    private static int SkipHeredocBody(string input, int pos, string delim, bool stripTabs, bool joinContinuations, out int bodyEnd)
    {
        int len = input.Length;
        bodyEnd = pos;
        while (pos < len)
        {
            int lineEnd = joinContinuations ? LogicalHereDocLineEnd(input, pos) : input.IndexOf('\n', pos);
            if (lineEnd < 0) lineEnd = len;
            int textEnd = lineEnd > pos && input[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            ReadOnlySpan<char> line = joinContinuations && lineEnd > pos
                ? JoinHereDocContinuations(input[pos..textEnd]).AsSpan()
                : input.AsSpan(pos, textEnd - pos);
            if (stripTabs) line = line.TrimStart('\t');
            if (line.SequenceEqual(delim))
            {
                bodyEnd = pos;
                return lineEnd >= len ? len : lineEnd + 1;
            }
            bodyEnd = lineEnd >= len ? len : lineEnd + 1;
            pos = bodyEnd;
        }
        return pos;
    }

    /// <summary>
    /// The offset of the newline that ends the here-document line starting at <paramref name="pos"/> when
    /// backslash-newline continues a line (the end of input when none does).
    /// </summary>
    private static int LogicalHereDocLineEnd(string input, int pos)
    {
        for (int i = pos; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\n') return i;
            // `\` takes the next character with it (`\\` is a pair), and a following newline (CRLF too) with it as well:
            // the line goes on past it.
            if (c == '\\' && i + 1 < input.Length)
                i += input[i + 1] == '\r' && i + 2 < input.Length && input[i + 2] == '\n' ? 2 : 1;
        }
        return input.Length;
    }

    /// <summary>
    /// The text of an unquoted-delimiter here-document as bash reads it: every backslash-newline (a line continuation)
    /// removed, a backslash taking the character after it with it, so <c>\\</c> before a newline is a literal backslash
    /// and the newline stays. bash removes them as it reads each line (before the <c>&lt;&lt;-</c> tab strip, the
    /// delimiter match and expansion); <c>\$</c>, <c>\`</c> and <c>\\</c> are left for expansion.
    /// The one rule the lexer's delimiter match and the parser's body share.
    /// </summary>
    public static string JoinHereDocContinuations(string text)
    {
        int at = text.IndexOf('\\');
        if (at < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        sb.Append(text, 0, at);
        for (int i = at; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char n = text[i + 1];
                if (n == '\n') { i++; continue; }
                if (n == '\r' && i + 2 < text.Length && text[i + 2] == '\n') { i += 2; continue; }
                sb.Append(c).Append(n);
                i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
    /// <summary>
    /// Is the next token an argument or a case pattern (not a command start)? True right after a
    /// non-reserved word; or after <c>in</c> (a for-list / the first case pattern) or a case-arm
    /// terminator <c>;;</c> <c>;&amp;</c> <c>;;&amp;</c> (the next pattern), newlines allowed between.
    /// </summary>
    /// <summary>
    /// Replaces <c>$LINENO</c> / <c>${LINENO}</c> in a word with the 1-based source line the word starts on
    /// (bash: the line of the command being run). Single-quoted spans and a backslash-escaped <c>$</c> stay literal.
    /// </summary>
    /// <summary>1-based source line of offset <paramref name="pos"/>.</summary>
    internal static int LineAt(string input, int pos)
    {
        int line = 1;
        int end = Math.Min(pos, input.Length);
        for (int i = 0; i < end; i++)
            if (input[i] == (char)10) line++;
        return line;
    }

    /// <summary>
    /// Replaces <c>LINENO</c> (bare, <c>$LINENO</c>, <c>${LINENO}</c>) in a raw arithmetic expression
    /// with <paramref name="line"/>; bare identifiers like <c>MYLINENO</c> / <c>LINENO2</c> are left alone.
    /// </summary>
    internal static string SubstituteArithLineNumber(string expr, int line)
    {
        if (!expr.Contains("LINENO", StringComparison.Ordinal)) return expr;
        string lineText = line.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder(expr.Length);
        for (int i = 0; i < expr.Length; i++)
        {
            if (string.CompareOrdinal(expr, i, "${LINENO}", 0, 9) == 0) { sb.Append(lineText); i += 8; continue; }
            int s = expr[i] == '$' ? i + 1 : i;
            if (string.CompareOrdinal(expr, s, "LINENO", 0, 6) == 0
                && (s == i ? i == 0 || !(char.IsLetterOrDigit(expr[i - 1]) || expr[i - 1] == '_') : true)
                && (s + 6 >= expr.Length || !(char.IsLetterOrDigit(expr[s + 6]) || expr[s + 6] == '_')))
            { sb.Append(lineText); i = s + 5; continue; }
            sb.Append(expr[i]);
        }
        return sb.ToString();
    }

    private static string SubstituteLineNumber(string word, string input, int wordStart, int line = 0)
    {
        if (line == 0) line = LineAt(input, wordStart);
        string lineText = line.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var sb = new System.Text.StringBuilder(word.Length);
        bool inSingle = false;
        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            if (c == (char)39) { inSingle = !inSingle; sb.Append(c); continue; }
            if (!inSingle && c == (char)92 && i + 1 < word.Length) { sb.Append(c).Append(word[++i]); continue; }
            if (!inSingle && c == (char)36)
            {
                if (string.CompareOrdinal(word, i, "$LINENO", 0, 7) == 0
                    && (i + 7 >= word.Length || !(char.IsLetterOrDigit(word[i + 7]) || word[i + 7] == (char)95)))
                { sb.Append(lineText); i += 6; continue; }
                if (string.CompareOrdinal(word, i, "${LINENO}", 0, 9) == 0)
                { sb.Append(lineText); i += 8; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsArgumentOrPatternPosition(List<BashToken> tokens)
    {
        int i = tokens.Count - 1;
        if (i < 0) return false;
        if (tokens[i].Kind == BashTokenKind.Word && !IsReservedWord(tokens[i].Value)) return true;
        while (i >= 0 && tokens[i].Kind == BashTokenKind.Newline) i--;
        if (i < 0) return false;
        if (tokens[i].Kind == BashTokenKind.Word) return tokens[i].Value == "in";
        // ;; / ;& / ;;& — the lexer emits these as Semi Semi / Semi Amp / Semi Semi Amp.
        bool amp = tokens[i].Kind == BashTokenKind.Amp;
        int j = amp ? i - 1 : i;
        if (j < 0 || tokens[j].Kind != BashTokenKind.Semi) return false;
        return amp || (j >= 1 && tokens[j - 1].Kind == BashTokenKind.Semi);
    }

    /// <summary>
    /// Advance past an extglob pattern list (<c>@( … )</c>, <c>+( … )</c>, …). <paramref name="pos"/>
    /// is just inside the opening <c>(</c>; returns one past the matching <c>)</c> (or end of input).
    /// Quotes and backslash escapes are taken whole so <c>@('a)'|b)</c> and <c>@(x\(y)</c> close at
    /// the right paren. Unlike <see cref="ScanBalancedParens"/> there is no <c>case</c>/heredoc
    /// handling: the list holds patterns, not commands. Shared by the lexer and the word parser.
    /// </summary>
    internal static int ScanExtGlob(string input, int pos)
    {
        int len = input.Length;
        int depth = 1;
        while (pos < len)
        {
            char c = input[pos];
            if (c == '\'') { pos = ScanSingleQuoted(input, pos); continue; }
            if (c == '"') { pos = ScanDoubleQuoted(input, pos); continue; }
            if (c == '\\' && pos + 1 < len) { pos += 2; continue; }
            pos++;
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) break;
        }
        return pos;
    }

    /// <summary>
    /// Read the delimiter word after a <c>&lt;&lt;</c>/<c>&lt;&lt;-</c> inside a raw-scanned
    /// region (<see cref="ScanBalancedParens"/>). <paramref name="pos"/> is just past the
    /// operator; blanks are skipped, then the word runs to whitespace or a metachar, with
    /// quoted segments taken whole (<c>'E O F'</c>). Returns the offset past the word, or
    /// <paramref name="pos"/> unchanged (and an empty <paramref name="rawDelim"/>) when none.
    /// </summary>
    private static int ReadRawHeredocDelimiter(string input, int pos, out string rawDelim)
    {
        int len = input.Length;
        while (pos < len && input[pos] is ' ' or '\t') pos++;
        int start = pos;
        while (pos < len)
        {
            char c = input[pos];
            if (c == '\'') { pos = ScanSingleQuoted(input, pos); continue; }
            if (c == '"') { pos = ScanDoubleQuoted(input, pos); continue; }
            if (c == '\\' && pos + 1 < len) { pos += 2; continue; }
            if (char.IsWhiteSpace(c) || c is ';' or '&' or '|' or '<' or '>' or '(' or ')') break;
            pos++;
        }
        rawDelim = input[start..pos];
        return pos;
    }

    /// <summary>
    /// Extract a heredoc's terminator string and whether its body expands, from the
    /// raw delimiter word. Quoting (<c>'EOF'</c> / <c>"EOF"</c>) or a backslash
    /// anywhere (<c>\EOF</c>) suppresses expansion and is stripped for the
    /// terminator-line match; otherwise the body expands. Shared by the lexer
    /// (body skipping) and the parser (body capture) so the two never disagree on
    /// where a body ends.
    /// </summary>
    public static (string Delimiter, bool Expand) ParseHeredocDelimiter(string rawDelim)
    {
        if (rawDelim.IndexOfAny(DelimiterQuoteChars) < 0)
            return (rawDelim, true);
        // bash quote-removes the whole word: quoted pieces may be concatenated (`'EOF'"2"` ends at EOF2).
        var sb = new System.Text.StringBuilder(rawDelim.Length);
        int i = 0;
        while (i < rawDelim.Length)
        {
            char c = rawDelim[i];
            if (c == '\'')
            {
                int close = rawDelim.IndexOf('\'', i + 1);
                if (close < 0) close = rawDelim.Length;
                sb.Append(rawDelim, i + 1, close - i - 1);
                i = close + 1;
            }
            else if (c == '"')
            {
                i++;
                while (i < rawDelim.Length && rawDelim[i] != '"')
                {
                    // Inside double quotes a backslash escapes only $ ` " \ (and a newline).
                    if (rawDelim[i] == '\\' && i + 1 < rawDelim.Length && rawDelim[i + 1] is '$' or '`' or '"' or '\\' or '\n')
                        i++;
                    sb.Append(rawDelim[i]);
                    i++;
                }
                i++;
            }
            else if (c == '\\' && i + 1 < rawDelim.Length)
            {
                sb.Append(rawDelim[i + 1]);
                i += 2;
            }
            else if (c == '\\')
            {
                i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return (sb.ToString(), false);
    }

    private static readonly char[] DelimiterQuoteChars = { '\'', '"', '\\' };

    /// <summary>
    /// Returns true if the given word is a bash reserved word.
    /// </summary>
    public static bool IsReservedWord(string word) => ReservedWords.Contains(word);

    /// <summary>
    /// Returns true if position <paramref name="pos"/> starts a brace expansion pattern:
    /// <c>{</c> followed by content containing <c>,</c> or <c>..</c>, ending with <c>}</c>,
    /// with no unquoted whitespace inside.
    /// </summary>
    /// <summary>
    /// Detect a named-fd redirect prefix <c>{varname}</c> immediately followed
    /// (no gap) by a redirect operator (<c>&lt;</c> or <c>&gt;</c>), e.g.
    /// <c>{fd}&gt;file</c>. On success <paramref name="end"/> is the position of
    /// the redirect operator (so the caller emits <c>{varname}</c> as one Word).
    /// </summary>
    private static bool TryScanNamedFd(string input, int pos, out int end)
    {
        end = pos;
        int len = input.Length;
        int p = pos + 1; // after '{'
        if (p >= len || !(char.IsLetter(input[p]) || input[p] == '_'))
            return false;
        while (p < len && (char.IsLetterOrDigit(input[p]) || input[p] == '_'))
            p++;
        if (p >= len || input[p] != '}')
            return false;
        p++; // after '}'
        if (p >= len || (input[p] != '>' && input[p] != '<'))
            return false;
        end = p; // Word spans {name}; redirect operator starts here.
        return true;
    }

    /// <summary>A CR that ends a line: CRLF (Windows scripts) or the last character of the input.</summary>
    private static bool IsLineEndCr(string input, int pos) => pos + 1 >= input.Length || input[pos + 1] == '\n';

    private static bool IsBlankOrOperatorChar(char c)
        => c is ' ' or '\t' or '\n' or '\r' or '|' or '&' or ';' or '(' or ')' or '<' or '>';

    private static bool IsBraceExpansion(string input, int pos)
    {
        int len = input.Length;
        if (pos >= len || input[pos] != '{')
            return false;

        int i = pos + 1;
        bool hasComma = false;
        bool hasDotDot = false;

        while (i < len)
        {
            char c = input[i];
            if (c is ' ' or '\t' or '\n' or '\r')
                return false;
            if (c == '}')
                return hasComma || hasDotDot;
            if (c == ',')
                hasComma = true;
            if (c == '.' && i + 1 < len && input[i + 1] == '.')
                hasDotDot = true;
            i++;
        }
        return false;
    }

    /// <summary>
    /// `a[1 + 2]=x`: at the start of a word, a subscript that holds blanks (or `| &amp; &lt; &gt; ( )`) belongs to the assignment word when the matching `]` is
    /// followed by `=` or `+=`. Returns the offset just past that `]` (the scan then carries on through the value), else
    /// <paramref name="pos"/> unchanged. Subscripts without blanks already scan as one word and are left alone.
    /// </summary>
    private static int SkipSpacedSubscript(string input, int pos)
    {
        int len = input.Length, i = pos;
        if (i >= len || !IsNameStart(input[i])) return pos;
        while (i < len && IsNameChar(input[i])) i++;
        if (i >= len || input[i] != '[') return pos;
        int depth = 0, j = i;
        bool blank = false;
        for (; j < len; j++)
        {
            char c = input[j];
            if (c == '\\' && j + 1 < len) { j++; continue; }
            if (c == '\'') { j = ScanSingleQuoted(input, j) - 1; continue; }
            if (c == '"') { j = ScanDoubleQuoted(input, j) - 1; continue; }
            if (c == '$' && j + 1 < len && input[j + 1] == '(') { j = ScanDollarParen(input, j) - 1; continue; }
            if (c == '$' && j + 1 < len && input[j + 1] == '{') { j = ScanDollarBrace(input, j) - 1; continue; }
            if (c == '[') depth++;
            else if (c == ']') { if (--depth == 0) break; }
            // An arithmetic operator that is also a shell metacharacter (`a[3|4]=8`, `a[(1+2)*3]=9`) splits the word just as a blank does.
            else if (c is ' ' or '\t' or '|' or '&' or '<' or '>' or '(' or ')') blank = true;
            else if (c is '\n' or ';') return pos;
            else if (c == '#' && input[j - 1] is ' ' or '\t') return pos;
        }
        if (!blank || j >= len) return pos;
        int k = j + 1;
        if (k < len && input[k] == '+') k++;
        return k < len && input[k] == '=' ? j + 1 : pos;
    }
    private static int ScanWord(string input, int pos)
    {
        int len = input.Length;
        pos = SkipSpacedSubscript(input, pos);

        while (pos < len)
        {
            char c = input[pos];

            // Brace expansion: {items,here} or {1..10}. Its items are ordinary word text, scanned by the rules below:
            // a quoted item (`{a,'{}','&&'}`) is taken whole, so its `}` / `&` neither closes the braces nor ends the word.
            if (c == '{' && IsBraceExpansion(input, pos))
            {
                pos++; // skip {
                continue;
            }

            // Extglob: +(...) *(...) ?(...) !(...) @(...) — consume through matching ')'.
            if ((c is '+' or '*' or '?' or '!' or '@') && pos + 1 < len && input[pos + 1] == '(')
            {
                pos = ScanExtGlob(input, pos + 2);
                continue;
            }

            // Unquoted metacharacters end the word. NOTE: '#' is deliberately
            // NOT here. Bash starts a comment only when '#' BEGINS a word; a
            // '#' mid-word is literal ("abc#def", "http://x#frag"). The main
            // tokenize loop only reaches a '#' at a word boundary (after
            // whitespace / operator / newline), so the comment rule there fires
            // exactly when bash's does — without this scan swallowing it.
            if (c is ' ' or '\t' or '\n' or '|' or '&' or ';'
                or '(' or ')' or '<' or '>'
                || (c == '\r' && IsLineEndCr(input, pos)))
                break;

            // Backslash escape: consume the backslash and the next character.
            // Line continuation: backslash followed by end-of-line skips both.
            if (c == '\\' && pos + 1 < len)
            {
                if (input[pos + 1] == '\n')
                {
                    pos += 2;
                    continue;
                }
                if (input[pos + 1] == '\r' && pos + 2 < len && input[pos + 2] == '\n')
                {
                    pos += 3;
                    continue;
                }
                pos += 2;
                continue;
            }

            // Single-quoted string: consume through closing quote. A quote the input never closes is a syntax error.
            if (c == '\'')
            {
                int open = pos;
                pos = ScanSingleQuoted(input, pos, out bool sqClosed);
                if (!sqClosed) throw UnterminatedQuote(input, open, '\'');
                continue;
            }

            // Double-quoted string. Quote-aware: recurses through $(...), ${...}
            // and `...` so an embedded '"' inside a command substitution does
            // not prematurely terminate the string.
            if (c == '"')
            {
                int open = pos;
                pos = ScanDoubleQuoted(input, pos, out bool dqClosed);
                if (!dqClosed) throw UnterminatedQuote(input, open, '"');
                continue;
            }

            // Parameter expansion ${...}: consume through matching closing brace.
            if (c == '$' && pos + 1 < len && input[pos + 1] == '{')
            {
                pos = ScanDollarBrace(input, pos);
                continue;
            }

            // Arithmetic expansion $((...)) or command substitution $(...).
            if (c == '$' && pos + 1 < len && input[pos + 1] == '(')
            {
                pos = ScanDollarParen(input, pos);
                continue;
            }

            // Backtick command substitution `...`.
            if (c == '`')
            {
                pos = ScanBacktick(input, pos);
                continue;
            }

            // ANSI-C quoting $'...': consume through the closing quote, respecting
            // backslash escapes (so $'it\'s' does not terminate at the escaped quote).
            // Must be handled here so the whole $'...' stays in one word token and a
            // \' inside is not mistaken for the terminator by the bare single-quote scan.
            if (c == '$' && pos + 1 < len && input[pos + 1] == '\'')
            {
                int open = pos + 1;
                pos = ScanAnsiCQuoted(input, pos, out bool ansiClosed);
                if (!ansiClosed) throw UnterminatedQuote(input, open, '\'');
                continue;
            }

            // Special variable: $# $? $! $$ $_ $@ $* $- $0-$9 must not break on the second char.
            if (c == '$' && pos + 1 < len
                && input[pos + 1] is '#' or '?' or '!' or '$' or '_' or '@' or '*' or '-'
                    or (>= '0' and <= '9'))
            {
                pos += 2;
                continue;
            }

            pos++;
        }

        return pos;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Quote- and nesting-aware region scanners.
    //
    // Bash word scanning must treat quotes and command substitutions as ATOMIC,
    // composing regions: a ')' inside a string must not close a $( ), and a '"'
    // inside a $(...) inside a "..." must not close the outer string. These
    // helpers are mutually recursive so any construct can nest inside any other.
    // Each takes the source and the offset of the construct's opening delimiter
    // and returns the offset one past its closing delimiter.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Advance past a single-quoted string. No escapes inside (bash).</summary>
    internal static int ScanSingleQuoted(string input, int pos) => ScanSingleQuoted(input, pos, out _);

    /// <summary>Advance past a single-quoted string; <paramref name="closed"/> is false when the input ends first.</summary>
    internal static int ScanSingleQuoted(string input, int pos, out bool closed)
    {
        int len = input.Length;
        pos++; // opening '
        while (pos < len && input[pos] != '\'')
            pos++;
        closed = pos < len;
        if (closed)
            pos++; // closing '
        return pos;
    }

    /// <summary>
    /// Advance past a double-quoted string, recursing through embedded $(...),
    /// ${...} and `...` (whose own quotes/parens must not end the string).
    /// </summary>
    internal static int ScanDoubleQuoted(string input, int pos) => ScanDoubleQuoted(input, pos, out _);

    /// <summary>As <see cref="ScanDoubleQuoted(string, int)"/>; <paramref name="closed"/> is false when the input ends
    /// before the closing quote, also when an embedded construct ran to the end (<c>"${v%'}"</c>: the <c>'</c> opens a quote).</summary>
    internal static int ScanDoubleQuoted(string input, int pos, out bool closed)
    {
        int len = input.Length;
        pos++; // opening "
        while (pos < len && input[pos] != '"')
        {
            char c = input[pos];
            if (c == '\\' && pos + 1 < len) { pos += 2; continue; }
            if (c == '$' && pos + 1 < len && input[pos + 1] == '(') { pos = ScanDollarParen(input, pos); continue; }
            if (c == '$' && pos + 1 < len && input[pos + 1] == '{') { pos = ScanDollarBrace(input, pos); continue; }
            if (c == '`') { pos = ScanBacktick(input, pos); continue; }
            pos++;
        }
        closed = pos < len;
        if (closed)
            pos++; // closing "
        return pos;
    }

    /// <summary>bash's syntax error for a quote the input never closes (status 2).</summary>
    /// <summary>
    /// For a word <c>input[start..end)</c> that is <c>NAME[</c> plus a subscript still open at <paramref name="end"/>
    /// (<c>a[5</c> of <c>a[5 + 1]=</c>): the index just past the <c>]</c> that closes it (quotes and escapes taken whole,
    /// nested brackets counted), or -1 when the input ends first. 0 when the word is no such open subscript.
    /// </summary>
    internal static int OpenSubscriptEnd(string input, int start, int end)
    {
        int i = start;
        if (i >= end || !(char.IsAsciiLetter(input[i]) || input[i] == '_')) return 0;
        while (i < end && (char.IsAsciiLetterOrDigit(input[i]) || input[i] == '_')) i++;
        if (i >= end || input[i] != '[') return 0;
        int depth = 0;
        for (int j = i; j < input.Length; j++)
        {
            char c = input[j];
            if (j >= end && depth == 0) return 0;
            switch (c)
            {
                case '\\': j++; break;
                case '\'' or '"':
                    int q = input.IndexOf(c, j + 1);
                    if (q < 0) return -1;
                    j = q;
                    break;
                case '[': depth++; break;
                case ']':
                    if (--depth == 0) return j < end ? 0 : j + 1;
                    break;
            }
        }
        return -1;
    }

    /// <summary>The next word starts a simple command (where an assignment may stand): at the start, after a
    /// command separator or opening keyword, or after an earlier assignment word.</summary>
    private static bool IsCommandWordPosition(List<BashToken> tokens, int count)
    {
        if (count == 0) return true;
        var last = tokens[count - 1];
        if (last.Kind is BashTokenKind.Newline or BashTokenKind.AndIf or BashTokenKind.OrIf or BashTokenKind.Pipe
            or BashTokenKind.PipeAmp or BashTokenKind.LParen or BashTokenKind.LBrace or BashTokenKind.Bang)
            return true;
        if (last.Kind is BashTokenKind.Semi or BashTokenKind.Amp)
            return !(count > 1 && tokens[count - 2].Kind == BashTokenKind.Semi); // `;;` / `;&`: a case pattern follows
        if (last.Kind != BashTokenKind.Word) return false;
        if (last.Value is "then" or "do" or "else" or "elif" or "if" or "while" or "until") return true;
        int eq = last.Value.IndexOf('=');
        return eq > 0 && OpenSubscriptEnd(last.Value, 0, last.Value.Length) == 0
            && (char.IsAsciiLetter(last.Value[0]) || last.Value[0] == '_') && IsCommandWordPosition(tokens, count - 1);
    }

    private static ParseException UnterminatedQuote(string input, int open, char quote)
    {
        var (line, col) = ParseException.ComputeLineCol(input, open);
        return new ParseException("unexpected EOF while looking for matching `" + quote + "'", line, col, "quote");
    }

    /// <summary>Advance past a `...` backtick command substitution (no nesting; escapes honored).</summary>
    internal static int ScanBacktick(string input, int pos) => ScanBacktick(input, pos, out _);

    /// <summary>As <see cref="ScanBacktick(string, int)"/>; <paramref name="closed"/> is false when the input ends first.</summary>
    internal static int ScanBacktick(string input, int pos, out bool closed)
    {
        int len = input.Length;
        pos++; // opening `
        while (pos < len && input[pos] != '`')
        {
            if (input[pos] == '\\' && pos + 1 < len)
                pos++; // skip escaped char
            pos++;
        }
        closed = pos < len;
        if (closed)
            pos++; // closing `
        return Math.Min(pos, len);
    }

    /// <summary>Advance past a ${...} parameter expansion (one past its closing <c>}</c>, or the input's end when it
    /// never closes). input[pos]=='$', [pos+1]=='{'. The extent is <see cref="DollarBraceClose"/>'s.</summary>
    internal static int ScanDollarBrace(string input, int pos)
    {
        int close = DollarBraceClose(input, pos);
        return close < 0 ? input.Length : close + 1;
    }

    /// <summary>
    /// THE extent of a <c>${…}</c> parameter expansion: the index of the <c>}</c> closing the <c>${</c> whose <c>$</c>
    /// is at <paramref name="dollar"/>, or -1 when the input ends first. Every reader of a <c>${…}</c> extent asks this
    /// (lexer words, the word parser, assignment-value and subscript splitting, brace items, heredoc bodies,
    /// arithmetic); guard <c>DollarBraceExtentGuardTests</c>, spec <c>parser-grammar.md</c> §Parameter expansion extent.
    /// <para>
    /// bash (parse_matched_pair with P_FIRSTCLOSE): the FIRST unquoted, unescaped <c>}</c> closes it. A bare <c>{</c> in
    /// the operand does not nest (<c>${u:-{a,b}}</c> is <c>${u:-{a,b}</c> then a literal <c>}</c>; <c>${x/ /{1,2\}}</c>
    /// closes at its last <c>}</c>); only a nested <c>${…}</c> does. Quoted text, <c>\x</c>, <c>$'…'</c>, <c>$( )</c>,
    /// <c>$(( ))</c> and backticks are taken whole, so their <c>}</c> is text.
    /// </para>
    /// <para>REGRESSION: the lexer and the word parser each counted every <c>{</c> as nesting, so <c>${x/ /{1,2\}}</c>
    /// never closed and swallowed the rest of the script as one word.</para>
    /// </summary>
    internal static int DollarBraceClose(string input, int dollar)
    {
        // ${x:-${y:-…}} recurses: bounded (NestingGuard).
        using var nesting = NestingGuard.Enter();
        int len = input.Length;
        int pos = dollar + 2; // skip ${
        while (pos < len)
        {
            char c = input[pos];
            switch (c)
            {
                case '}':
                    return pos;
                case '\\':
                    pos += 2;
                    continue;
                case '\'':
                    pos = ScanSingleQuoted(input, pos);
                    continue;
                case '"':
                    pos = ScanDoubleQuoted(input, pos);
                    continue;
                case '`':
                    pos = ScanBacktick(input, pos);
                    continue;
                case '$' when pos + 1 < len && input[pos + 1] == '{':
                    int inner = DollarBraceClose(input, pos);
                    if (inner < 0) return -1;
                    pos = inner + 1;
                    continue;
                case '$' when pos + 1 < len && input[pos + 1] == '(':
                    pos = ScanDollarParen(input, pos);
                    continue;
                case '$' when pos + 1 < len && input[pos + 1] == '\'':
                    pos = ScanAnsiCQuoted(input, pos, out _);
                    continue;
            }
            pos++;
        }
        return -1;
    }

    /// <summary>Advance past an ANSI-C quoted <c>$'…'</c> (input[pos]=='$', [pos+1]=='\''): one past the first
    /// unescaped <c>'</c> (<c>$'it\'s'</c> is one string), or the input's end with <paramref name="closed"/> false.</summary>
    internal static int ScanAnsiCQuoted(string input, int pos, out bool closed)
    {
        int len = input.Length;
        pos += 2; // skip $'
        while (pos < len && input[pos] != '\'')
        {
            if (input[pos] == '\\' && pos + 1 < len)
                pos++; // skip the escaped character
            pos++;
        }
        closed = pos < len;
        return closed ? pos + 1 : len;
    }

    /// <summary>
    /// Advance past $((...)) arithmetic or $(...) command substitution.
    /// input[pos]=='$', input[pos+1]=='('.
    /// </summary>
    internal static int ScanDollarParen(string input, int pos)
    {
        int len = input.Length;
        if (pos + 2 < len && input[pos + 2] == '(' && IsArithSub(input, pos))
            return ScanArith(input, pos);
        return ScanBalancedParens(input, pos + 2, 1); // skip $(
    }

    /// <summary>True where a `((` opens an arithmetic command or a C-style `for` header (command position, or after `for`/`do`/`then`…).</summary>
    private static bool AtArithHeaderPosition(List<BashToken> tokens)
    {
        if (tokens.Count == 0) return true;
        var last = tokens[^1];
        return last.Kind is BashTokenKind.Newline or BashTokenKind.Semi or BashTokenKind.Amp or BashTokenKind.Pipe
                or BashTokenKind.AndIf or BashTokenKind.OrIf or BashTokenKind.LBrace
            || (last.Kind == BashTokenKind.Word
                && last.Value is "for" or "do" or "then" or "else" or "elif" or "if" or "while" or "until");
    }

    /// <summary>Advance past $(( ... )). input[pos]=='$', [pos+1]=='(', [pos+2]=='('.</summary>
    internal static int ScanArith(string input, int pos) => ScanArithBody(input, pos + 3);

    /// <summary>True when the <c>$((</c> at <paramref name="pos"/> is an arithmetic expansion. Like bash
    /// (parse_arith_cmd), the inner <c>(</c> is matched to its <c>)</c> and the next character must be <c>)</c>;
    /// otherwise (<c>$(( echo 1 ) )</c>, <c>$((which x || y)2&gt;/dev/null)</c>) it is a command substitution
    /// whose body starts with a subshell.</summary>
    internal static bool IsArithSub(string input, int pos) => ArithBodyClosesDoubled(input, pos + 3);

    /// <summary><paramref name="pos"/> is just inside a <c>((</c>: true unless its first unmatched <c>)</c> is
    /// followed by something other than <c>)</c>. An unterminated body keeps the arithmetic reading.</summary>
    private static bool ArithBodyClosesDoubled(string input, int pos)
    {
        int len = input.Length;
        int open = 0;
        for (; pos < len; pos++)
        {
            char c = input[pos];
            // Quoted text and escapes hold no parens (`$(( $(grep -c "a)b" f) + 1 ))` is arithmetic).
            if (c == '\\') { pos++; continue; }
            if (c == '\'') { pos = ScanSingleQuoted(input, pos) - 1; continue; }
            if (c == '"') { pos = ScanDoubleQuoted(input, pos) - 1; continue; }
            if (c == '(') open++;
            else if (c == ')')
            {
                if (open > 0) open--;
                else return pos + 1 >= len || input[pos + 1] == ')';
            }
        }
        return true;
    }

    /// <summary>Advance past an arithmetic body to one past its closing <c>))</c>.
    /// <paramref name="pos"/> is just inside the opening <c>((</c>.</summary>
    private static int ScanArithBody(string input, int pos)
    {
        int len = input.Length;
        // Single-paren depth: the body ends at the first `))` met with no group still open, so
        // `$((!(1 || 2)))` closes on its last two `)`, not on the pair inside the group.
        int open = 0;
        while (pos < len)
        {
            char c = input[pos];
            if (c == '(') open++;
            else if (c == ')')
            {
                if (open > 0) open--;
                else if (pos + 1 < len && input[pos + 1] == ')') return pos + 2;
            }
            pos++;
        }
        return pos;
    }

    /// <summary>
    /// Scan a parenthesised region with paren depth starting at <paramref name="depth"/>,
    /// treating quotes and nested $(...) atomically. <paramref name="pos"/> is the offset
    /// just inside the opening '('. Returns the offset one past the matching ')'.
    /// Shared by command substitution $(...) and process substitution &lt;( )/&gt;( ).
    /// </summary>
    internal static int ScanBalancedParens(string input, int pos, int depth)
    {
        // Recursion: $( → here → nested $( / "…$(…)…" → here. Bounded (NestingGuard) so input can
        // never overflow the stack.
        using var nesting = NestingGuard.Enter();
        int len = input.Length;
        // Case-arm pattern terminators introduce UNMATCHED ')' (e.g. `case $y in a) cmd;; esac`).
        // A naive paren count closes the region early at the first pattern ')'. Track the paren
        // depth at which each `case` opens: a ')' at exactly that depth is a pattern terminator
        // (skip it), a ')' deeper is a real subshell close, and `esac` pops the case. The
        // command-sub/process-sub closer is the ')' that brings depth to 0 with no open case.
        var caseDepths = new List<int>();
        // Heredocs inside the region (`$(cat <<'EOF' … EOF\n)`, the commit-message idiom): the
        // body is raw text, so an apostrophe (`it's`) or `)` in it must not be read as a quote or
        // the closer. Delimiters queue at `<<`; at the next newline their bodies are skipped
        // whole, exactly as the top-level lexer does (SkipHeredocBody).
        List<(string Delim, bool StripTabs, bool Expand)>? heredocs = null;
        while (pos < len && depth > 0)
        {
            char c = input[pos];
            if (c == '\n' && heredocs is { Count: > 0 })
            {
                pos++;
                foreach (var (delim, stripTabs, expand) in heredocs)
                    pos = SkipHeredocBody(input, pos, delim, stripTabs, expand, out _);
                heredocs.Clear();
                continue;
            }
            if (c == '<' && pos + 1 < len && input[pos + 1] == '<')
            {
                if (pos + 2 < len && input[pos + 2] == '<') { pos += 3; continue; } // here-string
                bool stripTabs = pos + 2 < len && input[pos + 2] == '-';
                int after = ReadRawHeredocDelimiter(input, pos + (stripTabs ? 3 : 2), out var rawDelim);
                if (rawDelim.Length > 0)
                {
                    var (heredocDelim, heredocExpand) = ParseHeredocDelimiter(rawDelim);
                    (heredocs ??= new()).Add((heredocDelim, stripTabs, heredocExpand));
                }
                pos = after;
                continue;
            }
            // `\)` / `\(` / `\'` are escaped literals, not a closer, opener, or quote:
            // `echo "$(echo \))"` prints `)` in bash.
            if (c == '\\' && pos + 1 < len) { pos += 2; continue; }
            // `(( … ))` is arithmetic: its `<<` is a shift, never a heredoc.
            if (c == '(' && pos + 1 < len && input[pos + 1] == '(' && ArithBodyClosesDoubled(input, pos + 2)) { pos = ScanArithBody(input, pos + 2); continue; }
            if (c == '\'') { pos = ScanSingleQuoted(input, pos); continue; }
            if (c == '"') { pos = ScanDoubleQuoted(input, pos); continue; }
            if (c == '`') { pos = ScanBacktick(input, pos); continue; }
            if (c == '$' && pos + 1 < len && input[pos + 1] == '(') { pos = ScanDollarParen(input, pos); continue; }
            // A `${…}` is one unit: the `)` of `$(echo ${u:-)})` is operand text (DollarBraceClose).
            if (c == '$' && pos + 1 < len && input[pos + 1] == '{') { pos = ScanDollarBrace(input, pos); continue; }
            if (c == 'c' && IsWordAt(input, pos, "case")) { caseDepths.Add(depth); pos += 4; continue; }
            if (c == 'e' && IsWordAt(input, pos, "esac")) { if (caseDepths.Count > 0) caseDepths.RemoveAt(caseDepths.Count - 1); pos += 4; continue; }
            if (c == '(') { depth++; pos++; continue; }
            if (c == ')')
            {
                // A ')' at the depth where the innermost open `case` lives is that case's
                // pattern terminator, not a paren close — skip it without changing depth.
                if (caseDepths.Count > 0 && depth == caseDepths[^1])
                {
                    pos++;
                    continue;
                }
                depth--;
                pos++;
                continue;
            }
            pos++;
        }
        return pos;
    }

    /// <summary>
    /// True when the reserved word <paramref name="kw"/> appears as a whole word at
    /// <paramref name="pos"/> — i.e. bounded by start/end of input or a shell metachar /
    /// whitespace on each side. Prevents matching `case` inside `lowercase`/`database`.
    /// </summary>
    private static bool IsWordAt(string s, int pos, string kw)
    {
        if (pos + kw.Length > s.Length)
            return false;
        if (string.CompareOrdinal(s, pos, kw, 0, kw.Length) != 0)
            return false;
        if (pos > 0)
        {
            char b = s[pos - 1];
            if (!(char.IsWhiteSpace(b) || b is ';' or '(' or ')' or '&' or '|'))
                return false;
        }
        int after = pos + kw.Length;
        if (after < s.Length)
        {
            char a = s[after];
            if (!(char.IsWhiteSpace(a) || a is ';' or ')' or '&' or '|'))
                return false;
        }
        return true;
    }

    private static BashTokenKind ClassifyWord(string value)
    {
        // ASSIGNMENT_WORD: starts with NAME (letter/underscore, then alnum/underscore), then '='.
        // Also handles NAME[subscript]= for array element assignment (e.g. map[key]=val).
        // The '=' must not be inside quotes.
        if (value.Length >= 2 && IsNameStart(value[0]))
        {
            int i = 1;
            while (i < value.Length && IsNameChar(value[i]))
                i++;

            // Allow optional [subscript] after the name.
            // The subscript nests brackets and may hold quoted text (`m["${a[@]}"]=v`, `a[a[0]=1]=X`): a `]` inside
            // quotes or a nested `[` does not close it.
            if (i < value.Length && value[i] == '[')
            {
                int depth = 0;
                while (i < value.Length)
                {
                    char c = value[i];
                    if (c == '\\') { i += 2; continue; }
                    if (c is '\'' or '"')
                    {
                        int j = i + 1;
                        while (j < value.Length && value[j] != c) j += c == '"' && value[j] == '\\' ? 2 : 1;
                        i = j + 1;
                        continue;
                    }
                    if (c == '[') depth++;
                    else if (c == ']' && --depth == 0) { i++; break; }
                    i++;
                }
            }

            // Support += as well as plain =
            if (i < value.Length && value[i] == '+' && i + 1 < value.Length && value[i + 1] == '=')
                return BashTokenKind.AssignmentWord;
            if (i < value.Length && value[i] == '=')
                return BashTokenKind.AssignmentWord;
        }

        return BashTokenKind.Word;
    }

    private static bool IsNameStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    private static bool IsNameChar(char c) => IsNameStart(c) || c is (>= '0' and <= '9');

    /// <summary>
    /// If the last token is a Word consisting entirely of digits and we're about to
    /// emit a redirect operator, reclassify it as IoNumber.
    /// </summary>
    /// <summary>
    /// Reclassify the last token as an IoNumber if it is a digit-only word
    /// immediately adjacent to the redirect operator (no whitespace between).
    /// <paramref name="redirectPos"/> is the position of the redirect operator.
    /// </summary>
    private static void TryReclassifyIoNumber(List<BashToken> tokens, int redirectPos)
    {
        if (tokens.Count == 0)
            return;

        var last = tokens[^1];
        if (last.Kind != BashTokenKind.Word)
            return;

        if (!IsAllDigits(last.Value))
            return;

        // Only reclassify if the digit word is immediately adjacent to the redirect
        // operator (no whitespace). In bash, "2>" is fd redirect but "2 >" is arg + redirect.
        if (last.Position + last.Value.Length != redirectPos)
            return;

        tokens[^1] = last with { Kind = BashTokenKind.IoNumber };
    }

    /// <summary>The operators a digit word right before them is the descriptor of: the one table, <see cref="RedirectOperators"/>.</summary>
    private static bool IsRedirectKind(BashTokenKind kind) => RedirectOperators.TakesFdPrefix(kind);

    private static bool IsAllDigits(string s)
    {
        foreach (char c in s)
        {
            if (c is not (>= '0' and <= '9'))
                return false;
        }
        return s.Length > 0;
    }
}
