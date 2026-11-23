using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Compiles a sed regex (an address or an <c>s</c> pattern, as <see cref="SedDelimited"/> scanned it) when the script
/// is parsed, so a bad one is a script error in GNU's words (<c>Unmatched ( or \(</c>, status 1) before any input is
/// read. REGRESSION: address regexes were compiled lazily by <c>Regex.IsMatch</c> on the first input line, and the
/// parse failure surfaced inside the file loop as <c>sed: can't read f: Invalid pattern ...</c>, printed twice.
/// The text goes through sed's character escapes (<see cref="SedEscape.NormalizeRegex"/>) and then the one GNU
/// translator (<see cref="GnuRegex"/>, sed's flavor: the pattern space is one buffer, <c>M</c> makes it line-wise).
/// </summary>
internal static class SedRegex
{
    /// <summary>
    /// The empty regex (<c>//</c>, <c>s//x/</c>): GNU reuses the last regex used when the command RUNS, so the engine
    /// substitutes it (<see cref="SedEngine"/>). It matches nothing on its own.
    /// </summary>
    public static readonly Regex Last = new("(?!)", RegexOptions.CultureInvariant);

    public const string EmptyModifiersMessage = "cannot specify modifiers on empty regexp";

    public const string NoPreviousMessage = "no previous regular expression";

    /// <summary>
    /// True when <paramref name="message"/> (from <see cref="TryCompile"/>) is GNU's panic, not a script error at a
    /// character: <c>sed: character class syntax is [[:space:]], not [:space:]</c>, status 4, no position.
    /// </summary>
    public static bool IsPanic(string message) => message == GnuRegex.ColonClassMessage;

    /// <summary>
    /// Translate <paramref name="sedPattern"/> (BRE, or ERE when <paramref name="extended"/>) and compile it. False with
    /// GNU's message for the failure in <paramref name="error"/>. An empty pattern is <see cref="Last"/>.
    /// </summary>
    public static bool TryCompile(string sedPattern, bool extended, RegexOptions options, out Regex regex, out string error)
    {
        regex = null!;
        error = string.Empty;
        if (sedPattern.Length == 0)
        {
            if ((options & (RegexOptions.IgnoreCase | RegexOptions.Multiline)) != 0)
            {
                error = EmptyModifiersMessage;
                return false;
            }
            regex = Last;
            return true;
        }
        bool lineWise = (options & RegexOptions.Multiline) != 0;
        var syntax = extended ? GnuRegexSyntax.SedExtended : GnuRegexSyntax.SedBasic;
        if (!GnuRegex.TryTranslate(SedEscape.NormalizeRegex(sedPattern), syntax, out string net, out string? bad, newlineSensitive: lineWise))
        {
            error = bad ?? "Invalid regular expression";
            return false;
        }
        try
        {
            // The translation spells every anchor itself (`\A`, `(?m:^)`), so .NET's Multiline changes nothing; it stays
            // in the options as the record of the M flag (--debug lists it).
            regex = new Regex(net, options | RegexOptions.CultureInvariant);
            return true;
        }
        catch (ArgumentException ex)
        {
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
