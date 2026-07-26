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
    /// <summary>
    /// Sub-parse a WORD token's raw text into typed WordPart children.
    /// Handles single quotes, double quotes, backslash escapes, $VAR references,
    /// and bare literal text.
    /// </summary>
    internal static ImmutableArray<WordPart> DecomposeWord(string raw)
    {
        var parts = ImmutableArray.CreateBuilder<WordPart>();
        int pos = 0;
        int len = raw.Length;

        // Detect tilde at the start of a word.
        if (len > 0 && raw[0] == '~')
        {
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
                if (pos + 1 < len)
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
            else if (c == '$' && pos + 2 < len && raw[pos + 1] == '(' && raw[pos + 2] == '(')
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
                pos = ParseBracedVar(raw, pos, parts);
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
                pos = ParseBraceExpansion(raw, pos, parts);
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
            return DecomposeWord(raw);

        var parts = ImmutableArray.CreateBuilder<WordPart>();
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0)
                parts.Add(new WordPart.Literal(":"));
            parts.AddRange(DecomposeWord(segments[i]));
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
            if (c == '\'')
            {
                int j = raw.IndexOf('\'', i + 1);
                j = j < 0 ? len : j + 1;
                sb.Append(raw, i, j - i);
                i = j;
                continue;
            }
            if (c == '"')
            {
                int j = i + 1;
                while (j < len && raw[j] != '"')
                {
                    if (raw[j] == '\\' && j + 1 < len) { j += 2; continue; }
                    // A nested command substitution may contain its OWN double quotes.
                    // Scanning naively for the next `"` ended the region at the inner
                    // quote, so `X="$(grep "a:b" f)"` looked like it had an UNQUOTED
                    // colon and got split there — tearing `:b f)` out of the command
                    // and leaving a mangled pattern. ScanBalancedParens is quote-aware,
                    // so skip the whole substitution.
                    if (raw[j] == '$' && j + 1 < len && raw[j + 1] == '(')
                    {
                        j = BashLexer.ScanBalancedParens(raw, j + 2, 1);
                        continue;
                    }
                    j++;
                }
                j = j < len ? j + 1 : len;
                sb.Append(raw, i, j - i);
                i = j;
                continue;
            }
            if (c == '`')
            {
                int j = i + 1;
                while (j < len && raw[j] != '`')
                {
                    if (raw[j] == '\\' && j + 1 < len) j++;
                    j++;
                }
                j = j < len ? j + 1 : len;
                sb.Append(raw, i, j - i);
                i = j;
                continue;
            }
            if (c == '$' && i + 1 < len && (raw[i + 1] == '(' || raw[i + 1] == '{'))
            {
                char open = raw[i + 1];
                char close = open == '(' ? ')' : '}';
                int depth = 0, j = i + 1;
                while (j < len)
                {
                    if (raw[j] == open) depth++;
                    else if (raw[j] == close) { depth--; if (depth == 0) { j++; break; } }
                    j++;
                }
                sb.Append(raw, i, System.Math.Min(j, len) - i);
                i = j;
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
        pos++; // skip opening '
        int start = pos;
        while (pos < raw.Length && raw[pos] != '\'')
            pos++;
        parts.Add(new WordPart.SingleQuoted(raw[start..pos]));
        if (pos < raw.Length)
            pos++; // skip closing '
        return pos;
    }

    /// <summary>
    /// Parse an ANSI-C quoted string <c>$'...'</c>. The raw inner text (with
    /// backslash escapes still intact) is captured; the closing quote is the first
    /// unescaped <c>'</c>. Escape expansion happens in the emitter.
    /// </summary>
    private static int ParseAnsiCQuoted(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos += 2; // skip $'
        int start = pos;
        while (pos < raw.Length && raw[pos] != '\'')
        {
            if (raw[pos] == '\\' && pos + 1 < raw.Length)
                pos++; // keep the escaped char as part of the inner text
            pos++;
        }
        parts.Add(new WordPart.AnsiCQuoted(raw[start..pos]));
        if (pos < raw.Length)
            pos++; // skip closing '
        return pos;
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
                if (next is '$' or '`' or '"' or '\\' or '\n')
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
            else if (c == '$' && pos + 2 < len && raw[pos + 1] == '(' && raw[pos + 2] == '(')
            {
                pos = ParseArithSub(raw, pos, innerParts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '(')
            {
                pos = ParseCommandSub(raw, pos, innerParts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '{')
            {
                pos = ParseBracedVar(raw, pos, innerParts);
            }
            else if (c == '$' && pos + 1 < len && IsVarStart(raw[pos + 1]))
            {
                pos = ParseSimpleVar(raw, pos, innerParts);
            }
            else if (c == '`')
            {
                pos = ParseBacktickCommandSub(raw, pos, innerParts);
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

    private static int ParseBracedVar(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos += 2; // skip ${
        int len = raw.Length;

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

        // ${!arr[@]} or ${!arr[*]} -> array keys operator
        if (pos < len && raw[pos] == '!')
        {
            pos++; // skip !
            int nameStart = pos;
            while (pos < len && IsVarChar(raw[pos]))
                pos++;
            string name = raw[nameStart..pos];

            // ${!prefix*} / ${!prefix@} — names of variables whose name starts
            // with `prefix` (distinct from the ${!arr[@]} keys form). The `*`/`@`
            // comes immediately after the name with no `[`. Without this it fell
            // through and mis-emitted $prefix.Keys.
            if (pos < len && (raw[pos] == '*' || raw[pos] == '@'))
            {
                pos++; // skip * or @
                if (pos < len && raw[pos] == '}')
                    pos++; // skip }
                parts.Add(new WordPart.BracedVarSub(name, "!names"));
                return pos;
            }

            string keysSuffix = "![@]";
            if (pos < len && raw[pos] == '[')
            {
                pos++; // skip [
                while (pos < len && raw[pos] != ']')
                    pos++;
                if (pos < len)
                    pos++; // skip ]
            }
            if (pos < len && raw[pos] == '}')
                pos++; // skip }
            parts.Add(new WordPart.BracedVarSub(name, keysSuffix));
            return pos;
        }

        // ${#VAR} or ${#arr[@]} -> length operator
        if (pos < len && raw[pos] == '#')
        {
            pos++; // skip #
            int nameStart = pos;
            while (pos < len && IsVarChar(raw[pos]))
                pos++;
            string name = raw[nameStart..pos];

            // Check for array subscript: ${#arr[@]} or ${#arr[*]}
            string lengthSuffix = "#";
            if (pos < len && raw[pos] == '[')
            {
                int subStart = pos;
                pos++; // skip [
                while (pos < len && raw[pos] != ']')
                    pos++;
                if (pos < len)
                    pos++; // skip ]
                lengthSuffix = "#" + raw[subStart..pos];
            }

            if (pos < len && raw[pos] == '}')
                pos++; // skip }
            parts.Add(new WordPart.BracedVarSub(name, lengthSuffix));
            return pos;
        }

        // Read variable name
        int varStart = pos;
        while (pos < len && IsVarChar(raw[pos]))
            pos++;
        string varName = raw[varStart..pos];

        // Check for array subscript: ${arr[0]}, ${arr[@]}, ${arr[key]}, and the
        // subscript-PLUS-operator forms ${arr[@]:1:2}, ${arr[0]##*/}, ${arr[i]:-x}.
        if (pos < len && raw[pos] == '[')
        {
            int subStart = pos;
            pos++; // skip [
            while (pos < len && raw[pos] != ']')
            {
                // POSIX bracket expressions contain a nested terminator, e.g.
                // [[:alpha:]].  Keep the inner ":]" inside this glob part and
                // continue to the outer closing bracket.
                if (raw[pos] == '[' && pos + 1 < len && raw[pos + 1] is ':' or '.' or '=')
                {
                    char marker = raw[pos + 1];
                    int nestedClose = raw.IndexOf($"{marker}]", pos + 2, StringComparison.Ordinal);
                    if (nestedClose >= 0)
                    {
                        pos = nestedClose + 2;
                        continue;
                    }
                }
                pos++;
            }
            if (pos < len)
                pos++; // skip ]
            string subscript = raw[subStart..pos];

            // A suffix operator may follow the subscript (slice / removal / default
            // applied to the indexed or @-expanded value). Capture everything up to
            // the matching '}' as part of the suffix so EmitBracedVar can apply it —
            // previously this branch returned early and the operator was dropped.
            if (pos < len && raw[pos] != '}')
            {
                int opStart = pos;
                int braceDepth = 1;
                while (pos < len && braceDepth > 0)
                {
                    if (raw[pos] == '{') braceDepth++;
                    else if (raw[pos] == '}') braceDepth--;
                    if (braceDepth > 0) pos++;
                }
                subscript += raw[opStart..pos];
            }

            if (pos < len && raw[pos] == '}')
                pos++; // skip }
            parts.Add(new WordPart.BracedVarSub(varName, subscript));
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

            // Read value until closing brace. A brace inside a quoted / escaped
            // span is literal (`${x:-"}"}` must close at the LAST `}`), so skip
            // quoted spans instead of counting their braces.
            int valStart = pos;
            int braceDepth = 1;
            while (pos < len && braceDepth > 0)
            {
                char vc = raw[pos];
                if (vc == '\\' && pos + 1 < len) { pos += 2; continue; }
                if (vc == '\'')
                {
                    pos++;
                    while (pos < len && raw[pos] != '\'') pos++;
                    if (pos < len) pos++;
                    continue;
                }
                if (vc == '"')
                {
                    pos++;
                    while (pos < len && raw[pos] != '"')
                    {
                        if (raw[pos] == '\\' && pos + 1 < len) pos += 2;
                        else pos++;
                    }
                    if (pos < len) pos++;
                    continue;
                }
                if (vc == '{') braceDepth++;
                else if (vc == '}') braceDepth--;
                if (braceDepth > 0) pos++;
            }
            string val = raw[valStart..pos];
            suffix = op + val;

            // Word-bearing operators expand their argument (default / alternative /
            // assigned value / error message). Decompose it NOW so $var, $(cmd), and
            // "$@" recurse through normal word emission instead of surviving as a raw
            // literal slice — the ${1+"$@"} family bug. Pattern-removal / slice / case
            // / @-transform operators take no expandable word argument: leave ArgWord
            // null so the emitter keeps the raw-slice path.
            if (IsWordBearingBracedOp(op))
                argWord = DecomposeWord(val);
        }

        if (pos < len && raw[pos] == '}')
            pos++; // skip }

        parts.Add(new WordPart.BracedVarSub(varName, suffix, argWord));
        return pos;
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

        parts.Add(new WordPart.ArithSub(BashArithmeticParser.Parse(expr)));
        return end;
    }

    private static int ParseCommandSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        int start = pos + 2; // just inside $(
        // Quote-aware boundary via the lexer's shared scanner: a ')' inside a
        // string in the command sub must not close the region (e.g. $(grep ")" f)).
        int end = BashLexer.ScanBalancedParens(raw, start, 1);
        int innerEnd = end > start && raw[end - 1] == ')' ? end - 1 : end;
        string inner = raw[start..innerEnd];

        var body = Parse(inner);
        parts.Add(new WordPart.CommandSub(body ?? new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty)));
        return end;
    }

    private static int ParseBacktickCommandSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos++; // skip opening `
        int start = pos;
        while (pos < raw.Length && raw[pos] != '`')
        {
            if (raw[pos] == '\\' && pos + 1 < raw.Length)
                pos++; // skip escaped char
            pos++;
        }
        string inner = raw[start..pos];
        if (pos < raw.Length)
            pos++; // skip closing `

        // bash strips ONE level of backslash before `\`, `$`, and `\` itself
        // inside backtick command substitution, before re-parsing the body.
        // Without this, `` `echo \`date\`` `` re-parsed the literal backslashes
        // and mis-parsed the nested substitution.
        inner = UnescapeBacktickInner(inner);
        var body = Parse(inner);
        parts.Add(new WordPart.CommandSub(body ?? new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty)));
        return pos;
    }

    /// <summary>
    /// Strip one level of backslash-escaping inside a backtick command sub:
    /// <c>\\</c> -> <c>\</c>, <c>\`</c> -> <c>`</c>, <c>\$</c> -> <c>$</c>. Other
    /// backslashes are preserved (bash leaves them for the inner parse). Mirrors
    /// the bash rule applied before the backtick body is re-parsed.
    /// </summary>
    private static string UnescapeBacktickInner(string s)
    {
        if (s.IndexOf('\\') < 0)
            return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length
                && (s[i + 1] == '\\' || s[i + 1] == '`' || s[i + 1] == '$'))
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

    private static int ParseTilde(string raw, ImmutableArray<WordPart>.Builder parts)
    {
        int pos = 1; // skip ~
        int len = raw.Length;

        // Read optional username until '/' or end of word.
        int start = pos;
        while (pos < len && raw[pos] != '/')
            pos++;

        string? user = pos > start ? raw[start..pos] : null;
        parts.Add(new WordPart.TildeSub(user));

        // Consume the '/' separator so it doesn't appear in the following literal;
        // the emitter re-inserts a platform separator after a TildeSub (see
        // PsEmitter.EmitWord / AppendTildeSeparator).
        if (pos < len && raw[pos] == '/')
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
            if (c == '$' && pos + 1 < len && (IsVarStart(raw[pos + 1]) || raw[pos + 1] == '{' || raw[pos + 1] == '(' || raw[pos + 1] == '\''))
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
            int start = pos;
            pos++; // skip [
            // Allow leading ] or ! as part of the class
            if (pos < len && raw[pos] is '!' or '^')
                pos++;
            if (pos < len && raw[pos] == ']')
                pos++;
            while (pos < len)
            {
                // POSIX bracket expressions nest a named class/collating symbol/
                // equivalence class inside the outer glob class.  The inner "]"
                // is not the terminator for the outer expression.
                if (raw[pos] == '[' && pos + 1 < len && raw[pos + 1] is ':' or '.' or '=')
                {
                    char marker = raw[pos + 1];
                    int nestedClose = raw.IndexOf($"{marker}]", pos + 2, StringComparison.Ordinal);
                    if (nestedClose >= 0)
                    {
                        pos = nestedClose + 2;
                        continue;
                    }
                }
                if (raw[pos] == ']')
                    break;
                pos++;
            }
            if (pos < len)
                pos++; // skip closing ]
            parts.Add(new WordPart.GlobPart(raw[start..pos]));
            return pos;
        }

        // Shouldn't reach here, but treat as literal.
        parts.Add(new WordPart.Literal(c.ToString()));
        return pos + 1;
    }

    private static int ParseExtGlob(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        int start = pos;
        pos += 2; // skip operator + '('
        int depth = 1;
        while (pos < raw.Length && depth > 0)
        {
            if (raw[pos] == '(') depth++;
            else if (raw[pos] == ')') depth--;
            if (depth > 0) pos++;
        }
        if (pos < raw.Length)
            pos++; // skip closing )
        parts.Add(new WordPart.GlobPart(raw[start..pos]));
        return pos;
    }

    /// <summary>
    /// Returns true if position <paramref name="pos"/> starts a bracket glob class
    /// (i.e. <c>[</c> followed eventually by <c>]</c>).
    /// </summary>
    private static bool ContainsBracketGlob(string raw, int pos)
    {
        int i = pos + 1;
        int len = raw.Length;
        // Allow leading ! or ^ for negation
        if (i < len && raw[i] is '!' or '^')
            i++;
        // Allow leading ] as literal member
        if (i < len && raw[i] == ']')
            i++;
        while (i < len)
        {
            if (raw[i] == '[' && i + 1 < len && raw[i + 1] is ':' or '.' or '=')
            {
                char marker = raw[i + 1];
                int nestedClose = raw.IndexOf($"{marker}]", i + 2, StringComparison.Ordinal);
                if (nestedClose >= 0)
                {
                    i = nestedClose + 2;
                    continue;
                }
            }
            if (raw[i] == ']')
                return true;
            i++;
        }
        return false;
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
    /// Returns true if position <paramref name="pos"/> starts a brace expansion:
    /// <c>{</c> followed by content containing <c>,</c> or <c>..</c>, ending with <c>}</c>.
    /// </summary>
    private static bool IsBraceExpansionStart(string raw, int pos)
    {
        if (pos >= raw.Length || raw[pos] != '{')
            return false;

        int i = pos + 1;
        int len = raw.Length;
        bool hasComma = false;
        bool hasDotDot = false;

        while (i < len)
        {
            char c = raw[i];
            if (c == '}')
                return hasComma || hasDotDot;
            if (c == ',')
                hasComma = true;
            if (c == '.' && i + 1 < len && raw[i + 1] == '.')
                hasDotDot = true;
            i++;
        }
        return false;
    }

    private static int ParseBraceExpansion(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos++; // skip {
        int len = raw.Length;

        // Find closing brace at depth 0 (depth-aware to handle nested braces).
        int closePos = pos;
        int braceDepth = 0;
        while (closePos < len)
        {
            if (raw[closePos] == '{') braceDepth++;
            else if (raw[closePos] == '}')
            {
                if (braceDepth == 0) break;
                braceDepth--;
            }
            closePos++;
        }

        string inner = raw[pos..closePos];

        // Advance past closing brace
        if (closePos < len)
            closePos++;

        // Check for range pattern: N..M or N..M..S (only when no top-level commas).
        int dotDot = inner.IndexOf("..", StringComparison.Ordinal);
        if (dotDot >= 0 && !HasTopLevelComma(inner))
        {
            string startStr = inner[..dotDot];
            string rest = inner[(dotDot + 2)..];
            int stepVal = 0;
            string endStr = rest;

            // Check for optional step: N..M..S
            int dotDot2 = rest.IndexOf("..", StringComparison.Ordinal);
            if (dotDot2 >= 0)
            {
                endStr = rest[..dotDot2];
                int.TryParse(rest[(dotDot2 + 2)..], out stepVal);
            }

            if (int.TryParse(startStr, out int startVal) && int.TryParse(endStr, out int endVal))
            {
                // Determine zero-padding width from the longer of the two operands
                int zeroPad = 0;
                if ((startStr.Length > 1 && startStr[0] == '0') ||
                    (endStr.Length > 1 && endStr[0] == '0'))
                {
                    zeroPad = Math.Max(startStr.Length, endStr.Length);
                }

                parts.Add(new WordPart.BracedRange(startVal, endVal, zeroPad, stepVal));
                return closePos;
            }

            // Letter range: {a..e} or {A..Z} — expand inline as a BracedTuple.
            if (startStr.Length == 1 && endStr.Length == 1
                && char.IsLetter(startStr[0]) && char.IsLetter(endStr[0]))
            {
                char cStart = startStr[0], cEnd = endStr[0];
                int vStart = cStart, vEnd = cEnd;
                int step = vStart <= vEnd ? 1 : -1;
                var letterItems = ImmutableArray.CreateBuilder<string>();
                for (int v = vStart; step > 0 ? v <= vEnd : v >= vEnd; v += step)
                    letterItems.Add(((char)v).ToString());
                parts.Add(new WordPart.BracedTuple(letterItems.ToImmutable()));
                return closePos;
            }
        }

        // Comma-separated tuple: split on top-level commas only (depth-aware),
        // then recursively expand any nested brace items (e.g. b{1,2} -> b1, b2).
        var rawItems = SplitTopLevelCommas(inner);
        var expandedItems = new List<string>();
        foreach (var item in rawItems)
            expandedItems.AddRange(ExpandNestedBraces(item));
        parts.Add(new WordPart.BracedTuple(ImmutableArray.CreateRange(expandedItems)));
        return closePos;
    }

    /// <summary>
    /// Returns true when <paramref name="s"/> contains at least one comma at brace-depth 0.
    /// Used to determine whether a brace body might be a comma-list rather than a range.
    /// </summary>
    private static bool HasTopLevelComma(string s)
    {
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == ',' && depth == 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Splits <paramref name="s"/> on commas at brace-depth 0 only.
    /// Commas inside nested brace groups are not treated as separators.
    /// </summary>
    private static List<string> SplitTopLevelCommas(string s)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
            else if (s[i] == ',' && depth == 0)
            {
                result.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(s.Substring(start));
        return result;
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
            if (item[i] == '{' && IsBraceExpansionStart(item, i))
            {
                braceStart = i;
                break;
            }
        }
        if (braceStart < 0)
            return new List<string> { item };

        // Find the matching closing brace at depth 0.
        int depth = 0, braceEnd = braceStart + 1;
        while (braceEnd < item.Length)
        {
            if (item[braceEnd] == '{') depth++;
            else if (item[braceEnd] == '}')
            {
                if (depth == 0) break;
                depth--;
            }
            braceEnd++;
        }

        string prefix = item[..braceStart];
        string braceContent = item[(braceStart + 1)..braceEnd];
        string suffix = braceEnd < item.Length ? item[(braceEnd + 1)..] : "";

        var expansions = ExpandBraceContent(braceContent);
        var result = new List<string>();
        foreach (var exp in expansions)
            result.AddRange(ExpandNestedBraces(prefix + exp + suffix));
        return result;
    }

    /// <summary>
    /// Expands the inner content of a brace (without surrounding braces) into its items.
    /// Handles numeric ranges, letter ranges, and comma-separated tuples (including nested).
    /// </summary>
    private static List<string> ExpandBraceContent(string inner)
    {
        int dotDot = inner.IndexOf("..", StringComparison.Ordinal);
        if (dotDot >= 0 && !HasTopLevelComma(inner))
        {
            string startStr = inner[..dotDot];
            string rest = inner[(dotDot + 2)..];
            string endStr = rest;
            int dotDot2 = rest.IndexOf("..", StringComparison.Ordinal);
            if (dotDot2 >= 0) endStr = rest[..dotDot2];

            if (int.TryParse(startStr, out int startVal) && int.TryParse(endStr, out int endVal))
            {
                int step = startVal <= endVal ? 1 : -1;
                var nums = new List<string>();
                for (int v = startVal; step > 0 ? v <= endVal : v >= endVal; v += step)
                    nums.Add(v.ToString());
                return nums;
            }
            if (startStr.Length == 1 && endStr.Length == 1
                && char.IsLetter(startStr[0]) && char.IsLetter(endStr[0]))
            {
                int s = startStr[0], e = endStr[0], step = s <= e ? 1 : -1;
                var letters = new List<string>();
                for (int v = s; step > 0 ? v <= e : v >= e; v += step)
                    letters.Add(((char)v).ToString());
                return letters;
            }
        }
        var rawItems = SplitTopLevelCommas(inner);
        var result = new List<string>();
        foreach (var item in rawItems)
            result.AddRange(ExpandNestedBraces(item));
        return result;
    }

    private static bool IsVarStart(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_'
            or '?' or '!' or '#' or '$' or '@' or '*' or '-'
            or (>= '0' and <= '9');

    private static bool IsVarChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or (>= '0' and <= '9');
}
