using PsBash.Core.Parser;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>fnmatch(pattern, name, FNM_PERIOD)</c> for <c>ls -I / --hide</c> and one pathname component of a glob: the one
/// pattern compiler (<see cref="BashPattern"/>) plus the leading-period rule. A name that starts with a period is
/// matched only by a pattern that starts with a literal period (unless <c>dotglob</c>). Case-sensitive, as GNU, unless
/// <c>nocaseglob</c> asks otherwise; <c>ls</c> has no extglob.
/// </summary>
internal static class LsGlob
{
    public static bool Match(string pattern, string name)
        => Match(pattern, name, allowLeadingPeriod: false, noCase: false, extGlob: false);

    /// <summary>
    /// <see cref="Match(string, string)"/> with bash's <c>dotglob</c> switch: when <paramref name="allowLeadingPeriod"/>
    /// a leading period needs no literal period in the pattern.
    /// </summary>
    public static bool Match(string pattern, string name, bool allowLeadingPeriod, bool noCase = false, bool extGlob = false)
    {
        // The leading-period rule is the compiler's (BashPatternOptions.LeadingPeriod), not a look at the pattern's
        // first character: `@(.a|b)` and `?(.)a` match .a through an arm that starts with a literal period.
        var options = (noCase ? BashPatternOptions.NoCase : BashPatternOptions.None)
                      | (extGlob ? BashPatternOptions.None : BashPatternOptions.NoExtGlob)
                      | (!allowLeadingPeriod && name.Length > 0 && name[0] == '.' ? BashPatternOptions.LeadingPeriod : BashPatternOptions.None);
        return BashPatternMatch.Matches(name, pattern, options);
    }
}
