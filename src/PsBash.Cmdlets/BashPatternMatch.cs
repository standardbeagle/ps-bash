using System.Management.Automation;
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
    /// <summary>True when <c>shopt -s nocasematch</c> is set in the calling runspace.</summary>
    public static bool NoCase => InvokeBashShoptCommand.IsEnabled("nocasematch");

    // The parameter-expansion operators read their whole pattern at run time, so `shopt extglob` decides whether its
    // operators count (bash: `shopt -u extglob; v=+(a)x; echo ${v#+(a)}` prints x).
    private static BashPatternOptions RunTimeExtGlob =>
        InvokeBashShoptCommand.IsEnabled("extglob") ? BashPatternOptions.None : BashPatternOptions.NoExtGlob;

    /// <summary>
    /// <c>[[ s =~ re ]]</c>: <paramref name="input"/> against the <c>=~</c> word <paramref name="word"/>, built by the
    /// emitter from <see cref="EreQuote"/> (quoted parts) and <see cref="EreRaw"/> (unquoted parts), run by THE POSIX
    /// ERE primitive (<see cref="PsBash.Core.Parser.BashEre"/>, glibc regcomp/regexec semantics, never .NET regex).
    /// <c>Status</c> is the test's status (2 = a regex regcomp rejects: bash prints nothing and leaves BASH_REMATCH
    /// alone); <c>Groups</c> is what BASH_REMATCH becomes otherwise (empty after a failed match).
    /// </summary>
    public static PsBash.Core.Parser.BashEreResult EreTest(object? input, string word) =>
        PsBash.Core.Parser.BashEre.Match(PsBash.Core.Parser.BashEre.WordToRegex(word), NoCase, Text(input));

    /// <summary>A QUOTED part of the <c>=~</c> word: matches its characters literally.</summary>
    public static string EreQuote(object? value) => PsBash.Core.Parser.BashEre.MarkQuoted(Text(value));

    /// <summary>An UNQUOTED part of the <c>=~</c> word (a literal or an unquoted expansion): regex text.</summary>
    public static string EreRaw(object? value) => PsBash.Core.Parser.BashEre.MarkUnquoted(Text(value));

    /// <summary>
    /// An unquoted <c>${u:-w}</c> / <c>${u-w}</c> / <c>${u:+w}</c> / <c>${u+w}</c> in the <c>=~</c> word, whose value
    /// <paramref name="value"/> is either the variable's (regex text, as <see cref="EreRaw"/>) or the operand word the
    /// expansion chose, already marked part by part (<see cref="EreOperandText"/>: its quoted characters match
    /// literally, <c>${u:-a\{1\}}</c>). Which one is known only at run time. A variable's value is converted as
    /// PowerShell's <c>[string]</c> cast does (an array joins with spaces), as for any other unquoted expansion.
    /// </summary>
    public static string EreExpansion(object? value) =>
        (value is PSObject pso ? pso.BaseObject : value) is EreOperandText chosen
            ? chosen.Text
            : EreRaw(LanguagePrimitives.ConvertTo<string>(value));

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

    /// <summary><c>compgen -X</c>: <paramref name="input"/> against the filter pattern, a run-time value (extglob only
    /// while <c>shopt extglob</c> is on; ignore-case under nocasematch).</summary>
    public static bool MatchesFilter(string input, string pattern)
        => Matches(input, pattern, (NoCase ? BashPatternOptions.NoCase : BashPatternOptions.None) | RunTimeExtGlob);

    /// <summary>
    /// An unquoted expansion's value spliced into the pattern of a <c>case</c> arm or a glob word: as it is while
    /// <c>shopt extglob</c> is on, with its extglob syntax made literal while it is off (bash matches
    /// <c>v='+(a)'; case '+(a)' in $v)</c> literally then). The words the script spelled keep their extglob operators.
    /// </summary>
    public static string ValuePattern(object? value)
    {
        string s = Text(value);
        return InvokeBashShoptCommand.IsEnabled("extglob") ? s : BashPattern.ValueWithoutExtGlob(s);
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
        var program = Program(pat, RunTimeExtGlob, bytes);
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
        var program = Program(pat, (NoCase ? BashPatternOptions.NoCase : BashPatternOptions.None) | RunTimeExtGlob, bytes);
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
        var program = pat is null ? null : Program(pat, RunTimeExtGlob, bytes);
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

/// <summary>
/// The operand word of a parameter expansion in the <c>=~</c> word, as the expansion chose it: <see cref="Text"/> is
/// already in the marked form <c>BashEre.WordToRegex</c> reads (quoted characters CTLESC-escaped), so
/// <see cref="BashPatternMatch.EreExpansion"/> uses it as is instead of treating it as a variable's value.
/// </summary>
public sealed class EreOperandText(string text)
{
    /// <summary>The marked regex text.</summary>
    public string Text { get; } = text;
}
