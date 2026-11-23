using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// Simple-command parsing for <see cref="BashParser"/>: the word/redirect/
/// heredoc/env-prefix loop, here-document body capture, assignments, and
/// redirect parsing. Split from the parser spine in BashParser.cs.
/// </summary>
public sealed partial class BashParser
{
    private int _functionDepth;

    /// <summary>
    /// The builtin a command-word token names: `'local'`, `"export"`, `\declare` are the builtin (quoting only escapes
    /// alias expansion), so the declaration productions read the quote-removed text, under the wrapper rules
    /// (<see cref="QuotedDeclarationStaysACommand"/>). A word with an expansion is itself.
    /// REGRESSION: `'local' v=3` / `\export Q=1` were looked up as programs, `command not found`.
    /// </summary>
    private static string CommandWordName(BashToken t)
        => t.Value.AsSpan().IndexOfAny('$', '`') < 0 ? BashQuoting.RemoveQuotes(t.Value) : t.Value;

    /// <summary>`declare`/`typeset` followed directly by a NAME[=VAL] word (no -g/-x/-A… flag) inside a function body.</summary>
    private bool IsFunctionScopedDeclare()
    {
        if (_functionDepth == 0 || CommandWordName(Peek()) is not ("declare" or "typeset")) return false;
        // `declare -i n=1` / `declare -A m` in a function make a local, as `local` does: attribute flags only. -g (global),
        // the print forms (-p -f -F) and -t stay on the declare path. REGRESSION: any flag sent the declaration there,
        // which writes globals, so `f() { declare -a e; }` left e set after f returned.
        int k = _pos + 1;
        for (; k < _tokens.Count && _tokens[k] is { Kind: BashTokenKind.Word } flag && flag.Value.Length > 1 && flag.Value[0] == '-'; k++)
        {
            // `declare -- v=1`: the end of the options, as for `local -- v=1`. REGRESSION: it was a global declaration.
            if (flag.Value == "--") { k++; break; }
            if (flag.Value.AsSpan(1).IndexOfAnyExcept(LocalDeclareFlags) >= 0) return false;
        }
        return k < _tokens.Count
               && _tokens[k] is { Kind: BashTokenKind.Word or BashTokenKind.AssignmentWord } next
               && !next.Value.StartsWith('-');
    }

    /// <summary>The attribute letters a function's <c>declare</c> may carry and still be a local declaration.</summary>
    private const string LocalDeclareFlags = "aAilnrux";

    /// <summary>The value attributes (<c>-i</c>, <c>-l</c>, <c>-u</c>) the declaration being parsed set; null for none. -l and -u together cancel (bash).</summary>
    private string? DeclaredValueAttrs()
    {
        string letters = (_declSawInteger ? "i" : "") + (_declSawLower == _declSawUpper ? "" : _declSawLower ? "l" : "u");
        return letters.Length == 0 ? null : letters;
    }

    /// <summary>`command export x=1`, `builtin local y`, `\builtin declare z`, `command command readonly r=1`: the wrappers
    /// only bypass functions and aliases, so a declaration builtin behind them is parsed exactly as the bare form (otherwise
    /// the runtime looks `export` up as an external command and reports "command not found").</summary>
    private void SkipDeclarationWrappers()
    {
        int k = 0;
        while (_pos + k + 1 < _tokens.Count
               && _tokens[_pos + k] is { Kind: BashTokenKind.Word } w && CommandWordName(w) is "builtin" or "command"
               && _tokens[_pos + k + 1] is { Kind: BashTokenKind.Word or BashTokenKind.AssignmentWord } nx
               && !nx.Value.StartsWith('-'))
            k++;
        if (k == 0) return;
        if (_tokens[_pos + k] is { Kind: BashTokenKind.Word } d && CommandWordName(d) is "export" or "local" or "declare" or "typeset" or "readonly")
        {
            // `builtin export z=$x`: behind a wrapper the operands are ordinary words (split, globbed), not assignments.
            // An operand with an expansion keeps the wrapper; the emitter runs it through the run-time declaration. One
            // expanded only inside double quotes after NAME= is the same as the declaration (OperandExpandsAsAWord).
            for (int j = _pos + k + 1; j < _tokens.Count && _tokens[j].Kind is BashTokenKind.Word or BashTokenKind.AssignmentWord; j++)
                if (OperandExpandsAsAWord(_tokens[j].Value)) return;
            // `builtin declare a=(x y)` is a syntax error in bash (an array literal needs the bare declaration builtin).
            int saved = _pos;
            _pos += k;
            if (DeclarationHasArrayInitializer()) _pos = saved;
        }
    }

    /// <summary>
    /// A declaration builtin spelled quoted or escaped (<c>'export'</c>, <c>\local</c>, <c>"declare"</c>) that must stay a
    /// plain command: bash runs the builtin, but its operands are ORDINARY words, as behind a wrapper
    /// (<see cref="SkipDeclarationWrappers"/>, the same rules). So an operand with an expansion stays a word (with
    /// x='a b', <c>'export' v=$x</c> exports v=a and b; the emitter makes it <c>builtin export …</c>,
    /// PsEmitter.UnquoteBuiltinName), and an array initializer is a syntax error (<c>'declare' -a A=(x y)</c>, status 2).
    /// The static spelling (<c>'local' v=3</c>) is the declaration either way.
    /// </summary>
    private bool QuotedDeclarationStaysACommand()
    {
        if (Peek() is not { Kind: BashTokenKind.Word } t) return false;
        string name = CommandWordName(t);
        if (name == t.Value || name is not ("export" or "local" or "declare" or "typeset" or "readonly")) return false;
        for (int j = _pos + 1; j < _tokens.Count && _tokens[j].Kind is BashTokenKind.Word or BashTokenKind.AssignmentWord; j++)
            if (OperandExpandsAsAWord(_tokens[j].Value)) return true;
        return DeclarationHasArrayInitializer();
    }

    /// <summary>
    /// Whether an operand of a quoted or wrapped declaration builtin (an ordinary word there) expands differently from
    /// the same operand of the bare declaration: an expansion (<c>$</c>, backquote) outside double quotes is split and
    /// globbed (<c>\local m=$x</c>), and one in the NAME makes the name a run-time one (<c>\local "$n"=1</c>,
    /// <c>"m=$1"</c>). A value whose every expansion is inside double quotes after the unquoted <c>=</c> is neither
    /// (<c>\local cmd="${1-__missing__}"</c>, conda's activate script), so that operand keeps the static declaration
    /// and the call pays no run-time transpile. Single-quoted text and a backslash-escaped character are literal.
    /// </summary>
    private static bool OperandExpandsAsAWord(string raw)
    {
        bool inDouble = false, sawEquals = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\') { i++; continue; }
            if (c == '\'' && !inDouble)
            {
                int close = raw.IndexOf('\'', i + 1);
                if (close < 0) return true;
                i = close;
                continue;
            }
            if (c == '"') { inDouble = !inDouble; continue; }
            if (c is '$' or '`')
            {
                if (!inDouble || !sawEquals) return true;
                continue;
            }
            if (c == '=' && !inDouble) sawEquals = true;
        }
        return false;
    }

    /// <summary>`export` / `local` NAME=VAL … (see ParseDeclarationPairs for the shape); null leaves the cursor where it was.</summary>
    private Command.ShAssignment? TryParseLocalOrExport()
    {
        if (QuotedDeclarationStaysACommand()) return null;
        if (!(Peek().Kind == BashTokenKind.Word && (CommandWordName(Peek()) is "export" or "local" || IsFunctionScopedDeclare())))
            return null;
        // Inside a function a plain `declare NAME=VAL` is function-local, exactly like `local`.
        string keyword = CommandWordName(Peek());
        bool isLocal = keyword is "local" or "declare" or "typeset";
        int saved = _pos;
        Advance(); // consume the keyword

        // `local -p [NAME…]` is a listing, not a declaration: leave it to the emitter.
        if (!(Peek().Kind == BashTokenKind.Word && Peek().Value == "-p" && keyword == "local")
            && ParseDeclarationPairs() is { } pairs)
            return new Command.ShAssignment(pairs, IsLocal: isLocal, IsExport: !isLocal, IsReadonly: isLocal && _declSawReadonly, IsNameref: isLocal && _declSawNameref,
                // `local -A m` / `local -a q`: the kind is part of the declaration (REGRESSION: dropped, so `m[x]=1` wrote m[0]).
                IsAssoc: isLocal && _declSawAssoc, IsIndexed: isLocal && _declSawIndexed && !_declSawAssoc,
                Attrs: isLocal ? DeclaredValueAttrs() : null,
                Builtin: isLocal ? keyword : null,
                Redirects: _declRedirects.Count == 0 ? default : [.. _declRedirects],
                // `export -n NAME` takes the attribute away (-n is no nameref there); `local -x` / `typeset +x` set / clear it.
                ExportAttr: isLocal ? _declExport : _declSawNameref ? -1 : 0);

        // No assignment or name followed — rewind and parse as a normal command
        // so forms like `export -p` still reach the general path.
        _pos = saved;
        return null;
    }

    /// <summary>Text, quotes and plain <c>$name</c>: expanding it runs nothing and assigns nothing.</summary>
    private static bool IsInertWord(ImmutableArray<WordPart> parts)
    {
        foreach (var part in parts)
        {
            if (part is WordPart.Literal or WordPart.EscapedLiteral or WordPart.SingleQuoted or WordPart.SimpleVarSub) continue;
            if (part is WordPart.DoubleQuoted dq && IsInertWord(dq.Parts)) continue;
            return false;
        }
        return true;
    }

    private Command ParseSimpleCommand()
    {
        int startLine = BashLexer.LineAt(_input, Peek().Position);
        var parsed = ParseSimpleCommandCore();
        return parsed is Command.ShAssignment asg ? asg with { Line = startLine } : parsed;
    }

    private Command ParseSimpleCommandCore()
    {
        SkipDeclarationWrappers();
        if (TryParseLocalOrExport() is { } declaration)
            return declaration;

        // `declare -a A=(x y)` / `readonly C=(m n)` — an ARRAY initializer only.
        // Everything else about declare/typeset (the -i, -A, -p attribute handling)
        // stays on the emitter's TryEmitDeclare path, which this must not disturb:
        // it only claims the command when an actual `NAME=(` array literal is present.
        // Without this the array was dropped entirely (`declare -a A=(x y)` emitted
        // `$global:A = ''`) and the unconsumed `(x y)` desynced the parse, swallowing
        // the command that followed.
        if (Peek().Kind == BashTokenKind.Word
            && CommandWordName(Peek()) is "declare" or "typeset" or "readonly"
            && !QuotedDeclarationStaysACommand()
            && (DeclarationHasArrayInitializer() || DeclarationHasQuotedCompound()))
        {
            int saved = _pos;
            bool isReadonlyKw = CommandWordName(Peek()) == "readonly";
            Advance(); // consume the keyword

            // `readonly b[7]=8` is an error in bash (`declare -r b[7]=8` is not): leave it to the simple-command
            // path, whose identifier check reports it.
            if (ParseDeclarationPairs() is { } declPairs && !(isReadonlyKw && declPairs.Any(p => p.Name.Contains('['))))
                return new Command.ShAssignment(declPairs, IsReadonly: isReadonlyKw || _declSawReadonly, IsAssoc: _declSawAssoc, IsDeclare: true, IsIndexed: _declSawIndexed && !_declSawAssoc,
                    Redirects: _declRedirects.Count == 0 ? default : [.. _declRedirects], IsGlobal: isReadonlyKw || _declSawGlobal);

            _pos = saved;
        }

        // Collect leading assignment words (VAR=val ...).
        // Array assignments like arr=(a b c) are detected and consumed here.
        int simpleLine = BashLexer.LineAt(_input, Peek().Position);
        int reportLine = ReportLineOfCommandAt(_pos, simpleLine);
        var assignmentPairs = ImmutableArray.CreateBuilder<Assignment>();
        var envPairs = ImmutableArray.CreateBuilder<EnvPair>();
        bool hasArrayAssignment = false;
        while (Peek().Kind == BashTokenKind.AssignmentWord)
        {
            var assign = ParseAssignmentWordWithArray();
            if (assign.ArrayValue is not null)
                hasArrayAssignment = true;
            assignmentPairs.Add(assign);
            envPairs.Add(new EnvPair(assign.Name, assign.ArrayValue is { } arr ? ArrayPrefixAsScalar(arr) : assign.Value));
        }

        // `tmp='' local tx=tx`: a temp binding in front of `local`/`export` is gone when the builtin ends, and the
        // declaration's words were expanded before it was made, so it is the bare declaration (the runtime looked
        // `local` up as a command: "local: command not found"). One effect stays: a value-less `v=t local v` /
        // `v=t export v` keeps the binding's value (bash 5.2). A prefix whose value could do something (`$(cmd)`,
        // `$((i++))`) keeps the old path.
        if (assignmentPairs.Count > 0 && !hasArrayAssignment
            && assignmentPairs.All(a => a.Op == AssignOp.Equal && (a.Value is null || IsInertWord(a.Value.Parts)))
            && TryParseLocalOrExport() is { } prefixed)
        {
            var folded = prefixed.Pairs.Select(p => p.Value is null && p.ArrayValue is null && p.Op == AssignOp.Equal
                && assignmentPairs.LastOrDefault(a => a.Name == p.Name) is { } temp
                    ? p with { Value = temp.Value ?? new CompoundWord(ImmutableArray<WordPart>.Empty) }
                    : p).ToImmutableArray();
            return prefixed with { Pairs = folded, TempPrefix = prefixed.IsExport ? assignmentPairs.ToImmutable() : default };
        }

        // Array assignments with no command after them are an assignment statement. Before a command word
        // (`B=(b b) cmd`) bash binds the temporary as the STRING "(b b)": the expanded elements joined by spaces.
        // `arr=(p q) >f` goes on to collect the redirects (the redirect token was left unparsed: a parse error).
        if (hasArrayAssignment && Peek().Kind != BashTokenKind.Word && Peek().Kind != BashTokenKind.IoNumber && !IsCompoundRedirectOp(Peek().Kind))
            return new Command.ShAssignment(assignmentPairs.ToImmutable());

        var words = ImmutableArray.CreateBuilder<CompoundWord>();
        var redirects = ImmutableArray.CreateBuilder<Redirect>();
        var hereDocs = ImmutableArray.CreateBuilder<HereDoc>();
        var pendingHeredocs = new List<(string Delimiter, bool Expand, bool StripTabs, string? FdVar, int BodyStart, int BodyEnd, int Fd)>();

        while (true)
        {
            var kind = Peek().Kind;

            if (kind == BashTokenKind.Word)
            {
                // Named-fd redirect prefix `{name}` before a redirect (bash
                // allocates a free fd into $name). Preserve the name in the AST;
                // the emitter degrades it to the default-fd redirect form.
                if (IsNamedFdToken(Peek().Value)
                    && _pos + 1 < _tokens.Count
                    && IsRedirectOp(_tokens[_pos + 1].Kind))
                {
                    var fdVar = Peek().Value[1..^1];
                    Advance();
                    if (Peek().Kind is BashTokenKind.DLess or BashTokenKind.DLessDash)
                    {
                        bool stripTabs = Peek().Kind == BashTokenKind.DLessDash;
                        Advance();
                        var delimToken = Advance();
                        var (delimiter, expand) = BashLexer.ParseHeredocDelimiter(delimToken.Value);
                        pendingHeredocs.Add((delimiter, expand, stripTabs, fdVar, delimToken.BodyStart, delimToken.BodyEnd, 0));
                    }
                    else
                    {
                        AddRedirect(redirects, ParseRedirect(fdVar));
                    }
                    continue;
                }

                // Stop at reserved words that delimit compound commands —
                // but ONLY when they appear in the position bash itself
                // treats as a keyword. Per bash man: reserved words are
                // recognized only as the first word of a simple command,
                // OR following another reserved word (e.g. `do ... done`).
                // Inside `echo done`, `done` is just an argument; without
                // this guard the parser would drop the argument and emit
                // `Invoke-BashEcho` with no operand, which silently hangs
                // because Invoke-BashEcho reads stdin when no args.
                if (words.Count == 0 && IsCompoundDelimiter(Peek().Value))
                    break;

                var token = Advance();
                var parts = DecomposeWord(token.Value);
                words.Add(new CompoundWord(parts));
            }
            else if (kind == BashTokenKind.AssignmentWord)
            {
                // After command words have started, treat assignment-like tokens as arguments.
                var token = Advance();
                var parts = DecomposeWord(token.Value);
                // bash treats a word that LOOKS like an assignment (`echo x=~`, `PATH=~/bin:~/x`) as one for tilde
                // expansion: a ~ at the start of the value or after a ':' expands.
                int eqAt = token.Value.IndexOf('=');
                if (eqAt > 0 && IsValidVariableName(token.Value[..eqAt])
                    && (token.Value[(eqAt + 1)..].StartsWith('~') || token.Value.Contains(":~")))
                {
                    var tildeParts = ImmutableArray.CreateBuilder<WordPart>();
                    tildeParts.Add(new WordPart.Literal(token.Value[..(eqAt + 1)]));
                    tildeParts.AddRange(DecomposeAssignmentValue(token.Value[(eqAt + 1)..]));
                    parts = tildeParts.ToImmutable();
                }
                // `let x=( 1 )`: let's operand is arithmetic text, so the parenthesised tail belongs to this argument.
                if (words.Count > 0 && token.Value.EndsWith('=') && Peek().Kind == BashTokenKind.LParen
                    && words[0].Parts is [WordPart.Literal { Value: "let" }] && TakeBalancedParenText() is { } parenText)
                    parts = parts.Add(new WordPart.SingleQuoted(parenText));
                words.Add(new CompoundWord(parts));
            }
            else if (kind == BashTokenKind.LBrace && words.Count > 0)
            {
                // `{` AFTER the command word is an ordinary argument (`echo a {`, `type -t {`); it is reserved only in command position.
                words.Add(new CompoundWord(DecomposeWord(Advance().Value)));
            }
            else if (kind == BashTokenKind.RBrace && words.Count > 0 && RBraceIsArgument())
            {
                // `}` AFTER the command word is an ordinary argument too (`echo }`, `echo }x`).
                var token = Advance();
                string braceText = token.Value;
                while (Peek().Kind == BashTokenKind.Word && Peek().Position == token.Position + braceText.Length)
                    braceText += Advance().Value;
                words.Add(new CompoundWord(DecomposeWord(braceText)));
            }
            else if (kind == BashTokenKind.Bang && words.Count > 0)
            {
                // A `!` AFTER the command word is a literal argument, not negation:
                // `find . ! -name x`, `test ! -f y`. Per bash, `!` is the negation
                // reserved word only at the start of a pipeline — ParsePipeline
                // already consumes a leading run of `!` before we get here, so any
                // `!` reaching this loop mid-command is an operand. Without this it
                // fell to the `else break`, ending the command and leaving
                // `! -name x` to be mis-parsed as a separate negated pipeline.
                var token = Advance();
                // `!=` / `!x` / `!!` is ONE word when the text is adjacent (`test a != b`, `echo a !=b`, `echo !!`).
                string bangText = token.Value;
                while (Peek().Kind is BashTokenKind.Word or BashTokenKind.Bang && Peek().Position == token.Position + bangText.Length)
                    bangText += Advance().Value;
                words.Add(new CompoundWord(DecomposeWord(bangText)));
            }
            else if (kind == BashTokenKind.TLess)
            {
                hereDocs.Add(ParseHereString());
            }
            else if (kind is BashTokenKind.DLess or BashTokenKind.DLessDash)
            {
                bool stripTabs = kind == BashTokenKind.DLessDash;
                Advance(); // consume << or <<-
                var delimToken = Advance();
                // Quoted (`'EOF'` / `"EOF"`) or backslashed (`\EOF`) delimiters
                // suppress expansion; the shared helper is the single source the
                // lexer's body-skip also uses, so the two can't disagree on the
                // terminator.
                var (delimiter, expand) = BashLexer.ParseHeredocDelimiter(delimToken.Value);
                pendingHeredocs.Add((delimiter, expand, stripTabs, null, delimToken.BodyStart, delimToken.BodyEnd, 0));
            }
            else if (kind == BashTokenKind.IoNumber && _pos + 1 < _tokens.Count
                && _tokens[_pos + 1].Kind is BashTokenKind.DLess or BashTokenKind.DLessDash
                && int.TryParse(Peek().Value, out var hereFd) && (hereFd >= 3 || hereFd == 0))
            {
                // `cmd 3<<EOF`: the here-document is descriptor N's input (read with `read -u N`), not stdin.
                Advance();
                bool stripTabsN = Peek().Kind == BashTokenKind.DLessDash;
                Advance();
                var delimTokenN = Advance();
                var (delimiterN, expandN) = BashLexer.ParseHeredocDelimiter(delimTokenN.Value);
                pendingHeredocs.Add((delimiterN, expandN, stripTabsN, null, delimTokenN.BodyStart, delimTokenN.BodyEnd, hereFd));
            }
            else if (kind == BashTokenKind.IoNumber || IsRedirectOp(kind))
            {
                AddRedirect(redirects, ParseRedirect());
            }
            else
            {
                break;
            }
        }

        // If only assignments and no command words, it's a bare assignment.
        // `x=$(cmd) 2>/dev/null` / `y=1 >f` too: with no command word the assignments are the shell's own, and the
        // redirects are the statement's (a value's `$( )` is expanded before them). As a temp binding the value was lost.
        if (assignmentPairs.Count > 0 && words.Count == 0
            && pendingHeredocs.Count == 0 && hereDocs.Count == 0)
            return new Command.ShAssignment(assignmentPairs.ToImmutable(), Redirects: redirects.Count == 0 ? default : redirects.ToImmutable());

        // Collect heredoc bodies in order for all pending heredoc redirects.
        if (pendingHeredocs.Count > 0)
            CollectHereDocBodies(pendingHeredocs, hereDocs);

        // `\break` / `'return'`: quoting only escapes alias expansion, so it is still the control builtin.
        if (words.Count > 0 && EscapedControlBuiltin(words[0]) is { } controlName)
            words[0] = new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal(controlName)));

        // `builtin return 3`, `command continue`, `\builtin command exit 5`: the wrappers only bypass functions and
        // aliases, so the control builtin behind them is the bare one (otherwise `command` ran `continue` as a
        // command and the loop never saw it).
        StripControlBuiltinWrappers(words);

        return new Command.Simple(
            words.ToImmutable(),
            envPairs.ToImmutable(),
            redirects.ToImmutable(),
            hereDocs.ToImmutable(),
            simpleLine,
            reportLine);
    }

    /// <summary>
    /// <see cref="Command.Simple.ReportLine"/> of the command whose first token is <c>_tokens[first]</c>: bash numbers a
    /// simple command when its parser reads the token after the first word (it needs that token to tell <c>x</c> from
    /// <c>x ()</c>), so the line is where that token ends -- a word's last line, a newline's own line. bash 5.2 (WSL):
    /// <c>x \&lt;nl&gt; arg</c> on 2 is 3, <c>x 'a&lt;nl&gt;b'</c> on 9 is 10, <c>x 'a&lt;nl&gt;b' 'c&lt;nl&gt;d'</c> on
    /// 11 is 12, <c>x \&lt;nl&gt;&lt;nl&gt;</c> on 16 is 17, <c>'n&lt;nl&gt;F' x</c> on 14 is 15. A command that starts
    /// with a redirect or an array assignment keeps <paramref name="startLine"/>.
    /// </summary>
    private int ReportLineOfCommandAt(int first, int startLine)
    {
        if (first + 1 >= _tokens.Count) return startLine;
        var head = _tokens[first];
        if (head.Kind is not (BashTokenKind.Word or BashTokenKind.AssignmentWord) || head.Value.EndsWith('(')) return startLine;
        var next = _tokens[first + 1];
        int end = next.Kind == BashTokenKind.Eof || next.Value.Length == 0 ? head.Position + Math.Max(0, head.Value.Length - 1)
            : next.Kind == BashTokenKind.Newline ? next.Position
            : next.Position + next.Value.Length - 1;
        // startLine is head's line: count only the newlines from head on (LineAt rescans the whole input).
        end = Math.Min(end, _input.Length);
        int line = startLine;
        for (int i = Math.Max(0, head.Position); i < end; i++)
            if (_input[i] == '\n') line++;
        return line;
    }

    private static void StripControlBuiltinWrappers(ImmutableArray<CompoundWord>.Builder words)
    {
        int k = 0;
        while (k < words.Count - 1 && WordText(words[k]) is "builtin" or "command") k++;
        if (k == 0 || WordText(words[k]) is not ({ } name and ("break" or "continue" or "return" or "exit"))) return;
        for (int i = 0; i <= k; i++) words.RemoveAt(0);
        words.Insert(0,new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal(name))));
    }

    /// <summary>The text of a word made only of literal and backslash-escaped characters (`\builtin`), else null.</summary>
    private static string? WordText(CompoundWord word)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in word.Parts)
        {
            if (p is WordPart.Literal l) sb.Append(l.Value);
            else if (p is WordPart.EscapedLiteral e) sb.Append(e.Value);
            else return null;
        }
        return sb.ToString();
    }

    /// <summary>`\break`, `'return'`, `"continue"`: a quoted or escaped spelling of a control builtin, as its bare name
    /// (the AST the loop and function analyses read); else null. Other builtins: PsEmitter.UnquoteBuiltinName.</summary>
    private static string? EscapedControlBuiltin(CompoundWord word)
    {
        if (word.Parts.All(p => p is WordPart.Literal)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var p in word.Parts)
        {
            if (p is WordPart.Literal l) sb.Append(l.Value);
            else if (p is WordPart.EscapedLiteral e) sb.Append(e.Value);
            else if (p is WordPart.SingleQuoted s) sb.Append(s.Value);
            else if (p is WordPart.DoubleQuoted d && d.Parts.All(q => q is WordPart.Literal))
                foreach (var q in d.Parts) sb.Append(((WordPart.Literal)q).Value);
            else return null;
        }
        var name = sb.ToString();
        return name is "break" or "continue" or "return" or "exit" ? name : null;
    }

    /// <summary>
    /// Build the bodies of all pending here-documents, in order, from the raw
    /// source span the LEXER recorded on each delimiter token.
    /// </summary>
    /// <remarks>
    /// A heredoc body is raw, line-oriented text — bash matches the delimiter
    /// against whole physical lines with no quote/comment interpretation. The lexer
    /// already walked those bodies to skip them (it emits NO body tokens) and
    /// stamped each body's exact <c>[BodyStart, BodyEnd)</c> span on its delimiter
    /// token. We read the text straight from that span. This is deliberately NOT
    /// recomputed from the token cursor: a same-line <c>|</c>/<c>&amp;&amp;</c>/<c>;</c>
    /// after the heredoc put the next TOKEN on the command line, so a cursor-derived
    /// start landed mid-line and swallowed the operator tail into the body (while
    /// still parsing it as code). The body text already includes each line's
    /// trailing newline, so no join is needed; an empty span is an empty body.
    ///
    /// The token cursor is intentionally not advanced: the command-line newline
    /// remains for <c>ParseList</c> to see the separator and parse what follows.
    /// </remarks>
    private void CollectHereDocBodies(
        List<(string Delimiter, bool Expand, bool StripTabs, string? FdVar, int BodyStart, int BodyEnd, int Fd)> pending,
        ImmutableArray<HereDoc>.Builder hereDocs)
    {
        foreach (var (delimiter, expand, stripTabs, fdVar, bodyStart, bodyEnd, hereFd) in pending)
        {
            string body;
            if (bodyStart < 0 || bodyEnd <= bodyStart)
            {
                body = string.Empty;
            }
            else
            {
                // The span is raw source, so a folded CRLF line ending arrives as
                // "\r\n"; bash bodies are LF-only, so normalize before storing.
                body = _input.Substring(bodyStart, bodyEnd - bodyStart).Replace("\r\n", "\n");
                // A body that runs to end of input with no delimiter line and no
                // final newline ("here-document delimited by end-of-file") still
                // ends its last line with "\n" in bash, like every other line.
                if (!body.EndsWith('\n'))
                    body += "\n";
                if (stripTabs)
                    body = StripHereDocTabs(body);
            }
            hereDocs.Add(new HereDoc(body, expand, stripTabs, fdVar, hereFd));
        }
    }

    // For <<- bash strips leading TABS (and only tabs) from every body line before
    // storing it. The raw span already carries each line's trailing newline; strip
    // the leading tabs of each line without disturbing any other whitespace.
    private static string StripHereDocTabs(string body)
    {
        var lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimStart('\t');
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Parse the operands of a declaration builtin (<c>export</c> / <c>local</c> /
    /// <c>declare</c> / <c>readonly</c>): an interleaved run of <c>NAME=VAL</c>,
    /// <c>NAME=(array)</c>, and bare <c>NAME</c> args, preceded or separated by
    /// attribute flags. Returns null (consuming nothing beyond what the caller must
    /// rewind) when no pair was found, so the caller can fall back to a normal command.
    ///
    /// Interleaving matters: <c>eval $(printf 'export A=1\nexport B=2\n')</c>
    /// field-splits to <c>export A=1 export B=2</c>, so a bare Word in the middle must
    /// not stop the loop and silently drop the assignments after it.
    ///
    /// Attribute flags are SKIPPED, not taken as variable names. Treating them as names
    /// is what made `local -a A=(x y)` emit the junk statement `$-a = $-a`. ps-bash does
    /// not model bash variable attributes, so dropping the flag is the honest mapping;
    /// the initializer it decorates is what carries meaning.
    /// </summary>
    /// <summary>Set by <see cref="ParseDeclarationPairs"/> when an attribute flag of the declaration carried `r`.</summary>
    private bool _declSawReadonly;
    /// <summary>Set by <see cref="ParseDeclarationPairs"/> when an attribute flag of the declaration carried `n` (a nameref).</summary>
    private bool _declSawNameref;
    /// <summary>Set by <see cref="ParseDeclarationPairs"/> when an attribute flag of the declaration carried `A` (an associative array).</summary>
    private bool _declSawAssoc;
    /// <summary>Set by <see cref="ParseDeclarationPairs"/> when an attribute flag of the declaration carried `a` (an indexed array).</summary>
    private bool _declSawIndexed;
    private bool _declSawInteger;
    private bool _declSawLower;
    private bool _declSawUpper;
    private bool _declSawGlobal;
    /// <summary>Set by <see cref="ParseDeclarationPairs"/>: +1 after a flag with <c>x</c> (<c>-x</c>), -1 after <c>+x</c>, else 0.</summary>
    private int _declExport;
    /// <summary>The redirects <see cref="ParseDeclarationPairs"/> consumed (the builtin's own).</summary>
    private readonly List<Redirect> _declRedirects = [];

    /// <summary>The text of a word with only literal and quoted parts, quotes removed (<c>'q'</c>, <c>"q"r</c>); null when
    /// any part expands.</summary>
    private static string? StaticWordText(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in DecomposeWord(raw))
        {
            switch (part)
            {
                case WordPart.Literal l: sb.Append(l.Value); break;
                case WordPart.EscapedLiteral e: sb.Append(e.Value); break;
                case WordPart.SingleQuoted s: sb.Append(s.Value); break;
                case WordPart.DoubleQuoted d when d.Parts.All(p => p is WordPart.Literal or WordPart.EscapedLiteral):
                    foreach (var p in d.Parts) sb.Append(p is WordPart.Literal dl ? dl.Value : ((WordPart.EscapedLiteral)p).Value);
                    break;
                default: return null;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// A declaration operand whose quoted text holds the <c>=</c> (<c>"x=v"</c>, <c>'x=$y'</c>, <c>"x"=v</c>,
    /// <c>"x=$y"</c>) as the assignment it is after quote removal: the parts before the <c>=</c> must be static text
    /// forming NAME or NAME+, and the value must be quoted or plain text, so expanding it as an assignment value
    /// (no splitting, no globbing, no tilde) gives what bash's ordinary word expansion gives. Null otherwise.
    /// </summary>
    internal static Assignment? QuotedDeclarationAssignment(string raw)
    {
        var parts = DecomposeWord(raw, braceExpansion: false);
        var name = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            string? text = parts[i] switch
            {
                WordPart.Literal l => l.Value,
                WordPart.EscapedLiteral e => e.Value,
                WordPart.SingleQuoted s => s.Value,
                _ => null,
            };
            WordPart? rest = null;
            int eq;
            if (text is not null)
            {
                eq = text.IndexOf('=');
                if (eq < 0) { name.Append(text); continue; }
                name.Append(text, 0, eq);
                string after = text[(eq + 1)..];
                rest = parts[i] switch
                {
                    WordPart.SingleQuoted => new WordPart.SingleQuoted(after),
                    WordPart.EscapedLiteral => new WordPart.EscapedLiteral(after),
                    _ => new WordPart.Literal(after),
                };
            }
            else if (parts[i] is WordPart.DoubleQuoted dq)
            {
                int k = 0;
                eq = -1;
                for (; k < dq.Parts.Length; k++)
                {
                    string? inner = dq.Parts[k] switch { WordPart.Literal l => l.Value, WordPart.EscapedLiteral e => e.Value, _ => null };
                    if (inner is null) return null;
                    eq = inner.IndexOf('=');
                    if (eq >= 0) { name.Append(inner, 0, eq); break; }
                    name.Append(inner);
                }
                if (eq < 0) continue;
                var tail = ImmutableArray.CreateBuilder<WordPart>();
                string afterEq = ((dq.Parts[k] as WordPart.Literal)?.Value ?? ((WordPart.EscapedLiteral)dq.Parts[k]).Value)[(eq + 1)..];
                if (afterEq.Length > 0) tail.Add(new WordPart.Literal(afterEq));
                tail.AddRange(dq.Parts.Skip(k + 1));
                rest = new WordPart.DoubleQuoted(tail.ToImmutable());
            }
            else return null;

            string n = name.ToString();
            var op = AssignOp.Equal;
            if (n.EndsWith('+')) { op = AssignOp.PlusEqual; n = n[..^1]; }
            if (!IsValidVariableName(n) || i == 0 && parts[i] is WordPart.Literal) return null;
            var value = ImmutableArray.CreateBuilder<WordPart>();
            if (rest is not WordPart.Literal { Value.Length: 0 }) value.Add(rest!);
            foreach (var p in parts.Skip(i + 1))
            {
                if (p is WordPart.SingleQuoted or WordPart.DoubleQuoted or WordPart.EscapedLiteral
                    || p is WordPart.Literal pl && pl.Value.IndexOf('~') < 0)
                    value.Add(p);
                else return null;
            }
            if (rest is WordPart.Literal rl && rl.Value.IndexOf('~') >= 0) return null;
            return new Assignment(n, op, new CompoundWord(value.ToImmutable()));
        }
        return null;
    }

    private ImmutableArray<Assignment>? ParseDeclarationPairs()
    {
        _declSawReadonly = false;
        _declSawNameref = false;
        _declSawAssoc = false;
        _declSawIndexed = false;
        _declSawInteger = false;
        _declSawLower = false;
        _declSawUpper = false;
        _declSawGlobal = false;
        _declExport = 0;
        _declRedirects.Clear();
        var pairs = ImmutableArray.CreateBuilder<Assignment>();

        while (Peek().Kind is BashTokenKind.AssignmentWord or BashTokenKind.Word
            || Peek().Kind == BashTokenKind.IoNumber || IsCompoundRedirectOp(Peek().Kind))
        {
            // A redirect on a declaration builtin (`export PATH 2>/dev/null`,
            // `local x=1 >/dev/null`) is valid bash. It covers only the builtin's own
            // output — a value's command substitution is expanded BEFORE redirections,
            // so its stderr is not redirected (oracle-checked) — and a successful
            // declaration writes nothing. Consuming it here keeps the assignment;
            // leaving it unconsumed made the statement end early. The declaration keeps
            // them (_declRedirects): a `> file` target is created, a refused kind change
            // reports there. Gap: not applied when a value has a `$( )`.
            if (Peek().Kind == BashTokenKind.IoNumber || IsCompoundRedirectOp(Peek().Kind))
            {
                AddRedirect(_declRedirects, ParseRedirect());
                continue;
            }

            if (Peek().Kind == BashTokenKind.AssignmentWord)
            {
                string raw = Peek().Value;
                pairs.Add(QuotedCompoundArray(ParseAssignmentWordWithArray(), raw));
                continue;
            }

            var value = Peek().Value;
            // An attribute flag (-a, -r, -i, --) is an option, never a variable name.
            // A lone "-" is not a flag; bash has no such option and a name cannot
            // start with '-', so leaving it to the name branch keeps prior behavior.
            if (value.Length > 1 && value[0] == '-')
            {
                if (value[1] != '-' && value.Contains('r')) _declSawReadonly = true;
                if (value[1] != '-' && value.Contains('n')) _declSawNameref = true;
                if (value[1] != '-' && value.Contains('A')) _declSawAssoc = true;
                if (value[1] != '-' && value.Contains('a')) _declSawIndexed = true;
                if (value[1] != '-' && value.Contains('i')) _declSawInteger = true;
                if (value[1] != '-' && value.Contains('l')) _declSawLower = true;
                if (value[1] != '-' && value.Contains('u')) _declSawUpper = true;
                if (value[1] != '-' && value.Contains('x')) _declExport = 1;
                if (value[1] != '-' && value.Contains('g')) _declSawGlobal = true;
                Advance();
                continue;
            }
            // `local +x v` / `typeset +x v`: the export attribute is taken away.
            if (value == "+x")
            {
                _declExport = -1;
                Advance();
                continue;
            }

            // The operand must be a real bash NAME. `export PATH `envsubst -v "$1"``
            // (gettext.sh) exports whatever names the command prints — a dynamic form
            // with no static equivalent. Taking the raw token as a name emitted
            // ``$env:`envsubst -v "$1"` = ...``, which is not parseable PowerShell, so
            // ONE such line broke the whole file. Decline instead and let the caller
            // rewind to the general command path, where it fails loudly at runtime
            // rather than silently poisoning the transpile.
            if (!IsValidVariableName(value))
            {
                // `export 'z'` / `local "q"`: quote removal comes first, so a quoted static name is the name
                // (the run-time eval of `readonly $names` hands every field over single-quoted).
                if (StaticWordText(value) is { } unquoted && IsValidVariableName(unquoted))
                {
                    Advance();
                    pairs.Add(new Assignment(unquoted, AssignOp.Equal, null));
                    continue;
                }
                // `local "x=v"` / `local 'x=$y'` / `export "p"=q`: after quote removal the operand is NAME=VALUE, which
                // the builtin assigns (and `local` makes local); it was run as a plain command that set a global.
                if (QuotedDeclarationAssignment(value) is { } quotedPair)
                {
                    Advance();
                    pairs.Add(QuotedCompoundArray(quotedPair, value));
                    continue;
                }
                return null;
            }

            Advance();
            pairs.Add(new Assignment(value, AssignOp.Equal, null));
        }

        return pairs.Count > 0 ? pairs.ToImmutable() : null;
    }

    /// <summary>
    /// <c>local -a 'b=(3 4)'</c>, <c>declare -a a='(1 2)'</c>, <c>typeset -A "m=([k]=v)"</c>: with <c>-a</c> or <c>-A</c> an
    /// operand whose quote-removed text is <c>NAME=(…)</c> is the compound assignment (bash parses the value as the array
    /// literal, expanding inside it), not the scalar <c>(3 4)</c>. Only static text (<paramref name="raw"/>, the operand as
    /// written); a value that does not parse as one whole literal (<c>'a=(1); echo x)'</c>) stays the scalar.
    /// REGRESSION: <c>f(){ local -a 'b=(3 4)'; echo ${b[1]}; }</c> printed nothing (bash: 4).
    /// </summary>
    private Assignment QuotedCompoundArray(Assignment pair, string raw)
    {
        if (!(_declSawIndexed || _declSawAssoc) || pair.ArrayValue is not null || pair.Value is null || StaticWordText(raw) is not { } text)
            return pair;
        int eq = text.IndexOf('=');
        if (eq < 0 || text.Length - eq < 3 || text[eq + 1] != '(' || text[^1] != ')') return pair;
        string source = pair.Name + (pair.Op == AssignOp.PlusEqual ? "+=" : "=") + text[(eq + 1)..];
        try
        {
            var inner = new BashParser(BashLexer.Tokenize(source), source);
            if (inner.Peek().Kind != BashTokenKind.AssignmentWord) return pair;
            var compound = inner.ParseAssignmentWordWithArray();
            return compound.ArrayValue is not null && compound.Name == pair.Name && inner.Peek().Kind == BashTokenKind.Eof ? compound : pair;
        }
        catch (ParseException) { return pair; }
    }

    /// <summary>
    /// Look ahead (without consuming) for a <c>NAME=</c> immediately followed by
    /// <c>(</c> in the current declaration command — the array-initializer form.
    /// Scanning stops at the first token that cannot be part of a simple command so a
    /// later, unrelated <c>(</c> never triggers a false positive.
    /// </summary>
    private bool DeclarationHasArrayInitializer()
    {
        for (int i = _pos + 1; i < _tokens.Count; i++)
        {
            var kind = _tokens[i].Kind;
            if (kind == BashTokenKind.AssignmentWord)
            {
                return i + 1 < _tokens.Count
                    && _tokens[i + 1].Kind == BashTokenKind.LParen;
            }
            if (kind != BashTokenKind.Word) return false;
        }
        return false;
    }

    /// <summary>
    /// <c>declare -a a='(1 2)'</c>, <c>declare -ga "g=(1 2)"</c>: a declaration with an <c>-a</c>/<c>-A</c> flag and an
    /// operand whose quote-removed text is <c>NAME=(…)</c>, the compound assignment <see cref="QuotedCompoundArray"/> reads.
    /// It takes the array-initializer production, so the global declaration gets the array as a written one does.
    /// REGRESSION: <c>declare -a a='(1 2)'</c> stored the scalar <c>(1 2)</c>.
    /// </summary>
    private bool DeclarationHasQuotedCompound()
    {
        bool arrayFlag = false;
        for (int i = _pos + 1; i < _tokens.Count && _tokens[i].Kind is BashTokenKind.Word or BashTokenKind.AssignmentWord; i++)
        {
            string raw = _tokens[i].Value;
            if (_tokens[i].Kind == BashTokenKind.Word && raw.Length > 1 && raw[0] == '-' && raw[1] != '-')
            {
                arrayFlag |= raw.Contains('a') || raw.Contains('A');
                continue;
            }
            if (!arrayFlag || StaticWordText(raw) is not { } text) continue;
            int eq = text.IndexOf('=');
            if (eq > 0 && text.Length - eq >= 3 && text[eq + 1] == '(' && text[^1] == ')'
                && IsValidVariableName(text[eq - 1] == '+' ? text[..(eq - 1)] : text[..eq]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// At an <c>(</c> token: returns the raw source text of the balanced <c>( … )</c> group and consumes the tokens it
    /// spans, or null (nothing consumed) when it never closes.
    /// </summary>
    private string? TakeBalancedParenText()
    {
        int start = Peek().Position;
        int depth = 0;
        for (int i = start; i < _input.Length; i++)
        {
            if (_input[i] == '(') depth++;
            else if (_input[i] == ')' && --depth == 0)
            {
                while (Peek().Kind != BashTokenKind.Eof && Peek().Position <= i) Advance();
                return _input[start..(i + 1)];
            }
        }
        return null;
    }

    /// <summary>Rule of the syntax error for a <c>(</c> inside an array literal: bash skips the rest of that line and continues.</summary>
    internal const string ArrayLiteralNestedParenRule = "ArrayLiteralNestedParen";

    private Assignment ParseAssignmentWord()
    {
        var token = Advance();
        var (name, value, op) = SplitAssignmentWord(token.Value);
        return new Assignment(name, op, value);
    }

    /// <summary>
    /// Parse an assignment word, consuming a trailing array literal <c>(a b c)</c> if present.
    /// </summary>
    private Assignment ParseAssignmentWordWithArray()
    {
        var token = Advance();
        var (name, value, op) = SplitAssignmentWord(token.Value);

        // Array assignment: arr=(a b c) or arr+=('x') — the assignment word
        // ends at '=' / '+=' and the next token is LParen. A plain empty
        // assignment (`x=`) is different: it assigns an explicit empty string.
        if (value is not null && value.Parts.IsEmpty && Peek().Kind == BashTokenKind.LParen)
        {
            // `a= (1 2)`: the ( is not glued to the =, so it is no array literal but a stray ( (bash: syntax error).
            if (Peek().Position != token.Position + token.Value.Length)
                throw MakeError("syntax error near unexpected token `('", Peek().Position, "ArrayLiteral");
            Advance(); // consume (
            var elements = ImmutableArray.CreateBuilder<CompoundWord>();
            while (Peek().Kind != BashTokenKind.RParen && Peek().Kind != BashTokenKind.Eof)
            {
                // Inside an array literal a newline is just whitespace — bash accepts
                // the multi-line form that nearly every real script uses:
                //     PATTERNS=(
                //       "a.js" "b.js"
                //       "c.js"
                //     )
                // Breaking here instead left the `)` unconsumed. Worse, for
                // `A=(x\ny)` the caller then read `y` as a COMMAND and `A` silently
                // lost an element — a wrong-output bug, not just a parse error.
                if (Peek().Kind == BashTokenKind.Newline)
                {
                    Advance();
                    continue;
                }

                // `k=v` inside the literal is lexed as an AssignmentWord but is an
                // ordinary element to bash: `arr=(k=v other)` has two elements.
                if (Peek().Kind is BashTokenKind.Word or BashTokenKind.AssignmentWord)
                {
                    var wordToken = Advance();
                    string elementText = SubscriptedElementText(wordToken);
                    if (!ReferenceEquals(elementText, wordToken.Value))
                        wordToken = wordToken with { Value = elementText };
                    // [k]=~ / [k]=a:~: the value of a subscripted element is an assignment value, so a tilde at its start
                    // or after a : expands (bash), which a plain word decomposition would leave literal.
                    var subscript = System.Text.RegularExpressions.Regex.Match(wordToken.Value, @"^\[[^\[\]]+\]=");
                    if (subscript.Success && subscript.Length < wordToken.Value.Length)
                    {
                        var elemParts = DecomposeWord(subscript.Value).ToBuilder();
                        elemParts.AddRange(DecomposeAssignmentValue(wordToken.Value[subscript.Length..]));
                        elements.Add(new CompoundWord(elemParts.ToImmutable()));
                    }
                    else
                        elements.Add(new CompoundWord(DecomposeWord(wordToken.Value)));
                }
                else
                {
                    break;
                }
            }
            // Anything but `)` here (`arr=(a ; b)`, or end of input) is a bash syntax
            // error. Returning the partial array used to leave the tail as leftover
            // tokens that silently re-parsed as commands.
            // `a=( inside=() )`, `b=( x (y) )`: a `(` in the literal. bash discards the line and keeps reading at the next
            // one (status 1), so this error is recoverable (BashTranspiler.RunnablePrefix).
            if (Peek().Kind == BashTokenKind.LParen)
                throw MakeError("syntax error near unexpected token `('", Peek().Position, ArrayLiteralNestedParenRule);
            if (Peek().Kind != BashTokenKind.RParen)
                throw MakeError(
                    $"Expected ')' to close array literal but got '{Peek().Value}' ({Peek().Kind})",
                    Peek().Position, "ArrayLiteral");
            Advance(); // consume )
            return new Assignment(name, op, null,
                new ArrayWord(elements.ToImmutable()));
        }

        return new Assignment(name, op, value);
    }

    /// <summary>
    /// <c>declare -A m=([x y]=1)</c>: bash reads an array-literal element's <c>[subscript]</c> to its matching <c>]</c>,
    /// blanks included, where the lexer ended the word at the blank (<c>[x</c>, <c>y]=1</c>: two plain elements). When
    /// <paramref name="token"/> opens a subscript that closes, followed by <c>=</c> / <c>+=</c>, in a later word, the
    /// element is the source text from the <c>[</c> to the end of that word, and the words it spans are consumed.
    /// Otherwise the token's own text (the same instance).
    /// </summary>
    private string SubscriptedElementText(BashToken token)
    {
        string v = token.Value;
        int start = token.Position, end = start + v.Length;
        if (v.Length < 2 || v[0] != '[' || start < 0 || end > _input.Length || !_input.AsSpan(start, v.Length).SequenceEqual(v))
            return v;
        int close = -1, depth = 0;
        for (int i = start; i < _input.Length && close < 0; i++)
        {
            char c = _input[i];
            if (c == '\\') { i++; continue; }
            if (c == '\'') { int q = _input.IndexOf('\'', i + 1); if (q < 0) return v; i = q; continue; }
            if (c == '"')
            {
                int j = i + 1;
                while (j < _input.Length && _input[j] != '"') j += _input[j] == '\\' ? 2 : 1;
                if (j >= _input.Length) return v;
                i = j;
                continue;
            }
            if (c is '\n' or ')' or ';' or '&' or '|' or '<' or '>') return v;
            if (c == '[') depth++;
            else if (c == ']' && --depth == 0) close = i;
        }
        if (close < end) return v;   // closed inside the word itself: an ordinary element
        bool assigns = close + 1 < _input.Length && (_input[close + 1] == '='
            || _input[close + 1] == '+' && close + 2 < _input.Length && _input[close + 2] == '=');
        if (!assigns) return v;
        while (end <= close && Peek().Kind is BashTokenKind.Word or BashTokenKind.AssignmentWord && Peek().Position <= close)
        {
            var next = Advance();
            end = next.Position + next.Value.Length;
        }
        return end > close + 1 ? _input[start..end] : v;
    }

    /// <summary>
    /// The scalar a prefix binding <c>B=(b "x y") cmd</c> gives the command: bash binds the literal text
    /// <c>(b x y)</c>, the elements expanded and joined by single spaces inside the parentheses.
    /// </summary>
    private static CompoundWord ArrayPrefixAsScalar(ArrayWord arr)
    {
        var parts = ImmutableArray.CreateBuilder<WordPart>();
        parts.Add(new WordPart.SingleQuoted("("));
        for (int i = 0; i < arr.Elements.Length; i++)
        {
            if (i > 0) parts.Add(new WordPart.SingleQuoted(" "));
            parts.AddRange(arr.Elements[i].Parts);
        }
        parts.Add(new WordPart.SingleQuoted(")"));
        return new CompoundWord(parts.ToImmutable());
    }

    private (string Name, CompoundWord? Value, AssignOp Op) SplitAssignmentWord(string raw)
    {
        int eqIndex = FindAssignmentEquals(raw);
        bool isPlus = eqIndex > 0 && raw[eqIndex - 1] == '+';
        string name = isPlus ? raw[..(eqIndex - 1)] : raw[..eqIndex];
        string valueRaw = raw[(eqIndex + 1)..];
        var op = isPlus ? AssignOp.PlusEqual : AssignOp.Equal;

        if (valueRaw.Length == 0)
            return (name, new CompoundWord(ImmutableArray<WordPart>.Empty), op);

        // Assignment values expand a tilde at the start AND after each unquoted
        // ':' (PATH-style), unlike ordinary words — use the assignment-aware path.
        var parts = DecomposeAssignmentValue(valueRaw);
        return (name, new CompoundWord(parts), op);
    }

    /// <summary>
    /// Locate the '=' that starts an assignment word's value, skipping any '=' that
    /// appears inside an unclosed <c>[...]</c> array subscript (e.g. <c>map[a=b]=c</c> is
    /// the key <c>a=b</c> assigned <c>c</c>, not the key <c>map[a</c> assigned <c>b]=c</c>).
    /// Falls back to the first '=' if brackets never close (defensive; keeps prior behavior
    /// for malformed input rather than throwing).
    /// </summary>
    private static int FindAssignmentEquals(string raw)
    {
        int depth = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '[')
                depth++;
            else if (c == ']' && depth > 0)
                depth--;
            else if (c == '=' && depth == 0)
                return i;
        }
        return raw.IndexOf('=');
    }

    /// <summary>
    /// Add a parsed redirect, except <c>&lt; /dev/stdin</c> (or <c>&lt; /dev/fd/0</c>) on fd 0: it reopens the stdin the
    /// command already has (<see cref="DevicePath"/>), so there is nothing to open and the command keeps its pipe.
    /// </summary>
    private static void AddRedirect(ICollection<Redirect> redirects, Redirect redirect)
    {
        if (redirect is { Op: "<", Fd: 0, FdVar: null, Target.Parts: [WordPart.Literal { Value: var target }] }
            && DevicePath.Classify(target, windows: false, out _) == DeviceKind.Stdin)
            return;
        redirects.Add(redirect);
    }

    private Redirect ParseRedirect(string? fdVar = null)
    {
        int fd = -1;

        if (Peek().Kind == BashTokenKind.IoNumber)
        {
            var fdToken = Advance();
            // A pathological digit run (e.g. `999999999999>file`) overflows int.
            // Bash rejects an out-of-range fd; convert to a clean ParseException
            // rather than the raw OverflowException that violated the fuzz invariant.
            if (!int.TryParse(fdToken.Value, out fd))
                throw MakeError($"file descriptor '{fdToken.Value}' out of range", fdToken.Position, "redirect");
        }

        var opToken = Advance();
        string op = opToken.Kind switch
        {
            BashTokenKind.Great => ">",
            BashTokenKind.DGreat => ">>",
            BashTokenKind.Less => "<",
            BashTokenKind.GreatAnd => ">&",
            BashTokenKind.LessAnd => "<&",
            BashTokenKind.AmpGreat => "&>",
            BashTokenKind.AmpDGreat => "&>>",
            BashTokenKind.DLess => "<<",
            BashTokenKind.DLessDash => "<<-",
            _ => opToken.Value,
        };

        // Default fd: 0 for input redirects, 1 for output redirects.
        if (fd == -1)
        {
            fd = op[0] == '<' ? 0 : 1;
        }

        // Consume the target word. It MUST be a word — a redirect with no target
        // (`echo > ; ls`, `cat <` at a separator/EOF) is a syntax error in bash.
        // Without this guard Advance() swallowed the following `;`/newline/operator
        // token as the "target", silently merging two statements.
        if (Peek().Kind != BashTokenKind.Word)
            throw MakeError($"syntax error near unexpected token `{Peek().Value}'", Peek().Position, "redirect");
        var targetToken = Advance();
        var targetParts = DecomposeWord(targetToken.Value);
        var target = new CompoundWord(targetParts);

        // `echo two 1<&2` / `2<&1`: duplicating onto stdout or stderr is the same dup2 in either direction, so it is
        // `1>&2` / `2>&1`. Kept as `<&` it was emitted raw, and PowerShell took the trailing `&` for its background op.
        if (op == "<&" && fd is 1 or 2 && fdVar is null)
            op = ">&";

        return new Redirect(op, fd, target, fdVar, Force: opToken.Value == ">|", ReadWrite: opToken.Value == "<>");
    }

    /// <summary>True for a <c>{varname}</c> named-fd prefix token (the lexer only
    /// emits this Word shape when a redirect immediately follows).</summary>
    private static bool IsNamedFdToken(string s)
    {
        if (s.Length < 3 || s[0] != '{' || s[^1] != '}')
            return false;
        string inner = s[1..^1];
        if (!(char.IsLetter(inner[0]) || inner[0] == '_'))
            return false;
        foreach (var ch in inner)
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                return false;
        return true;
    }

    /// <summary>
    /// Collect trailing file-descriptor redirects that follow a COMPOUND command's
    /// closing keyword (<c>done</c> / <c>fi</c> / <c>esac</c> / <c>}</c>), e.g.
    /// <c>while ...; done &lt; input.txt</c> or <c>for x in 1 2; do ...; done &gt; out</c>.
    /// Bash attaches the redirect to the whole compound; the emitter wraps the body in
    /// <c>&amp; { ... }</c> and applies these. Heredoc operators (<c>&lt;&lt;</c> /
    /// <c>&lt;&lt;-</c>) are intentionally excluded — their body collection is driven by
    /// the simple-command loop, not by <see cref="ParseRedirect"/>, so a compound heredoc
    /// stays out of scope here rather than being mis-parsed as a plain redirect target.
    /// </summary>
    /// <summary>
    /// Consume <c>&lt;&lt;&lt; word</c> and turn the word into a here-doc body. Shared by
    /// the simple-command path and the compound-command trailing-redirect path so both
    /// agree on quote stripping, expansion, and the trailing newline.
    /// </summary>
    private HereDoc ParseHereString()
    {
        Advance(); // consume <<<
        var wordToken = Advance();
        string raw = wordToken.Value;

        // Strip surrounding quotes from the here-string word. Require at least two
        // chars so a lone quote (`<<< '`) — where StartsWith and EndsWith both see the
        // SAME single char — is not sliced to a start-past-end range (raw[1..^1] on
        // length 1 threw ArgumentOutOfRangeException).
        bool expand = true;
        bool unquoted = true;
        // `<<< $'a\nb'`: an ANSI-C word is decoded here (its escapes are the text) and is never re-expanded.
        if (raw.Length >= 3 && raw.StartsWith("$'", StringComparison.Ordinal) && raw.EndsWith('\''))
        {
            expand = false;
            unquoted = false;
            raw = PsEmitter.ExpandAnsiCEscapes(raw[2..^1]);
        }
        else if (raw.Length >= 2
            && ((raw.StartsWith('\'') && raw.EndsWith('\''))
                || (raw.StartsWith('"') && raw.EndsWith('"'))))
        {
            expand = raw[0] == '"';
            unquoted = false;
            raw = raw[1..^1];
        }

        // bash: a here-string supplies the word PLUS a trailing newline to the
        // command's stdin. `cat <<< "hello"` prints "hello\n", not "hello". The emitter
        // wraps the body in a here-string @"..."@ which itself swallows the @-line
        // newlines, so encode the trailing newline into the body value here.
        // An unquoted <<< ~/x word expands its tilde (the body is a PowerShell here-string, which expands $HOME).
        if (unquoted && raw.Length > 0 && raw[0] == '~')
        {
            int slash = raw.IndexOf('/');
            string prefix = slash < 0 ? raw[1..] : raw[1..slash];
            string? home = prefix switch { "" => "${HOME}", "+" => "${env:PWD}","-" => "${env:OLDPWD}", _ => null };
            if (home is not null)
                raw = home + (slash < 0 ? "" : raw[slash..]);
        }

        // Always, even when the word already ends in a newline: `mapfile -t a <<< $'hello\n'` reads two lines.
        raw += "\n";

        // The word itself, parsed as any word is (without brace expansion, which a here-string does not get): the emitter
        // expands it from this. REGRESSION: the text above, unquoted by hand and expanded as a heredoc body, kept `\"` and
        // inner quotes (`<<< a\"b`, `<<< "a"'b'`, `<<< "a\\\"b"` printed a\"b, "a"'b', a\\"b).
        return new HereDoc(raw, expand, StripTabs: false,
            Word: new CompoundWord(DecomposeWord(wordToken.Value, braceExpansion: false)));
    }

    private ImmutableArray<Redirect> ParseTrailingRedirects()
    {
        if (!IsNamedCompoundRedirect() && Peek().Kind != BashTokenKind.IoNumber
            && Peek().Kind != BashTokenKind.TLess
            && Peek().Kind is not (BashTokenKind.DLess or BashTokenKind.DLessDash)
            && !IsCompoundRedirectOp(Peek().Kind))
            return ImmutableArray<Redirect>.Empty;

        var redirects = ImmutableArray.CreateBuilder<Redirect>();
        while (true)
        {
            if (IsNamedCompoundRedirect())
            {
                var fdVar = Advance().Value[1..^1];
                // `{ ...; } {abc}<<< walrus`: a here-string on a descriptor of its own, its number stored in abc.
                if (Peek().Kind == BashTokenKind.TLess)
                    redirects.Add(new Redirect("<<<", 0, new CompoundWord(ImmutableArray<WordPart>.Empty), FdVar: fdVar, Here: ParseHereString()));
                else
                    AddRedirect(redirects, ParseRedirect(fdVar));
            }
            else if (Peek().Kind == BashTokenKind.TLess)
            {
                // `done <<< "$output"` — a here-string feeding a whole loop. Before
                // this, `<<<` was not a compound redirect op at all, so the operator
                // and its word were left unconsumed: the here-string was DROPPED and
                // the loop silently read nothing (and in some surroundings the stray
                // tokens emitted an empty PowerShell pipe element, breaking the parse
                // of the entire file).
                var here = ParseHereString();
                redirects.Add(new Redirect("<<<", 0, new CompoundWord(
                    ImmutableArray<WordPart>.Empty), FdVar: null, Here: here));
            }
            else if (Peek().Kind is BashTokenKind.DLess or BashTokenKind.DLessDash)
            {
                // `{ sort; } <<EOF` / `( cat ) <<EOF`: a here-document feeding a whole compound.
                // Same body capture as a simple command (the lexer stamped the span on the
                // delimiter token); the AST carries it like a here-string: Op "<<", Here = the body.
                bool stripTabs = Peek().Kind == BashTokenKind.DLessDash;
                Advance(); // << or <<-
                var delimToken = Advance();
                var (delimiter, expand) = BashLexer.ParseHeredocDelimiter(delimToken.Value);
                var docs = ImmutableArray.CreateBuilder<HereDoc>(1);
                CollectHereDocBodies(
                    new() { (delimiter, expand, stripTabs, null, delimToken.BodyStart, delimToken.BodyEnd, 0) },
                    docs);
                redirects.Add(new Redirect("<<", 0, new CompoundWord(
                    ImmutableArray<WordPart>.Empty), FdVar: null, Here: docs[0]));
            }
            else if (Peek().Kind == BashTokenKind.IoNumber || IsCompoundRedirectOp(Peek().Kind))
            {
                AddRedirect(redirects, ParseRedirect());
            }
            else
            {
                break;
            }
        }
        return redirects.ToImmutable();
    }

    private bool IsNamedCompoundRedirect() =>
        Peek().Kind == BashTokenKind.Word
        && IsNamedFdToken(Peek().Value)
        && _pos + 1 < _tokens.Count
        && (IsCompoundRedirectOp(_tokens[_pos + 1].Kind) || _tokens[_pos + 1].Kind == BashTokenKind.TLess);

    private static bool IsCompoundRedirectOp(BashTokenKind kind) =>
        IsRedirectOp(kind)
            && kind is not BashTokenKind.DLess
            && kind is not BashTokenKind.DLessDash;

    private static bool IsRedirectOp(BashTokenKind kind) =>
        kind is BashTokenKind.Great or BashTokenKind.DGreat
            or BashTokenKind.Less or BashTokenKind.GreatAnd
            or BashTokenKind.LessAnd or BashTokenKind.AmpGreat
            or BashTokenKind.AmpDGreat or BashTokenKind.DLess
            or BashTokenKind.DLessDash;

    private static bool IsCompoundDelimiter(string word) =>
        word is "then" or "elif" or "else" or "fi"
            or "do" or "done" or "esac";
}
