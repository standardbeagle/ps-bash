using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// Exception thrown when the bash parser encounters unsupported or invalid syntax.
/// Includes source location (line/column) and the parser rule that failed.
/// </summary>
public sealed class ParseException : Exception
{
    public int Line { get; }
    public int Column { get; }
    public string Rule { get; }

    public ParseException(string message, int line, int column, string rule)
        : base(message)
    {
        Line = line;
        Column = column;
        Rule = rule;
    }

    /// <summary>
    /// Compute 1-based line and column from a zero-based character offset in the input.
    /// </summary>
    internal static (int Line, int Column) ComputeLineCol(string input, int position)
    {
        int line = 1;
        int col = 1;
        int end = Math.Min(position, input.Length);
        for (int i = 0; i < end; i++)
        {
            if (input[i] == '\n')
            {
                line++;
                col = 1;
            }
            else
            {
                col++;
            }
        }
        return (line, col);
    }
}

/// <summary>
/// Hand-rolled recursive descent parser for bash input.
/// Consumes the flat token list produced by <see cref="BashLexer"/> and builds an AST.
/// The implementation is split across partial files: this file holds the spine
/// (token cursor, list/and-or/pipeline, compound dispatch); <c>BashParser.Words.cs</c>
/// holds word decomposition (raw WORD text -> typed WordPart children).
/// </summary>
public sealed partial class BashParser
{
    private readonly List<BashToken> _tokens;
    private readonly string _input;
    private int _pos;

    private BashParser(List<BashToken> tokens, string input)
    {
        _tokens = tokens;
        _input = input;
        _pos = 0;
    }

    /// <summary>
    /// Parse the given bash input into an AST command node.
    /// Returns null if the input is empty or contains only whitespace/comments.
    /// </summary>
    public static Command? Parse(string input)
    {
        return NormalizeArithmeticErrors(() =>
        {
            var tokens = BashLexer.Tokenize(input);
            var parser = new BashParser(tokens, input);
            return parser.ParseCommand();
        });
    }

    /// <summary>
    /// Parse the given bash input into a sequence of top-level statements,
    /// each annotated with the character offset of its first token. Top-level
    /// statements are the elements of a <see cref="Command.CommandList"/>;
    /// for a single-command input the result has one entry. Returns an empty
    /// list for empty/whitespace/comment-only input.
    /// </summary>
    /// <remarks>
    /// Used by <c>BashTranspiler.TranspileWithMap</c> to build the bash↔pwsh
    /// line map without requiring source-position fields on every AST node.
    /// </remarks>
    public static IReadOnlyList<(Command Command, int Position)> ParseTopLevelWithPositions(string input)
    {
        return NormalizeArithmeticErrors<IReadOnlyList<(Command Command, int Position)>>(() =>
        {
            var tokens = BashLexer.Tokenize(input);
            var parser = new BashParser(tokens, input);
            return parser.ParseTopLevelWithPositionsCore();
        });
    }

    private static T NormalizeArithmeticErrors<T>(Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (BashArithmeticParseException ex)
        {
            // Arithmetic has its own typed parser, but every public bash-parser
            // entry point must retain the clean, location-bearing error contract.
            throw new ParseException(ex.Message, 1, 1, "arithmetic");
        }
    }

    private List<(Command, int)> ParseTopLevelWithPositionsCore()
    {
        var result = new List<(Command, int)>();
        SkipNewlines();
        while (Peek().Kind != BashTokenKind.Eof)
        {
            int startPos = Peek().Position;
            result.Add((ParseListStatement(AtEndOfInputOnly, "ParseTopLevelWithPositions"), startPos));
            SkipTerminators();
        }
        return result;
    }

    private static bool AtEndOfInputOnly() => false;

    /// <summary>
    /// Parse ONE statement of a statement list — an and-or list plus an optional
    /// background <c>&amp;</c> — and enforce the one rule every list shares: after a
    /// statement only a terminator (<c>;</c> / newline), end of input, or the
    /// enclosing construct's own closing token (<paramref name="atStop"/>: <c>}</c>,
    /// <c>)</c>, <c>fi</c>, <c>done</c>, <c>esac</c>, <c>;;</c>) may follow. A background
    /// <c>&amp;</c> is itself a terminator, so anything may follow it.
    /// </summary>
    /// <remarks>
    /// Any other token means the statement parser stopped early. Before this rule the
    /// top level silently DROPPED the rest of the script, and every compound body
    /// re-read the leftover as the start of a new command with no separator
    /// (<c>rm -f !(keep).txt</c> became <c>rm -f !</c>, a subshell and a <c>.txt</c>
    /// command). The top-level loops and every body loop call this, so none can
    /// disagree on what counts as a syntax error.
    /// </remarks>
    private Command ParseListStatement(Func<bool> atStop, string rule)
    {
        var cmd = ParseAndOrProgress();

        if (Peek().Kind == BashTokenKind.Amp)
        {
            Advance(); // consume &
            return new Command.Background(cmd);
        }

        var kind = Peek().Kind;
        if (kind is BashTokenKind.Eof or BashTokenKind.Semi or BashTokenKind.Newline || atStop())
            return cmd;

        throw MakeError(
            $"Unexpected token '{Peek().Value}' ({kind})",
            Peek().Position, rule);
    }

    private ParseException MakeError(string message, int position, string rule)
    {
        var (line, col) = ParseException.ComputeLineCol(_input, position);
        return new ParseException(
            $"{message} at line {line}, col {col}",
            line, col, rule);
    }

    // Peek clamps to the trailing Eof sentinel so an unguarded read at/just-past the end
    // returns Eof rather than throwing ArgumentOutOfRangeException (a raw crash). Eof at the
    // tail is a legitimate Peek target (loops guard on `Peek().Kind != Eof`).
    private BashToken Peek() => _tokens[_pos < _tokens.Count ? _pos : _tokens.Count - 1];

    // Advance, by contrast, must REFUSE to consume the Eof sentinel: a truncated construct
    // (`for`, `function`, `2>&`, `echo $(`, …) reaches Advance() expecting another token but
    // finds only Eof. Throwing a clean ParseException here both (a) prevents the old raw
    // ArgumentOutOfRangeException crash and (b) prevents an infinite loop — a `while (Peek != X)
    // Advance()` that never finds X would spin forever if Advance silently stalled at Eof.
    // Valid parsing never advances the Eof token (it Peek-guards), so this only fires on
    // genuinely incomplete input, converting it to a positioned "unexpected end of input".
    private BashToken Advance()
    {
        var token = Peek();
        if (token.Kind == BashTokenKind.Eof)
            throw MakeError("Unexpected end of input", token.Position, "Advance");
        _pos++;
        return token;
    }

    private Command? ParseCommand()
    {
        SkipNewlines();

        if (Peek().Kind == BashTokenKind.Eof)
            return null;

        return ParseList();
    }

    private Command ParseList()
    {
        // ParseList is only entered with a non-Eof token (Parse() short-circuits
        // empty input). A first statement that consumes nothing (a leading stray
        // `)`/`}`) throws inside ParseAndOrProgress rather than yielding an empty
        // command.
        var commands = ImmutableArray.CreateBuilder<Command>();
        commands.Add(ParseListStatement(AtEndOfInputOnly, "ParseList"));

        while (true)
        {
            SkipTerminators();
            if (Peek().Kind == BashTokenKind.Eof)
                break;

            commands.Add(ParseListStatement(AtEndOfInputOnly, "ParseList"));
        }

        if (commands.Count == 1)
            return commands[0];

        return new Command.CommandList(commands.ToImmutable());
    }

    // Parse a unit that MUST consume at least one token, else the operand is missing
    // (a dangling `&&`/`||`/`|`, a leading binary operator, or EOF) — reject cleanly
    // instead of building an empty command that emits unparseable PowerShell.
    private Command RequireCommand(Func<Command> parse, string afterWhat, string rule)
    {
        int before = _pos;
        var cmd = parse();
        if (_pos == before)
            throw MakeError($"Expected a command {afterWhat} but got '{Peek().Value}' ({Peek().Kind})",
                Peek().Position, rule);
        return cmd;
    }

    private Command ParseAndOr()
    {
        int firstStart = _pos;
        var first = ParsePipeline();

        if (Peek().Kind is not BashTokenKind.AndIf and not BashTokenKind.OrIf)
            return first;

        // A `&&`/`||` with no left operand (leading binary operator, e.g. `&& x`).
        if (_pos == firstStart)
            throw MakeError($"Expected a command before '{Peek().Value}'",
                Peek().Position, "ParseAndOr");

        var commands = ImmutableArray.CreateBuilder<Command>();
        var ops = ImmutableArray.CreateBuilder<string>();
        commands.Add(first);

        while (Peek().Kind is BashTokenKind.AndIf or BashTokenKind.OrIf)
        {
            var opToken = Advance();
            ops.Add(opToken.Value);
            NestingGuard.CheckBreadth(ops.Count, NestingGuard.MaxChainLength, "&&/|| list");
            // In bash, a newline after && or || is a line continuation.
            SkipNewlines();
            commands.Add(RequireCommand(ParsePipeline, $"after '{opToken.Value}'", "ParseAndOr"));
        }

        return new Command.AndOrList(commands.ToImmutable(), ops.ToImmutable());
    }

    private Command ParsePipeline()
    {
        // Consume a RUN of leading `!`. bash allows `! ! cmd` (double negation =
        // identity); negation toggles per `!`, so an even count is identity and
        // an odd count negates. Reading only one `!` left the second as a stray
        // Bang that ParseSimpleCommand turned into an empty command (dropping cmd).
        var negated = false;
        while (Peek().Kind == BashTokenKind.Bang)
        {
            negated = !negated;
            Advance();
        }

        var first = ParseCompoundOrSimple();
        if (Peek().Kind is not BashTokenKind.Pipe and not BashTokenKind.PipeAmp)
        {
            if (negated)
                return new Command.Pipeline(
                    [first], ImmutableArray<string>.Empty, Negated: true);
            return first;
        }

        var commands = ImmutableArray.CreateBuilder<Command>();
        var ops = ImmutableArray.CreateBuilder<string>();
        commands.Add(first);

        while (Peek().Kind is BashTokenKind.Pipe or BashTokenKind.PipeAmp)
        {
            var isPipeAmp = Peek().Kind == BashTokenKind.PipeAmp;
            Advance(); // consume | or |&

            ops.Add(isPipeAmp ? "|&" : "|");
            NestingGuard.CheckBreadth(ops.Count + 1, NestingGuard.MaxPipelineStages, "pipeline");

            // In bash, a newline after | is a line continuation — skip newlines
            // before reading the next pipeline command.
            SkipNewlines();

            commands.Add(RequireCommand(ParseCompoundOrSimple, "after '|'", "ParsePipeline"));
        }

        return new Command.Pipeline(commands.ToImmutable(), ops.ToImmutable(), Negated: negated);
    }

    private Command ParseCompoundOrSimple()
    {
        // Every compound nesting (if/while/for/case/(…)/{…}, and command-sub bodies, which re-enter
        // the parser) passes through here once per level. Bounded so input cannot overflow the stack.
        using var nesting = NestingGuard.Enter();
        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "if")
            return ParseIf();

        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "for")
            return ParseFor();

        if (Peek().Kind == BashTokenKind.Word && Peek().Value is "while" or "until")
            return ParseWhile();

        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "case")
            return ParseCase();

        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "select")
            return ParseSelect();

        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "function")
            return ParseFunction();

        if (Peek().Kind == BashTokenKind.Word && Peek().Value is "[" or "[[")
            return ParseTestExpr();

        // name() { body } form: Word followed by LParen RParen
        if (Peek().Kind == BashTokenKind.Word && IsParensFunctionDef())
            return ParseParensFunction();

        // Standalone arithmetic: (( expr ))
        if (Peek().Kind == BashTokenKind.LParen && IsDoubleLParen())
            return ParseArithCommand();

        // Subshell: (cmd1; cmd2)
        if (Peek().Kind == BashTokenKind.LParen)
            return ParseSubshell();

        // Standalone brace group: { cmd1; cmd2; }
        if (Peek().Kind == BashTokenKind.LBrace)
            return ParseStandaloneBraceGroup();

        return ParseSimpleCommand();
    }

    private Command.BoolExpr ParseTestExpr()
    {
        var open = Advance(); // consume "[" or "[["
        bool extended = open.Value == "[[";
        string close = extended ? "]]" : "]";

        var inner = ImmutableArray.CreateBuilder<CompoundWord>();
        while (Peek().Kind != BashTokenKind.Eof)
        {
            if (Peek().Kind == BashTokenKind.Word && Peek().Value == close)
            {
                Advance(); // consume closing bracket
                break;
            }

            // Inside [[ ]], && and || are logical operators, not shell operators.
            // The lexer produces them as AndIf/OrIf tokens, so consume them as words.
            if (extended && Peek().Kind is BashTokenKind.AndIf or BashTokenKind.OrIf)
            {
                var opToken = Advance();
                inner.Add(new CompoundWord(ImmutableArray.Create<WordPart>(
                    new WordPart.Literal(opToken.Value))));
                continue;
            }

            // Inside test expressions, operator tokens like <, >, ! are comparison
            // operators, not shell redirects/negation. Consume them as literal words.
            if (IsTestOperatorToken(Peek().Kind))
            {
                var opToken = Advance();
                // Handle != (Bang followed by word starting with =)
                // ADJACENT only: `[ a != b ]` is one operator, but `[ ! = x ]` has a space
                // between the words, so `!` is a negation/operand and `=` the binary operator.
                if (opToken.Kind == BashTokenKind.Bang
                    && Peek().Kind == BashTokenKind.Word
                    && Peek().Value.StartsWith('=')
                    && Peek().Position == opToken.Position + opToken.Value.Length)
                {
                    var eqToken = Advance();
                    inner.Add(new CompoundWord(ImmutableArray.Create<WordPart>(
                        new WordPart.Literal("!" + eqToken.Value))));
                }
                else
                {
                    inner.Add(new CompoundWord(ImmutableArray.Create<WordPart>(
                        new WordPart.Literal(opToken.Value))));
                }
                continue;
            }

            // The right-hand side of =~ is a REGEX, not a shell token stream: in
            // `[[ $x =~ ^(a|b)$ ]]` the parens belong to the pattern. The lexer is
            // context-free and emits them as LParen/RParen, so consuming tokens here
            // would stop at the `(` — silently truncating the regex to `^` on the
            // `&&` path, and throwing "Expected 'then'" from inside an `if`.
            // Bash draws the same line: `(` groups in a [[ ]] condition, EXCEPT after
            // =~ where it is regex syntax. So re-read the RHS straight from the source
            // as one whitespace-delimited word.
            //
            // This MUST run before the grouping-paren branch below: a regex that
            // STARTS with `(` (e.g. `(a)(b)`) was otherwise grabbed by that branch as
            // a test group, splitting the pattern and leaving the `=~` with no RHS
            // (emitted the "unsupported test operator" diagnostic and matched nothing).
            if (extended && LastInnerWordIs(inner, "=~"))
            {
                inner.Add(ConsumeRegexOperand());
                continue;
            }

            // Grouping parens: `[[ ! -e $f || ( -f $f && ! -L $f ) ]]`. The lexer emits
            // them as LParen/RParen, and the loop used to BREAK on them — leaving the
            // group's operands out of the word list, so the whole clause silently
            // evaluated to a constant. Consume them as literal words; the emitter's
            // depth-aware splitter turns them back into PowerShell grouping.
            if (extended && Peek().Kind is BashTokenKind.LParen or BashTokenKind.RParen)
            {
                var parenToken = Advance();
                inner.Add(new CompoundWord(ImmutableArray.Create<WordPart>(
                    new WordPart.Literal(parenToken.Value))));
                continue;
            }

            if (Peek().Kind == BashTokenKind.Word)
            {
                var token = Advance();
                var parts = DecomposeWord(token.Value);
                inner.Add(new CompoundWord(parts));
            }
            else
            {
                break;
            }
        }

        // Each -o/-a/&&/|| costs about two words; bound the expression before the emitter lowers it to
        // a PowerShell -or/-and chain (see NestingGuard.MaxChainLength).
        NestingGuard.CheckBreadth(inner.Count, 2 * NestingGuard.MaxChainLength, "test expression");
        return new Command.BoolExpr(inner.ToImmutable(), extended);
    }

    /// <summary>True when the last collected test word is exactly <paramref name="op"/>.</summary>
    private static bool LastInnerWordIs(
        ImmutableArray<CompoundWord>.Builder inner, string op)
    {
        if (inner.Count == 0) return false;
        var parts = inner[^1].Parts;
        return parts.Length == 1
            && parts[0] is WordPart.Literal lit
            && lit.Value == op;
    }

    /// <summary>
    /// Consume the =~ right-hand side as ONE raw, whitespace-delimited word starting at
    /// the current token, then skip every token the lexer produced inside that span.
    /// The scan is quote-aware so `[[ $x =~ "a b" ]]` keeps its quoted space, and it
    /// stops at the first unquoted whitespace so the trailing `]]` is never absorbed.
    /// A regex with no spaces (`[[:digit:]]+`, `^(a|b)$`) is therefore taken whole.
    /// </summary>
    private CompoundWord ConsumeRegexOperand()
    {
        int start = Peek().Position;
        int i = start;
        char quote = '\0';

        while (i < _input.Length)
        {
            char c = _input[i];

            if (quote == '\0' && c == '\\' && i + 1 < _input.Length) { i += 2; continue; }
            if (quote == '\0' && (c == '"' || c == '\'')) { quote = c; i++; continue; }
            if (quote != '\0')
            {
                // Inside single quotes a backslash is literal; inside double quotes it escapes.
                if (quote == '"' && c == '\\' && i + 1 < _input.Length) { i += 2; continue; }
                if (c == quote) quote = '\0';
                i++;
                continue;
            }
            if (c is ' ' or '\t' or '\n' or '\r') break;
            i++;
        }

        var raw = _input[start..i];

        // Skip the tokens the lexer split this span into. A token that starts at or
        // past the span end is the next real word (`]]`), so it must survive.
        while (_pos < _tokens.Count
            && _tokens[_pos].Kind != BashTokenKind.Eof
            && _tokens[_pos].Position < i)
        {
            _pos++;
        }

        return new CompoundWord(DecomposeRegexWord(raw));
    }

    /// <summary>
    /// Decompose the =~ right-hand side. Parameter/command expansions still expand
    /// (bash: <c>re='^a.b$'; [[ $x =~ $re ]]</c> matches), but EVERY other character —
    /// crucially backslashes and <c>$</c> anchors — passes through verbatim to the
    /// regex engine. Running the ordinary word decomposer here would turn <c>\.</c>
    /// into an escaped literal <c>.</c> (which then matches ANY character, silently
    /// widening the pattern) and would try to read <c>$)</c> as a variable.
    /// Verified against the oracle: <c>[[ axb =~ ^a\.b$ ]]</c> does NOT match.
    /// </summary>
    private ImmutableArray<WordPart> DecomposeRegexWord(string raw)
    {
        var parts = ImmutableArray.CreateBuilder<WordPart>();
        var literal = new System.Text.StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            parts.Add(new WordPart.Literal(
                TranslatePosixClassesForRegex(literal.ToString())));
            literal.Clear();
        }

        for (int i = 0; i < raw.Length;)
        {
            if (raw[i] == '$' && i + 1 < raw.Length && IsExpansionStart(raw[i + 1]))
            {
                int end = FindExpansionEnd(raw, i);
                if (end > i)
                {
                    FlushLiteral();
                    parts.AddRange(DecomposeWord(raw[i..end]));
                    i = end;
                    continue;
                }
            }

            literal.Append(raw[i]);
            i++;
        }

        FlushLiteral();
        return parts.ToImmutable();
    }

    /// <summary>
    /// POSIX bracket classes (<c>[[:digit:]]</c>) are valid ERE but NOT valid .NET regex —
    /// .NET would read <c>[[:digit:]]</c> as the character set <c>[:digt</c>. Rewrite the
    /// inner <c>[:name:]</c> to its .NET equivalent, leaving the surrounding bracket
    /// expression intact so <c>[^[:space:]]</c> and <c>[[:digit:]_-]</c> still work.
    /// Unknown class names are left alone rather than guessed at.
    /// </summary>
    internal static string TranslatePosixClassesForRegex(string s)
    {
        if (!s.Contains("[:", StringComparison.Ordinal)) return s;

        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length;)
        {
            if (s[i] == '[' && i + 1 < s.Length && s[i + 1] == ':')
            {
                int close = s.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (close > 0)
                {
                    string? repl = s[(i + 2)..close] switch
                    {
                        "alnum" => "a-zA-Z0-9",
                        "alpha" => "a-zA-Z",
                        "blank" => @" \t",
                        "cntrl" => @"\x00-\x1f\x7f",
                        "digit" => "0-9",
                        "graph" => @"\x21-\x7e",
                        "lower" => "a-z",
                        "print" => @"\x20-\x7e",
                        "punct" => @"\p{P}\p{S}",
                        "space" => @"\s",
                        "upper" => "A-Z",
                        "word" => @"\w",
                        "xdigit" => "A-Fa-f0-9",
                        _ => null,
                    };
                    if (repl is not null)
                    {
                        sb.Append(repl);
                        i = close + 2;
                        continue;
                    }
                }
            }

            sb.Append(s[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>A <c>$</c> starts an expansion only before <c>{</c>, <c>(</c>, or a name
    /// character. A bare <c>$</c> (as in the anchor <c>(\s|$)</c>) is literal regex.</summary>
    private static bool IsExpansionStart(char c) =>
        c == '{' || c == '(' || c == '_' || char.IsLetter(c);

    /// <summary>
    /// Exclusive end index of the expansion starting at <paramref name="dollar"/>.
    /// Brace/paren forms are matched with a depth counter; a bare <c>$name</c> runs to
    /// the end of the name. Returns <paramref name="dollar"/> when unterminated, so the
    /// caller falls back to treating the <c>$</c> as literal regex text.
    /// </summary>
    private static int FindExpansionEnd(string s, int dollar)
    {
        char open = s[dollar + 1];
        if (open is '{' or '(')
        {
            char close = open == '{' ? '}' : ')';
            int depth = 0;
            for (int i = dollar + 1; i < s.Length; i++)
            {
                if (s[i] == open) depth++;
                else if (s[i] == close && --depth == 0) return i + 1;
            }
            return dollar; // unterminated — treat as literal
        }

        int j = dollar + 1;
        while (j < s.Length && (s[j] == '_' || char.IsLetterOrDigit(s[j]))) j++;
        return j;
    }

    private static bool IsTestOperatorToken(BashTokenKind kind) =>
        kind is BashTokenKind.Less or BashTokenKind.Great or BashTokenKind.Bang;


    private Command.If ParseIf()
    {
        var arms = ImmutableArray.CreateBuilder<IfArm>();

        // Parse "if cond; then body" (first arm).
        Expect("if");
        arms.Add(ParseIfArm());

        // Parse zero or more "elif cond; then body" arms.
        while (Peek().Kind == BashTokenKind.Word && Peek().Value == "elif")
        {
            Advance(); // consume "elif"
            arms.Add(ParseIfArm());
        }

        // Parse optional "else body".
        Command? elseBody = null;
        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "else")
        {
            Advance(); // consume "else"
            SkipTerminators();
            elseBody = ParseCompoundBody("fi", "fi");
        }

        Expect("fi");
        return new Command.If(arms.ToImmutable(), elseBody, ParseTrailingRedirects());
    }

    /// <summary>The words that end an if/while condition list: its keyword, plus every closing
    /// reserved word (a malformed `if a; b; fi` then fails at the keyword's Expect).</summary>
    private static string[] ConditionStopWords(string keyword) =>
        [keyword, "fi", "done", "esac", "else", "elif", "then", "do"];

    private IfArm ParseIfArm()
    {
        SkipTerminators();
        // bash's condition is a compound_list (`if a; b; then`): its status is the last command's.
        // It also stops at a closing word, so a missing `then` reports "Expected 'then' but got 'fi'".
        var cond = ParseCompoundBody(ConditionStopWords("then"));
        SkipTerminators();
        Expect("then");
        SkipTerminators();
        var body = ParseCompoundBody("elif", "else", "fi");
        return new IfArm(cond, body);
    }

    /// <summary>
    /// Parse a sequence of commands until one of the stop words is seen.
    /// Returns a single command or a CommandList.
    /// </summary>
    private Command ParseCompoundBody(params string[] stopWords)
    {
        var commands = ImmutableArray.CreateBuilder<Command>();
        bool AtStopWord() => Peek().Kind == BashTokenKind.Word && stopWords.Contains(Peek().Value);

        while (true)
        {
            SkipTerminators();
            if (Peek().Kind == BashTokenKind.Eof)
                break;
            if (AtStopWord())
                break;

            commands.Add(ParseListStatement(AtStopWord, "ParseCompoundBody"));
        }

        if (commands.Count == 1)
            return commands[0];

        return new Command.CommandList(commands.ToImmutable());
    }

    /// <summary>
    /// Calls <see cref="ParseAndOr"/> but guarantees forward progress. A stray
    /// close-token (`)`, `}`, `!`) that <see cref="ParseSimpleCommand"/> cannot
    /// absorb yields an empty command WITHOUT consuming a token; in a body loop
    /// (<c>while (true)</c> over ParseAndOr) that spins forever. This wrapper
    /// throws a clean <see cref="ParseException"/> at the offending position
    /// instead of hanging on malformed input like `{ ) }` or `case x in a);; ) esac`.
    /// </summary>
    private Command ParseAndOrProgress()
    {
        int before = _pos;
        var cmd = ParseAndOr();
        if (_pos == before)
            throw MakeError(
                $"Unexpected token '{Peek().Value}' ({Peek().Kind})",
                Peek().Position, "ParseAndOrProgress");
        return cmd;
    }

    private void Expect(string word)
    {
        var token = Peek();
        if (token.Kind != BashTokenKind.Word || token.Value != word)
            throw MakeError(
                $"Expected '{word}' but got '{token.Value}' ({token.Kind})",
                token.Position, "Expect");
        Advance();
    }

    /// <summary>
    /// Skip semicolons and newlines (used between compound command parts).
    /// </summary>
    private void SkipTerminators()
    {
        while (Peek().Kind is BashTokenKind.Semi or BashTokenKind.Newline)
            _pos++;
    }

    // A bash variable name: [A-Za-z_][A-Za-z0-9_]*. Used to reject `for $i` /
    // `for 1` before they reach the emitter as a broken foreach variable.
    private static bool IsValidVariableName(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
        for (int i = 1; i < s.Length; i++)
            if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) return false;
        return true;
    }

    private Command ParseFor()
    {
        Expect("for");

        // C-style: for ((init; cond; step)); do body; done
        if (Peek().Kind == BashTokenKind.LParen)
        {
            return ParseForArith();
        }

        // for-in: for var [in words]; do body; done
        var varToken = Advance();
        string varName = varToken.Value;
        // The loop variable must be a bare identifier. `for $i in ...` is a bash
        // syntax error; without this guard the `$i` word passed straight through
        // and the emitter produced the broken `foreach ($$i ...)`.
        if (!IsValidVariableName(varName))
            throw MakeError($"`for': `{varName}' is not a valid variable name", varToken.Position, "for");

        var list = ImmutableArray.CreateBuilder<CompoundWord>();

        SkipTerminators();

        // "in" keyword introduces list; absence means implicit $@
        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "in")
        {
            Advance(); // consume "in"

            while (Peek().Kind == BashTokenKind.Word
                && Peek().Value != "do"
                && !IsCompoundDelimiter(Peek().Value))
            {
                var token = Advance();
                var parts = DecomposeWord(token.Value);
                list.Add(new CompoundWord(parts));
            }
        }

        SkipTerminators();
        Expect("do");
        SkipTerminators();
        var body = ParseCompoundBody("done");
        Expect("done");

        return new Command.ForIn(varName, list.ToImmutable(), body, ParseTrailingRedirects());
    }

    /// <summary>
    /// Parse <c>select var [in words]; do body; done</c> — identical grammar to
    /// for-in. ps-bash does not implement the interactive menu loop; we still
    /// parse the full construct (so it does not abort the surrounding script's
    /// transpile) and the emitter degrades it to a documented comment.
    /// </summary>
    private Command ParseSelect()
    {
        Expect("select");

        var varToken = Advance();
        string varName = varToken.Value;

        var list = ImmutableArray.CreateBuilder<CompoundWord>();
        SkipTerminators();

        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "in")
        {
            Advance(); // consume "in"
            while (Peek().Kind == BashTokenKind.Word
                && Peek().Value != "do"
                && !IsCompoundDelimiter(Peek().Value))
            {
                var token = Advance();
                list.Add(new CompoundWord(DecomposeWord(token.Value)));
            }
        }

        SkipTerminators();
        Expect("do");
        SkipTerminators();
        var body = ParseCompoundBody("done");
        Expect("done");

        return new Command.Select(varName, list.ToImmutable(), body);
    }

    private Command ParseForArith()
    {
        Advance(); // consume first (
        var secondParen = Advance(); // consume second (
        int exprStart = secondParen.Position + 1;

        // Slice the raw header between `((` and the matching `))` rather than
        // joining token values. The old per-token approach stopped at the FIRST
        // RParen, so an inner single paren (`for ((i=(a+b); i<n; i++))`)
        // terminated the header early and corrupted the loop; it also lost
        // whitespace/quoting. Walk to the closing `))` (skipping inner single
        // `)`), then split the raw text on top-level `;`. Mirrors ParseArithCommand.
        int exprEnd = -1;
        int nestedDepth = 0;
        while (Peek().Kind != BashTokenKind.Eof)
        {
            if (Peek().Kind == BashTokenKind.RParen && nestedDepth == 0 && IsDoubleRParen())
            {
                exprEnd = Peek().Position;
                Advance(); // consume first )
                Advance(); // consume second )
                break;
            }
            if (Peek().Kind == BashTokenKind.LParen) nestedDepth++;
            else if (Peek().Kind == BashTokenKind.RParen && nestedDepth > 0) nestedDepth--;
            Advance();
        }
        string header = exprEnd >= 0 ? _input[exprStart..exprEnd] : _input[exprStart..];
        var (init, cond, step) = SplitArithClauses(header);

        SkipTerminators();
        Expect("do");
        SkipTerminators();
        var body = ParseCompoundBody("done");
        Expect("done");

        return new Command.ForArith(
            ParseOptionalArithmetic(init),
            ParseOptionalArithmetic(cond),
            ParseOptionalArithmetic(step),
            body,
            ParseTrailingRedirects());
    }

    /// <summary>
    /// Split a C-style for header (<c>init ; cond ; step</c>) on top-level
    /// semicolons, ignoring <c>;</c> nested inside parens/brackets. Clause slices
    /// retain their exact source text. Fewer than three clauses yields empty strings
    /// for the rest, which are subsequently modeled as absent clauses.
    /// </summary>
    private static (string Init, string Cond, string Step) SplitArithClauses(string header)
    {
        var clauses = new List<string>();
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        foreach (char ch in header)
        {
            if (ch is '(' or '[') depth++;
            else if (ch is ')' or ']') { if (depth > 0) depth--; }

            if (ch == ';' && depth == 0)
            {
                clauses.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        clauses.Add(sb.ToString());

        string init = clauses.Count > 0 ? clauses[0] : string.Empty;
        string cond = clauses.Count > 1 ? clauses[1] : string.Empty;
        string step = clauses.Count > 2 ? clauses[2] : string.Empty;
        return (init, cond, step);
    }

    private static ArithmeticSyntax? ParseOptionalArithmetic(string source) =>
        string.IsNullOrWhiteSpace(source) ? null : BashArithmeticParser.Parse(source);

    private Command.While ParseWhile()
    {
        var keyword = Advance(); // consume "while" or "until"
        bool isUntil = keyword.Value == "until";

        SkipTerminators();
        // A compound_list, like if's (`while read x; [ -n "$x" ]; do`).
        var cond = ParseCompoundBody(ConditionStopWords("do"));
        SkipTerminators();
        Expect("do");
        SkipTerminators();
        var body = ParseCompoundBody("done");
        Expect("done");

        return new Command.While(isUntil, cond, body, ParseTrailingRedirects());
    }

    private Command.Case ParseCase()
    {
        Expect("case");

        var exprToken = Advance();
        var exprParts = DecomposeWord(exprToken.Value);
        var expr = new CompoundWord(exprParts);

        SkipTerminators();
        Expect("in");
        SkipTerminators();

        var arms = ImmutableArray.CreateBuilder<CaseArm>();

        while (Peek().Kind != BashTokenKind.Eof
            && !(Peek().Kind == BashTokenKind.Word && Peek().Value == "esac"))
        {
            arms.Add(ParseCaseArm());
            SkipTerminators();
        }

        Expect("esac");
        return new Command.Case(expr, arms.ToImmutable(), ParseTrailingRedirects());
    }

    private CaseArm ParseCaseArm()
    {
        // Collect patterns separated by Pipe until RParen.
        var patterns = ImmutableArray.CreateBuilder<string>();

        // Optional leading ( before pattern list.
        if (Peek().Kind == BashTokenKind.LParen)
            Advance();

        var patternPos = Peek().Position;
        patterns.Add(ConsumeCasePattern());

        while (Peek().Kind == BashTokenKind.Pipe)
        {
            Advance(); // consume |
            patterns.Add(ConsumeCasePattern());
        }

        // An arm with no pattern text (`case x in ) esac`, or an empty alternative
        // `a|)`) is a bash syntax error. Reject it here: an empty pattern would
        // otherwise silently become a match-nothing arm.
        foreach (var p in patterns)
        {
            if (p.Length == 0)
                throw MakeError("Empty case pattern before ')'", patternPos, "ParseCaseArm");
        }

        if (Peek().Kind != BashTokenKind.RParen)
            throw MakeError(
                $"Expected ')' after case pattern but got '{Peek().Value}' ({Peek().Kind})",
                Peek().Position, "ParseCaseArm");
        Advance(); // consume )

        // Skip NEWLINES only — never semicolons. An empty arm body (`x) ;;`) is
        // legal bash, and SkipTerminators() would eat the `;;` that terminates
        // it, making the parser read the NEXT arm's pattern as a command word
        // and die on its `)`. A lone `;` right after `)` is a bash syntax error,
        // so there is nothing else here worth skipping.
        SkipNewlines();

        // Parse body commands until a terminator (;; / ;& / ;;&) or esac.
        var body = ParseCaseBody();

        // Consume the terminator and record which it was (controls fall-through).
        // Order matters: ;;& (Semi+Semi+Amp) before ;; (Semi+Semi), and ;& (Semi+Amp).
        var terminator = CaseTerminator.Break;
        if (IsDoubleSemiAmp())            // ;;&  continue testing
        {
            Advance(); Advance(); Advance();
            terminator = CaseTerminator.ContinueTest;
        }
        else if (IsDoubleSemi())          // ;;   break
        {
            Advance(); Advance();
        }
        else if (IsSemiAmp())             // ;&   fall through
        {
            Advance(); Advance();
            terminator = CaseTerminator.FallThrough;
        }
        else if (Peek().Kind == BashTokenKind.Semi)  // lone ; before esac
        {
            Advance();
        }

        return new CaseArm(patterns.ToImmutable(), body, terminator);
    }

    /// <summary>
    /// Consume a single case pattern. The pattern may contain glob chars like *.
    /// Stops at Pipe or RParen.
    /// </summary>
    private string ConsumeCasePattern()
    {
        var parts = new List<string>();

        while (Peek().Kind != BashTokenKind.Eof
            && Peek().Kind != BashTokenKind.Pipe
            && Peek().Kind != BashTokenKind.RParen)
        {
            parts.Add(Advance().Value);
        }

        return string.Join("", parts).Trim();
    }

    /// <summary>
    /// Parse commands inside a case arm until ;; or esac is seen.
    /// Only skips newlines between commands, not semicolons (to detect ;; delimiter).
    /// </summary>
    private Command ParseCaseBody()
    {
        var commands = ImmutableArray.CreateBuilder<Command>();

        while (true)
        {
            SkipNewlines();
            if (Peek().Kind == BashTokenKind.Eof)
                break;
            if (Peek().Kind == BashTokenKind.Word && Peek().Value == "esac")
                break;
            if (IsCaseArmTerminator())
                break;

            commands.Add(ParseListStatement(
                () => IsCaseArmTerminator()
                    || (Peek().Kind == BashTokenKind.Word && Peek().Value == "esac"),
                "ParseCaseBody"));

            // After a command, consume a single ; separator if present, but stop
            // at an arm terminator (;;, ;&, or ;;&).
            SkipNewlines();
            if (IsCaseArmTerminator())
                break;
            if (Peek().Kind == BashTokenKind.Semi)
                Advance();
        }

        if (commands.Count == 1)
            return commands[0];

        return new Command.CommandList(commands.ToImmutable());
    }

    private bool IsDoubleSemi() =>
        Peek().Kind == BashTokenKind.Semi
        && _pos + 1 < _tokens.Count
        && _tokens[_pos + 1].Kind == BashTokenKind.Semi;

    /// <summary>
    /// True at any case-arm terminator. The lexer is context-free (it never
    /// emits dedicated <c>;&amp;</c> / <c>;;&amp;</c> tokens — those would silently drop commands
    /// when they appear outside a case), so terminators are detected here as
    /// token sequences: <c>;;</c> = Semi+Semi, <c>;&amp;</c> = Semi+Amp, <c>;;&amp;</c>
    /// = Semi+Semi+Amp. A lone Semi (next token is a command) is a separator,
    /// not a terminator.
    /// </summary>
    private bool IsCaseArmTerminator() => IsDoubleSemi() || IsSemiAmp();

    /// <summary><c>;&amp;</c> — Semi immediately followed by Amp.</summary>
    private bool IsSemiAmp() =>
        Peek().Kind == BashTokenKind.Semi
        && _pos + 1 < _tokens.Count
        && _tokens[_pos + 1].Kind == BashTokenKind.Amp;

    /// <summary><c>;;&amp;</c> — Semi, Semi, Amp.</summary>
    private bool IsDoubleSemiAmp() =>
        IsDoubleSemi()
        && _pos + 2 < _tokens.Count
        && _tokens[_pos + 2].Kind == BashTokenKind.Amp;

    private Command.ShFunction ParseFunction()
    {
        Expect("function");
        var nameToken = Advance();
        string name = nameToken.Value;

        // Optional EMPTY () after name in "function name() { body }" form. Only
        // consume when it is literally `()`; a `(` that opens a subshell BODY
        // (`function f ( cmd )`) must not be eaten here.
        if (Peek().Kind == BashTokenKind.LParen
            && _pos + 1 < _tokens.Count
            && _tokens[_pos + 1].Kind == BashTokenKind.RParen)
        {
            Advance(); // consume (
            Advance(); // consume )
        }

        SkipTerminators();
        var body = ParseFunctionBody();
        return new Command.ShFunction(name, body);
    }

    /// <summary>
    /// Parse a function body. bash allows any COMPOUND command as the body
    /// (brace group, subshell, for/while/until/if/case), not just <c>{ ... }</c>.
    /// A brace-group body stays on the ParseBraceGroup path so its AST/emission
    /// is unchanged (unwrapped); other compound forms go through
    /// ParseCompoundOrSimple (subshell, loops, conditionals).
    /// </summary>
    /// <remarks>
    /// A redirect written after the body (<c>f() { …; } &gt;/dev/null</c>) is part of
    /// the definition and applies on EVERY call (bash). The non-brace compound bodies
    /// already consume their trailing redirects; a brace body keeps its unwrapped
    /// shape unless it carries some, in which case it becomes a redirected
    /// <see cref="Command.BraceGroup"/> so the emitter applies them inside the function.
    /// </remarks>
    private Command ParseFunctionBody()
    {
        if (Peek().Kind != BashTokenKind.LBrace)
            return ParseCompoundOrSimple();

        var body = ParseBraceGroup();
        var redirects = ParseTrailingRedirects();
        return redirects.IsEmpty ? body : new Command.BraceGroup(body, redirects);
    }

    /// <summary>
    /// Check whether the current position starts a <c>name() { ... }</c> function definition.
    /// Requires Word LParen RParen ahead without consuming tokens.
    /// </summary>
    private bool IsParensFunctionDef()
    {
        if (_pos + 2 >= _tokens.Count)
            return false;
        return _tokens[_pos + 1].Kind == BashTokenKind.LParen
            && _tokens[_pos + 2].Kind == BashTokenKind.RParen;
    }

    private Command.ShFunction ParseParensFunction()
    {
        var nameToken = Advance(); // consume name
        string name = nameToken.Value;
        Advance(); // consume (
        Advance(); // consume )
        SkipTerminators();
        var body = ParseFunctionBody();
        return new Command.ShFunction(name, body);
    }

    private bool IsDoubleLParen() =>
        _pos + 1 < _tokens.Count
        && _tokens[_pos + 1].Kind == BashTokenKind.LParen;

    private Command.ArithCommand ParseArithCommand()
    {
        Advance(); // consume first (
        var secondParen = Advance(); // consume second (
        int exprStart = secondParen.Position + 1;
        int nestedDepth = 0;

        while (Peek().Kind != BashTokenKind.Eof)
        {
            if (Peek().Kind == BashTokenKind.RParen && nestedDepth == 0 && IsDoubleRParen())
            {
                int exprEnd = Peek().Position;
                Advance(); // consume first )
                Advance(); // consume second )
                string expr = _input[exprStart..exprEnd];
                return new Command.ArithCommand(BashArithmeticParser.Parse(expr));
            }

            if (Peek().Kind == BashTokenKind.LParen) nestedDepth++;
            else if (Peek().Kind == BashTokenKind.RParen && nestedDepth > 0) nestedDepth--;
            Advance();
        }

        // Reached EOF without finding ))
        string remaining = _input[exprStart..];
        return new Command.ArithCommand(BashArithmeticParser.Parse(remaining));
    }

    private bool IsDoubleRParen() =>
        _pos + 1 < _tokens.Count
        && _tokens[_pos + 1].Kind == BashTokenKind.RParen;

    private Command.Subshell ParseSubshell()
    {
        Advance(); // consume (
        SkipTerminators();

        var commands = ImmutableArray.CreateBuilder<Command>();

        while (true)
        {
            SkipTerminators();
            if (Peek().Kind == BashTokenKind.Eof)
                break;
            if (Peek().Kind == BashTokenKind.RParen)
                break;

            commands.Add(ParseListStatement(() => Peek().Kind == BashTokenKind.RParen, "ParseSubshell"));
        }

        if (Peek().Kind != BashTokenKind.RParen)
            throw MakeError(
                $"Expected ')' to close subshell but got '{Peek().Value}' ({Peek().Kind})",
                Peek().Position, "ParseSubshell");
        Advance(); // consume )

        Command body = commands.Count == 1
            ? commands[0]
            : new Command.CommandList(commands.ToImmutable());

        // Trailing redirects (e.g. (cmd) > out.txt, (cmd) <<< word) — the one shared parser.
        return new Command.Subshell(body, ParseTrailingRedirects());
    }

    private Command.BraceGroup ParseStandaloneBraceGroup()
    {
        var body = ParseBraceGroup();
        return new Command.BraceGroup(body, ParseTrailingRedirects());
    }

    /// <summary>
    /// Parse a brace group: <c>{ commands }</c>.
    /// Used for function bodies and standalone brace groups.
    /// </summary>
    private Command ParseBraceGroup()
    {
        if (Peek().Kind != BashTokenKind.LBrace)
            throw MakeError(
                $"Expected '{{' but got '{Peek().Value}' ({Peek().Kind})",
                Peek().Position, "ParseBraceGroup");
        Advance(); // consume {
        SkipTerminators();

        var commands = ImmutableArray.CreateBuilder<Command>();

        while (true)
        {
            SkipTerminators();
            if (Peek().Kind == BashTokenKind.Eof)
                break;
            if (Peek().Kind == BashTokenKind.RBrace)
                break;

            commands.Add(ParseListStatement(() => Peek().Kind == BashTokenKind.RBrace, "ParseBraceGroup"));
        }

        if (Peek().Kind != BashTokenKind.RBrace)
            throw MakeError(
                $"Expected '}}' but got '{Peek().Value}' ({Peek().Kind})",
                Peek().Position, "ParseBraceGroup");
        Advance(); // consume }

        if (commands.Count == 1)
            return commands[0];

        return new Command.CommandList(commands.ToImmutable());
    }

    private void SkipNewlines()
    {
        while (Peek().Kind == BashTokenKind.Newline)
            _pos++;
    }
}
