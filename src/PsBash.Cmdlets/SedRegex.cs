using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Compiles a sed regex (an address or an <c>s</c> pattern, as <see cref="SedDelimited"/> scanned it) when the script
/// is parsed, so a bad one is a script error in GNU's words (<c>Unmatched ( or \(</c>, status 1) before any input is
/// read. REGRESSION: address regexes were compiled lazily by <c>Regex.IsMatch</c> on the first input line, and the
/// parse failure surfaced inside the file loop as <c>sed: can't read f: Invalid pattern ...</c>, printed twice.
/// </summary>
internal static class SedRegex
{
    /// <summary>
    /// Translate <paramref name="sedPattern"/> (BRE, or ERE when <paramref name="extended"/>) and compile it. False with
    /// GNU's message for the failure in <paramref name="error"/>.
    /// </summary>
    public static bool TryCompile(string sedPattern, bool extended, RegexOptions options, out Regex regex, out string error)
    {
        try
        {
            regex = new Regex(BashRuntime.SedRegexToNet(sedPattern, extended), options);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null!;
            error = GnuMessage(ex);
            return false;
        }
    }

    /// <summary>GNU regcomp's wording for a .NET parse failure (the .NET text for one GNU has no counterpart of).</summary>
    public static string GnuMessage(ArgumentException ex)
    {
        if (ex is RegexParseException rpe)
        {
            switch (rpe.Error)
            {
                case RegexParseError.InsufficientClosingParentheses: return @"Unmatched ( or \(";
                case RegexParseError.InsufficientOpeningParentheses: return @"Unmatched ) or \)";
                case RegexParseError.UnterminatedBracket: return "Unmatched [, [^, [:, [., or [=";
                case RegexParseError.ReversedQuantifierRange: return @"Invalid content of \{\}";
                case RegexParseError.ReversedCharacterRange: return "Invalid range end";
                case RegexParseError.UndefinedNumberedReference: return "Invalid back reference";
                case RegexParseError.UnescapedEndingBackslash: return "Trailing backslash";
                case RegexParseError.QuantifierAfterNothing:
                case RegexParseError.NestedQuantifiersNotParenthesized:
                    return "Invalid preceding regular expression";
            }
        }
        // "Invalid pattern 'x' at offset 3. Illegal \ at end of pattern." -> the reason only.
        string m = ex.Message;
        int at = m.IndexOf(" at offset ", StringComparison.Ordinal);
        int dot = at < 0 ? -1 : m.IndexOf(". ", at, StringComparison.Ordinal);
        return dot < 0 ? m : m.Substring(dot + 2);
    }

    /// <summary>
    /// The highest <c>\N</c> back-reference in an <c>s</c> replacement as typed (<c>\\</c> is an escaped backslash,
    /// not the start of one), or 0. GNU refuses one past the regex's groups: <c>invalid reference \N on `s' command's RHS</c>.
    /// </summary>
    public static int MaxReplacementReference(string replacement)
    {
        int max = 0;
        for (int i = 0; i + 1 < replacement.Length; i++)
        {
            if (replacement[i] != '\\') continue;
            char n = replacement[i + 1];
            if (n is >= '1' and <= '9' && n - '0' > max) max = n - '0';
            i++;
        }
        return max;
    }
}
