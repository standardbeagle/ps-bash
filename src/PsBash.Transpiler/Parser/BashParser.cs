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
        var tokens = BashLexer.Tokenize(input);
        var parser = new BashParser(tokens, input);
        return parser.ParseCommand();
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
        var tokens = BashLexer.Tokenize(input);
        var parser = new BashParser(tokens, input);
        return parser.ParseTopLevelWithPositionsCore();
    }

    private List<(Command, int)> ParseTopLevelWithPositionsCore()
    {
        var result = new List<(Command, int)>();
        SkipNewlines();
        while (Peek().Kind != BashTokenKind.Eof)
        {
            int startPos = Peek().Position;
            var cmd = ParseAndOrProgress();

            bool bg = false;
            if (Peek().Kind == BashTokenKind.Amp)
            {
                Advance();
                bg = true;
            }

            result.Add((bg ? new Command.Background(cmd) : cmd, startPos));

            // Skip statement terminators (; and newlines).
            while (Peek().Kind is BashTokenKind.Semi or BashTokenKind.Newline)
                Advance();
        }
        return result;
    }

    private ParseException MakeError(string message, int position, string rule)
    {
        var (line, col) = ParseException.ComputeLineCol(_input, position);
        return new ParseException(
            $"{message} at line {line}, col {col}",
            line, col, rule);
    }

    private BashToken Peek() => _tokens[_pos];

    private BashToken Advance()
    {
        var token = _tokens[_pos];
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
        // empty input), so a first ParseAndOr that consumes nothing means a
        // leading stray close-token (`)`/`}`) — a syntax error, not an empty
        // command. Guard it so such input throws instead of silently returning
        // an empty command (and so the top-level path matches the body loops).
        var first = ParseAndOrProgress();

        // Check for & (background) after the first command.
        // In bash, & is a command terminator like ; or newline — the next
        // command can start immediately without a separator.
        bool firstBg = false;
        if (Peek().Kind == BashTokenKind.Amp)
        {
            Advance(); // consume &
            firstBg = true;
        }

        // If no separator follows and the first command is not backgrounded
        // (or it is backgrounded but there's no next command), return single.
        if (Peek().Kind is not BashTokenKind.Semi and not BashTokenKind.Newline)
        {
            if (!firstBg || Peek().Kind == BashTokenKind.Eof)
                return firstBg ? new Command.Background(first) : first;
        }

        var commands = ImmutableArray.CreateBuilder<Command>();
        commands.Add(firstBg ? new Command.Background(first) : first);

        while (true)
        {
            bool prevBg = commands[^1] is Command.Background;

            if (Peek().Kind is BashTokenKind.Semi or BashTokenKind.Newline)
                SkipTerminators();
            else if (!prevBg)
                break;

            if (Peek().Kind == BashTokenKind.Eof)
                break;

            var cmd = ParseAndOrProgress();

            if (Peek().Kind == BashTokenKind.Amp)
            {
                Advance(); // consume &
                cmd = new Command.Background(cmd);
            }

            commands.Add(cmd);
        }

        if (commands.Count == 1)
            return commands[0];

        return new Command.CommandList(commands.ToImmutable());
    }

    private Command ParseAndOr()
    {
        var first = ParsePipeline();

        if (Peek().Kind is not BashTokenKind.AndIf and not BashTokenKind.OrIf)
            return first;

        var commands = ImmutableArray.CreateBuilder<Command>();
        var ops = ImmutableArray.CreateBuilder<string>();
        commands.Add(first);

        while (Peek().Kind is BashTokenKind.AndIf or BashTokenKind.OrIf)
        {
            var opToken = Advance();
            ops.Add(opToken.Value);
            // In bash, a newline after && or || is a line continuation.
            SkipNewlines();
            commands.Add(ParsePipeline());
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

            // In bash, a newline after | is a line continuation — skip newlines
            // before reading the next pipeline command.
            SkipNewlines();

            commands.Add(ParseCompoundOrSimple());
        }

        return new Command.Pipeline(commands.ToImmutable(), ops.ToImmutable(), Negated: negated);
    }

    private Command ParseCompoundOrSimple()
    {
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
                if (opToken.Kind == BashTokenKind.Bang
                    && Peek().Kind == BashTokenKind.Word
                    && Peek().Value.StartsWith('='))
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

        return new Command.BoolExpr(inner.ToImmutable(), extended);
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
        return new Command.If(arms.ToImmutable(), elseBody);
    }

    private IfArm ParseIfArm()
    {
        SkipTerminators();
        var cond = ParseAndOr();
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

        while (true)
        {
            SkipTerminators();
            if (Peek().Kind == BashTokenKind.Eof)
                break;
            if (Peek().Kind == BashTokenKind.Word && stopWords.Contains(Peek().Value))
                break;

            commands.Add(ParseAndOrProgress());
            SkipTerminators();
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

        return new Command.ForIn(varName, list.ToImmutable(), body);
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
        while (Peek().Kind != BashTokenKind.Eof)
        {
            if (Peek().Kind == BashTokenKind.RParen && IsDoubleRParen())
            {
                exprEnd = Peek().Position;
                Advance(); // consume first )
                Advance(); // consume second )
                break;
            }
            Advance();
        }
        string header = exprEnd >= 0 ? _input[exprStart..exprEnd] : _input[exprStart..];
        var (init, cond, step) = SplitArithClauses(header);

        SkipTerminators();
        Expect("do");
        SkipTerminators();
        var body = ParseCompoundBody("done");
        Expect("done");

        return new Command.ForArith(init, cond, step, body);
    }

    /// <summary>
    /// Split a C-style for header (<c>init ; cond ; step</c>) on top-level
    /// semicolons, ignoring <c>;</c> nested inside parens/brackets, and trimming
    /// each clause. Fewer than three clauses yields empty strings for the rest.
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
                clauses.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        clauses.Add(sb.ToString().Trim());

        string init = clauses.Count > 0 ? clauses[0] : string.Empty;
        string cond = clauses.Count > 1 ? clauses[1] : string.Empty;
        string step = clauses.Count > 2 ? clauses[2] : string.Empty;
        return (init, cond, step);
    }

    private Command.While ParseWhile()
    {
        var keyword = Advance(); // consume "while" or "until"
        bool isUntil = keyword.Value == "until";

        SkipTerminators();
        var cond = ParseAndOr();
        SkipTerminators();
        Expect("do");
        SkipTerminators();
        var body = ParseCompoundBody("done");
        Expect("done");

        return new Command.While(isUntil, cond, body);
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
        return new Command.Case(expr, arms.ToImmutable());
    }

    private CaseArm ParseCaseArm()
    {
        // Collect patterns separated by Pipe until RParen.
        var patterns = ImmutableArray.CreateBuilder<string>();

        // Optional leading ( before pattern list.
        if (Peek().Kind == BashTokenKind.LParen)
            Advance();

        patterns.Add(ConsumeCasePattern());

        while (Peek().Kind == BashTokenKind.Pipe)
        {
            Advance(); // consume |
            patterns.Add(ConsumeCasePattern());
        }

        if (Peek().Kind != BashTokenKind.RParen)
            throw MakeError(
                $"Expected ')' after case pattern but got '{Peek().Value}' ({Peek().Kind})",
                Peek().Position, "ParseCaseArm");
        Advance(); // consume )

        SkipTerminators();

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

            commands.Add(ParseAndOrProgress());

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
    /// emits dedicated `;&`/`;;&` tokens — those would silently drop commands
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

        // Optional () after name in "function name() { body }" form
        if (Peek().Kind == BashTokenKind.LParen)
        {
            Advance(); // consume (
            if (Peek().Kind == BashTokenKind.RParen)
                Advance(); // consume )
        }

        SkipTerminators();
        var body = ParseBraceGroup();
        return new Command.ShFunction(name, body);
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
        var body = ParseBraceGroup();
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

        while (Peek().Kind != BashTokenKind.Eof)
        {
            if (Peek().Kind == BashTokenKind.RParen && IsDoubleRParen())
            {
                int exprEnd = Peek().Position;
                Advance(); // consume first )
                Advance(); // consume second )
                string expr = _input[exprStart..exprEnd].Trim();
                return new Command.ArithCommand(expr);
            }

            Advance();
        }

        // Reached EOF without finding ))
        string remaining = _input[exprStart..].Trim();
        return new Command.ArithCommand(remaining);
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

            commands.Add(ParseAndOrProgress());
            SkipTerminators();
        }

        if (Peek().Kind != BashTokenKind.RParen)
            throw MakeError(
                $"Expected ')' to close subshell but got '{Peek().Value}' ({Peek().Kind})",
                Peek().Position, "ParseSubshell");
        Advance(); // consume )

        Command body = commands.Count == 1
            ? commands[0]
            : new Command.CommandList(commands.ToImmutable());

        // Collect trailing redirects (e.g. (cmd) > out.txt)
        var redirects = ImmutableArray.CreateBuilder<Redirect>();
        while (Peek().Kind == BashTokenKind.IoNumber || IsRedirectOp(Peek().Kind))
            redirects.Add(ParseRedirect());

        return new Command.Subshell(body, redirects.ToImmutable());
    }

    private Command.BraceGroup ParseStandaloneBraceGroup()
    {
        var body = ParseBraceGroup();
        return new Command.BraceGroup(body);
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

            commands.Add(ParseAndOrProgress());
            SkipTerminators();
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
