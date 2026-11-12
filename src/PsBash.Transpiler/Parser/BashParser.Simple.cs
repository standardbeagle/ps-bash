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

    /// <summary>`declare`/`typeset` followed directly by a NAME[=VAL] word (no -g/-x/-A… flag) inside a function body.</summary>
    private bool IsFunctionScopedDeclare()
        => _functionDepth > 0
           && Peek().Value is "declare" or "typeset"
           && _pos + 1 < _tokens.Count
           && _tokens[_pos + 1] is { Kind: BashTokenKind.Word or BashTokenKind.AssignmentWord } next
           && !next.Value.StartsWith('-');

    /// <summary>`command export x=1`, `builtin local y`, `\builtin declare z`, `command command readonly r=1`: the wrappers
    /// only bypass functions and aliases, so a declaration builtin behind them is parsed exactly as the bare form (otherwise
    /// the runtime looks `export` up as an external command and reports "command not found").</summary>
    private void SkipDeclarationWrappers()
    {
        int k = 0;
        static string Bare(string v) => v.StartsWith('\\') ? v[1..] : v;
        while (_pos + k + 1 < _tokens.Count
               && _tokens[_pos + k] is { Kind: BashTokenKind.Word } w && Bare(w.Value) is "builtin" or "command"
               && _tokens[_pos + k + 1] is { Kind: BashTokenKind.Word or BashTokenKind.AssignmentWord } nx
               && !nx.Value.StartsWith('-'))
            k++;
        if (k == 0) return;
        if (_tokens[_pos + k] is { Kind: BashTokenKind.Word } d && Bare(d.Value) is "export" or "local" or "declare" or "typeset" or "readonly")
        {
            // `builtin export z=$x`: behind a wrapper the operands are ordinary words (split, globbed), not assignments.
            // An operand with an expansion keeps the wrapper; the emitter runs it through the run-time declaration.
            for (int j = _pos + k + 1; j < _tokens.Count && _tokens[j].Kind is BashTokenKind.Word or BashTokenKind.AssignmentWord; j++)
                if (_tokens[j].Value.AsSpan().IndexOfAny('$', '`') >= 0) return;
            // `builtin declare a=(x y)` is a syntax error in bash (an array literal needs the bare declaration builtin).
            int saved = _pos;
            _pos += k;
            if (DeclarationHasArrayInitializer()) _pos = saved;
        }
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
        // `export` / `local` NAME=VAL … — see ParseDeclarationPairs for the shape.
        if (Peek().Kind == BashTokenKind.Word && (Peek().Value is "export" or "local" || IsFunctionScopedDeclare()))
        {
            // Inside a function a plain `declare NAME=VAL` is function-local, exactly like `local`.
            bool isLocal = Peek().Value is "local" or "declare" or "typeset";
            int saved = _pos;
            Advance(); // consume the keyword

            // `local -p [NAME…]` is a listing, not a declaration: leave it to the emitter.
            if (!(Peek().Kind == BashTokenKind.Word && Peek().Value == "-p" && _tokens[saved].Value == "local")
                && ParseDeclarationPairs() is { } pairs)
                return new Command.ShAssignment(pairs, IsLocal: isLocal, IsExport: !isLocal, IsReadonly: isLocal && _declSawReadonly, IsNameref: isLocal && _declSawNameref);

            // No assignment or name followed — rewind and parse as a normal command
            // so forms like `export -p` still reach the general path.
            _pos = saved;
        }

        // `declare -a A=(x y)` / `readonly C=(m n)` — an ARRAY initializer only.
        // Everything else about declare/typeset (the -i, -A, -p attribute handling)
        // stays on the emitter's TryEmitDeclare path, which this must not disturb:
        // it only claims the command when an actual `NAME=(` array literal is present.
        // Without this the array was dropped entirely (`declare -a A=(x y)` emitted
        // `$global:A = ''`) and the unconsumed `(x y)` desynced the parse, swallowing
        // the command that followed.
        if (Peek().Kind == BashTokenKind.Word
            && Peek().Value is "declare" or "typeset" or "readonly"
            && DeclarationHasArrayInitializer())
        {
            int saved = _pos;
            bool isReadonlyKw = Peek().Value == "readonly";
            Advance(); // consume the keyword

            // `readonly b[7]=8` is an error in bash (`declare -r b[7]=8` is not): leave it to the simple-command
            // path, whose identifier check reports it.
            if (ParseDeclarationPairs() is { } declPairs && !(isReadonlyKw && declPairs.Any(p => p.Name.Contains('['))))
                return new Command.ShAssignment(declPairs, IsReadonly: isReadonlyKw || _declSawReadonly, IsAssoc: _declSawAssoc, IsDeclare: true, IsIndexed: _declSawIndexed && !_declSawAssoc,
                    Redirects: _declRedirects.Count == 0 ? default : [.. _declRedirects]);

            _pos = saved;
        }

        // Collect leading assignment words (VAR=val ...).
        // Array assignments like arr=(a b c) are detected and consumed here.
        int simpleLine = BashLexer.LineAt(_input, Peek().Position);
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

        // Array assignments with no command after them are an assignment statement. Before a command word
        // (`B=(b b) cmd`) bash binds the temporary as the STRING "(b b)": the expanded elements joined by spaces.
        if (hasArrayAssignment && Peek().Kind != BashTokenKind.Word)
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
                        redirects.Add(ParseRedirect(fdVar));
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
                // `!=` / `!x` is ONE word when the text is adjacent (`test a != b`, `echo a !=b`).
                string bangText = token.Value;
                while (Peek().Kind == BashTokenKind.Word && Peek().Position == token.Position + bangText.Length)
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
                redirects.Add(ParseRedirect());
            }
            else
            {
                break;
            }
        }

        // If only assignments and no command words, it's a bare assignment.
        if (assignmentPairs.Count > 0 && words.Count == 0 && redirects.Count == 0
            && pendingHeredocs.Count == 0 && hereDocs.Count == 0)
            return new Command.ShAssignment(assignmentPairs.ToImmutable());

        // Collect heredoc bodies in order for all pending heredoc redirects.
        if (pendingHeredocs.Count > 0)
            CollectHereDocBodies(pendingHeredocs, hereDocs);

        // `\break` / `\return`: the backslash only escapes the letter, so it is still the control builtin.
        if (words.Count > 0 && EscapedControlBuiltin(words[0]) is { } controlName)
            words[0] = new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.Literal(controlName)));

        return new Command.Simple(
            words.ToImmutable(),
            envPairs.ToImmutable(),
            redirects.ToImmutable(),
            hereDocs.ToImmutable(),
            simpleLine);
    }

    private static string? EscapedControlBuiltin(CompoundWord word)
    {
        if (!word.Parts.Any(p => p is WordPart.EscapedLiteral)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var p in word.Parts)
        {
            if (p is WordPart.Literal l) sb.Append(l.Value);
            else if (p is WordPart.EscapedLiteral e) sb.Append(e.Value);
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
    /// <summary>The redirects <see cref="ParseDeclarationPairs"/> consumed (the builtin's own).</summary>
    private readonly List<Redirect> _declRedirects = [];

    private ImmutableArray<Assignment>? ParseDeclarationPairs()
    {
        _declSawReadonly = false;
        _declSawNameref = false;
        _declSawAssoc = false;
        _declSawIndexed = false;
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
            // leaving it unconsumed made the statement end early. Known gap: bash
            // still creates/truncates a `> file` target, which this does not.
            // `declare` keeps them (_declRedirects): a refused kind change reports there.
            if (Peek().Kind == BashTokenKind.IoNumber || IsCompoundRedirectOp(Peek().Kind))
            {
                _declRedirects.Add(ParseRedirect());
                continue;
            }

            if (Peek().Kind == BashTokenKind.AssignmentWord)
            {
                pairs.Add(ParseAssignmentWordWithArray());
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
                return null;

            Advance();
            pairs.Add(new Assignment(value, AssignOp.Equal, null));
        }

        return pairs.Count > 0 ? pairs.ToImmutable() : null;
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

        return new Redirect(op, fd, target, fdVar, Force: opToken.Value == ">|");
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
            string? home = prefix switch { "" => "${HOME}", "+" => "${PWD}", "-" => "${env:OLDPWD}", _ => null };
            if (home is not null)
                raw = home + (slash < 0 ? "" : raw[slash..]);
        }

        if (!raw.EndsWith('\n'))
            raw += "\n";

        return new HereDoc(raw, expand, StripTabs: false);
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
                redirects.Add(ParseRedirect(fdVar));
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
                redirects.Add(ParseRedirect());
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
        && IsCompoundRedirectOp(_tokens[_pos + 1].Kind);

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
