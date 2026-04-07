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
/// </summary>
public sealed class BashParser
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
        var first = ParseAndOr();

        if (Peek().Kind is not BashTokenKind.Semi and not BashTokenKind.Newline)
            return first;

        var commands = ImmutableArray.CreateBuilder<Command>();
        commands.Add(first);

        while (Peek().Kind is BashTokenKind.Semi or BashTokenKind.Newline)
        {
            SkipTerminators();

            if (Peek().Kind == BashTokenKind.Eof)
                break;

            commands.Add(ParseAndOr());
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
            commands.Add(ParsePipeline());
        }

        return new Command.AndOrList(commands.ToImmutable(), ops.ToImmutable());
    }

    private Command ParsePipeline()
    {
        var first = ParseCompoundOrSimple();
        if (Peek().Kind is not BashTokenKind.Pipe and not BashTokenKind.PipeAmp)
            return first;

        var commands = ImmutableArray.CreateBuilder<Command>();
        var ops = ImmutableArray.CreateBuilder<string>();
        commands.Add(first);

        while (Peek().Kind is BashTokenKind.Pipe or BashTokenKind.PipeAmp)
        {
            var isPipeAmp = Peek().Kind == BashTokenKind.PipeAmp;
            Advance(); // consume | or |&

            ops.Add(isPipeAmp ? "|&" : "|");

            commands.Add(ParseCompoundOrSimple());
        }

        return new Command.Pipeline(commands.ToImmutable(), ops.ToImmutable(), Negated: false);
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

        // Bail for compound constructs not yet implemented — triggers regex fallback in auto mode.
        if (Peek().Kind == BashTokenKind.Word && IsUnimplementedCompoundKeyword(Peek().Value))
            throw MakeError(
                $"Compound construct '{Peek().Value}' is not supported; use PSBASH_PARSER=auto for regex fallback",
                Peek().Position, "ParseCompoundOrSimple");

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

    private static bool IsUnimplementedCompoundKeyword(string word) =>
        word is "select";

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

            commands.Add(ParseAndOr());
            SkipTerminators();
        }

        if (commands.Count == 1)
            return commands[0];

        return new Command.CommandList(commands.ToImmutable());
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

    private Command ParseForArith()
    {
        Advance(); // consume first (
        Advance(); // consume second (

        // Collect tokens for each of the three clauses, separated by Semi.
        string init = CollectArithClause();
        string cond = CollectArithClause();
        string step = CollectArithClause();

        // Consume closing )) — may already be past them if CollectArithClause consumed RParen
        SkipTerminators();
        Expect("do");
        SkipTerminators();
        var body = ParseCompoundBody("done");
        Expect("done");

        return new Command.ForArith(init, cond, step, body);
    }

    /// <summary>
    /// Collect tokens for one clause of a C-style for loop's arithmetic expression.
    /// Stops at Semi (consumes it) or RParen (consumed to close the (( ))).
    /// </summary>
    private string CollectArithClause()
    {
        var parts = new List<string>();
        while (Peek().Kind != BashTokenKind.Eof)
        {
            if (Peek().Kind == BashTokenKind.Semi)
            {
                Advance(); // consume ;
                break;
            }
            if (Peek().Kind == BashTokenKind.RParen)
            {
                Advance(); // consume first )
                if (Peek().Kind == BashTokenKind.RParen)
                    Advance(); // consume second )
                break;
            }

            var token = Advance();
            parts.Add(token.Value);
        }
        return string.Join("", parts);
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

        // Parse body commands until ;; or esac.
        var body = ParseCaseBody();

        // Consume ;; (two Semi tokens) if present.
        if (Peek().Kind == BashTokenKind.Semi)
        {
            Advance();
            if (Peek().Kind == BashTokenKind.Semi)
                Advance();
        }

        return new CaseArm(patterns.ToImmutable(), body);
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
            if (IsDoubleSemi())
                break;

            commands.Add(ParseAndOr());

            // After a command, consume a single ; separator if present,
            // but stop if it's ;; (arm delimiter).
            SkipNewlines();
            if (IsDoubleSemi())
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

            commands.Add(ParseAndOr());
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

            commands.Add(ParseAndOr());
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

    private Command ParseSimpleCommand()
    {
        // Check for "export" keyword followed by assignment words.
        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "export")
        {
            int saved = _pos;
            Advance(); // consume "export"

            if (Peek().Kind == BashTokenKind.AssignmentWord)
            {
                var pairs = ImmutableArray.CreateBuilder<Assignment>();
                while (Peek().Kind == BashTokenKind.AssignmentWord)
                    pairs.Add(ParseAssignmentWord());
                return new Command.ShAssignment(pairs.ToImmutable());
            }

            // Not followed by assignment — rewind and parse as normal command.
            _pos = saved;
        }

        // Check for "local" keyword followed by assignment words.
        if (Peek().Kind == BashTokenKind.Word && Peek().Value == "local")
        {
            int saved = _pos;
            Advance(); // consume "local"

            if (Peek().Kind == BashTokenKind.AssignmentWord)
            {
                var pairs = ImmutableArray.CreateBuilder<Assignment>();
                while (Peek().Kind == BashTokenKind.AssignmentWord)
                    pairs.Add(ParseAssignmentWordWithArray());
                return new Command.ShAssignment(pairs.ToImmutable(), IsLocal: true);
            }

            // Not followed by assignment — rewind and parse as normal command.
            _pos = saved;
        }

        // Collect leading assignment words (VAR=val ...).
        // Array assignments like arr=(a b c) are detected and consumed here.
        var assignmentPairs = ImmutableArray.CreateBuilder<Assignment>();
        var envPairs = ImmutableArray.CreateBuilder<EnvPair>();
        bool hasArrayAssignment = false;
        while (Peek().Kind == BashTokenKind.AssignmentWord)
        {
            var assign = ParseAssignmentWordWithArray();
            if (assign.ArrayValue is not null)
                hasArrayAssignment = true;
            assignmentPairs.Add(assign);
            envPairs.Add(new EnvPair(assign.Name, assign.Value));
        }

        // If we consumed array assignments, they must be bare assignments.
        if (hasArrayAssignment)
            return new Command.ShAssignment(assignmentPairs.ToImmutable());

        var words = ImmutableArray.CreateBuilder<CompoundWord>();
        var redirects = ImmutableArray.CreateBuilder<Redirect>();
        var hereDocs = ImmutableArray.CreateBuilder<HereDoc>();
        var pendingHeredocs = new List<(string Delimiter, bool Expand, bool StripTabs)>();

        while (true)
        {
            var kind = Peek().Kind;

            if (kind == BashTokenKind.Word)
            {
                // Stop at reserved words that delimit compound commands.
                if (IsCompoundDelimiter(Peek().Value))
                    break;

                var token = Advance();
                var parts = DecomposeWord(token.Value);
                words.Add(new CompoundWord(parts));
            }
            else if (kind == BashTokenKind.TLess)
            {
                // Here-string: <<< word — the word becomes the body directly.
                Advance(); // consume <<<
                var wordToken = Advance();
                string raw = wordToken.Value;
                // Strip surrounding quotes from the here-string word.
                bool expand = true;
                if ((raw.StartsWith('\'') && raw.EndsWith('\''))
                    || (raw.StartsWith('"') && raw.EndsWith('"')))
                {
                    expand = raw[0] == '"';
                    raw = raw[1..^1];
                }
                hereDocs.Add(new HereDoc(raw, expand, StripTabs: false));
            }
            else if (kind is BashTokenKind.DLess or BashTokenKind.DLessDash)
            {
                bool stripTabs = kind == BashTokenKind.DLessDash;
                Advance(); // consume << or <<-
                var delimToken = Advance();
                string rawDelim = delimToken.Value;

                bool expand;
                string delimiter;
                // Quoted delimiter (single or double quotes) suppresses expansion.
                if ((rawDelim.StartsWith('\'') && rawDelim.EndsWith('\''))
                    || (rawDelim.StartsWith('"') && rawDelim.EndsWith('"')))
                {
                    expand = false;
                    delimiter = rawDelim[1..^1];
                }
                else
                {
                    expand = true;
                    delimiter = rawDelim;
                }
                pendingHeredocs.Add((delimiter, expand, stripTabs));
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
        foreach (var (delimiter, expand, stripTabs) in pendingHeredocs)
            hereDocs.Add(CollectHereDocBody(delimiter, expand, stripTabs));

        return new Command.Simple(
            words.ToImmutable(),
            envPairs.ToImmutable(),
            redirects.ToImmutable(),
            hereDocs.ToImmutable());
    }

    /// <summary>
    /// Collect the body lines of a here-document from the token stream.
    /// Consumes tokens after the current line's newline until the delimiter
    /// is found on its own line.
    /// </summary>
    private HereDoc CollectHereDocBody(string delimiter, bool expand, bool stripTabs)
    {
        // Skip the newline that ends the command line containing <<.
        if (Peek().Kind == BashTokenKind.Newline)
            Advance();

        var bodyLines = new List<string>();

        while (Peek().Kind != BashTokenKind.Eof)
        {
            // Collect all tokens on the current line into a single string.
            var lineTokens = new List<string>();
            while (Peek().Kind != BashTokenKind.Newline && Peek().Kind != BashTokenKind.Eof)
            {
                lineTokens.Add(Advance().Value);
            }

            string line = string.Join(" ", lineTokens);

            // For <<- the delimiter line may have leading tabs.
            string trimmedLine = stripTabs ? line.TrimStart('\t') : line;
            if (trimmedLine == delimiter)
            {
                // Consume the newline after the delimiter if present.
                if (Peek().Kind == BashTokenKind.Newline)
                    Advance();
                break;
            }

            if (stripTabs)
                line = line.TrimStart('\t');

            bodyLines.Add(line);

            // Consume the newline separator between body lines.
            if (Peek().Kind == BashTokenKind.Newline)
                Advance();
        }

        string body = string.Join("\n", bodyLines);
        return new HereDoc(body, expand, stripTabs);
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

        // Array assignment: arr=(a b c) or arr+=('x') — value is empty and next token is LParen.
        if (value is null && Peek().Kind == BashTokenKind.LParen)
        {
            Advance(); // consume (
            var elements = ImmutableArray.CreateBuilder<CompoundWord>();
            while (Peek().Kind != BashTokenKind.RParen && Peek().Kind != BashTokenKind.Eof)
            {
                if (Peek().Kind == BashTokenKind.Word)
                {
                    var wordToken = Advance();
                    elements.Add(new CompoundWord(DecomposeWord(wordToken.Value)));
                }
                else
                {
                    break;
                }
            }
            if (Peek().Kind == BashTokenKind.RParen)
                Advance(); // consume )
            return new Assignment(name, op, null,
                new ArrayWord(elements.ToImmutable()));
        }

        return new Assignment(name, op, value);
    }

    private (string Name, CompoundWord? Value, AssignOp Op) SplitAssignmentWord(string raw)
    {
        int eqIndex = raw.IndexOf('=');
        bool isPlus = eqIndex > 0 && raw[eqIndex - 1] == '+';
        string name = isPlus ? raw[..(eqIndex - 1)] : raw[..eqIndex];
        string valueRaw = raw[(eqIndex + 1)..];
        var op = isPlus ? AssignOp.PlusEqual : AssignOp.Equal;

        if (valueRaw.Length == 0)
            return (name, null, op);

        var parts = DecomposeWord(valueRaw);
        return (name, new CompoundWord(parts), op);
    }

    private Redirect ParseRedirect()
    {
        int fd = -1;

        if (Peek().Kind == BashTokenKind.IoNumber)
        {
            fd = int.Parse(Advance().Value);
        }

        var opToken = Advance();
        string op = opToken.Kind switch
        {
            BashTokenKind.Great => ">",
            BashTokenKind.DGreat => ">>",
            BashTokenKind.Less => "<",
            BashTokenKind.GreatAnd => ">&",
            BashTokenKind.LessAnd => "<&",
            BashTokenKind.DLess => "<<",
            BashTokenKind.DLessDash => "<<-",
            _ => opToken.Value,
        };

        // Default fd: 0 for input redirects, 1 for output redirects.
        if (fd == -1)
        {
            fd = op[0] == '<' ? 0 : 1;
        }

        // Consume the target word.
        var targetToken = Advance();
        var targetParts = DecomposeWord(targetToken.Value);
        var target = new CompoundWord(targetParts);

        return new Redirect(op, fd, target);
    }

    private static bool IsRedirectOp(BashTokenKind kind) =>
        kind is BashTokenKind.Great or BashTokenKind.DGreat
            or BashTokenKind.Less or BashTokenKind.GreatAnd
            or BashTokenKind.LessAnd or BashTokenKind.DLess
            or BashTokenKind.DLessDash;

    private static bool IsCompoundDelimiter(string word) =>
        word is "then" or "elif" or "else" or "fi"
            or "do" or "done" or "esac";

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
            else if (c == '\\' && pos + 1 < len)
            {
                parts.Add(new WordPart.EscapedLiteral(raw[pos + 1].ToString()));
                pos += 2;
            }
            else if (c == '$' && pos + 2 < len && raw[pos + 1] == '(' && raw[pos + 2] == '(')
            {
                pos = ParseArithSub(raw, pos, parts);
            }
            else if (c == '$' && pos + 1 < len && raw[pos + 1] == '(')
            {
                pos = ParseCommandSub(raw, pos, parts);
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
                innerParts.Add(new WordPart.Literal(raw[start..pos]));
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
        // Special single-char variables: $? $! $# $$ $@ $* $- $0-$9
        if (pos < raw.Length && raw[pos] is '?' or '!' or '#' or '$' or '@' or '*' or '-'
            or (>= '0' and <= '9'))
        {
            parts.Add(new WordPart.SimpleVarSub(raw[pos].ToString()));
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

        // ${!arr[@]} or ${!arr[*]} -> array keys operator
        if (pos < len && raw[pos] == '!')
        {
            pos++; // skip !
            int nameStart = pos;
            while (pos < len && IsVarChar(raw[pos]))
                pos++;
            string name = raw[nameStart..pos];
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

        // Check for array subscript: ${arr[0]}, ${arr[@]}, ${arr[key]}
        if (pos < len && raw[pos] == '[')
        {
            int subStart = pos;
            pos++; // skip [
            while (pos < len && raw[pos] != ']')
                pos++;
            if (pos < len)
                pos++; // skip ]
            string subscript = raw[subStart..pos];

            if (pos < len && raw[pos] == '}')
                pos++; // skip }
            parts.Add(new WordPart.BracedVarSub(varName, subscript));
            return pos;
        }

        // Check for suffix operator or closing brace
        string? suffix = null;
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

            // Read value until closing brace
            int valStart = pos;
            int braceDepth = 1;
            while (pos < len && braceDepth > 0)
            {
                if (raw[pos] == '{') braceDepth++;
                else if (raw[pos] == '}') braceDepth--;
                if (braceDepth > 0) pos++;
            }
            string val = raw[valStart..pos];
            suffix = op + val;
        }

        if (pos < len && raw[pos] == '}')
            pos++; // skip }

        parts.Add(new WordPart.BracedVarSub(varName, suffix));
        return pos;
    }

    private static int ParseArithSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos += 3; // skip $((
        int depth = 1;
        int start = pos;
        while (pos < raw.Length && depth > 0)
        {
            if (pos + 1 < raw.Length && raw[pos] == '(' && raw[pos + 1] == '(')
            {
                depth++;
                pos += 2;
            }
            else if (pos + 1 < raw.Length && raw[pos] == ')' && raw[pos + 1] == ')')
            {
                depth--;
                if (depth > 0) pos += 2;
            }
            else
            {
                pos++;
            }
        }
        string expr = raw[start..pos];
        if (pos < raw.Length)
            pos += 2; // skip closing ))

        parts.Add(new WordPart.ArithSub(expr.Trim()));
        return pos;
    }

    private static int ParseCommandSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        pos += 2; // skip $(
        int depth = 1;
        int start = pos;
        while (pos < raw.Length && depth > 0)
        {
            if (raw[pos] == '(') depth++;
            else if (raw[pos] == ')') depth--;
            if (depth > 0) pos++;
        }
        string inner = raw[start..pos];
        if (pos < raw.Length)
            pos++; // skip closing )

        var body = Parse(inner);
        parts.Add(new WordPart.CommandSub(body ?? new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty)));
        return pos;
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

        var body = Parse(inner);
        parts.Add(new WordPart.CommandSub(body ?? new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty)));
        return pos;
    }

    private static bool IsProcessSubStart(string raw, int pos) =>
        pos + 1 < raw.Length && raw[pos] is '<' or '>' && raw[pos + 1] == '(';

    private static int ParseProcessSub(string raw, int pos, ImmutableArray<WordPart>.Builder parts)
    {
        bool isInput = raw[pos] == '<';
        pos += 2; // skip <( or >(
        int depth = 1;
        int start = pos;
        while (pos < raw.Length && depth > 0)
        {
            if (raw[pos] == '(') depth++;
            else if (raw[pos] == ')') depth--;
            if (depth > 0) pos++;
        }
        string inner = raw[start..pos];
        if (pos < raw.Length)
            pos++; // skip closing )

        var body = Parse(inner);
        parts.Add(new WordPart.ProcessSub(body ?? new Command.Simple(
            ImmutableArray<CompoundWord>.Empty,
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty), isInput));
        return pos;
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

        // Consume the '/' separator so it doesn't appear in the following literal.
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
            if (c == '$' && pos + 1 < len && (IsVarStart(raw[pos + 1]) || raw[pos + 1] == '{' || raw[pos + 1] == '('))
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
            while (pos < len && raw[pos] != ']')
                pos++;
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

        // Find closing brace
        int closePos = pos;
        while (closePos < len && raw[closePos] != '}')
            closePos++;

        string inner = raw[pos..closePos];

        // Advance past closing brace
        if (closePos < len)
            closePos++;

        // Check for range pattern: N..M or N..M..S
        int dotDot = inner.IndexOf("..", StringComparison.Ordinal);
        if (dotDot >= 0 && !inner.Contains(','))
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
        }

        // Comma-separated tuple: split on commas
        string[] items = inner.Split(',');
        parts.Add(new WordPart.BracedTuple(
            ImmutableArray.Create(items)));
        return closePos;
    }

    private static bool IsVarStart(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_'
            or '?' or '!' or '#' or '$' or '@' or '*' or '-'
            or (>= '0' and <= '9');

    private static bool IsVarChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or (>= '0' and <= '9');

    private void SkipNewlines()
    {
        while (Peek().Kind == BashTokenKind.Newline)
            _pos++;
    }
}
