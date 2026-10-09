using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Runtime pattern test for <c>case</c> arms and <c>[[ … == / != … ]]</c>, called from emitted
/// PowerShell. The transpiler converts a bash pattern to an anchored regex (<c>BashPattern</c>);
/// this applies it CASE-SENSITIVELY, as bash does, unless <c>shopt -s nocasematch</c> is on in the
/// calling runspace — an option that can change at run time, which is why the choice cannot be
/// baked into the emitted <c>switch</c>. PowerShell's own <c>-like</c> / <c>-eq</c> /
/// <c>switch -Wildcard</c> are case-INSENSITIVE, so <c>case abc in A*)</c> matched (bash: no).
/// </summary>
public static class BashPatternMatch
{
    // Regex.IsMatch's static cache holds 15 patterns; a script's case statements easily exceed it.
    private static readonly ConcurrentDictionary<(string, bool), Regex> Cache = new();
    // The host is long-lived and a run-time pattern can differ every iteration: bound both caches.
    private const int MaxCached = 512;

    /// <summary>True when <c>shopt -s nocasematch</c> is set in the calling runspace.</summary>
    public static bool NoCase => InvokeBashShoptCommand.IsEnabled("nocasematch");

    /// <summary>Does <paramref name="input"/> (null = "") match the anchored <paramref name="regex"/>?</summary>
    public static bool IsMatch(object? input, string regex)
    {
        bool noCase = NoCase;
        if (Cache.Count > MaxCached) Cache.Clear();
        var rx = Cache.GetOrAdd((regex, noCase), static k => new Regex(k.Item1,
            RegexOptions.CultureInvariant | (k.Item2 ? RegexOptions.IgnoreCase : RegexOptions.None)));
        return rx.IsMatch(Text(input));
    }

    // Bash pattern text → anchored regex, for patterns assembled at run time (one script line in a
    // loop evaluates the same pattern every iteration).
    private static readonly ConcurrentDictionary<string, string> PatternRegex = new();

    /// <summary>Does <paramref name="input"/> match the bash <paramref name="pattern"/> text (from
    /// <c>[[ x == $pat ]]</c>; <c>\c</c> = literal c, quotes already escaped by the transpiler)?</summary>
    public static bool MatchesPattern(object? input, object? pattern)
    {
        if (PatternRegex.Count > MaxCached) PatternRegex.Clear();
        return IsMatch(input, PatternRegex.GetOrAdd(Text(pattern),
            static p => PsBash.Core.Parser.BashPattern.ToAnchoredRegex(p, honorQuotes: false)));
    }

    /// <summary>String equality with bash's case rule (ordinal; ignore-case under nocasematch).</summary>
    public static bool StringEquals(object? left, object? right) =>
        string.Equals(Text(left), Text(right), NoCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // A BashObject carries its text in BashText; anything else stringifies (null = "").
    private static string Text(object? value) => BashRuntime.GetBashText(value);
}
