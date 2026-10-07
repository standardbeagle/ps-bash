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

    /// <summary>True when <c>shopt -s nocasematch</c> is set in the calling runspace.</summary>
    public static bool NoCase => InvokeBashShoptCommand.IsEnabled("nocasematch");

    /// <summary>Does <paramref name="input"/> (null = "") match the anchored <paramref name="regex"/>?</summary>
    public static bool IsMatch(object? input, string regex)
    {
        bool noCase = NoCase;
        var rx = Cache.GetOrAdd((regex, noCase), static k => new Regex(k.Item1,
            RegexOptions.CultureInvariant | (k.Item2 ? RegexOptions.IgnoreCase : RegexOptions.None)));
        return rx.IsMatch(Text(input));
    }

    /// <summary>String equality with bash's case rule (ordinal; ignore-case under nocasematch).</summary>
    public static bool StringEquals(object? left, object? right) =>
        string.Equals(Text(left), Text(right), NoCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // A BashObject carries its text in BashText; anything else stringifies (null = "").
    private static string Text(object? value) => BashRuntime.GetBashText(value);
}
