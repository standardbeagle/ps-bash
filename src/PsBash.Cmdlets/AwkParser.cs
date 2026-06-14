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

    public AwkParser(List<Tok> tokens) { _t = tokens; }

    private Tok Cur => _t[_pos];
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
                throw Err("user-defined functions are not supported");

            var rule = ParseRule();
            switch (rule.Kind)
            {
                case RuleKind.Begin: prog.Begin.Add(rule); break;
                case RuleKind.End: prog.End.Add(rule); break;
                default: prog.Main.Add(rule); break;
            }
            SkipTerminators();
        }
        return prog;
    }

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
                case "return": throw Err("'return' outside a function is not supported");
                case "getline": throw Err("getline is not supported");
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
        var args = ParsePrintArgs();
        return new PrintStmt { Args = args };
    }

    private AwkStmt ParsePrintf()
    {
        Advance();
        var args = ParsePrintArgs();
        return new PrintfStmt { Args = args };
    }

    /// <summary>
    /// Parse a print/printf argument list under the no-greater-than rule, then
    /// swallow (and discard) any output redirection target. Output redirection
    /// to files/pipes is a documented gap — untested, routed to stdout.
    /// </summary>
    private List<AwkExpr> ParsePrintArgs()
    {
        var args = new List<AwkExpr>();
        if (IsStatementEnd() || Is(TokKind.Gt) || Is(TokKind.Append) || Is(TokKind.Pipe))
        {
            // bare `print` — no args; fall through to redirection handling
        }
        else
        {
            bool prev = _noGt;
            _noGt = true;
            try
            {
                args.Add(ParseExpr());
                while (Is(TokKind.Comma)) { Advance(); SkipNewlines(); args.Add(ParseExpr()); }
            }
            finally { _noGt = prev; }
        }

        // discard redirection target (unsupported)
        if (Is(TokKind.Gt) || Is(TokKind.Append) || Is(TokKind.Pipe))
        {
            Advance();
            ParseExpr();
        }
        return args;
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
            left = new InExpr { Keys = { left }, ArrayName = arr };
        }
        return left;
    }

    private AwkExpr ParseComparison()
    {
        var left = ParseConcat();
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

            default:
                throw Err("unexpected token in expression");
        }
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
