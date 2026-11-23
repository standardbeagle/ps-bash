using System.Text;
using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// Word decomposition for <see cref="BashParser"/>: turns a WORD token's raw
/// text into typed WordPart children (quotes, $VAR, command/process substitution,
/// globs, brace expansion, tilde). Split from the parser spine in BashParser.cs;
/// every member here is a static helper over the raw word string.
/// </summary>
public sealed partial class BashParser
{
    /// <summary>The length of a <c>\&lt;newline&gt;</c> (or <c>\&lt;CR&gt;&lt;LF&gt;</c>) line continuation at <paramref name="i"/>, else 0.</summary>
    private static int LineContinuationLength(string raw, int i)
        => i + 1 < raw.Length && raw[i] == '\\'
            ? raw[i + 1] == '\n' ? 2 : raw[i + 1] == '\r' && i + 2 < raw.Length && raw[i + 2] == '\n' ? 3 : 0
            : 0;
    /// <summary>Set when a word holds a bad substitution (<c>${a[]}</c>, <c>${(m)x}</c>); the emitter clears it before a
    /// parse and reads it after, to add the guards that let the error abandon only its input line.</summary>
    [ThreadStatic] internal static bool SawBadSubstitution;

    /// <summary>
    /// Sub-parse a WORD token's raw text into typed WordPart children.
    /// Handles single quotes, double quotes, backslash escapes, $VAR references,
    /// and bare literal text.
    /// </summary>
    /// <param name="raw">The word's source text.</param>
    /// <param name="braceExpansion">False inside <c>[[ ]]</c>, where bash does no brace expansion
    /// (<c>[[ --X{a,b}X == ... ]]</c> compares the text <c>--X{a,b}X</c>).</param>
    /// <param name="assignmentValue">True for a segment of an assignment value: the word of a <c>${v-w}</c> /
    /// <c>${v:-w}</c> / <c>${v+w}</c> / <c>${v:+w}</c> in it is itself read as an assignment value (a tilde after an
    /// unquoted <c>:</c> expands: <c>x=${undef-~:~}</c> is <c>$HOME:$HOME</c>).</param>

    internal static ImmutableArray<WordPart> DecomposeWord(string raw, bool braceExpansion = true, bool assignmentValue = false)
    {
        var parts = ImmutableArray.CreateBuilder<WordPart>();
        int pos = 0;
        int len = raw.Length;

        // Detect tilde at the start of a word. Brace expansion comes before tilde expansion, so a brace in the
        // tilde prefix (`~{/src,root}` is `~/src ~root`) leaves the ~ as text; the brace emitter expands each
        // resulting word's tilde (PsEmitter.EmitBraceExpandedWord).
        if (len > 0 && raw[0] == '~')
        {
            if (braceExpansion && TildePrefixHasBrace(raw))
            {
                parts.Add(new WordPart.Literal("~"));
                pos = 1;
            }
            else
                pos = ParseTilde(raw, parts);
        }

        while (pos < len)
        {
            char c = raw[pos];

            if (c == '\'')
            {
                pos = ParseSingleQuoted(raw, pos, parts);
            }
            else if (c == '"')
            {
                pos = ParseDoubleQuoted(raw, pos, parts);
            }
            else if (c == '\\')
            {
                if (pos + 1 < len && raw[pos + 1] == '\n')
                {
                    pos += 2;   // line continuation inside a word: removed ("foo\<nl>bar" is "foobar")
                }
                else if (pos + 2 < len && raw[pos + 1] == '\r' && raw[pos + 2] == '\n')
                {
                    pos += 3;
                }
                else if (pos + 1 < len)
                {
                    parts.Add(new WordPart.EscapedLiteral(raw[pos + 1].ToString()));
                    pos += 2;
                }
                else
                {
                    // Trailing backslash with nothing to escape. Emit it as a literal and
                    // ADVANCE — the bare-literal fallback breaks on '\' without consuming it,
                    // so a lone trailing '\' (`echo \`, or any word ending in '\') spun the
                    // decomposition loop forever (garbage-fuzz find: minimal hang was "\").
                    parts.Add(new WordPart.Literal("\\"));
                    pos++;
                }
            }
            else if (c == '$' && LineContinuationLength(raw, pos + 1) is var cont and > 0)
            {
                // `$\<newline>?` is `$?`: bash removes the continuation before it reads the expansion.
                raw = raw[..(pos + 1)] + raw[(pos + 1 + cont)..];
                len = raw.Length;
            }
            else if (c == '$' && pos + 2 < len && raw[pos + 1] == '(' && raw[pos + 2] == '(' && BashLexer.IsArithSub(raw, pos))
            {
                pos = ParseArithSub(raw, pos, parts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '(')
            {
                pos = ParseCommandSub(raw, pos, parts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '\'')
            {
                pos = ParseAnsiCQuoted(raw, pos, parts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '"')
            {
                // Locale-translation quoting $"...": with no message catalog this
                // is identical to a plain double-quoted string. Bash strips the $;
                // drop it and parse the body as double-quoted (skip the $, parse
                // from the opening "). Without this, the $ was kept as a literal.
                pos = ParseDoubleQuoted(raw, pos + 1, parts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '{')
            {
                pos = ParseBracedVar(raw, pos, parts, assignmentValue: assignmentValue);
            }
            else if (c == '$' && pos + 1 < len && IsVarStart(raw[pos + 1]))
            {
                pos = ParseSimpleVar(raw, pos, parts);
            }
            else if (c == '`')
            {
                pos = ParseBacktickCommandSub(raw, pos, parts);
            }
            else if (IsProcessSubStart(raw, pos))
            {
                pos = ParseProcessSub(raw, pos, parts);
            }
            else if (IsGlobStart(raw, pos))
            {
                pos = ParseGlob(raw, pos, parts);
            }
            else if (IsBraceExpansionStart(raw, pos))
            {
                if (braceExpansion)
                {
                    pos = ParseBraceExpansion(raw, pos, parts);
                }
                else
                {
                    parts.Add(new WordPart.Literal("{"));
                    pos++;
                }
            }
            else
            {
                pos = ParseBareLiteral(raw, pos, parts);
            }
        }

        return parts.ToImmutable();
    }

    /// <summary>
    /// Decompose an assignment value (RHS of <c>NAME=...</c>). bash expands a
    /// tilde at the START of the value AND after each UNQUOTED <c>:</c> (so
    /// <c>PATH=~/bin:~/x</c> -> <c>$HOME/bin:$HOME/x</c>) — but NOT in ordinary
    /// command words (<c>echo a:~b</c> stays literal). Split on top-level
    /// (unquoted, unsubstituted) <c>:</c> and decompose each segment with
    /// <see cref="DecomposeWord"/> (which handles a leading <c>~</c>), rejoining
    /// with a literal <c>:</c>. A value with no top-level <c>:</c> takes the plain
    /// <see cref="DecomposeWord"/> path (no behavior change).
    /// </summary>
    internal static ImmutableArray<WordPart> DecomposeAssignmentValue(string raw)
    {
        var segments = SplitOnUnquotedColon(raw);
        if (segments.Count <= 1)
            return DecomposeWord(raw, assignmentValue: true);

        var parts = ImmutableArray.CreateBuilder<WordPart>();
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0)
                parts.Add(new WordPart.Literal(":"));
            parts.AddRange(DecomposeWord(segments[i], assignmentValue: true));
        }
        return parts.ToImmutable();
    }

    /// <summary>
    /// Split on <c>:</c> that is not inside single/double quotes, <c>$(...)</c>,
    /// <c>${...}</c>, or backticks — the PATH-style segment boundaries of an
    /// assignment value. Quoted/substituted spans are copied verbatim.
    /// </summary>
    private static List<string> SplitOnUnquotedColon(string raw)
    {
        var segments = new List<string>();
        var sb = new System.Text.StringBuilder();
        int i = 0, len = raw.Length;
        while (i < len)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < len)
            {
                sb.Append(c).Append(raw[i + 1]);
                i += 2;
                continue;
            }
            // Quoted and substituted spans are atomic, with the lexer's own extents (a nested `"` in
            // `X="$(grep "a:b" f)"`, the `}` of `${u:-{a:b}`, a `:` in `${x//:/-}` neither end nor split them).
            int atomEnd = c switch
            {
                '\'' => BashLexer.ScanSingleQuoted(raw, i),
                '"' => BashLexer.ScanDoubleQuoted(raw, i),
                '`' => BashLexer.ScanBacktick(raw, i),
                '$' when i + 1 < len && raw[i + 1] == '(' => BashLexer.ScanDollarParen(raw, i),
                '$' when i + 1 < len && raw[i + 1] == '{' => BashLexer.ScanDollarBrace(raw, i),
                '$' when i + 1 < len && raw[i + 1] == '\'' => BashLexer.ScanAnsiCQuoted(raw, i, out _),
                _ => -1,
            };
            if (atomEnd >= 0)
            {
                sb.Append(raw, i, atomEnd - i);
                i = atomEnd;
                continue;
            }
            if (c == ':')
            {
                segments.Add(sb.ToString());
                sb.Clear();
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        segments.Add(sb.ToString());
        return segments;
    }

    private static int ParseSingleQuoted(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        // The lexer's scanner decides where the quote ends (one scanner per construct).
        int end = BashLexer.ScanSingleQuoted(raw, pos, out bool closed);
        parts.Add(new WordPart.SingleQuoted(raw[(pos + 1)..(closed ? end - 1 : end)]));
        return end;
    }

    /// <summary>
    /// Parse an ANSI-C quoted string <c>$'...'</c>. The raw inner text (with
    /// backslash escapes still intact) is captured; the closing quote is the first
    /// unescaped <c>'</c>. Escape expansion happens in the emitter.
    /// </summary>
    private static int ParseAnsiCQuoted(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        int end = BashLexer.ScanAnsiCQuoted(raw, pos, out bool closed);
        parts.Add(new WordPart.AnsiCQuoted(raw[(pos + 2)..(closed ? end - 1 : end)]));
        return end;
    }

    private static int ParseDoubleQuoted(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos++; // skip opening "
        var innerParts = ImmutableArray.CreateBuilder<WordPart>();
        int len = raw.Length;

        while (pos < len && raw[pos] != '"')
        {
            char c = raw[pos];

            if (c == '\\' && pos + 1 < len)
            {
                char next = raw[pos + 1];
                // Inside double quotes, only these characters are special after backslash:
                // $ ` " \ newline
                if (next == '\n')
                {
                    pos += 2;   // line continuation: the pair is removed ("a\<nl>b" is "ab")
                }
                else if (next == '\r' && pos + 2 < len && raw[pos + 2] == '\n')
                {
                    pos += 3;
                }
                else if (next is '$' or '`' or '"' or '\\')
                {
                    innerParts.Add(new WordPart.Literal(next.ToString()));
                    pos += 2;
                }
                else
                {
                    // Backslash is literal when not before a special char
                    innerParts.Add(new WordPart.Literal("\\"));
                    pos++;
                }
            }
            else if (c == '$' && pos + 2 < len && raw[pos + 1] == '(' && raw[pos + 2] == '(' && BashLexer.IsArithSub(raw, pos))
            {
                pos = ParseArithSub(raw, pos, innerParts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '(')
            {
                pos = ParseCommandSub(raw, pos, innerParts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '{')
            {
                pos = ParseBracedVar(raw, pos, innerParts, inDoubleQuote: true);
            }
            else if (c == '$' && pos + 1 < len && IsVarStart(raw[pos + 1]))
            {
                pos = ParseSimpleVar(raw, pos, innerParts);
            }
            else if (c == '`')
            {
                pos = ParseBacktickCommandSub(raw, pos, innerParts, inDoubleQuote: true);
            }
            else
            {
                int start = pos;
                while (pos < len && raw[pos] != '"' && raw[pos] != '\\' && raw[pos] != '$' && raw[pos] != '`')
                    pos++;
                if (pos == start)
                {
                    // A '$' or '`' that did not form an expansion — a trailing '$' at EOF, or
                    // '$' not followed by a var/'('/'{'. The scan above stops on it without
                    // consuming it, so emit it literally and ADVANCE; otherwise the outer loop
                    // spins forever on an unterminated `"$` (garbage-fuzz find: minimal hang "$).
                    innerParts.Add(new WordPart.Literal(raw[pos].ToString()));
                    pos++;
                }
                else
                {
                    innerParts.Add(new WordPart.Literal(raw[start..pos]));
                }
            }
        }

        if (pos < len)
            pos++; // skip closing "

        parts.Add(new WordPart.DoubleQuoted(innerParts.ToImmutable()));
        return pos;
    }

    private static int ParseSimpleVar(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos++; // skip $
        int start = pos;
        // Special single-char variables: $? $! $# $$ $_ $@ $* $- $0-$9
        // `_` is special ONLY as a lone `$_` (last arg). `$_cc_bin` is the ordinary
        // variable `_cc_bin` — bash names match [A-Za-z_][A-Za-z0-9_]* — so when `_` is
        // followed by another name char, fall through to the named-variable scan below.
        if (pos < raw.Length && raw[pos] is '?' or '!' or '#' or '$' or '@' or '*' or '-'
            or (>= '0' and <= '9'))
        {
            parts.Add(new WordPart.SimpleVarSub(raw[pos].ToString()));
            return pos + 1;
        }
        if (pos < raw.Length && raw[pos] == '_'
            && !(pos + 1 < raw.Length && IsVarChar(raw[pos + 1])))
        {
            parts.Add(new WordPart.SimpleVarSub("_"));
            return pos + 1;
        }
        // Named variable: letter/underscore followed by alnum/underscore
        while (pos < raw.Length && IsVarChar(raw[pos]))
            pos++;
        if (pos > start)
            parts.Add(new WordPart.SimpleVarSub(raw[start..pos]));
        return pos;
    }

    /// <summary>The <c>${…}</c> at [<paramref name="dollar"/>, <paramref name="close"/>] with each <c>$'…'</c> replaced
    /// by its ANSI-C value, or null when it holds none. <c>\x</c>, <c>$( )</c> and backtick spans are copied as written.</summary>
    private static string? TranslateAnsiCQuotes(string raw, int dollar, int close)
    {
        if (raw.IndexOf("$'", dollar, close - dollar, StringComparison.Ordinal) < 0) return null;
        System.Text.StringBuilder? sb = null;
        int copied = dollar;
        for (int i = dollar + 2; i < close;)
        {
            char c = raw[i];
            int end = c == '\\' ? i + 2
                : c == '`' ? BashLexer.ScanBacktick(raw, i)
                : c == '$' && i + 1 < close && raw[i + 1] == '(' ? BashLexer.ScanDollarParen(raw, i)
                : i + 1;
            if (c == '$' && i + 1 < close && raw[i + 1] == '\'')
            {
                end = BashLexer.ScanAnsiCQuoted(raw, i, out bool closed);
                if (closed && end <= close)
                {
                    sb ??= new System.Text.StringBuilder(close - dollar + 1);
                    sb.Append(raw, copied, i - copied).Append(PsEmitter.ExpandAnsiCEscapes(raw[(i + 2)..(end - 1)]));
                    copied = end;
                }
            }
            i = end;
        }
        if (sb is null) return null;
        return sb.Append(raw, copied, close + 1 - copied).ToString();
    }

    // Inside "...", a backslash in a ${v-WORD} operand escapes only $ ` " \ newline (`"${u-\z}"` is `\z`).
    // Doubling every other backslash lets DecomposeWord's unquoted rule give the same result.
    private static string KeepNonSpecialBackslashes(string val)
    {
        // No tilde expansion inside double quotes: `"${u:-~}"` is a literal ~.
        // (A backslash keeps DecomposeWord from reading the leading ~ as a tilde prefix.)
        string lead = val.StartsWith('~') ? "\\" : "";
        if (val.IndexOf('\\') < 0 && val.IndexOf('\'') < 0) return lead + val;
        var sb = new System.Text.StringBuilder(val.Length + 4);
        for (int i = 0; i < val.Length; i++)
        {
            char c = val[i];
            if (c == '\\' && i + 1 < val.Length)
            {
                char next = val[i + 1];
                if (next == '\n') { /* line continuation: removed */ }
                else if (next is '$' or '`' or '"' or '\\' or '}') sb.Append(c).Append(next);
                else sb.Append("\\\\").Append(next);
                i++;
            }
            // Inside "…" a single quote is an ordinary character: "${u:-'a'}" is the text 'a' with its quotes. (A `$'…'`
            // there was translated first, ParseBracedVar; one left here is a here-document's text, `$'a'` as written.)
            else if (c == '\'')
                sb.Append("\\'");
            else if (c == '$' && i + 1 < val.Length && val[i + 1] == '(')
            {
                int end = BashLexer.ScanBalancedParens(val, i + 2, 1);
                sb.Append(val, i, end - i);
                i = end - 1;
            }
            else sb.Append(c);
        }
        return lead + sb.ToString();
    }

    /// <summary>
    /// The index of the `]` closing the subscript whose `[` is at <paramref name="open"/>, or -1: the lexer's subscript
    /// extent (<see cref="BashLexer.SubscriptClose"/>, the one the `${…}` extent uses), so `["${a[@]}"]`, `[a[1]]`,
    /// `["]"]` and ``[`echo ]`]`` close at their last `]`.
    /// </summary>
    internal static int FindSubscriptClose(string s, int open) => BashLexer.SubscriptClose(s, open);

    /// <summary>The <c>]</c> closing the subscript at <paramref name="open"/> of a <c>${…}</c> that ends before
    /// <paramref name="len"/>, or <paramref name="len"/> when it does not close there (the subscript then holds the rest).</summary>
    private static int SubscriptCloseWithin(string raw, int open, int len)
        => FindSubscriptClose(raw, open) is var close and > 0 && close < len ? close : len;

    /// <summary>
    /// A <c>${…}</c> of a here-document body: its operand words read as inside double quotes (a <c>'</c> is text, a
    /// backslash escapes only <c>$ ` " \</c>) but a <c>$'…'</c> is NOT translated (bash 5.2: <c>${u:-$'a'}</c> there is
    /// the text <c>$'a'</c>).
    /// </summary>
    internal static ImmutableArray<WordPart> DecomposeHereDocBracedVar(string text)
    {
        var parts = ImmutableArray.CreateBuilder<WordPart>();
        int end = ParseBracedVar(text, 0, parts, inDoubleQuote: true, ansiCQuotes: false);
        if (end < text.Length) parts.Add(new WordPart.Literal(text[end..]));
        return parts.ToImmutable();
    }

    private static int ParseBracedVar(string raw, int pos, ImmutableArray<WordPart>.Builder parts, bool inDoubleQuote = false,
        bool assignmentValue = false, bool ansiCQuotes = true)
    {
        // ${x:-${x:-…}} recurses through the argument word: bounded (NestingGuard).
        using var nesting = NestingGuard.Enter();
        // The expansion's extent is the lexer's (ONE scanner, BashLexer.DollarBraceClose): every branch below reads
        // inside [pos, close] only, so no operand can run past the `}` that bash closes on into the following text.
        int extentClose = BashLexer.DollarBraceClose(raw, pos);
        // shopt extquote (on by default): inside "…" bash translates each `$'…'` of a `${…}` while it reads it, and the
        // translated TEXT is what expands: `"${u:-$'a\x41'}"` is aA, `"${u:-$'\x24x'}"` is the value of x, and
        // `"${m[$'\x5d']}"` reads `${m[]]}`, a bad substitution. A here-document body does not translate.
        if (inDoubleQuote && ansiCQuotes && extentClose > pos && TranslateAnsiCQuotes(raw, pos, extentClose) is { } translated)
        {
            int end = ParseBracedVar(translated, 0, parts, inDoubleQuote: true, assignmentValue, ansiCQuotes: false);
            if (end < translated.Length) parts.Add(new WordPart.Literal(translated[end..]));
            return extentClose + 1;
        }
        int len = extentClose < 0 ? raw.Length : extentClose + 1;
        pos += 2; // skip ${

        // Braced special parameters: ${#} ${@} ${*} ${?} ${$} ${!} ${-}. These collide
        // with the length (${#var}) / keys (${!arr[@]}) branches below, so detect the
        // bare single-char-then-`}` form FIRST and map it like the equivalent $# / $@ /
        // $! (emitted via the null-suffix BracedVarSub -> EmitSimpleVar). Multi-digit
        // positionals ${10}, ${11} need no special-case here: the name-read consumes the
        // digits and EmitSimpleVar maps an all-digit name to a positional parameter.
        if (pos + 1 < len && raw[pos + 1] == '}' && raw[pos] is '#' or '@' or '*' or '?' or '$' or '!' or '-')
        {
            string special = raw[pos].ToString();
            pos += 2; // skip the special char and the closing }
            parts.Add(new WordPart.BracedVarSub(special, null));
            return pos;
        }

        // ${!arr[@]} or ${!arr[*]} -> array keys operator. `${!:+pid}` / `${!-none}` are the `!` PARAMETER (the last job's
        // pid) with an operator: no name can start with an operator character (IsSpecialParameterWithOperator).
        if (pos < len && raw[pos] == '!' && !IsSpecialParameterWithOperator(raw, pos))
        {
            pos++; // skip !
            if (pos + 1 < len && raw[pos] == '#' && raw[pos + 1] == '}')
            {
                pos += 2; // ${!#}: the value of the variable named by $# = the last positional parameter
                parts.Add(new WordPart.BracedVarSub("#", "!indirect"));
                return pos;
            }
            int nameStart = pos;
            while (pos < len && IsVarChar(raw[pos]))
                pos++;
            string name = raw[nameStart..pos];

            // ${!a[1]}: indirection through ONE array element (`a[1]` binds first, there is no "(!a)[1]"). Only `[@]`/`[*]`
            // keep the keys meaning. The synthetic name carries the subscript, so every operator form below sees `a[1]`.
            if (name.Length > 0 && pos < len && raw[pos] == '[')
            {
                int close = FindSubscriptClose(raw, pos);
                if (close > pos + 1 && close + 1 < len && raw[pos + 1] is not ('@' or '*' or ']'))
                {
                    name = raw[nameStart..(close + 1)];
                    pos = close + 1;
                }
                // ${!a[@]-d} / ${!a[*]:-d}: with an operator after it, `[@]` is no longer the keys form but indirection
                // through the joined elements (bash 5: "x y: invalid variable name" for a=(x y)).
                else if (close == pos + 2 && close + 1 < len && raw[pos + 1] is '@' or '*'
                    && raw[close + 1] is ':' or '-' or '=' or '+' or '?' or '%' or '#' or '/' or '^' or ',' or '@')
                {
                    name = raw[nameStart..(close + 1)];
                    pos = close + 1;
                }
            }

            // ${!prefix*} / ${!prefix@} — names of variables whose name starts
            // with `prefix` (distinct from the ${!arr[@]} keys form). The `*`/`@`
            // comes immediately after the name with no `[`. Without this it fell
            // through and mis-emitted $prefix.Keys.
            // ${!ref OP …}: an operator after the name applies to the indirect value. Re-parse `${ref OP …}` and
            // tag its suffix so the emitter swaps ref's value for the value of the variable it names.
            if (name.Length > 0 && pos < len && (raw[pos] is ':' or '-' or '=' or '+' or '?' or '%' or '#' or '/' or '^' or ','
                || (raw[pos] == '@' && pos + 1 < len && raw[pos + 1] != '}')))
            {
                var inner = ImmutableArray.CreateBuilder<WordPart>();
                string synthetic = "${" + name + raw[pos..len];   // `${a[1]:-d}` keeps the element
                int end = ParseBracedVar(synthetic, 0, inner);
                int elemOpen = name.IndexOf('[');
                string baseName = elemOpen > 0 ? name[..elemOpen] : name;
                string elemPrefix = elemOpen > 0 ? name[elemOpen..] : "";   // `[1]` of `${!a[1]:-d}`: the suffix then carries it before the operator
                if (inner.Count == 1 && inner[0] is WordPart.BracedVarSub { Suffix: { } rawSfx } innerBvs && innerBvs.Name == baseName
                    && rawSfx.StartsWith(elemPrefix, StringComparison.Ordinal))
                {
                    string innerSfx = rawSfx[elemPrefix.Length..];
                    parts.Add(new WordPart.BracedVarSub(name, "!indirect" + innerSfx, innerBvs.ArgWord));
                    return pos + (end - (2 + name.Length));
                }
            }

            // ${!p@} / ${!p*}: the names that start with p. Quoted, `@` is one word per name and `*` one word joined with the
            // first IFS character, so the two stay apart ("!names" / "!names*").
            if (pos < len && (raw[pos] == '*' || raw[pos] == '@'))
            {
                bool star = raw[pos] == '*';
                pos++; // skip * or @
                if (pos < len && raw[pos] == '}')
                    pos++; // skip }
                parts.Add(new WordPart.BracedVarSub(name, star ? "!names*" : "!names"));
                return pos;
            }

            // ${!name}: indirect expansion — the value of the variable NAMED by $name (not its array keys).
            if (pos < len && raw[pos] == '}')
            {
                pos++; // skip }
                parts.Add(new WordPart.BracedVarSub(name, "!indirect"));
                return pos;
            }

            // ${!a[@]} / ${!a[*]}: the keys; `*` joins them in quotes, as above.
            string keysSuffix = "![@]";
            if (pos < len && raw[pos] == '[')
            {
                if (pos + 2 < len && raw[pos + 1] == '*' && raw[pos + 2] == ']') keysSuffix = "![*]";
                pos = SubscriptCloseWithin(raw, pos, len);
                if (pos < len)
                    pos++; // skip ]
            }
            if (pos < len && raw[pos] == '}')
                pos++; // skip }
            parts.Add(new WordPart.BracedVarSub(name, keysSuffix));
            return pos;
        }

        // ${#@} / ${#*}: the number of positional parameters, i.e. $#.
        if (pos + 2 < len && raw[pos] == '#' && raw[pos + 1] is '@' or '*' && raw[pos + 2] == '}')
        {
            parts.Add(new WordPart.BracedVarSub("#", null));
            return pos + 3;
        }
        // ${##} is the length of $#; ${###} ${##2} ${###2} are $# with a prefix-strip operator (# or ##) -- the `#` parameter, not
        // the length operator applied to a variable named `#`.
        if (pos + 1 < len && raw[pos] == '#' && raw[pos + 1] == '#')
        {
            int hashClose = extentClose;
            if (hashClose > 0)
            {
                string rest = raw[(pos + 1)..hashClose];
                parts.Add(new WordPart.BracedVarSub("#", rest.Length == 1 ? "#L" : rest));
                return hashClose + 1;
            }
        }
        // ${#!} ${#$} ${#?} ${#-}: the length of a special parameter's value (bash: ${#-} is the length of $-, while
        // ${#-y} is $# with a default).
        if (pos + 2 < len && raw[pos] == '#' && raw[pos + 1] is '!' or '$' or '?' or '-' && raw[pos + 2] == '}')
        {
            parts.Add(new WordPart.BracedVarSub(raw[pos + 1].ToString(), "#L"));
            return pos + 3;
        }
        // ${#VAR} or ${#arr[@]} -> length operator; ${#:+n} / ${#-y} / ${#%1} are the `#` PARAMETER with an operator.
        if (pos < len && raw[pos] == '#' && !IsSpecialParameterWithOperator(raw, pos))
        {
            pos++; // skip #
            int nameStart = pos;
            while (pos < len && IsVarChar(raw[pos]))
                pos++;
            string name = raw[nameStart..pos];

            // Check for array subscript: ${#arr[@]} or ${#arr[*]}
            string lengthSuffix = "#L"; // "#L" = length; a bare "#" suffix is an EMPTY strip pattern (${v#})
            if (pos < len && raw[pos] == '[')
            {
                int subStart = pos;
                pos = SubscriptCloseWithin(raw, pos, len);
                if (pos < len)
                    pos++; // skip ]
                lengthSuffix = "#L" + raw[subStart..pos]; // "#L[" = length of an element/array; "#[" is a strip pattern
            }

            if (name.Length > 0 && pos < len && raw[pos] != '}')
            {
                // ${#x-default}: no operator may follow the length operator. bash reports 'bad substitution' when it runs.
                int end = len;
                SawBadSubstitution = true; parts.Add(new WordPart.BracedVarSub(name, "#BAD:" + raw[Math.Max(0, nameStart - 3)..end]));
                return end;
            }

            if (pos < len && raw[pos] == '}')
                pos++; // skip }
            parts.Add(new WordPart.BracedVarSub(name, lengthSuffix));
            return pos;
        }

        // Read variable name.
        //
        // `@` / `*` are NOT var chars, so a positional-list expansion carrying an
        // OPERATOR — `${@:1}`, `${@: -1}`, `${@:1:2}`, `${*:2}` — read an EMPTY name
        // here and emitted the bare `$env:`, which is not valid PowerShell ("':' was
        // not followed by a valid variable name character") and broke the whole file.
        // (The bare `${@}` / `${*}` forms are handled by the special-parameter branch
        // above; only the with-operator forms reach this point.) Take the sigil as the
        // name so the emitter's positional-slice path can see it.
        // The other special parameters take operators the same way: ${?:-q} ${$:+pp} ${-:+i} ${!-none} ${#:-0}.
        int varStart = pos;
        if (pos < len && raw[pos] is '@' or '*' || IsSpecialParameterWithOperator(raw, pos))
            pos++;
        else
            while (pos < len && IsVarChar(raw[pos]))
                pos++;
        string varName = raw[varStart..pos];
        if (varName.Length == 0 && pos < len && raw[pos] is '%' or '/' or '}' or ' ' or '\t' or '\n' or '|')
        {
            // ${%}, ${}, ${/x}: no name at all. bash reports 'bad substitution' when it runs. So do the bash 5.3
            // command substitutions `${ cmd;}` and `${|cmd;}`, which bash 5.2 does not have.
            int badEnd = len;
            SawBadSubstitution = true; parts.Add(new WordPart.BracedVarSub(varName, "#BAD:" + raw[Math.Max(0, varStart - 2)..badEnd]));
            return badEnd;
        }

        // Check for array subscript: ${arr[0]}, ${arr[@]}, ${arr[key]}, and the
        // subscript-PLUS-operator forms ${arr[@]:1:2}, ${arr[0]##*/}, ${arr[i]:-x}.
        if (pos < len && raw[pos] == '[')
        {
            int subStart = pos;
            // `${m["${a[@]}"]}` / `${a[a[1]]}`: a quoted span, a nested `${…}` and a nested `[…]` do not end the
            // subscript at their `]`.
            pos = SubscriptCloseWithin(raw, pos, len);
            if (pos < len)
                pos++; // skip ]
            string subscript = raw[subStart..pos];

            // ${a[]}: an empty subscript is bash's 'bad substitution' (it read element 0).
            if (subscript == "[]" || (pos < len && raw[pos] != '}' && ":-=+?@%#/^,".IndexOf(raw[pos]) < 0))
            {
                // ${a[0][0]}, ${a[0]x}: only an operator may follow the subscript. bash reports 'bad substitution' when it runs.
                int badEnd = len;
                SawBadSubstitution = true; parts.Add(new WordPart.BracedVarSub(varName, "#BAD:" + raw[Math.Max(0, varStart - 2)..badEnd]));
                return badEnd;
            }

            // A suffix operator may follow the subscript (slice / removal / default
            // applied to the indexed or @-expanded value). Capture everything up to
            // the matching '}' as part of the suffix so EmitBracedVar can apply it —
            // previously this branch returned early and the operator was dropped.
            ImmutableArray<WordPart>? elemArgWord = null;
            if (pos < len && raw[pos] != '}')
            {
                int opStart = pos;
                pos = extentClose < 0 ? len : extentClose;
                string tail = raw[opStart..pos];
                subscript += tail;

                // ${arr[i]:-$0}: a default / alternative / assigned word is an expandable WORD, as for a plain
                // variable (see below); left raw, `$0` and `$(cmd)` in it came out literally.
                int opLen = tail.Length >= 2 && tail[0] == ':' && tail[1] is '-' or '=' or '+' or '?' ? 2
                    : tail.Length >= 1 && tail[0] is '-' or '=' or '+' or '?' ? 1 : 0;
                if (opLen > 0 && IsWordBearingBracedOp(tail[..opLen]))
                    elemArgWord = DecomposeWord(tail[opLen..]);
            }

            if (pos < len && raw[pos] == '}')
                pos++; // skip }
            parts.Add(new WordPart.BracedVarSub(varName, subscript, elemArgWord));
            return pos;
        }

        // Check for suffix operator or closing brace
        string? suffix = null;
        ImmutableArray<WordPart>? argWord = null;
        if (pos < len && raw[pos] != '}')
        {
            // Read operator: :-, :=, :+, :?, :offset:len, %, %%, #, ##, /, //, ^^, ,,, ^, ,
            int opStart = pos;
            if (pos < len && raw[pos] == ':' && pos + 1 < len && raw[pos + 1] is '-' or '=' or '+' or '?')
            {
                pos += 2;
            }
            else if (pos < len && raw[pos] == ':' && pos + 1 < len && (char.IsDigit(raw[pos + 1]) || raw[pos + 1] == '-'))
            {
                pos++; // just the leading ':', rest goes into val
            }
            else if (pos < len && raw[pos] is '-' or '=' or '+' or '?')
            {
                pos++; // colon-less unset-only operator: ${VAR-w} ${VAR=w} ${VAR+w} ${VAR?msg}
            }
            else if (pos < len && raw[pos] == '@')
            {
                pos++; // transform operator: ${VAR@Q} ${VAR@E} ${VAR@P} ${VAR@A} ${VAR@U} ...
            }
            else if (pos < len && raw[pos] == '%')
            {
                pos++;
                if (pos < len && raw[pos] == '%')
                    pos++;
            }
            else if (pos < len && raw[pos] == '#')
            {
                pos++;
                if (pos < len && raw[pos] == '#')
                    pos++;
            }
            else if (pos < len && raw[pos] == '/')
            {
                pos++;
                if (pos < len && raw[pos] == '/')
                    pos++;
            }
            else if (pos < len && raw[pos] == '^')
            {
                pos++;
                if (pos < len && raw[pos] == '^')
                    pos++;
            }
            else if (pos < len && raw[pos] == ',')
            {
                pos++;
                if (pos < len && raw[pos] == ',')
                    pos++;
            }
            string op = raw[opStart..pos];
            if (op.Length == 0 && raw[pos] != ':')
            {
                // ${a&}: no operator can start here. bash reports 'bad substitution' when it runs.
                int end = len;
                SawBadSubstitution = true; parts.Add(new WordPart.BracedVarSub(varName, "#BAD:" + raw[Math.Max(0, varStart - 2)..end]));
                return end;
            }

            // The value runs to the `}` that closes the expansion (DollarBraceClose): `${x:-"}"}` closes at the LAST
            // `}`, `${u:-{a,b}}` at the FIRST (a bare `{` does not nest).
            int valStart = pos;
            pos = extentClose < 0 ? len : extentClose;
            string val = raw[valStart..pos];
            suffix = op + val;

            // Word-bearing operators expand their argument (default / alternative /
            // assigned value / error message). Decompose it NOW so $var, $(cmd), and
            // "$@" recurse through normal word emission instead of surviving as a raw
            // literal slice — the ${1+"$@"} family bug. Pattern-removal / slice / case
            // / @-transform operators take no expandable word argument: leave ArgWord
            // null so the emitter keeps the raw-slice path.
            // In an assignment value the word of a default / alternative operator is an assignment value too: a tilde
            // after an unquoted `:` expands (`x=${undef-~:~}`). The assign operators (`${v=~:~}`) keep the plain reading (bash).
            if (IsWordBearingBracedOp(op))
                argWord = inDoubleQuote ? DecomposeWord(KeepNonSpecialBackslashes(val))
                    : assignmentValue && op is "-" or ":-" or "+" or ":+" ? DecomposeAssignmentValue(val)
                    : DecomposeWord(val);
        }

        if (pos < len && raw[pos] == '}')
            pos++; // skip }

        parts.Add(new WordPart.BracedVarSub(varName, suffix, argWord));
        return pos;
    }

    /// <summary>
    /// <c>raw[pos]</c> is a one-character special parameter (<c>! $ ? - #</c>) followed by an operator, so it is the
    /// parameter's NAME: <c>${!:+pid}</c>, <c>${?:-q}</c>, <c>${#:+n}</c>, <c>${$%1}</c>. The forms that mean something
    /// else keep their branches: <c>${!name}</c> / <c>${!#}</c> (indirection), <c>${#name}</c> / <c>${##}</c> (length).
    /// </summary>
    private static bool IsSpecialParameterWithOperator(string raw, int pos)
    {
        if (pos + 1 >= raw.Length || raw[pos] is not ('!' or '$' or '?' or '-' or '#')) return false;
        char next = raw[pos + 1];
        if (next is ':' or '=' or '+' or '%' or '/' or '^' or ',') return true;
        // `-`/`?` after `!` or `#` is an operator unless it is the whole body (`${#-}` / `${#?}` = a length, above).
        if (next is '-' or '?') return pos + 2 < raw.Length && raw[pos + 2] != '}' || raw[pos] is not ('#' or '!');
        // `${$#x}` / `${?#1}` / `${-#h}`: prefix removal on the parameter (`${!#}` and `${##}` are other forms).
        return next == '#' && raw[pos] is '$' or '?' or '-';
    }

    /// <summary>
    /// True for the parameter-expansion operators whose argument is an expandable WORD
    /// (Oils <c>suffix_op.Unary</c>): default <c>:-</c>/<c>-</c>, assign <c>:=</c>/<c>=</c>,
    /// alternative <c>:+</c>/<c>+</c>, error message <c>:?</c>/<c>?</c>. These — and ONLY these —
    /// get their argument decomposed into word parts so embedded expansions are emitted, not
    /// copied verbatim. Pattern removal (<c>#</c> <c>##</c> <c>%</c> <c>%%</c>), replace (<c>/</c>
    /// <c>//</c>), slice (<c>:</c>N), case (<c>^</c> <c>,</c>), and <c>@</c>-transforms are excluded.
    /// </summary>
    private static bool IsWordBearingBracedOp(string op) =>
        op is ":-" or ":=" or ":+" or ":?" or "-" or "=" or "+" or "?";

    private static int ParseArithSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        int start = pos + 3; // just inside $((
        // Reuse the lexer's arithmetic scanner so boundary logic lives in ONE
        // place (see BashLexer's region scanners). It returns the offset one
        // past the closing )).
        int end = BashLexer.ScanArith(raw, pos);
        int innerEnd = end >= start + 2 && raw[end - 1] == ')' && raw[end - 2] == ')' ? end - 2 : end;
        string expr = raw[start..innerEnd];
        // `$((1 + \<newline>2))`: a line continuation is removed before the expression is read.
        if (expr.Contains("\\\n", StringComparison.Ordinal))
            expr = expr.Replace("\\\r\n", "", StringComparison.Ordinal).Replace("\\\n", "", StringComparison.Ordinal);

        parts.Add(new WordPart.ArithSub(BashArithmeticParser.ParseOrDefer(expr)));
        return end;
    }

    private static int ParseCommandSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        int start = pos + 2; // just inside $(
        // Quote-aware boundary via the lexer's shared scanner: a ')' inside a
        // string in the command sub must not close the region (e.g. $(grep ")" f)).
        int end = BashLexer.ScanBalancedParens(raw, start, 1);
        if (!(end > start && raw[end - 1] == ')'))
            throw new ParseException("unexpected EOF while looking for matching `)'", 1, 1, "CommandSub");
        int innerEnd = end > start && raw[end - 1] == ')' ? end - 1 : end;
        string inner = raw[start..innerEnd];

        var body = Parse(inner);
        parts.Add(new WordPart.CommandSub(body ?? new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty)));
        return end;
    }

    private static int ParseBacktickCommandSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts, bool inDoubleQuote = false)
    {
        int start = pos + 1;
        int end = BashLexer.ScanBacktick(raw, pos, out bool closed);   // the lexer's extent
        if (!closed)
            throw new ParseException("unexpected EOF while looking for matching ``'", 1, 1, "BacktickSub");
        string inner = raw[start..(end - 1)];
        pos = end;

        // bash strips ONE level of backslash before `\`, `$`, and `\` itself
        // inside backtick command substitution, before re-parsing the body.
        // Without this, `` `echo \`date\`` `` re-parsed the literal backslashes
        // and mis-parsed the nested substitution.
        // Inside double quotes \" loses its backslash too: "x `echo \"hi\"`" runs echo "hi".
        inner = UnescapeBacktickInner(inner, inDoubleQuote);
        // bash parses a backtick body only when the substitution runs: a syntax error in it (`echo \\"`, the
        // unescaped `echo \\\\"`) is reported then, the substitution is empty with status 2, and the command
        // holding it goes on. A $( ) body is parsed with the script, so its syntax error stays fatal.
        Command? body;
        try { body = Parse(inner); }
        catch (ParseException ex) when (ex.Rule is not ("nesting" or "breadth"))
        {
            body = BacktickSyntaxErrorBody(ex, inner);
        }
        parts.Add(new WordPart.CommandSub(body ?? new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty)));
        return pos;
    }

    /// <summary>
    /// The run-time stand-in for a backtick body that does not parse: bash's report on stderr
    /// (<c>t.sh: command substitution: line N: MSG</c>) and status 2, from the substitution's own shell. The name is the
    /// script's (<c>${BASH_SOURCE[0]:-$0}</c>: <c>bash</c> under <c>-c</c>) and N the outer line the substitution is on
    /// (<c>${LINENO:-1}</c>: the lexer resolves it to the substitution's word, where a plain <c>$LINENO</c> in this
    /// generated text would read its own line 1) plus the failing line's offset in the body. bash's line count has
    /// already passed the body's newlines when it parses it, so a multi-line body adds those too
    /// (<c>z=`&lt;newline&gt;fi`</c> on line 7 reports line 9).
    /// </summary>
    internal static Command? BacktickSyntaxErrorBody(ParseException ex, string body)
    {
        int offset = Math.Max(1, ex.Line) - 1 + body.AsSpan().Count('\n');
        string line = offset == 0 ? "${LINENO:-1}" : "$(( ${LINENO:-1} + " + offset + " ))";
        string message = ": " + ex.Message;
        return Parse("printf '%s\\n' \"${BASH_SOURCE[0]:-$0}\"': command substitution: line '\"" + line + "\"'"
            + message.Replace("'", "'\\''") + "' >&2; (exit 2)");
    }

    /// <summary>
    /// Strip one level of backslash-escaping inside a backtick command sub:
    /// <c>\\</c> -> <c>\</c>, <c>\`</c> -> <c>`</c>, <c>\$</c> -> <c>$</c>. Other
    /// backslashes are preserved (bash leaves them for the inner parse). Mirrors
    /// the bash rule applied before the backtick body is re-parsed.
    /// </summary>
    private static string UnescapeBacktickInner(string s, bool inDoubleQuote = false)
    {
        if (s.IndexOf('\\') < 0)
            return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length
                && (s[i + 1] == '\\' || s[i + 1] == '`' || s[i + 1] == '$' || (inDoubleQuote && s[i + 1] == '"')))
            {
                sb.Append(s[i + 1]);
                i++;
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    private static bool IsProcessSubStart(string raw, int pos) =>
        pos + 1 < raw.Length && raw[pos] is '<' or '>' && raw[pos + 1] == '(';

    private static int ParseProcessSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        bool isInput = raw[pos] == '<';
        int start = pos + 2; // just inside <( or >(
        // Quote-aware boundary via the lexer's shared scanner.
        int end = BashLexer.ScanBalancedParens(raw, start, 1);
        int innerEnd = end > start && raw[end - 1] == ')' ? end - 1 : end;
        string inner = raw[start..innerEnd];

        var body = Parse(inner);
        parts.Add(new WordPart.ProcessSub(body ?? new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty), isInput));
        return end;
    }

    /// <summary>True when the tilde prefix of <paramref name="raw"/> (the text after <c>~</c> up to the first <c>/</c>)
    /// holds a brace expansion: <c>~{/src,root}</c>, <c>~{a,b}x</c>.</summary>
    internal static bool TildePrefixHasBrace(string raw)
    {
        for (int i = 1; i < raw.Length && raw[i] != '/'; i++)
        {
            if (raw[i] is '\'' or '"' or '\\' or '$' or '`') return false;
            if (IsBraceExpansionStart(raw, i)) return true;
        }
        return false;
    }

    private static int ParseTilde(string raw, ImmutableArray<WordPart>.Builder parts)
    {
        int pos = 1; // skip ~
        int len = raw.Length;

        // Read optional username until '/' or end of word. A quoted or substituted character in the prefix (~'x',
        // ~"x", ~\x, ~$u, ~cmd) means bash does not expand the tilde at all: return 0 so the caller reads the
        // word as plain text.
        int start = pos;
        while (pos < len && raw[pos] != '/')
        {
            if (raw[pos] is '\'' or '"' or '\\' or '$' or '`') return 0;
            pos++;
        }

        string? user = pos > start ? raw[start..pos] : null;
        // ~user (not ~+ / ~- / ~N): the passwd database's home for that login (getpwnam) rides on the node, so a site
        // where bash leaves the tilde as written (an associative `[k]=~user`) still sees the TildeSub.
        string? home = user is not null && user is not ("+" or "-") ? PsBash.Core.Platform.UserHome.Lookup(user) : null;
        // ~CURRENTUSER with no passwd entry (Windows has no passwd database) is the home directory.
        if (home is null && user is not null && string.Equals(user, Environment.UserName, StringComparison.OrdinalIgnoreCase))
            user = null;
        // Consume the '/' separator so it doesn't appear in the following literal;
        // the emitter re-inserts a platform separator after a TildeSub that consumed one (see
        // PsEmitter.EmitWord / AppendTildeSeparator).
        // A resolved ~user is quoted text in every context (ResolveNamedTildes): its `/` stays in the following text.
        bool slash = home is null && pos < len && raw[pos] == '/';
        parts.Add(new WordPart.TildeSub(user, slash, home));
        if (slash)
            pos++;

        return pos;
    }

    private static int ParseBareLiteral(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        int start = pos;
        int len = raw.Length;
        while (pos < len)
        {
            char c = raw[pos];
            if (c is '\'' or '"' or '\\' or '`')
                break;
            // Every `$` form DecomposeWord reads ends the literal, `$"…"` too: `a$"*"` is `a*` (the `$` is quoting).
            if (c == '$' && pos + 1 < len && (IsVarStart(raw[pos + 1]) || raw[pos + 1] is '{' or '(' or '\'' or '"'))
                break;
            // Stop before glob characters so they get parsed separately.
            if (c is '*' or '?')
                break;
            if (c == '[' && ContainsBracketGlob(raw, pos))
                break;
            // Extglob: +( *( ?( !( @( preceded by nothing special.
            if ((c is '+' or '*' or '?' or '!' or '@') && pos + 1 < len && raw[pos + 1] == '(')
                break;
            // Brace expansion: {a,b,c} or {1..10}
            if (IsBraceExpansionStart(raw, pos))
                break;
            // Process substitution: <(...) or >(...)
            if (IsProcessSubStart(raw, pos))
                break;
            pos++;
        }
        if (pos > start)
            parts.Add(new WordPart.Literal(raw[start..pos]));
        return pos;
    }

    private static int ParseGlob(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        int len = raw.Length;
        char c = raw[pos];

        // Extglob: +(...) *(...) ?(...) !(...) @(...)
        if ((c is '+' or '*' or '?' or '!' or '@') && pos + 1 < len && raw[pos + 1] == '(')
            return ParseExtGlob(raw, pos, parts);

        // Simple * or ?
        if (c is '*' or '?')
        {
            parts.Add(new WordPart.GlobPart(c.ToString()));
            return pos + 1;
        }

        // Character class [...]
        if (c == '[')
        {
            // A class body containing an EXPANSION is not a class we can model.
            // bash expands first and globs second, so `echo [$x]` prints the value
            // of x wrapped in brackets. Swallowing the whole raw slice into a
            // GlobPart dropped the expansion entirely: the emitted pattern text
            // `[$x]` made PowerShell read `$x` as ITS OWN (undefined) variable, so
            // `x="a b"; echo [$x]` printed `[]` — total, silent data loss.
            //
            // Emit a plain literal `[` instead and let the ordinary word loop parse
            // the expansion and the closing `]`. The value is then preserved. (The
            // word is no longer treated as a glob — acceptable, since a bracket
            // expression that matches no file is a literal word in bash anyway.)
            if (ClassBodyHasExpansion(raw, pos))
            {
                parts.Add(new WordPart.Literal("["));
                return pos + 1;
            }

            int start = pos;
            int close = FindClassClose(raw, pos);
            pos = close < 0 ? len : close + 1;
            parts.Add(new WordPart.GlobPart(raw[start..pos]));
            return pos;
        }

        // Shouldn't reach here, but treat as literal.
        parts.Add(new WordPart.Literal(c.ToString()));
        return pos + 1;
    }

    /// <summary>
    /// True when the bracket expression starting at <paramref name="pos"/> (the
    /// <c>[</c>) contains a parameter or command substitution before its closing
    /// <c>]</c> — i.e. it is not a static character class. Returns false for an
    /// UNTERMINATED bracket too, so a stray <c>[</c> keeps its existing handling.
    /// </summary>
    private static bool ClassBodyHasExpansion(string raw, int pos)
    {
        int close = FindClassClose(raw, pos);
        if (close < 0) return false;       // unterminated
        return raw.AsSpan(pos + 1, close - pos - 1).IndexOfAny('$', '`') >= 0;
    }

    private static int ParseExtGlob(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        int start = pos;
        pos = BashLexer.ScanExtGlob(raw, pos + 2); // quote/escape-aware, same scan as the lexer
        parts.Add(new WordPart.GlobPart(raw[start..pos]));
        return pos;
    }

    /// <summary>
    /// Returns true if position <paramref name="pos"/> starts a bracket glob class
    /// (i.e. <c>[</c> followed eventually by <c>]</c>).
    /// </summary>
    private static bool ContainsBracketGlob(string raw, int pos) => FindClassClose(raw, pos) >= 0;

    /// <summary>
    /// A bracket expression with its quotes removed and each quoted character backslash-escaped, the pattern
    /// dialect's own literal form: <c>[a"]"b]</c> is the class {a, ], b} and becomes <c>[a\]b]</c>; <c>['a-z']</c>
    /// matches a, - or z and becomes <c>[\a\-\z]</c>. A class without quotes is returned unchanged. The GlobPart
    /// itself keeps the source text (an array literal's <c>['k']=v</c> key is read from it); a pathname pattern
    /// goes through this.
    /// </summary>
    internal static string UnquoteClass(string cls)
    {
        if (cls.AsSpan().IndexOfAny('\'', '"', '\\') < 0) return cls;
        var sb = new System.Text.StringBuilder(cls.Length + 4);
        for (int i = 0; i < cls.Length; i++)
        {
            char c = cls[i];
            // A class name's quotes and backslashes drop: `[[:punct\:]]` and `[[:"punct":]]` are the class [:punct:].
            if (c == '[' && i > 0 && i + 1 < cls.Length && cls[i + 1] == ':' && cls.IndexOf(":]", i + 2, StringComparison.Ordinal) is var nameEnd and >= 0)
            {
                sb.Append("[:");
                for (int k = i + 2; k < nameEnd; k++)
                    if (cls[k] is not ('\\' or '\'' or '"')) sb.Append(cls[k]);
                sb.Append(":]");
                i = nameEnd + 1;
                continue;
            }
            if (c == '\\' && i + 1 < cls.Length) { sb.Append(c).Append(cls[++i]); continue; }
            if (c == '\'')
            {
                int end = BashLexer.ScanSingleQuoted(cls, i);
                int bodyEnd = end > i + 1 && end <= cls.Length && cls[end - 1] == '\'' ? end - 1 : end;
                for (int k = i + 1; k < bodyEnd; k++) sb.Append('\\').Append(cls[k]);
                i = end - 1;
                continue;
            }
            if (c == '"')
            {
                int end = BashLexer.ScanDoubleQuoted(cls, i);
                int bodyEnd = end > i + 1 && end <= cls.Length && cls[end - 1] == '"' ? end - 1 : end;
                for (int k = i + 1; k < bodyEnd; k++)
                {
                    char q = cls[k];
                    // Inside double quotes a backslash escapes only $ ` " \ and newline.
                    if (q == '\\' && k + 1 < bodyEnd && cls[k + 1] is '$' or '`' or '"' or '\\') q = cls[++k];
                    sb.Append('\\').Append(q);
                }
                i = end - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The index of the <c>]</c> that closes the bracket expression opened at <paramref name="pos"/>, or -1 when it is
    /// unterminated. Only an UNQUOTED, unescaped <c>]</c> closes it: bash's <c>[hello"]"</c> is the literal word
    /// <c>[hello]</c>, not the class [hello]. Quoted text and <c>\x</c> are skipped whole; a leading <c>]</c> (after an
    /// optional <c>!</c>/<c>^</c>) is a member, and a nested <c>[:name:]</c> / <c>[.x.]</c> / <c>[=x=]</c> is one unit.
    /// </summary>
    private static int FindClassClose(string raw, int pos)
    {
        int i = pos + 1;
        int len = raw.Length;
        if (i < len && raw[i] is '!' or '^')
            i++;
        if (i < len && raw[i] == ']')
            i++;
        while (i < len)
        {
            char c = raw[i];
            if (c == '[' && i + 1 < len && raw[i + 1] is ':' or '.' or '=')
            {
                char marker = raw[i + 1];
                int nestedClose = raw.IndexOf($"{marker}]", i + 2, StringComparison.Ordinal);
                if (nestedClose >= 0)
                {
                    i = nestedClose + 2;
                    continue;
                }
            }
            if (c == '\\') { i += 2; continue; }
            if (c == '\'') { i = BashLexer.ScanSingleQuoted(raw, i); continue; }
            if (c == '"') { i = BashLexer.ScanDoubleQuoted(raw, i); continue; }
            if (c == ']')
                return i;
            i++;
        }
        return -1;
    }

    private static bool IsGlobStart(string raw, int pos)
    {
        char c = raw[pos];
        if (c is '*' or '?')
            return true;
        if (c == '[' && ContainsBracketGlob(raw, pos))
            return true;
        // Extglob: +( !( @( (and *( ?( already covered by * ? above when not followed by '(')
        if ((c is '+' or '!' or '@') && pos + 1 < raw.Length && raw[pos + 1] == '(')
            return true;
        return false;
    }

    /// <summary>
    /// True when position <paramref name="pos"/> starts a brace expansion: a <c>{</c> whose matching <c>}</c>
    /// (<see cref="BraceClose"/>) encloses a top-level <c>,</c> or is a sequence expression (<see cref="BraceSequence"/>).
    /// Any other pair is literal text while the rest of the word expands, as in bash: <c>{1..$n}</c> is <c>{1..3}</c>
    /// for n=3, <c>{1...3}</c> stays, <c>{x{a,b}y}</c> is <c>{xay} {xby}</c>.
    /// </summary>
    private static bool IsBraceExpansionStart(string raw, int pos)
    {
        if (pos >= raw.Length || raw[pos] != '{')
            return false;
        int close = BraceClose(raw, pos);
        if (close < 0)
            return false;
        string inner = raw[(pos + 1)..close];
        return HasTopLevelComma(inner) || BraceSequence.TryParse(inner, out _);
    }

    /// <summary>
    /// The index of the <c>}</c> matching the brace <c>{</c> at <paramref name="open"/>, or -1. Nested pairs count
    /// (<c>{{a,b}</c> has none: its first <c>{</c> is literal); quoted text, <c>\x</c>, <c>$( )</c>, <c>$'…'</c> and
    /// backticks are item text (<c>{a,$(echo },)}</c> is <c>a },</c>). A <c>${…}</c> is NOT taken by its expansion
    /// extent here: bash's brace scan (braces.c brace_gobbler) counts its braces as nesting, so <c>{a,${u:-{b}}</c> has
    /// no closing brace and stays literal (then <c>${u:-{b}</c> expands).
    /// </summary>
    private static int BraceClose(string raw, int open)
    {
        int depth = 0;
        for (int i = open + 1; i < raw.Length;)
        {
            if (IsBraceAtomStart(raw, i)) { i = SkipBraceAtom(raw, i); continue; }
            char c = raw[i];
            if (c == '{') depth++;
            else if (c == '}' && depth-- == 0) return i;
            i++;
        }
        return -1;
    }

    private static int ParseBraceExpansion(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        // IsBraceExpansionStart said yes: the pair closes and holds a top-level comma or a sequence expression.
        int close = BraceClose(raw, pos);
        if (close < 0) close = raw.Length;
        string inner = raw[(pos + 1)..close];
        int next = Math.Min(close + 1, raw.Length);

        // A numeric sequence stays a BracedRange node (assignment values print it back as written); its words are
        // BraceSequence's, the same expander a nested sequence uses.
        BraceSequence seq = default;
        bool isSeq = !HasTopLevelComma(inner) && BraceSequence.TryParse(inner, out seq);
        if (isSeq && !seq.IsChar
            && seq.Start is >= int.MinValue and <= int.MaxValue && seq.End is >= int.MinValue and <= int.MaxValue
            && seq.Increment is > int.MinValue and <= int.MaxValue)
        {
            parts.Add(new WordPart.BracedRange((int)seq.Start, (int)seq.End, seq.Width, (int)seq.Increment) { Source = raw[pos..next] });
            return next;
        }

        var expandedItems = ExpandBraceContent(inner);
        // A generated backtick is re-read as the start of a command substitution: alone at the end of its word it
        // passes through (`{Z..b..3}` ends in `), but with text after it (`-{z..A}-`) it never closes, a bad
        // substitution when the command runs (status 1).
        if (isSeq && seq.IsChar && next < raw.Length && expandedItems.Contains(BraceSequence.GeneratedBacktick))
        {
            SawBadSubstitution = true;
            parts.Add(new WordPart.BracedVarSub("", "#BAD:{" + inner + "}"));
            return next;
        }
        // Quote removal on each finished item (`{'a',b}`); `{"",a}`: quote removal leaves "" as it leaves a bare
        // `{,a}` slot, but bash keeps only the quoted one.
        var finished = expandedItems.ConvertAll(DequoteBraceItem);
        ImmutableArray<int>.Builder? quotedEmpty = null;
        for (int i = 0; i < finished.Count; i++)
            if (finished[i].Length == 0 && expandedItems[i].Length > 0)
                (quotedEmpty ??= ImmutableArray.CreateBuilder<int>()).Add(i);
        parts.Add(new WordPart.BracedTuple(ImmutableArray.CreateRange(finished))
        {
            QuotedEmpty = quotedEmpty?.ToImmutable() ?? default,
            Source = raw[pos..next],
        });
        return next;
    }

    /// <summary>Quote removal on a finished brace item (`{'a',b}`, `{a,\{x\}}`): the quotes and backslashes that
    /// protected a `,` or brace during splitting are not part of the word. Items with a `$` expansion keep their text.</summary>
    private static string DequoteBraceItem(string s)
    {
        if (s.IndexOfAny(['\'', '"', '\\']) < 0 || s.Contains('$') || s.Contains('`')) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length) sb.Append(s[++i]);
            else if (c is '\'' or '"')
            {
                int close = s.IndexOf(c, i + 1);
                if (close < 0) { sb.Append(c); continue; }
                sb.Append(s, i + 1, close - i - 1);
                i = close;
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns true when <paramref name="s"/> contains at least one comma at brace-depth 0.
    /// Used to determine whether a brace body might be a comma-list rather than a range.
    /// </summary>
    private static bool HasTopLevelComma(string s) => TopLevelCommas(s, null);

    /// <summary>A quoted span, <c>\x</c> escape, <c>$( )</c>, <c>$'…'</c> or backtick starts at <paramref name="i"/>: brace
    /// item text whose <c>,</c> / <c>{</c> / <c>}</c> are not brace syntax.</summary>
    private static bool IsBraceAtomStart(string s, int i) =>
        s[i] is '\'' or '"' or '\\' or '`' || (s[i] == '$' && i + 1 < s.Length && s[i + 1] is '(' or '\'');

    /// <summary>The index just past the atom <see cref="IsBraceAtomStart"/> found at <paramref name="i"/>, by the lexer's
    /// own scanners.</summary>
    private static int SkipBraceAtom(string s, int i) => s[i] switch
    {
        '\\' => Math.Min(s.Length, i + 2),
        '\'' => BashLexer.ScanSingleQuoted(s, i),
        '"' => BashLexer.ScanDoubleQuoted(s, i),
        '`' => BashLexer.ScanBacktick(s, i),
        _ => s[i + 1] == '(' ? BashLexer.ScanDollarParen(s, i) : BashLexer.ScanAnsiCQuoted(s, i, out _),
    };

    /// <summary>
    /// Splits <paramref name="s"/> on commas at brace-depth 0 only.
    /// Commas inside nested brace groups are not treated as separators.
    /// </summary>
    private static List<string> SplitTopLevelCommas(string s)
    {
        var result = new List<string>();
        TopLevelCommas(s, result);
        return result;
    }

    /// <summary>The top-level commas of a brace body: true when it has one; with <paramref name="items"/>, the text between
    /// them too. A nested <c>{…}</c> (a <c>${…}</c>'s braces included, bash brace_gobbler) is skipped whole by
    /// <see cref="BraceClose"/>, the one brace-depth counter; an unclosed <c>{</c> holds the rest of the body.</summary>
    private static bool TopLevelCommas(string s, List<string>? items)
    {
        bool found = false;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (IsBraceAtomStart(s, i)) { i = SkipBraceAtom(s, i) - 1; continue; }
            if (s[i] == '{')
            {
                int close = BraceClose(s, i);
                if (close < 0) break;
                i = close;
            }
            else if (s[i] == ',')
            {
                found = true;
                if (items is null) return true;
                items.Add(s[start..i]);
                start = i + 1;
            }
        }
        items?.Add(s[start..]);
        return found;
    }

    /// <summary>
    /// Recursively expands nested brace expansions within a single item string.
    /// For example, "b{1,2}" becomes ["b1", "b2"] and "a" stays ["a"].
    /// </summary>
    private static List<string> ExpandNestedBraces(string item)
    {
        // Find the first brace expansion start within the item.
        int braceStart = -1;
        for (int i = 0; i < item.Length; i++)
        {
            if (IsBraceAtomStart(item, i)) { i = SkipBraceAtom(item, i) - 1; continue; }
            // A `${…}` is one nesting level of item text (bash brace_gobbler), never a brace expansion: `{a,${x/ /,}}` is
            // `a` and `${x/ /,}`, and `{a,${x:-{b,c}}}` keeps `${x:-{b,c}}` whole.
            if (item[i] == '$' && i + 1 < item.Length && item[i + 1] == '{')
            {
                int close = BraceClose(item, i + 1);
                if (close < 0) break;
                i = close;
                continue;
            }
            if (item[i] == '{' && IsBraceExpansionStart(item, i))
            {
                braceStart = i;
                break;
            }
        }
        if (braceStart < 0)
            return new List<string> { item };

        int braceEnd = BraceClose(item, braceStart);
        string prefix = item[..braceStart];
        string braceContent = item[(braceStart + 1)..braceEnd];
        string suffix = item[(braceEnd + 1)..];

        var expansions = ExpandBraceContent(braceContent);
        var result = new List<string>();
        foreach (var exp in expansions)
        {
            result.AddRange(ExpandNestedBraces(prefix + exp + suffix));
            PsEmitter.CheckBraceWords(result.Count);
        }
        return result;
    }

    /// <summary>
    /// The items of a brace body that <see cref="IsBraceExpansionStart"/> accepted (without its braces): a sequence
    /// expression's words (<see cref="BraceSequence"/>, the one expander), else its top-level comma items, each with
    /// its own nested braces expanded. Items are still bash text (quote removal is the caller's).
    /// </summary>
    private static List<string> ExpandBraceContent(string inner)
    {
        if (!HasTopLevelComma(inner) && BraceSequence.TryParse(inner, out var seq))
            return seq.Words();
        var result = new List<string>();
        foreach (var item in SplitTopLevelCommas(inner))
        {
            result.AddRange(ExpandNestedBraces(item));
            PsEmitter.CheckBraceWords(result.Count);
        }
        return result;
    }

    /// <summary>Test seam for <c>BraceSequenceGuardTests</c>: the words brace expansion makes of one word's text, after
    /// quote removal (the parser's own nested-expansion path).</summary>
    internal static List<string> BraceExpandForTest(string word) => ExpandNestedBraces(word).ConvertAll(DequoteBraceItem);

    /// <summary>Test seam for <c>DollarBraceExtentGuardTests</c>: where the word parser stops reading the <c>${</c> at
    /// <paramref name="dollar"/> (one past its closing <c>}</c>).</summary>
    internal static int BracedVarEndForTest(string raw, int dollar, bool inDoubleQuote = false)
        => ParseBracedVar(raw, dollar, ImmutableArray.CreateBuilder<WordPart>(), inDoubleQuote);

    private static bool IsVarStart(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_'
            or '?' or '!' or '#' or '$' or '@' or '*' or '-'
            or (>= '0' and <= '9');

    private static bool IsVarChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or (>= '0' and <= '9');
}
