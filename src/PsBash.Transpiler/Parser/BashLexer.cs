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
        // into one token. So we record each `<<`/`<<-` delimiter as it is seen and,
        // at the newline that ends the command line, skip past the bodies in raw
        // source — the parser re-reads the same raw region to build the body text.
        var pendingHeredocs = new List<(string Delim, bool StripTabs)>();
        // The next word token is a heredoc delimiter: 0 = none, 1 = `<<`, 2 = `<<-`.
        int heredocDelimPending = 0;

        while (pos < len)
        {
            char c = input[pos];

            // Skip spaces and tabs (not newlines).
            if (c is ' ' or '\t')
            {
                pos++;
                continue;
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
                    pos = SkipHeredocBodies(input, pos, pendingHeredocs);
                    pendingHeredocs.Clear();
                }
                continue;
            }

            // Carriage return (normalize \r\n to single newline token).
            if (c == '\r')
            {
                int start = pos;
                pos++;
                if (pos < len && input[pos] == '\n')
                    pos++;
                tokens.Add(new BashToken(BashTokenKind.Newline, "\n", start));
                heredocDelimPending = 0;
                if (pendingHeredocs.Count > 0)
                {
                    pos = SkipHeredocBodies(input, pos, pendingHeredocs);
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
                if (c == '<' && c2 == '<')
                {
                    if (c3 == '<')
                    {
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
                    if (IsRedirectKind(BashTokenKind.DLess))
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

                // `>|` (force-clobber): ps-bash ignores noclobber, so emit a plain Great
                // with op text ">" so downstream emitters match.
                if (c == '>' && c2 == '|')
                {
                    TryReclassifyIoNumber(tokens, pos);
                    tokens.Add(new BashToken(BashTokenKind.Great, ">", pos));
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

            // Single-character operators. Interned literal token text avoids the
            // per-token c.ToString() allocation.
            (BashTokenKind Kind, string Text)? one = c switch
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

            BashTokenKind wordKind = ClassifyWord(value);
            tokens.Add(new BashToken(wordKind, value, wordStart));

            // The word immediately following `<<`/`<<-` is the heredoc delimiter.
            // Record it (with the strip-tabs flag from the operator) so the next
            // newline knows which line ends each body.
            if (heredocDelimPending != 0)
            {
                var (delim, _) = ParseHeredocDelimiter(value);
                pendingHeredocs.Add((delim, heredocDelimPending == 2));
                heredocDelimPending = 0;
            }
        }

        tokens.Add(new BashToken(BashTokenKind.Eof, "", pos));
        return tokens;
    }

    /// <summary>
    /// Skip past one or more heredoc bodies in raw source, consuming each body up
    /// to and including its delimiter line. <paramref name="pos"/> is the offset of
    /// the first body line (just past the command-line newline); the return value
    /// is the offset where normal tokenizing resumes (the line after the last
    /// delimiter, or end of input if a delimiter never appears — bash's
    /// "here-document delimited by end-of-file"). Matches bash exactly: the
    /// delimiter is compared against whole physical lines with no quote or comment
    /// interpretation; <c>&lt;&lt;-</c> strips leading tabs before the comparison.
    /// </summary>
    private static int SkipHeredocBodies(string input, int pos, List<(string Delim, bool StripTabs)> pending)
    {
        int len = input.Length;
        foreach (var (delim, stripTabs) in pending)
        {
            while (pos < len)
            {
                int nlPos = input.IndexOf('\n', pos);
                int lineEnd = nlPos < 0 ? len : nlPos;
                int textEnd = lineEnd > pos && input[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
                string line = input.Substring(pos, textEnd - pos);
                string trimmed = stripTabs ? line.TrimStart('\t') : line;
                bool isDelim = trimmed == delim;
                pos = nlPos < 0 ? len : nlPos + 1;
                if (isDelim) break;
            }
        }
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
        if ((rawDelim.StartsWith('\'') && rawDelim.EndsWith('\''))
            || (rawDelim.StartsWith('"') && rawDelim.EndsWith('"')))
            return (rawDelim.Length >= 2 ? rawDelim[1..^1] : string.Empty, false);
        if (rawDelim.Contains('\\'))
            return (rawDelim.Replace("\\", ""), false);
        return (rawDelim, true);
    }

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

    private static int ScanWord(string input, int pos)
    {
        int len = input.Length;

        while (pos < len)
        {
            char c = input[pos];

            // Brace expansion: {items,here} or {1..10} — consume through closing brace.
            if (c == '{' && IsBraceExpansion(input, pos))
            {
                pos++; // skip {
                while (pos < len && input[pos] != '}')
                    pos++;
                if (pos < len)
                    pos++; // skip }
                continue;
            }

            // Extglob: +(...) *(...) ?(...) !(...) @(...) — consume through matching ')'.
            if ((c is '+' or '*' or '?' or '!' or '@') && pos + 1 < len && input[pos + 1] == '(')
            {
                pos += 2; // skip operator + '('
                int depth = 1;
                while (pos < len && depth > 0)
                {
                    if (input[pos] == '(') depth++;
                    else if (input[pos] == ')') depth--;
                    if (depth > 0) pos++;
                }
                if (pos < len)
                    pos++; // skip closing )
                continue;
            }

            // Unquoted metacharacters end the word. NOTE: '#' is deliberately
            // NOT here. Bash starts a comment only when '#' BEGINS a word; a
            // '#' mid-word is literal ("abc#def", "http://x#frag"). The main
            // tokenize loop only reaches a '#' at a word boundary (after
            // whitespace / operator / newline), so the comment rule there fires
            // exactly when bash's does — without this scan swallowing it.
            if (c is ' ' or '\t' or '\n' or '\r' or '|' or '&' or ';'
                or '(' or ')' or '<' or '>')
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

            // Single-quoted string: consume through closing quote.
            if (c == '\'')
            {
                pos = ScanSingleQuoted(input, pos);
                continue;
            }

            // Double-quoted string. Quote-aware: recurses through $(...), ${...}
            // and `...` so an embedded '"' inside a command substitution does
            // not prematurely terminate the string.
            if (c == '"')
            {
                pos = ScanDoubleQuoted(input, pos);
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
                pos += 2; // skip $'
                while (pos < len && input[pos] != '\'')
                {
                    if (input[pos] == '\\' && pos + 1 < len)
                        pos++; // skip the escaped character
                    pos++;
                }
                if (pos < len)
                    pos++; // skip closing '
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
    private static int ScanSingleQuoted(string input, int pos)
    {
        int len = input.Length;
        pos++; // opening '
        while (pos < len && input[pos] != '\'')
            pos++;
        if (pos < len)
            pos++; // closing '
        return pos;
    }

    /// <summary>
    /// Advance past a double-quoted string, recursing through embedded $(...),
    /// ${...} and `...` (whose own quotes/parens must not end the string).
    /// </summary>
    private static int ScanDoubleQuoted(string input, int pos)
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
        if (pos < len)
            pos++; // closing "
        return pos;
    }

    /// <summary>Advance past a `...` backtick command substitution (no nesting; escapes honored).</summary>
    private static int ScanBacktick(string input, int pos)
    {
        int len = input.Length;
        pos++; // opening `
        while (pos < len && input[pos] != '`')
        {
            if (input[pos] == '\\' && pos + 1 < len)
                pos++; // skip escaped char
            pos++;
        }
        if (pos < len)
            pos++; // closing `
        return pos;
    }

    /// <summary>Advance past a ${...} parameter expansion. input[pos]=='$', [pos+1]=='{'.</summary>
    private static int ScanDollarBrace(string input, int pos)
    {
        int len = input.Length;
        pos += 2; // skip ${
        int depth = 1;
        while (pos < len && depth > 0)
        {
            char c = input[pos];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            if (depth > 0) pos++;
        }
        if (pos < len)
            pos++; // skip closing }
        return pos;
    }

    /// <summary>
    /// Advance past $((...)) arithmetic or $(...) command substitution.
    /// input[pos]=='$', input[pos+1]=='('.
    /// </summary>
    private static int ScanDollarParen(string input, int pos)
    {
        int len = input.Length;
        if (pos + 2 < len && input[pos + 2] == '(')
            return ScanArith(input, pos);
        return ScanBalancedParens(input, pos + 2, 1); // skip $(
    }

    /// <summary>Advance past $(( ... )). input[pos]=='$', [pos+1]=='(', [pos+2]=='('.</summary>
    internal static int ScanArith(string input, int pos)
    {
        int len = input.Length;
        pos += 3; // skip $((
        int depth = 1;
        while (pos < len && depth > 0)
        {
            if (pos + 1 < len && input[pos] == '(' && input[pos + 1] == '(')
            {
                depth++;
                pos += 2;
            }
            else if (pos + 1 < len && input[pos] == ')' && input[pos + 1] == ')')
            {
                depth--;
                if (depth > 0) pos += 2;
            }
            else
            {
                pos++;
            }
        }
        if (pos < len)
            pos += 2; // skip closing ))
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
        int len = input.Length;
        // Case-arm pattern terminators introduce UNMATCHED ')' (e.g. `case $y in a) cmd;; esac`).
        // A naive paren count closes the region early at the first pattern ')'. Track the paren
        // depth at which each `case` opens: a ')' at exactly that depth is a pattern terminator
        // (skip it), a ')' deeper is a real subshell close, and `esac` pops the case. The
        // command-sub/process-sub closer is the ')' that brings depth to 0 with no open case.
        var caseDepths = new List<int>();
        while (pos < len && depth > 0)
        {
            char c = input[pos];
            if (c == '\'') { pos = ScanSingleQuoted(input, pos); continue; }
            if (c == '"') { pos = ScanDoubleQuoted(input, pos); continue; }
            if (c == '`') { pos = ScanBacktick(input, pos); continue; }
            if (c == '$' && pos + 1 < len && input[pos + 1] == '(') { pos = ScanDollarParen(input, pos); continue; }
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
            if (i < value.Length && value[i] == '[')
            {
                i++; // skip [
                while (i < value.Length && value[i] != ']')
                    i++;
                if (i < value.Length)
                    i++; // skip ]
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

    private static bool IsRedirectKind(BashTokenKind kind) =>
        kind is BashTokenKind.Less or BashTokenKind.Great
            or BashTokenKind.DLess or BashTokenKind.DGreat
            or BashTokenKind.LessAnd or BashTokenKind.GreatAnd
            or BashTokenKind.DLessDash;

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
