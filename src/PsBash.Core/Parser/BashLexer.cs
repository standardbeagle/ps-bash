using System.Collections.Frozen;

namespace PsBash.Core.Parser;

/// <summary>
/// Hand-rolled lexer for bash input. Produces a flat token list from a string.
/// Context-sensitive aspects (here-docs, alias expansion) are deferred to the parser.
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

            // Two-character and three-character operators.
            if (pos + 1 < len)
            {
                string two = input.Substring(pos, 2);

                // <<- must be checked before <<.
                if (two == "<<" && pos + 2 < len && input[pos + 2] == '-')
                {
                    TryReclassifyIoNumber(tokens);
                    tokens.Add(new BashToken(BashTokenKind.DLessDash, "<<-", pos));
                    pos += 3;
                    continue;
                }

                BashTokenKind? twoKind = two switch
                {
                    "&&" => BashTokenKind.AndIf,
                    "||" => BashTokenKind.OrIf,
                    ">>" => BashTokenKind.DGreat,
                    "<<" => BashTokenKind.DLess,
                    "<&" => BashTokenKind.LessAnd,
                    ">&" => BashTokenKind.GreatAnd,
                    _ => null,
                };

                if (twoKind is not null)
                {
                    if (IsRedirectKind(twoKind.Value))
                        TryReclassifyIoNumber(tokens);

                    tokens.Add(new BashToken(twoKind.Value, two, pos));
                    pos += 2;
                    continue;
                }
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
                    TryReclassifyIoNumber(tokens);

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

    private static int ScanWord(string input, int pos)
    {
        int len = input.Length;

        while (pos < len)
        {
            char c = input[pos];

            // Unquoted metacharacters end the word.
            if (c is ' ' or '\t' or '\n' or '\r' or '|' or '&' or ';'
                or '(' or ')' or '<' or '>' or '#')
                break;

            // Backslash escape: consume the backslash and the next character.
            if (c == '\\' && pos + 1 < len)
            {
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

            pos++;
        }

        return pos;
    }

    private static BashTokenKind ClassifyWord(string value)
    {
        // ASSIGNMENT_WORD: starts with NAME (letter/underscore, then alnum/underscore), then '='.
        // The '=' must not be inside quotes.
        if (value.Length >= 2 && IsNameStart(value[0]))
        {
            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '=')
                    return BashTokenKind.AssignmentWord;
                if (!IsNameChar(c))
                    break;
            }
        }

        return BashTokenKind.Word;
    }

    private static bool IsNameStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    private static bool IsNameChar(char c) => IsNameStart(c) || c is (>= '0' and <= '9');

    /// <summary>
    /// If the last token is a Word consisting entirely of digits and we're about to
    /// emit a redirect operator, reclassify it as IoNumber.
    /// </summary>
    private static void TryReclassifyIoNumber(List<BashToken> tokens)
    {
        if (tokens.Count == 0)
            return;

        var last = tokens[^1];
        if (last.Kind != BashTokenKind.Word)
            return;

        if (!IsAllDigits(last.Value))
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
