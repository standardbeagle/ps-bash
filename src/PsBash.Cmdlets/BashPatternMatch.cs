using System.Collections.Concurrent;
using System.Management.Automation;
using System.Text.RegularExpressions;
using PsBash.Core;
using PsBash.Core.Parser;

namespace PsBash.Cmdlets;

/// <summary>
/// The run-time side of every bash pattern context, called from emitted PowerShell: <c>case</c> arms,
/// <c>[[ … == / != … ]]</c>, <c>${v#p}</c>-style removal, <c>${v/p/r}</c> substitution and <c>${v^p}</c> case
/// modification. The transpiler hands over the pattern as canonical text; the one compiler (<c>BashPattern</c>)
/// matches it. Matching is CASE-SENSITIVE, as bash's is, unless <c>shopt -s nocasematch</c> is on in the
/// calling runspace (for the contexts bash applies it to) — an option that can change at run time, which is why the
/// choice cannot be baked into the emitted code. PowerShell's own <c>-like</c> / <c>-eq</c> /
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

    /// <summary>
    /// BASH_REMATCH after a successful <c>[[ s =~ re ]]</c>, from PowerShell's <c>$Matches</c>: an indexed array of the
    /// whole match and then every group in order, a group that took no part being "" (bash). <c>$Matches</c> itself is a
    /// hashtable: it enumerates in hash order (<c>${BASH_REMATCH[@]}</c> was "c b bc"), leaves non-participating groups
    /// out, and is keyed by int where every other array reads by a string index.
    /// </summary>
    public static string[] Rematch(System.Collections.IDictionary matches, string pattern)
    {
        int count = 0;
        foreach (var key in matches.Keys)
            if (key is int i && i >= count) count = i + 1;
        if (Cache.Count > MaxCached) Cache.Clear();
        try
        {
            var rx = Cache.GetOrAdd((pattern, false), static k => new Regex(k.Item1, RegexOptions.CultureInvariant));
            count = Math.Max(count, rx.GetGroupNumbers().Length);
        }
        catch (ArgumentException) { }
        var result = new string[count];
        for (int g = 0; g < count; g++)
            result[g] = matches.Contains(g) ? Text(matches[g]) : "";
        return result;
    }

    private static readonly ConcurrentDictionary<string, string> EreCache = new();

    /// <summary>The run-time regex of <c>[[ s =~ $re ]]</c> in .NET syntax (<see cref="PsBash.Core.Parser.BashEre"/>);
    /// throws <see cref="ArgumentException"/> for a regex bash's regcomp rejects (status 2).</summary>
    public static string Ere(object? pattern)
    {
        string p = Text(pattern);
        if (p.IndexOf('{') < 0 && !p.Contains("[:", StringComparison.Ordinal)) return p;
        if (EreCache.TryGetValue(p, out var hit)) return hit;
        string result = PsBash.Core.Parser.BashEre.ToDotNet(p);
        if (EreCache.Count > MaxCached) EreCache.Clear();
        EreCache[p] = result;
        return result;
    }

    /// <summary>Does <paramref name="input"/> match the bash <paramref name="pattern"/> (canonical text, built by the
    /// transpiler: quoted parts escaped, an unquoted expansion's value active)? <c>case</c>, <c>[[ == != ]]</c> and
    /// <c>compgen -X</c>; ignore-case under <c>nocasematch</c>.</summary>
    public static bool MatchesPattern(object? input, object? pattern)
    {
        string text = Text(input), pat = Text(pattern);
        var opts = NoCase ? BashPatternOptions.NoCase : BashPatternOptions.None;
        // A temp-dir value is /tmp to the script (`cd /tmp; [[ $PWD == /tmp* ]]`): match either spelling.
        return Matches(text, pat, opts)
            || (PsBash.Core.RuntimePath.TryUnmapTmp(text, out var bash) && Matches(bash, pat, opts));
    }

    /// <summary>One whole-string match in the shell's locale: in C / POSIX the subject and pattern are bytes.</summary>
    internal static bool Matches(string text, string pattern, BashPatternOptions options)
    {
        if (BashLocale.IsByteLocale())
        {
            options |= BashPatternOptions.Bytes;
            text = ToBytes(text);
            pattern = ToBytes(pattern);
        }
        return BashPattern.Compile(pattern, options).IsMatch(text);
    }

    /// <summary>
    /// <c>${v#p}</c> (<paramref name="suffix"/> false, <paramref name="longest"/> false), <c>${v##p}</c>, <c>${v%p}</c>,
    /// <c>${v%%p}</c>: the value without its shortest / longest matching prefix / suffix. nocasematch does not apply
    /// (bash 5.2). A list (<c>${a[@]#p}</c>) is done element by element.
    /// </summary>
    public static object Remove(object? value, object? pattern, bool suffix, bool longest)
    {
        string pat = Text(pattern);
        bool bytes = BashLocale.IsByteLocale();
        var program = Program(pat, BashPatternOptions.None, bytes);
        return Each(value, s =>
        {
            if (s.Length == 0 && pat.Length == 0) return s;
            string v = bytes ? ToBytes(s) : s;
            string r;
            if (suffix)
            {
                int st = program.MatchSuffix(v, longest);
                r = st < 0 ? v : v[..st];
            }
            else
            {
                int n = program.MatchPrefix(v, longest);
                r = n < 0 ? v : v[n..];
            }
            return bytes && !ReferenceEquals(r, v) ? FromBytes(r) : ReferenceEquals(r, v) ? s : r;
        });
    }

    /// <summary>
    /// <c>${v/p/r}</c> (<paramref name="mode"/> 0), <c>${v//p/r}</c> (1), <c>${v/#p/r}</c> (2), <c>${v/%p/r}</c> (3).
    /// <paramref name="replacement"/> is the replacement template (<see cref="BashPattern.Substitute"/>): a bare
    /// <c>&amp;</c> is the match while <c>patsub_replacement</c> is on (the default). Ignore-case under nocasematch.
    /// </summary>
    public static object Substitute(object? value, object? pattern, object? replacement, int mode)
    {
        string pat = Text(pattern), template = Text(replacement);
        bool bytes = BashLocale.IsByteLocale();
        var program = Program(pat, NoCase ? BashPatternOptions.NoCase : BashPatternOptions.None, bytes);
        bool amp = InvokeBashShoptCommand.IsEnabled("patsub_replacement");
        if (bytes) template = ToBytes(template);
        var m = (BashPattern.SubstituteMode)mode;
        return Each(value, s =>
        {
            string v = bytes ? ToBytes(s) : s;
            string r = BashPattern.Substitute(v, program, pat.Length == 0, template, amp, m);
            return ReferenceEquals(r, v) ? s : bytes ? FromBytes(r) : r;
        });
    }

    /// <summary>
    /// <c>${v^p}</c> <c>${v^^p}</c> <c>${v,p}</c> <c>${v,,p}</c>: <paramref name="pattern"/> null when none was written
    /// (every character). A character is converted when it matches the whole pattern; nocasematch does not apply.
    /// </summary>
    public static object ModifyCase(object? value, object? pattern, bool upper, bool all)
    {
        bool bytes = BashLocale.IsByteLocale();
        string? pat = pattern is null ? null : Text(pattern);
        var program = pat is null ? null : Program(pat, BashPatternOptions.None, bytes);
        return Each(value, s => BashPattern.ModifyCase(s, program, upper, all, asciiOnly: bytes));
    }

    /// <summary>The value of an UNQUOTED expansion in a replacement: inserted as it is (a <c>&amp;</c> in it is the
    /// match, <c>\&amp;</c> an ampersand), or, with <c>patsub_replacement</c> off, literally.</summary>
    public static string ReplacementText(object? value)
    {
        string s = Text(value);
        return InvokeBashShoptCommand.IsEnabled("patsub_replacement") ? s : BashPattern.EscapeReplacement(s);
    }

    /// <summary>A quoted part of a replacement: always its literal text.</summary>
    public static string LiteralReplacement(object? value) => BashPattern.EscapeReplacement(Text(value));

    private static BashPatternProgram Program(string pattern, BashPatternOptions options, bool bytes)
        => bytes ? BashPattern.Compile(ToBytes(pattern), options | BashPatternOptions.Bytes) : BashPattern.Compile(pattern, options);

    private static string ToBytes(string s) => System.Text.Ascii.IsValid(s) ? s : RawBytes.ToByteChars(s);

    private static string FromBytes(string s) => System.Text.Ascii.IsValid(s) ? s : RawBytes.FromByteChars(s);

    // A list (an array's elements) is transformed element by element, anything else as its text (null = "").
    private static object Each(object? value, Func<string, string> op)
    {
        if (value is PSObject pso && pso.BaseObject is System.Collections.IList) value = pso.BaseObject;
        if (value is System.Collections.IList list)
        {
            var result = new object[list.Count];
            for (int i = 0; i < list.Count; i++) result[i] = op(Text(list[i]));
            return result;
        }
        return op(Text(value));
    }

    /// <summary>String equality with bash's case rule (ordinal; ignore-case under nocasematch).</summary>
    public static bool StringEquals(object? left, object? right)
    {
        var cmp = NoCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string l = Text(left), r = Text(right);
        return string.Equals(l, r, cmp) || string.Equals(BashSpelling(l), BashSpelling(r), cmp);
    }

    private static string BashSpelling(string s) =>
        PsBash.Core.RuntimePath.TryUnmapTmp(s, out var bash) ? bash : s;

    // A BashObject carries its text in BashText; anything else stringifies (null = "").
    private static string Text(object? value) => BashRuntime.GetBashText(value);
}
