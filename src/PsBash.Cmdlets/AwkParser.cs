namespace PsBash.Cmdlets;

/// <summary>
/// Recursive-descent parser for the AWK grammar. Produces the
/// <see cref="AwkProgram"/> AST evaluated by <see cref="AwkMachine"/>.
/// Implements awk operator precedence (assignment &lt; ternary &lt; || &lt; &amp;&amp; &lt;
/// in &lt; comparison/match &lt; concat &lt; additive &lt; multiplicative &lt; unary &lt;
/// power &lt; postfix &lt; field/primary) and the print/printf redirection rule
/// (a top-level <c>&gt;</c> / <c>&gt;&gt;</c> / <c>|</c> in a print arg list is
/// output redirection, not a comparison — suppressed via <c>_noGt</c>).
/// </summary>
internal sealed class AwkParser
{
    private readonly List<Tok> _t;
    private int _pos;
    private bool _noGt; // inside an unparenthesized print/printf arg list
    private int _depth; // nesting depth, to bound recursion (see EnterDepth)
    private bool _usesMainInput; // saw a getline that reads the main input (AwkProgram.UsesMainInput)

    public AwkParser(List<Tok> tokens) { _t = tokens; }

    // The recursive-descent expression/statement grammar self-recurses on every
    // nesting level — `(((…)))`, `---x`, `a=b=c=…`, nested if/blocks. A crafted
    // program with thousands of levels would otherwise overflow the native stack
    // (an uncatchable process kill). Cap the depth and report a syntax error.
    private const int MaxDepth = 1000;

    private void EnterDepth()
    {
        if (++_depth > MaxDepth) throw Err("expression or statement nesting too deep");
    }

    private Tok Cur => _pos < _t.Count ? _t[_pos] : _t[^1];
    private Tok Peek(int k = 1) => _t[Math.Min(_pos + k, _t.Count - 1)];
    private bool Is(TokKind k) => Cur.Kind == k;
    private bool IsKw(string w) => Cur.Kind == TokKind.Keyword && Cur.Text == w;
    private Tok Advance() => _t[_pos++];

    private Tok Expect(TokKind k, string what)
    {
        if (Cur.Kind != k) throw Err($"expected {what}");
        return Advance();
    }

    private AwkInterpreter.AwkSyntaxException Err(string msg) =>
        new($"awk: syntax error: {msg} near token '{Cur.Text}' ({Cur.Kind})");

    private void SkipNewlines() { while (Is(TokKind.Newline)) _pos++; }
    private void SkipTerminators() { while (Is(TokKind.Newline) || Is(TokKind.Semicolon)) _pos++; }

    // ── program ────────────────────────────────────────────────────────────

    public AwkProgram ParseProgram()
    {
        var prog = new AwkProgram();
        SkipTerminators();
        while (!Is(TokKind.Eof))
        {
            if (IsKw("function") || IsKw("func"))
            {
                ParseFunction(prog);
                SkipTerminators();
                continue;
            }

            var rule = ParseRule();
            switch (rule.Kind)
            {
                case RuleKind.Begin: prog.Begin.Add(rule); break;
                case RuleKind.End: prog.End.Add(rule); break;
                default: prog.Main.Add(rule); break;
            }
            SkipTerminators();
        }
        ValidateFunctions(prog);
        prog.UsesMainInput = _usesMainInput;
        return prog;
    }

    // ── user-defined functions ─────────────────────────────────────────────

    /// <summary>A static (compile-time) error in gawk's wording; reported like any syntax error.</summary>
    private static AwkInterpreter.AwkSyntaxException StaticErr(string kind, string msg) => new($"awk: {kind}: {msg}");

    /// <summary><c>function name(p1, p2, local1) { body }</c> (also <c>func</c>). Newlines are allowed inside the
    /// parameter list and between <c>)</c> and <c>{</c>.</summary>
    private void ParseFunction(AwkProgram prog)
    {
        Advance(); // function | func
        if (Cur.Kind == TokKind.Builtin)
            throw StaticErr("error", $"`{Cur.Text}' is a built-in function, it cannot be redefined");
        if (Cur.Kind != TokKind.Name && Cur.Kind != TokKind.FuncName) throw Err("expected a function name");
        string name = Advance().Text;
        if (prog.Functions.ContainsKey(name)) throw StaticErr("error", $"function name `{name}' previously defined");

        var fn = new AwkFunction { Name = name };
        Expect(TokKind.LParen, "'('");
        SkipNewlines();
        while (!Is(TokKind.RParen))
        {
            string p = Expect(TokKind.Name, "parameter name").Text;
            int dup = fn.Params.IndexOf(p);
            if (dup >= 0)
                throw StaticErr("error", $"function `{name}': parameter #{fn.Params.Count + 1}, `{p}', duplicates parameter #{dup + 1}");
            fn.Params.Add(p);
            SkipNewlines();
            if (!Is(TokKind.Comma)) break;
            Advance();
            SkipNewlines();
        }
        Expect(TokKind.RParen, "')'");
        SkipNewlines();

        prog.Functions[name] = fn; // registered before the body so a recursive call resolves
        bool prev = _inFunction;
        _inFunction = true;
        try { fn.Body = ParseBlock(); }
        finally { _inFunction = prev; }
    }

    /// <summary>
    /// gawk's whole-program checks: a parameter may not be named like a function, a function name may
    /// not be used as a variable or array (which is also what <c>f (x)</c> — a space before the
    /// parenthesis — looks like), and every called function must exist.
    /// </summary>
    private void ValidateFunctions(AwkProgram prog)
    {
        foreach (var fn in prog.Functions.Values)
            foreach (var p in fn.Params)
                if (prog.Functions.ContainsKey(p))
                    throw StaticErr("error", $"function `{fn.Name}': cannot use function name as parameter name");

        foreach (var used in _usedNames)
            if (prog.Functions.ContainsKey(used))
                throw StaticErr("error",
                    $"function `{used}' called with space between name and `(', or used as a variable or an array");

        foreach (var called in _callNames)
            if (!prog.Functions.ContainsKey(called))
                throw StaticErr("fatal", $"function `{called}' not defined");
    }

    private bool _inFunction;
    private readonly HashSet<string> _usedNames = new();   // names read/written as variables or arrays
    private readonly List<string> _callNames = new();      // user-function call sites (name( ... ))

    private AwkRule ParseRule()
    {
        if (IsKw("BEGIN"))
        {
            Advance();
            SkipNewlines();
            return new AwkRule { Kind = RuleKind.Begin, Action = ParseBlock() };
        }
        if (IsKw("END"))
        {
            Advance();
            SkipNewlines();
            return new AwkRule { Kind = RuleKind.End, Action = ParseBlock() };
        }
        if (Is(TokKind.LBrace))
        {
            return new AwkRule { Kind = RuleKind.Always, Action = ParseBlock() };
        }

        // pattern [ action ]
        AwkExpr pattern = ParseExpr();
        BlockStmt? action = null;
        if (Is(TokKind.LBrace)) action = ParseBlock();
        var kind = pattern is RegexLit ? RuleKind.Regex : RuleKind.Expr;
        return new AwkRule { Kind = kind, Pattern = pattern, Action = action };
    }

    // ── statements ───────────────────────────────────────────────────────────

    private BlockStmt ParseBlock()
    {
        Expect(TokKind.LBrace, "'{'");
        var block = new BlockStmt();
        SkipTerminators();
        while (!Is(TokKind.RBrace) && !Is(TokKind.Eof))
        {
            block.Statements.Add(ParseStatement());
            SkipTerminators();
        }
        Expect(TokKind.RBrace, "'}'");
        return block;
    }

    private AwkStmt ParseStatement()
    {
        EnterDepth();
        try { return ParseStatementInner(); }
        finally { _depth--; }
    }

    private AwkStmt ParseStatementInner()
    {
        if (Is(TokKind.LBrace)) return ParseBlock();

        if (Cur.Kind == TokKind.Keyword)
        {
            switch (Cur.Text)
            {
                case "if": return ParseIf();
                case "while": return ParseWhile();
                case "do": return ParseDoWhile();
                case "for": return ParseFor();
                case "break": Advance(); return new BreakStmt();
                case "continue": Advance(); return new ContinueStmt();
                case "next": Advance(); return new NextStmt();
                case "nextfile": Advance(); return new NextFileStmt();
                case "exit": return ParseExit();
                case "delete": return ParseDelete();
                case "print": return ParsePrint();
                case "printf": return ParsePrintf();
                case "return": return ParseReturn();
            }
        }

        // expression statement
        return new ExprStmt { Expr = ParseExpr() };
    }

    private AwkStmt ParseIf()
    {
        Advance(); // if
        Expect(TokKind.LParen, "'('");
        var cond = ParseExpr();
        Expect(TokKind.RParen, "')'");
        SkipNewlines();
        var then = ParseStatement();

        // optional else, possibly after terminators
        int save = _pos;
        SkipTerminators();
        if (IsKw("else"))
        {
            Advance();
            SkipNewlines();
            var els = ParseStatement();
            return new IfStmt { Cond = cond, Then = then, Else = els };
        }
        _pos = save;
        return new IfStmt { Cond = cond, Then = then };
    }

    private AwkStmt ParseWhile()
    {
        Advance();
        Expect(TokKind.LParen, "'('");
        var cond = ParseExpr();
        Expect(TokKind.RParen, "')'");
        SkipNewlines();
        var body = ParseStatement();
        return new WhileStmt { Cond = cond, Body = body };
    }

    private AwkStmt ParseDoWhile()
    {
        Advance();
        SkipNewlines();
        var body = ParseStatement();
        SkipTerminators();
        if (!IsKw("while")) throw Err("expected 'while' after 'do' body");
        Advance();
        Expect(TokKind.LParen, "'('");
        var cond = ParseExpr();
        Expect(TokKind.RParen, "')'");
        return new DoWhileStmt { Body = body, Cond = cond };
    }

    private AwkStmt ParseFor()
    {
        Advance();
        Expect(TokKind.LParen, "'('");

        // for (Name in Array)
        if (Is(TokKind.Name) && Peek().Kind == TokKind.Keyword && Peek().Text == "in")
        {
            string v = Advance().Text;
            Advance(); // in
            string arr = Expect(TokKind.Name, "array name").Text;
            _usedNames.Add(v);
            _usedNames.Add(arr);
            Expect(TokKind.RParen, "')'");
            SkipNewlines();
            var b = ParseStatement();
            return new ForInStmt { Var = v, ArrayName = arr, Body = b };
        }

        AwkStmt? init = Is(TokKind.Semicolon) ? null : new ExprStmt { Expr = ParseExpr() };
        Expect(TokKind.Semicolon, "';'");
        AwkExpr? cond = Is(TokKind.Semicolon) ? null : ParseExpr();
        Expect(TokKind.Semicolon, "';'");
        AwkStmt? post = Is(TokKind.RParen) ? null : new ExprStmt { Expr = ParseExpr() };
        Expect(TokKind.RParen, "')'");
        SkipNewlines();
        var body = ParseStatement();
        return new ForStmt { Init = init, Cond = cond, Post = post, Body = body };
    }

    private AwkStmt ParseReturn()
    {
        if (!_inFunction) throw StaticErr("error", "`return' used outside function context");
        Advance();
        AwkExpr? value = null;
        if (!IsStatementEnd()) value = ParseExpr();
        return new ReturnStmt { Value = value };
    }

    private AwkStmt ParseExit()
    {
        Advance();
        AwkExpr? code = null;
        if (!IsStatementEnd()) code = ParseExpr();
        return new ExitStmt { Code = code };
    }

    private AwkStmt ParseDelete()
    {
        Advance();
        string name = Expect(TokKind.Name, "array name").Text;
        _usedNames.Add(name);
        if (Is(TokKind.LBracket))
        {
            Advance();
            var subs = new List<AwkExpr> { ParseExpr() };
            while (Is(TokKind.Comma)) { Advance(); subs.Add(ParseExpr()); }
            Expect(TokKind.RBracket, "']'");
            return new DeleteStmt { ArrayName = name, Subscripts = subs };
        }
        return new DeleteStmt { ArrayName = name };
    }

    private AwkStmt ParsePrint()
    {
        Advance();
        var stmt = new PrintStmt();
        ParseOutputRest(stmt);
        return stmt;
    }

    private AwkStmt ParsePrintf()
    {
        Advance();
        var stmt = new PrintfStmt();
        ParseOutputRest(stmt);
        return stmt;
    }

    /// <summary>
    /// Parse a print/printf argument list under the no-greater-than rule (a top-level <c>&gt;</c> is a
    /// redirection, not a comparison) and then the optional redirection: <c>&gt; target</c>,
    /// <c>&gt;&gt; target</c> or <c>| target</c>. As in gawk the target is a concatenation-level
    /// expression (<c>print &gt; "out" n</c> writes to the file named <c>"out" n</c>; a comparison or
    /// <c>?:</c> after it is a syntax error — parenthesize it).
    /// </summary>
    private void ParseOutputRest(OutputStmt stmt)
    {
        if (IsStatementEnd() || Is(TokKind.Gt) || Is(TokKind.Append) || Is(TokKind.Pipe))
        {
            // bare `print` — no args; fall through to redirection handling
        }
        else if (!TryParseParenthesizedArgList(stmt.Args))
        {
            bool prev = _noGt;
            _noGt = true;
            try
            {
                stmt.Args.Add(ParseExpr());
                while (Is(TokKind.Comma)) { Advance(); SkipNewlines(); stmt.Args.Add(ParseExpr()); }
            }
            finally { _noGt = prev; }
        }

        if (Is(TokKind.Gt) || Is(TokKind.Append) || Is(TokKind.Pipe))
        {
            stmt.Redir = Cur.Kind switch
            {
                TokKind.Gt => RedirKind.File,
                TokKind.Append => RedirKind.Append,
                _ => RedirKind.Pipe,
            };
            Advance();
            bool prev = _noGt;
            _noGt = true;
            try { stmt.Target = ParseConcat(); }
            finally { _noGt = prev; }
        }
    }

    /// <summary>
    /// <c>print (a, b) &gt; "f"</c> / <c>printf("%s\n", x)</c>: a parenthesized list that is the WHOLE
    /// argument list (followed by a terminator or a redirection). Anything else — <c>print (a)(b)</c>,
    /// <c>print (1 &gt; 2) ? x : y</c> — is rewound and parsed as an ordinary expression.
    /// </summary>
    private bool TryParseParenthesizedArgList(List<AwkExpr> args)
    {
        if (!Is(TokKind.LParen)) return false;
        int save = _pos;
        bool prevGt = _noGt;
        int prevDepth = _depth;
        _noGt = false;
        try
        {
            Advance(); // (
            var list = new List<AwkExpr>();
            SkipNewlines();
            list.Add(ParseExpr());
            while (Is(TokKind.Comma)) { Advance(); SkipNewlines(); list.Add(ParseExpr()); }
            SkipNewlines();
            if (Is(TokKind.RParen))
            {
                Advance();
                if (IsStatementEnd() || Is(TokKind.Gt) || Is(TokKind.Append) || Is(TokKind.Pipe))
                {
                    args.AddRange(list);
                    return true;
                }
            }
        }
        catch (AwkInterpreter.AwkSyntaxException) { /* not a plain list: reparse as an expression */ }
        finally { _noGt = prevGt; _depth = prevDepth; }
        _pos = save;
        return false;
    }

    private bool IsStatementEnd() =>
        Is(TokKind.Semicolon) || Is(TokKind.Newline) || Is(TokKind.RBrace) || Is(TokKind.Eof);

    // ── expressions ──────────────────────────────────────────────────────────

    private AwkExpr ParseExpr() => ParseAssignment();

    private static readonly Dictionary<TokKind, string> AssignOps = new()
    {
        [TokKind.Assign] = "=",
        [TokKind.AddAssign] = "+=",
        [TokKind.SubAssign] = "-=",
        [TokKind.MulAssign] = "*=",
        [TokKind.DivAssign] = "/=",
        [TokKind.ModAssign] = "%=",
        [TokKind.PowAssign] = "^=",
    };

    private AwkExpr ParseAssignment()
    {
        EnterDepth();
        try { return ParseAssignmentInner(); }
        finally { _depth--; }
    }

    private AwkExpr ParseAssignmentInner()
    {
        var left = ParseTernary();
        if (AssignOps.TryGetValue(Cur.Kind, out var op))
        {
            if (!IsLvalue(left)) throw Err("assignment to a non-lvalue");
            Advance();
            var right = ParseAssignment();
            return new Assign { Target = left, Op = op, Value = right };
        }
        return left;
    }

    private AwkExpr ParseTernary()
    {
        var cond = ParseOr();
        if (Is(TokKind.Question))
        {
            Advance(); SkipNewlines();
            var then = ParseAssignment();
            Expect(TokKind.Colon, "':'"); SkipNewlines();
            var els = ParseAssignment();
            return new Ternary { Cond = cond, Then = then, Else = els };
        }
        return cond;
    }

    private AwkExpr ParseOr()
    {
        var left = ParseAnd();
        while (Is(TokKind.Or))
        {
            Advance(); SkipNewlines();
            var right = ParseAnd();
            left = new Logical { Op = "||", Left = left, Right = right };
        }
        return left;
    }

    private AwkExpr ParseAnd()
    {
        var left = ParseIn();
        while (Is(TokKind.And))
        {
            Advance(); SkipNewlines();
            var right = ParseIn();
            left = new Logical { Op = "&&", Left = left, Right = right };
        }
        return left;
    }

    private AwkExpr ParseIn()
    {
        var left = ParseComparison();
        while (IsKw("in"))
        {
            Advance();
            string arr = Expect(TokKind.Name, "array name").Text;
            _usedNames.Add(arr);
            left = new InExpr { Keys = { left }, ArrayName = arr };
        }
        return left;
    }

    private AwkExpr ParseComparison()
    {
        var left = ParseConcat();

        // `cmd | getline [var]`: the pipe binds below concatenation (`"echo " x | getline` pipes the
        // whole concatenation) and above comparison (`cmd | getline > 0` is `(cmd | getline) > 0`).
        // `print x | "cmd"` is an output redirection: only a `getline` after the bar makes it this form.
        while (Is(TokKind.Pipe) && Peek().Kind == TokKind.Keyword && Peek().Text == "getline")
        {
            Advance(); Advance(); // | getline
            left = new GetlineExpr { Source = GetlineSource.Command, Operand = left, Target = ParseGetlineTarget() };
        }

        // non-associative: at most one comparison/match operator
        switch (Cur.Kind)
        {
            case TokKind.Lt: Advance(); return new Compare { Op = "<", Left = left, Right = ParseConcat() };
            case TokKind.Le: Advance(); return new Compare { Op = "<=", Left = left, Right = ParseConcat() };
            case TokKind.Eq: Advance(); return new Compare { Op = "==", Left = left, Right = ParseConcat() };
            case TokKind.Ne: Advance(); return new Compare { Op = "!=", Left = left, Right = ParseConcat() };
            case TokKind.Ge: Advance(); return new Compare { Op = ">=", Left = left, Right = ParseConcat() };
            case TokKind.Gt:
                if (_noGt) return left; // redirection, handled by print-arg parser
                Advance(); return new Compare { Op = ">", Left = left, Right = ParseConcat() };
            case TokKind.Match: Advance(); return new MatchExpr { Left = left, Right = ParseConcat(), Negated = false };
            case TokKind.NotMatch: Advance(); return new MatchExpr { Left = left, Right = ParseConcat(), Negated = true };
            default: return left;
        }
    }

    private AwkExpr ParseConcat()
    {
        var left = ParseAdditive();
        while (CanStartConcatOperand())
        {
            var right = ParseAdditive();
            left = new Concat { Left = left, Right = right };
        }
        return left;
    }

    private bool CanStartConcatOperand()
    {
        switch (Cur.Kind)
        {
            case TokKind.Number:
            case TokKind.String:
            case TokKind.Regex:
            case TokKind.Name:
            case TokKind.FuncName:
            case TokKind.Builtin:
            case TokKind.Dollar:
            case TokKind.LParen:
            case TokKind.Not:
            case TokKind.Incr:
            case TokKind.Decr:
                return true;
            default:
                return false;
        }
    }

    private AwkExpr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Is(TokKind.Plus) || Is(TokKind.Minus))
        {
            char op = Advance().Text[0];
            var right = ParseMultiplicative();
            left = new Arith { Op = op, Left = left, Right = right };
        }
        return left;
    }

    private AwkExpr ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Is(TokKind.Star) || Is(TokKind.Slash) || Is(TokKind.Percent))
        {
            char op = Advance().Text[0];
            var right = ParseUnary();
            left = new Arith { Op = op, Left = left, Right = right };
        }
        return left;
    }

    private AwkExpr ParseUnary()
    {
        EnterDepth();
        try { return ParseUnaryInner(); }
        finally { _depth--; }
    }

    private AwkExpr ParseUnaryInner()
    {
        if (Is(TokKind.Not)) { Advance(); return new Unary { Op = '!', Operand = ParseUnary() }; }
        if (Is(TokKind.Minus)) { Advance(); return new Unary { Op = '-', Operand = ParseUnary() }; }
        if (Is(TokKind.Plus)) { Advance(); return new Unary { Op = '+', Operand = ParseUnary() }; }
        if (Is(TokKind.Incr)) { Advance(); return new IncDec { Increment = true, Prefix = true, Target = ParseUnary() }; }
        if (Is(TokKind.Decr)) { Advance(); return new IncDec { Increment = false, Prefix = true, Target = ParseUnary() }; }
        return ParsePower();
    }

    private AwkExpr ParsePower()
    {
        var left = ParsePostfix();
        if (Is(TokKind.Caret))
        {
            Advance();
            var right = ParseUnary(); // right-associative; allows unary on the exponent
            return new Power { Left = left, Right = right };
        }
        return left;
    }

    private AwkExpr ParsePostfix()
    {
        var e = ParsePrimary();
        while ((Is(TokKind.Incr) || Is(TokKind.Decr)) && IsLvalue(e))
        {
            bool inc = Advance().Kind == TokKind.Incr;
            e = new IncDec { Increment = inc, Prefix = false, Target = e };
        }
        return e;
    }

    private AwkExpr ParsePrimary()
    {
        EnterDepth();
        try { return ParsePrimaryInner(); }
        finally { _depth--; }
    }

    private AwkExpr ParsePrimaryInner()
    {
        switch (Cur.Kind)
        {
            case TokKind.Number: { double v = Advance().Num; return new NumLit { Value = v }; }
            case TokKind.String: { string s = Advance().Text; return new StrLit { Value = s }; }
            case TokKind.Regex: { string r = Advance().Text; return new RegexLit { Pattern = r }; }

            case TokKind.Dollar:
            {
                Advance();
                var idx = ParsePrimary();
                return new FieldRef { Index = idx };
            }

            case TokKind.FuncName:
            {
                string name = Advance().Text;
                Expect(TokKind.LParen, "'('");
                var args = ParseCallArgs();
                _callNames.Add(name);
                return new Call { Name = name, Args = args };
            }

            case TokKind.Builtin:
            {
                string name = Advance().Text;
                if (Is(TokKind.LParen))
                {
                    Advance();
                    var args = ParseCallArgs();
                    return new Call { Name = name, Args = args };
                }
                // length without parens (the only legal no-paren builtin)
                return new Call { Name = name, Args = new List<AwkExpr>() };
            }

            case TokKind.Name:
            {
                string name = Advance().Text;
                _usedNames.Add(name);
                if (Is(TokKind.LBracket))
                {
                    Advance();
                    var subs = new List<AwkExpr> { ParseSubExpr() };
                    while (Is(TokKind.Comma)) { Advance(); subs.Add(ParseSubExpr()); }
                    Expect(TokKind.RBracket, "']'");
                    return new ArrayRef { Name = name, Subscripts = subs };
                }
                return new VarRef { Name = name };
            }

            case TokKind.LParen:
                return ParseParenthesized();

            case TokKind.Keyword when Cur.Text == "getline":
                return ParseGetline();

            default:
                throw Err("unexpected token in expression");
        }
    }

    /// <summary>
    /// <c>getline [lvalue] [&lt; file]</c> at primary level. The file operand is gawk's <c>simp_exp</c>:
    /// additive-level, NO concatenation and no comparison, so <c>getline &lt; "a" "b"</c> is
    /// <c>(getline &lt; "a") "b"</c> and <c>getline line &lt; f &gt; 0</c> is <c>(getline line &lt; f) &gt; 0</c>.
    /// </summary>
    private AwkExpr ParseGetline()
    {
        Advance(); // getline
        var target = ParseGetlineTarget();
        if (!Is(TokKind.Lt))
        {
            _usesMainInput = true;
            return new GetlineExpr { Source = GetlineSource.Main, Target = target };
        }
        Advance(); // <
        var file = ParseAdditive();
        // A literal "-" / "/dev/stdin" names the main stdin, which a push-driven stdin run cannot serve.
        if (file is StrLit { Value: "-" or "/dev/stdin" }) _usesMainInput = true;
        return new GetlineExpr { Source = GetlineSource.File, Target = target, Operand = file };
    }

    /// <summary>The optional lvalue after <c>getline</c>: <c>name</c>, <c>name[subs]</c> or <c>$expr</c>.</summary>
    private AwkExpr? ParseGetlineTarget()
    {
        if (Is(TokKind.Dollar))
        {
            Advance();
            return new FieldRef { Index = ParsePrimary() };
        }
        return Is(TokKind.Name) ? ParsePrimary() : null;
    }

    /// <summary>A subscript expression parsed with greater-than re-enabled.</summary>
    private AwkExpr ParseSubExpr()
    {
        bool prev = _noGt; _noGt = false;
        try { return ParseExpr(); } finally { _noGt = prev; }
    }

    private List<AwkExpr> ParseCallArgs()
    {
        bool prev = _noGt; _noGt = false;
        try
        {
            var args = new List<AwkExpr>();
            SkipNewlines();
            if (!Is(TokKind.RParen))
            {
                args.Add(ParseExpr());
                while (Is(TokKind.Comma)) { Advance(); SkipNewlines(); args.Add(ParseExpr()); }
            }
            Expect(TokKind.RParen, "')'");
            return args;
        }
        finally { _noGt = prev; }
    }

    private AwkExpr ParseParenthesized()
    {
        bool prev = _noGt; _noGt = false;
        try
        {
            Advance(); // (
            var first = ParseExpr();
            if (Is(TokKind.Comma))
            {
                // (a, b, ...) in arr  → grouped membership test
                var keys = new List<AwkExpr> { first };
                while (Is(TokKind.Comma)) { Advance(); keys.Add(ParseExpr()); }
                Expect(TokKind.RParen, "')'");
                if (IsKw("in"))
                {
                    Advance();
                    string arr = Expect(TokKind.Name, "array name").Text;
                    _usedNames.Add(arr);
                    return new InExpr { Keys = keys, ArrayName = arr };
                }
                // Not an `in` test — degrade to the last expression (rare).
                return new Grouping { Inner = keys[^1] };
            }
            Expect(TokKind.RParen, "')'");
            return new Grouping { Inner = first };
        }
        finally { _noGt = prev; }
    }

    private static bool IsLvalue(AwkExpr e) => e is VarRef or FieldRef or ArrayRef
        || (e is Grouping g && IsLvalue(g.Inner));
}
