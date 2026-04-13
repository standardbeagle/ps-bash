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
                continue;
            }

            // Process substitution: <(...) or >(...) — consume as a word.
            if (c is '<' or '>' && pos + 1 < len && input[pos + 1] == '(')
            {
                int psStart = pos;
                pos += 2; // skip <( or >(
                int depth = 1;
                while (pos < len && depth > 0)
                {
                    if (input[pos] == '(') depth++;
                    else if (input[pos] == ')') depth--;
                    if (depth > 0) pos++;
                }
                if (pos < len)
                    pos++; // skip closing )
                string psText = input[psStart..pos];
                tokens.Add(new BashToken(BashTokenKind.Word, psText, psStart));
                continue;
            }

            // Two-character and three-character operators.
            if (pos + 1 < len)
            {
                string two = input.Substring(pos, 2);

                // <<< (here-string) must be checked before <<- and <<.
                if (two == "<<" && pos + 2 < len && input[pos + 2] == '<')
                {
                    tokens.Add(new BashToken(BashTokenKind.TLess, "<<<", pos));
                    pos += 3;
                    continue;
                }

                // <<- must be checked before <<.
                if (two == "<<" && pos + 2 < len && input[pos + 2] == '-')
                {
                    TryReclassifyIoNumber(tokens, pos);
                    tokens.Add(new BashToken(BashTokenKind.DLessDash, "<<-", pos));
                    pos += 3;
                    continue;
                }

                BashTokenKind? twoKind = two switch
                {
                    "&&" => BashTokenKind.AndIf,
                    "||" => BashTokenKind.OrIf,
                    "|&" => BashTokenKind.PipeAmp,
                    ">>" => BashTokenKind.DGreat,
                    "<<" => BashTokenKind.DLess,
                    "<&" => BashTokenKind.LessAnd,
                    ">&" => BashTokenKind.GreatAnd,
                    _ => null,
                };

                if (twoKind is not null)
                {
                    if (IsRedirectKind(twoKind.Value))
                        TryReclassifyIoNumber(tokens, pos);

                    tokens.Add(new BashToken(twoKind.Value, two, pos));
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

            // Single-character operators.
            BashTokenKind? oneKind = c switch
            {
                '|' => BashTokenKind.Pipe,
                ';' => BashTokenKind.Semi,
                '&' => BashTokenKind.Amp,
                '(' => BashTokenKind.LParen,
                ')' => BashTokenKind.RParen,
                '{' => BashTokenKind.LBrace,
                '}' => BashTokenKind.RBrace,
                '<' => BashTokenKind.Less,
                '>' => BashTokenKind.Great,
                '!' => BashTokenKind.Bang,
                _ => null,
            };

            if (oneKind is not null)
            {
                if (IsRedirectKind(oneKind.Value))
                    TryReclassifyIoNumber(tokens, pos);

                tokens.Add(new BashToken(oneKind.Value, c.ToString(), pos));
                pos++;
                continue;
            }

            // Word (including quoted strings as part of a word).
            int wordStart = pos;
            pos = ScanWord(input, pos);
            string value = input[wordStart..pos];

            BashTokenKind wordKind = ClassifyWord(value);
            tokens.Add(new BashToken(wordKind, value, wordStart));
        }

        tokens.Add(new BashToken(BashTokenKind.Eof, "", pos));
        return tokens;
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

            // Unquoted metacharacters end the word.
            if (c is ' ' or '\t' or '\n' or '\r' or '|' or '&' or ';'
                or '(' or ')' or '<' or '>' or '#')
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
                pos++;
                while (pos < len && input[pos] != '\'')
                    pos++;
                if (pos < len)
                    pos++; // skip closing quote
                continue;
            }

            // Double-quoted string: consume through closing quote, respecting backslash.
            if (c == '"')
            {
                pos++;
                while (pos < len && input[pos] != '"')
                {
                    if (input[pos] == '\\' && pos + 1 < len)
                        pos++;
                    pos++;
                }
                if (pos < len)
                    pos++; // skip closing quote
                continue;
            }

            // Brace expansion ${...}: consume through closing brace.
            if (c == '$' && pos + 1 < len && input[pos + 1] == '{')
            {
                pos += 2; // skip ${
                int depth = 1;
                while (pos < len && depth > 0)
                {
                    if (input[pos] == '{') depth++;
                    else if (input[pos] == '}') depth--;
                    if (depth > 0) pos++;
                }
                if (pos < len)
                    pos++; // skip closing }
                continue;
            }

            // Arithmetic expansion $(( ... )): consume through matching )).
            if (c == '$' && pos + 2 < len && input[pos + 1] == '(' && input[pos + 2] == '(')
            {
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
                continue;
            }

            // Command substitution $(...): consume through matching closing paren.
            if (c == '$' && pos + 1 < len && input[pos + 1] == '(')
            {
                pos += 2; // skip $(
                int depth = 1;
                while (pos < len && depth > 0)
                {
                    char ch = input[pos];
                    if (ch == '(') depth++;
                    else if (ch == ')') depth--;
                    if (depth > 0) pos++;
                }
                if (pos < len)
                    pos++; // skip closing )
                continue;
            }

            // Backtick command substitution `...`: consume through closing backtick.
            if (c == '`')
            {
                pos++; // skip opening `
                while (pos < len && input[pos] != '`')
                {
                    if (input[pos] == '\\' && pos + 1 < len)
                        pos++; // skip escaped char
                    pos++;
                }
                if (pos < len)
                    pos++; // skip closing `
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
