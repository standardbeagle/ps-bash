namespace PsBash.Core.Parser;

/// <summary>
/// Which bash literal text PowerShell reads back as a NUMBER when the emitter writes it unquoted. bash words are
/// strings; PowerShell's tokenizer turns <c>1e5</c>, <c>0x10</c>, <c>1kb</c>, <c>10d</c>, <c>08</c>, <c>.5</c> into
/// numeric constants, so <c>for f in 33441e77</c> printed <c>3.3441E+81</c> and <c>f 08</c> gave the function 8
/// (spec emitter-strategy.md §Number-shaped words). This mirrors PowerShell's number scanner (Tokenizer.ScanNumber,
/// PowerShell 7.4+): the guard <c>PsNumberLiteralGuardTests</c> compares it with the real tokenizer over a fuzzed
/// corpus, so a PowerShell grammar change fails a test instead of converting a word.
/// <para>
/// Pure and allocation-free: the transpiler runs in the AOT launcher, which has no System.Management.Automation.
/// </para>
/// </summary>
public static class PsNumberLiteral
{
    /// <summary>
    /// True when <paramref name="text"/> is a decimal integer PowerShell reads back with exactly the same text in
    /// EVERY position (argument and expression mode): <c>0</c>, or an optional <c>-</c> and up to 18 digits without a
    /// leading zero. <c>08</c> (→ 8), <c>-0</c> (→ 0), <c>+5</c> (→ 5) and a 30-digit run (→ a double) are not.
    /// </summary>
    public static bool IsCanonicalInteger(ReadOnlySpan<char> text)
    {
        bool negative = text.Length > 0 && text[0] == '-';
        ReadOnlySpan<char> digits = negative ? text[1..] : text;
        if (digits.Length is 0 or > 18) return false;
        if (digits[0] == '0') return digits.Length == 1 && !negative;
        foreach (char c in digits)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// True when <paramref name="text"/>, written as a whole unquoted COMMAND ARGUMENT, is a PowerShell numeric
    /// literal: decimal (<c>1</c>, <c>1.</c>, <c>.5</c>, <c>1e5</c>, <c>1.5e-3</c>), hex (<c>0x1F</c>, also bare
    /// <c>0x</c>) or binary (<c>0b101</c>), then an optional type suffix (<c>y uy s us l ul u n d</c>) and an optional
    /// multiplier (<c>kb mb gb tb pb</c>), all case-insensitive. A leading sign makes an argument a generic token
    /// (<c>-5</c>, <c>+5</c> stay text), so it is not a number here. The value is not checked: a suffixed literal that
    /// overflows its type (<c>9e91d</c>) is a generic token to PowerShell, and true here only quotes it (same text).
    /// </summary>
    public static bool IsArgumentNumber(ReadOnlySpan<char> text)
    {
        int i = 0;
        int n = text.Length;
        if (n == 0) return false;
        if (n >= 2 && text[0] == '0' && (text[1] | 0x20) is 'x' or 'b')
        {
            bool hex = (text[1] | 0x20) == 'x';
            i = 2;
            while (i < n && (hex ? char.IsAsciiHexDigit(text[i]) : text[i] is '0' or '1')) i++;
            // A bare `0x` is the number 0; a bare `0b` is text.
            if (!hex && i == 2) return false;
        }
        else
        {
            int intDigits = 0;
            while (i < n && char.IsAsciiDigit(text[i])) { i++; intDigits++; }
            int fracDigits = 0;
            if (i < n && text[i] == '.')
            {
                // `1..3` is a range in expression mode and a generic token as an argument.
                if (i + 1 < n && text[i + 1] == '.') return false;
                i++;
                while (i < n && char.IsAsciiDigit(text[i])) { i++; fracDigits++; }
            }
            if (intDigits + fracDigits == 0) return false;
            if (i < n && (text[i] | 0x20) == 'e')
            {
                int j = i + 1;
                if (j < n && (text[j] is '+' or '-' || IsDashMark(text[j]))) j++;
                int expDigits = 0;
                while (j < n && char.IsAsciiDigit(text[j])) { j++; expDigits++; }
                if (expDigits == 0) return false;
                i = j;
            }
        }
        i = SkipTypeSuffix(text, i);
        if (i + 2 <= n && (text[i + 1] | 0x20) == 'b' && (text[i] | 0x20) is 'k' or 'm' or 'g' or 't' or 'p') i += 2;
        return i == n;
    }

    /// <summary>
    /// True when <paramref name="text"/>, written bare at the START of a PowerShell statement (a bash command name),
    /// may begin with a numeric constant: it starts with a digit, or with <c>.</c> and a digit. There PowerShell scans a
    /// number prefix before deciding on a command, so <c>1e5=1</c> and <c>08=x</c> parsed as an assignment to a number
    /// ("The assignment expression is not valid"), and <c>127</c> evaluated to itself. Such a command name is called by
    /// its quoted name (<c>&amp; '1e5=1'</c>), which PowerShell looks up as bash does (<c>1e5=1: command not found</c>).
    /// </summary>
    public static bool MayStartStatementAsNumber(ReadOnlySpan<char> text)
        => text.Length > 0 && (char.IsAsciiDigit(text[0]) || (text.Length > 1 && text[0] == '.' && char.IsAsciiDigit(text[1])));

    // y | s | l | n | d | u | uy | us | ul — the longest match, as the tokenizer reads it.
    private static int SkipTypeSuffix(ReadOnlySpan<char> text, int i)
    {
        if (i >= text.Length) return i;
        char c = (char)(text[i] | 0x20);
        if (c == 'u')
        {
            if (i + 1 < text.Length && (text[i + 1] | 0x20) is 'y' or 's' or 'l') return i + 2;
            return i + 1;
        }
        return c is 'y' or 's' or 'l' or 'n' or 'd' ? i + 1 : i;
    }

    private static bool IsDashMark(char c) => c is '–' or '—' or '―';
}
