using System.Collections.Immutable;
using System.Text;
using PsBash.Core.Parser.Ast;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Parser;

/// <summary>
/// Word expansion of an unquoted word (bash §3.5: word splitting, pathname expansion, quote removal), decided in ONE
/// place. <see cref="IsUnquotedSplitWord"/> is the only predicate for "this word may become a number of fields other
/// than one"; <see cref="EmitUnquotedSplitWordArray"/> turns the word's parts into the ordered segments of the runtime
/// primitive <c>PsBash.Cmdlets.BashWordFields</c> (via <c>ConvertTo-BashWords -Parts</c>). No other code decides
/// splitting by the shape of a word or by scanning its text (guard <c>WordFieldsGuardTests</c>; spec
/// emitter-strategy.md "Word expansion: one primitive").
/// </summary>
public static partial class PsEmitter
{
    // Segment kinds: the constants of PsBash.Cmdlets.BashWordFields (the transpiler does not reference that assembly).
    private const int SegQuoted = 1;
    private const int SegPattern = 2;
    private const int SegSplit = 3;
    private const int SegSplitList = 4;
    private const int SegQuotedList = 5;
    private const int SegBreak = 6;

    /// <summary>
    /// THE predicate: true when expanding the unquoted <paramref name="word"/> may give other than exactly one field, so it
    /// goes through <see cref="EmitUnquotedSplitWordArray"/>. That is a word with an unquoted expansion whose value is split
    /// (a variable, <c>$@</c>, a parameter operator, a command or arithmetic substitution), a quoted list
    /// (<c>"$@"</c>, <c>"${a[@]}"</c>, <c>"${@^^}"</c>), a glob or a brace expansion. Everything else is statically one word.
    /// <para>Not split (the transpiler's policy, unchanged): a loop or <c>read</c> variable bound from already split
    /// items, the special parameters <c>$? $# $$ $! $-</c>, and the names PowerShell backs (<c>HOME</c>, <c>PWD</c>, ...).
    /// A word with a process substitution keeps its own emission.</para>
    /// </summary>
    private static bool IsUnquotedSplitWord(CompoundWord word)
    {
        var parts = word.Parts;
        if (parts.IsDefaultOrEmpty) return false;
        bool split = false, globOrBrace = false;
        foreach (var part in parts)
        {
            switch (part)
            {
                case WordPart.ProcessSub:
                    return false;
                case WordPart.GlobPart or WordPart.BracedTuple or WordPart.BracedRange:
                    globOrBrace = true;
                    break;
                case WordPart.CommandSub or WordPart.ArithSub:
                    split = true;
                    break;
                case WordPart.SimpleVarSub v:
                    split |= ExpansionSplits(v.Name, null);
                    break;
                case WordPart.BracedVarSub b:
                    split |= ExpansionSplits(b.Name, b.Suffix);
                    break;
                case WordPart.DoubleQuoted dq:
                    split |= HasQuotedListPart(dq.Parts);
                    break;
            }
        }
        if (split) return true;
        return globOrBrace && (IsGlobWord(word) || IsBraceGlobWord(word) || IsPlainBraceWord(word));
    }

    /// <summary>An unquoted <c>$name</c> / <c>${name&lt;suffix&gt;}</c> whose value is split (see <see cref="IsUnquotedSplitWord"/>).</summary>
    private static bool ExpansionSplits(string name, string? suffix)
    {
        if (name is "@" or "*") return suffix is null || !suffix.StartsWith("#BAD:", StringComparison.Ordinal);
        if (suffix is null) return IsOrdinaryVarName(name) || IsPositionalDigits(name);
        if (suffix.StartsWith("#BAD:", StringComparison.Ordinal)) return false;
        if (suffix.StartsWith("[@]", StringComparison.Ordinal) || suffix.StartsWith("[*]", StringComparison.Ordinal)
            || suffix.StartsWith('!')) return true;
        return IsOrdinaryVarName(name) || IsPositionalDigits(name);
    }

    private static bool IsPositionalDigits(string name)
    {
        if (name.Length == 0) return false;
        foreach (char c in name)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    /// <summary>A double-quoted part that expands to one field per value: <c>"$@"</c>, <c>"${a[@]}"</c>, <c>"${!a[@]}"</c>,
    /// <c>"${@/x/y}"</c>, <c>"${a[@]:1}"</c>, <c>"${@:-d}"</c>. Not a length (<c>"${#a[@]}"</c>).</summary>
    private static bool IsQuotedListPart(WordPart part) => part switch
    {
        WordPart.SimpleVarSub { Name: "@" } => true,
        WordPart.BracedVarSub { Name: "@", Suffix: null } => true,
        WordPart.BracedVarSub { Name: "@", Suffix: { } s } => !IsLengthOrBadSuffix(s),
        WordPart.BracedVarSub { Suffix: "![@]" } => true,
        WordPart.BracedVarSub { Suffix: { } s } => s.StartsWith("[@]", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary><c>#L…</c> (a length: <c>${#@}</c>, <c>${#a[@]}</c>) or <c>#BAD:</c> (a bad substitution): one value, never a list.</summary>
    private static bool IsLengthOrBadSuffix(string suffix)
        => suffix.StartsWith("#L", StringComparison.Ordinal) || suffix.StartsWith("#BAD:", StringComparison.Ordinal);

    private static bool HasQuotedListPart(ImmutableArray<WordPart> dqParts)
    {
        foreach (var p in dqParts)
            if (IsQuotedListPart(p)) return true;
        return false;
    }

    /// <summary>A word made of literal / quoted text and at least one quoted list (<c>-"${a[@]}"</c>, <c>"p$@q"</c>): one
    /// operand per element behind <c>builtin</c> / <c>eval</c>.</summary>
    private static bool IsQuotedListWord(CompoundWord word)
    {
        bool list = false;
        foreach (var p in word.Parts)
        {
            switch (p)
            {
                case WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.AnsiCQuoted:
                    break;
                case WordPart.DoubleQuoted dq:
                    list |= HasQuotedListPart(dq.Parts);
                    break;
                default:
                    return false;
            }
        }
        return list;
    }

    /// <summary>A command name of text glued to a split expansion (<c>${b}uiltin</c>, <c>x$(cmd)</c>): it is split, and the
    /// first field names the command. Not a glob, brace or quoted list word (their own command-name forms).</summary>
    private static bool IsAffixedSplitName(CompoundWord word)
    {
        if (word.Parts.Length < 2 || !IsUnquotedSplitWord(word)) return false;
        foreach (var p in word.Parts)
            if (p is WordPart.GlobPart or WordPart.BracedTuple or WordPart.BracedRange
                || p is WordPart.DoubleQuoted dq && HasQuotedListPart(dq.Parts))
                return false;
        return true;
    }

    /// <summary>The fields of a split word as a PowerShell array expression (guard with <see cref="IsUnquotedSplitWord"/>).</summary>
    private static string EmitUnquotedSplitWordArray(CompoundWord word)
    {
        // `ab+($ABC)`: the extglob's static text around a bare variable arm is text of the word (see TryFlattenSplitExtglob).
        if (TryFlattenSplitExtglob(word, out var flatExtglob)) word = flatExtglob;
        var parts = word.Parts;

        if (HasBraceExpansion(parts)) return EmitBraceWordFields(word);
        if (parts.Length == 1 && TryEmitLoneExpansionFields(parts[0]) is { } lone) return lone;

        bool hasGlob = false, unrendered = false;
        foreach (var p in parts)
        {
            if (p is not WordPart.GlobPart gp) continue;
            hasGlob = true;
            if (IsDynamicExtglob(gp.Pattern)) unrendered = true;
        }
        // `@($a|$b)`: an extglob with expansion arms is one run-time pattern (its arms are not split).
        if (unrendered) return PsBuild.GlobWordArray(EmitGlobPatternExpr(word));

        var segs = new WordSegments(hasGlob);
        AppendWordSegments(segs, parts, topLevel: true);
        // Only text and globs: the word is one pattern (`'a b'*`, `x[ab]`).
        if (!segs.AnyExpansion) return PsBuild.GlobWordArray(EmitGlobPatternExpr(word));
        return PsBuild.WordFieldsArray(segs.ToList());
    }

    /// <summary>The segments of one word, in order; adjacent quoted parts collect into one quoted segment and adjacent
    /// pattern text into one pattern segment (`x[$y]z` is `x[`, `$y`, `]z`: fewer elements for the run-time walk).</summary>
    private sealed class WordSegments(bool hasGlob)
    {
        public readonly List<string> Items = new();
        private readonly ImmutableArray<WordPart>.Builder _run = ImmutableArray.CreateBuilder<WordPart>();
        private readonly StringBuilder _pattern = new();
        public readonly bool HasGlob = hasGlob;
        public bool AnyExpansion;

        public void Add(int kind, string valueExpr)
        {
            Flush();
            Items.Add(PsBuild.WordSegment(kind, valueExpr));
        }

        public void AddGroup(string groupExpr)
        {
            Flush();
            Items.Add(groupExpr);
        }

        /// <summary>Unquoted literal or glob text in pattern form (a literal backslash already doubled).</summary>
        public void AddPattern(string patternText)
        {
            FlushRun();
            _pattern.Append(patternText);
        }

        public void AddQuoted(WordPart part)
        {
            FlushPattern();
            _run.Add(part);
        }

        private void Flush()
        {
            FlushRun();
            FlushPattern();
        }

        private void FlushRun()
        {
            if (_run.Count == 0) return;
            Items.Add(PsBuild.WordSegment(SegQuoted, FlattenPartsToDoubleQuotedString(_run.ToImmutable())));
            _run.Clear();
        }

        private void FlushPattern()
        {
            if (_pattern.Length == 0) return;
            Items.Add(PsBuild.WordSegment(SegPattern, PsBuild.SingleQuote(_pattern.ToString())));
            _pattern.Clear();
        }

        public string ToList()
        {
            Flush();
            return string.Join(", ", Items);
        }
    }

    /// <summary>
    /// The segments of unquoted <paramref name="parts"/>. <paramref name="topLevel"/>: the word itself (its literal text is
    /// pattern text, never split; a leading <c>/tmp/</c> or drive path is mapped like any path word). Otherwise the word of
    /// a <c>${v:-word}</c>, whose unquoted literal text is part of the expansion's value and is split.
    /// </summary>
    private static void AppendWordSegments(WordSegments segs, ImmutableArray<WordPart> parts, bool topLevel)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            switch (part)
            {
                case WordPart.Literal l:
                {
                    string text = l.Value;
                    if (text.Length == 0) break;
                    if (!topLevel) { segs.Add(SegSplit, PsBuild.SingleQuote(text)); break; }
                    if (i == 0 && !_operandsAreText && text.StartsWith("/tmp/", StringComparison.Ordinal))
                    {
                        segs.Add(SegQuoted, segs.HasGlob ? PsBuild.RuntimeToShellPath(PsBuild.TempDirExpr) : PsBuild.TempDirExpr);
                        text = text[4..];
                    }
                    else if (i == 0 && !_operandsAreText && TryTranslateMsysDrivePath(text, out var drive))
                    {
                        segs.Add(SegQuoted, PsBuild.SingleQuote(segs.HasGlob ? BashPath.ToShell(drive, windows: true) : drive));
                        break;
                    }
                    segs.AddPattern(text.Replace("\\", "\\\\", StringComparison.Ordinal));
                    break;
                }
                case WordPart.GlobPart gp:
                    segs.AddPattern(GlobPartPatternText(gp.Pattern));
                    break;
                case WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.AnsiCQuoted or WordPart.TildeSub:
                    segs.AddQuoted(part);
                    break;
                case WordPart.DoubleQuoted dq:
                    if (HasQuotedListPart(dq.Parts)) AppendQuotedListSegments(segs, dq.Parts);
                    else segs.AddQuoted(part);
                    break;
                case WordPart.SimpleVarSub v:
                    if (!ExpansionSplits(v.Name, null)) { segs.AddQuoted(part); break; }
                    segs.AnyExpansion = true;
                    if (v.Name is "@" or "*") segs.Add(SegSplitList, "@(" + PsBuild.BuildPositionalExpansion("@") + ")");
                    else segs.Add(SegSplit, EmitSimpleVar(v.Name, strict: true));
                    break;
                case WordPart.BracedVarSub b:
                    if (!ExpansionSplits(b.Name, b.Suffix)) { segs.AddQuoted(part); break; }
                    segs.AnyExpansion = true;
                    AppendBracedVarSegments(segs, b);
                    break;
                case WordPart.CommandSub cs:
                    segs.AnyExpansion = true;
                    segs.Add(SegSplit, EmitCommandSubString(cs, nested: false));
                    break;
                case WordPart.ArithSub a:
                    segs.AnyExpansion = true;
                    segs.Add(SegSplit, "(" + EmitArithSub(a) + ")");
                    break;
                default:
                    segs.AddQuoted(part);
                    break;
            }
        }
    }

    /// <summary>A glob part's pattern text: a class's quoted members literal (<c>[a"]"b]</c> is <c>[a\]b]</c>), a static extglob's
    /// quoted arms escaped (as <see cref="EmitGlobPatternExpr"/> renders them).</summary>
    private static string GlobPartPatternText(string pattern)
    {
        if (pattern.StartsWith('[')) return BashParser.UnquoteClass(pattern);
        if (pattern.Length > 1 && pattern[1] == '(' && pattern.AsSpan().IndexOfAny('\'', '"') >= 0)
        {
            var sb = new StringBuilder(pattern.Length);
            foreach (var (text, isOperator) in SplitExtglob(pattern))
                sb.Append(isOperator ? text : StaticExtglobArm(text));
            return sb.ToString();
        }
        return pattern;
    }

    /// <summary>
    /// <c>${v...}</c> unquoted. A default / alternative operator (<c>:- - :+ +</c>) is a group chosen at run time: the
    /// variable's value (split), or the operator's word with its own quoting (<c>${e:-'a b'}</c> is one field,
    /// <c>${e:-a b}</c> two). Every other operator's result is one split value, a list form (<c>$@</c>, <c>${a[@]}</c>,
    /// <c>${!a[@]}</c>, <c>${!p*}</c>, with any per-element operator or slice) a split list.
    /// </summary>
    private static void AppendBracedVarSegments(WordSegments segs, WordPart.BracedVarSub b)
    {
        if (b.Suffix is null)
        {
            if (b.Name is "@" or "*") segs.Add(SegSplitList, "@(" + PsBuild.BuildPositionalExpansion("@") + ")");
            else segs.Add(SegSplit, EmitSimpleVar(b.Name, strict: true));
            return;
        }
        if (TryDefaultOperator(b.Suffix, out var pre, out var op))
        {
            if (op is ":-" or "-" or ":+" or "+")
            {
                // The value arm is the operand itself: a list's elements (`${*:-d}` with IFS=- and an empty parameter
                // keeps its empty field), a scalar's value.
                bool list = IsListBase(b.Name, pre);
                string valueArm = list
                    ? PsBuild.WordSegment(SegSplitList, EmitQuotedArrayElements(new WordPart.BracedVarSub(b.Name, pre.Length == 0 ? null : pre)))
                    : PsBuild.WordSegment(SegSplit, "(" + EmitBracedVar(b) + ")");
                var wordSegs = new WordSegments(segs.HasGlob);
                if (b.ArgWord is { } arg) AppendWordSegments(wordSegs, arg, topLevel: false);
                string wordArm = wordSegs.ToList();
                bool isDefault = op is ":-" or "-";
                segs.AddGroup(PsBuild.WordSegmentGroup(DefaultOperatorProbe(b, pre, op), isDefault ? valueArm : wordArm, isDefault ? wordArm : ""));
                return;
            }
            // `${v:=w}` assigns and `${v:?m}` fails: the whole result is the value, split (`${e:='v w'}` is two fields).
            segs.Add(SegSplit, "(" + EmitBracedVar(b) + ")");
            return;
        }
        segs.Add(IsListSuffix(b.Name, b.Suffix) ? SegSplitList : SegSplit,
            IsListSuffix(b.Name, b.Suffix) ? "@(" + EmitBracedVar(b) + ")" : "(" + EmitBracedVar(b) + ")");
    }

    /// <summary>
    /// The parts of a double-quoted string that holds a quoted list: text runs are quoted segments, each list a quoted list
    /// (the first element joins the text before it, the last the text after). <c>"${@:-d}"</c> is the list when set and the
    /// quoted word otherwise; <c>"${@:+w}"</c> the quoted word when set and NO field otherwise.
    /// </summary>
    private static void AppendQuotedListSegments(WordSegments segs, ImmutableArray<WordPart> dqParts)
    {
        segs.AnyExpansion = true;
        var run = ImmutableArray.CreateBuilder<WordPart>();
        void FlushText()
        {
            if (run.Count == 0) return;
            segs.Add(SegQuoted, FlattenPartsToDoubleQuotedString(ImmutableArray.Create<WordPart>(new WordPart.DoubleQuoted(run.ToImmutable()))));
            run.Clear();
        }
        foreach (var p in dqParts)
        {
            if (!IsQuotedListPart(p)) { run.Add(p); continue; }
            FlushText();
            if (p is WordPart.BracedVarSub { Suffix: { } s } b && TryDefaultOperator(s, out var pre, out var op) && op is ":-" or "-" or ":+" or "+")
            {
                // The value is the list itself, every element whole (an empty one too): `"${@:-d}"` is `"$@"` when set.
                var basePart = new WordPart.BracedVarSub(b.Name, pre.Length == 0 ? null : pre);
                string listArm = PsBuild.WordSegment(SegQuotedList, EmitQuotedArrayElements(basePart));
                string wordArm = PsBuild.WordSegment(SegQuoted, EmitBracedVar(b, inDoubleQuote: true));
                bool isDefault = op is ":-" or "-";
                segs.AddGroup(PsBuild.WordSegmentGroup(DefaultOperatorProbe(b, pre, op), isDefault ? listArm : wordArm, isDefault ? wordArm : ""));
                continue;
            }
            segs.Add(SegQuotedList, EmitQuotedArrayElements(p));
        }
        FlushText();
    }

    /// <summary>
    /// Splits a parameter suffix into what names the value (<c>!indirect</c>, a subscript <c>[i]</c>, or nothing) and a
    /// default / alternative / assign / error operator (<c>:- - :+ + := = :? ?</c>). False for any other operator.
    /// </summary>
    private static bool TryDefaultOperator(string suffix, out string pre, out string op)
    {
        pre = op = "";
        string s = suffix;
        if (s.StartsWith("!indirect", StringComparison.Ordinal))
        {
            pre = "!indirect";
            s = s["!indirect".Length..];
        }
        else if (s.StartsWith('['))
        {
            int close = BashParser.FindSubscriptClose(s, 0);
            if (close < 0) return false;
            pre = s[..(close + 1)];
            s = s[(close + 1)..];
        }
        if (s.Length >= 2 && s[0] == ':' && s[1] is '-' or '+' or '=' or '?') { op = s[..2]; return true; }
        if (s.Length >= 1 && s[0] is '-' or '+' or '=' or '?') { op = s[..1]; return true; }
        return false;
    }

    /// <summary>The same expansion with the alternative <c>x</c>: it is <c>x</c> exactly when the default operator's value
    /// is used (every name form goes through the one operator path in <see cref="EmitBracedVar"/>).</summary>
    private static string DefaultOperatorProbe(WordPart.BracedVarSub b, string pre, string op)
    {
        string alt = op[0] == ':' ? ":+" : "+";
        return EmitBracedVar(new WordPart.BracedVarSub(b.Name, pre + alt + "x", ImmutableArray.Create<WordPart>(new WordPart.Literal("x"))));
    }

    /// <summary><c>$@</c> / <c>$*</c> or <c>${a[@]}</c> / <c>${a[*]}</c> as the operand of a default operator.</summary>
    private static bool IsListBase(string name, string pre)
        => name is "@" or "*" && pre.Length == 0 || pre is "[@]" or "[*]";

    /// <summary>An unquoted operator form whose result is a list of values (one per parameter, element, key or name).</summary>
    private static bool IsListSuffix(string name, string suffix)
    {
        if (IsLengthOrBadSuffix(suffix)) return false; // a length is one number
        if (name is "@" or "*") return true;
        return suffix.StartsWith("[@]", StringComparison.Ordinal) || suffix.StartsWith("[*]", StringComparison.Ordinal)
            || suffix is "![@]" or "![*]" or "!names";
    }

    /// <summary>
    /// One expansion alone (<c>$x</c>, <c>$(cmd)</c>, <c>$((n))</c>, <c>$@</c>, <c>${v#p}</c>, <c>"$@"</c>): the shortest form of
    /// the same segments (one split value, one list), or null when the word needs the general form.
    /// </summary>
    private static string? TryEmitLoneExpansionFields(WordPart part)
    {
        switch (part)
        {
            case WordPart.SimpleVarSub { Name: "@" or "*" }:
            case WordPart.BracedVarSub { Name: "@" or "*", Suffix: null }:
                return PsBuild.ListFieldsArray("@(" + PsBuild.BuildPositionalExpansion("@") + ")");
            case WordPart.SimpleVarSub v:
                return PsBuild.WordSplitArray(EmitSimpleVar(v.Name, strict: true));
            case WordPart.BracedVarSub { Suffix: null } b:
                return PsBuild.WordSplitArray(EmitSimpleVar(b.Name, strict: true));
            case WordPart.CommandSub cs:
                return PsBuild.WordSplitArray(EmitCommandSubString(cs, nested: false));
            case WordPart.ArithSub a:
                return PsBuild.NumberFieldsArray(EmitArithSub(a));
            case WordPart.BracedVarSub { Suffix: { } s } b:
                if (TryDefaultOperator(s, out var pre, out var op))
                {
                    // A word of plain text and expansions is split as one value with the result: `${v:-a b}`, `${v:+$x}`.
                    if (op is ":-" or "-" or ":+" or "+" && (IsListBase(b.Name, pre) || !IsPlainDefaultWord(b.ArgWord)))
                        return null;
                    return PsBuild.WordSplitArray("(" + EmitBracedVar(b) + ")");
                }
                return IsListSuffix(b.Name, s)
                    ? PsBuild.ListFieldsArray("@(" + EmitBracedVar(b) + ")")
                    : PsBuild.WordSplitArray("(" + EmitBracedVar(b) + ")");
            case WordPart.DoubleQuoted { Parts: [var only] } when IsQuotedListPart(only)
                && !(only is WordPart.BracedVarSub { Suffix: { } qs } && TryDefaultOperator(qs, out _, out _)):
                return EmitQuotedArrayElements(only);
            default:
                return null;
        }
    }

    /// <summary>A default word whose fields are those of its value split as a whole: unquoted literal text and scalar expansions.</summary>
    private static bool IsPlainDefaultWord(ImmutableArray<WordPart>? word)
    {
        if (word is not { } parts) return true;
        foreach (var p in parts)
        {
            switch (p)
            {
                case WordPart.Literal or WordPart.CommandSub or WordPart.ArithSub:
                    break;
                case WordPart.SimpleVarSub v when v.Name is not ("@" or "*"):
                    break;
                case WordPart.BracedVarSub bv when bv.Name is not ("@" or "*") && (bv.Suffix is null || !IsListSuffix(bv.Name, bv.Suffix)):
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A brace word. Plain text (<c>{a,b}x</c>, <c>*.{c,h}</c>) keeps its static array. With an expansion (<c>{1,2}$x</c>,
    /// <c>{a,$(echo q r)}</c>): brace expansion comes first, then every resulting word is expanded and split on its own,
    /// so the words are segments with a field break between them.
    /// </summary>
    private static string EmitBraceWordFields(CompoundWord word)
    {
        var combos = DynamicBraceCombos(word.Parts);
        if (combos is null)
            return IsBraceGlobWord(word)
                ? "@(" + EmitBraceExpandedWord(word.Parts) + " | ConvertTo-BashGlob)"
                : "@(" + EmitBraceExpandedWord(word.Parts) + ")";
        var items = new List<string>(combos.Count * 2);
        foreach (var combo in combos)
        {
            bool glob = false;
            foreach (var p in combo) glob |= p is WordPart.GlobPart;
            var segs = new WordSegments(glob);
            AppendWordSegments(segs, combo, topLevel: true);
            string list = segs.ToList();
            if (list.Length > 0) items.Add(list);
            items.Add(PsBuild.WordSegment(SegBreak, "$null"));
        }
        return PsBuild.WordFieldsArray(string.Join(", ", items));
    }

    /// <summary>
    /// The words of a brace word with an expansion, a quote or a leading tilde in it, each as its parts (bash expands the
    /// braces first, textually: <c>{$a,b}_c</c> reads <c>$a_c</c>); null for a brace word of plain text.
    /// </summary>
    private static List<ImmutableArray<WordPart>>? DynamicBraceCombos(ImmutableArray<WordPart> parts)
    {
        int braceCount = 0;
        bool dynamicPart = false;
        foreach (var p in parts)
        {
            if (p is WordPart.BracedTuple or WordPart.BracedRange)
            {
                braceCount++;
                // `{$a,b}`: an item with an expansion is text that must be expanded, not printed.
                if (p is WordPart.BracedTuple t && t.Items.Any(i => i.Contains('$') || i.Contains('`')))
                    dynamicPart = true;
                else if (braceCount == 1 && ReferenceEquals(p, parts[0]) && p is WordPart.BracedTuple lead && lead.Items.Any(i => i.StartsWith('~')))
                    dynamicPart = true;
            }
            else if (p is not (WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.GlobPart))
                dynamicPart = true;
        }
        // `~{/src,root}`: the parser left the leading ~ as text (brace expansion comes first); each resulting word's
        // tilde prefix is expanded afterwards (`~/src ~root`).
        bool leadingTilde = parts.Length > 1 && parts[0] is WordPart.Literal { Value: "~" } && parts[1] is WordPart.BracedTuple or WordPart.BracedRange;
        if (leadingTilde) dynamicPart = true;
        if (!dynamicPart) return null;

        var combos = new List<ImmutableArray<WordPart>> { ImmutableArray<WordPart>.Empty };
        foreach (var part in parts)
        {
            var alts = part is WordPart.BracedTuple or WordPart.BracedRange
                ? BraceAlternatives(part, leading: combos.Count == 1 && combos[0].IsEmpty)
                : new List<ImmutableArray<WordPart>> { ImmutableArray.Create(part) };
            CheckBraceWords((long)combos.Count * alts.Count);
            var next = new List<ImmutableArray<WordPart>>(combos.Count * alts.Count);
            foreach (var head in combos)
                foreach (var alt in alts)
                    next.Add(AppendBraceParts(head, alt));
            combos = next;
        }
        if (leadingTilde) combos = combos.ConvertAll(RetildeBraceWord);
        return combos;
    }
}
