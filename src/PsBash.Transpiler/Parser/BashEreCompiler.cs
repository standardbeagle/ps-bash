namespace PsBash.Core.Parser;

internal enum EreKind : byte
{
    Char, Set, Period, Anchor, BackRef, End, OpenSub, CloseSub, Alt, Star, Concat, Subexp,
}

/// <summary>Anchor contexts (glibc's ctx_type), evaluated at a position of the subject.</summary>
internal static class EreAnchor
{
    internal const int LineFirst = 1, LineLast = 2, BufFirst = 3, BufLast = 4, WordFirst = 5, WordLast = 6,
        InsideWord = 7, InsideNotWord = 8;
}

/// <summary>A bracket expression / class escape: members, ranges and classes, possibly negated.</summary>
internal sealed class EreSet
{
    internal bool Negate;
    internal int Classes;
    internal List<int>? Chars;
    internal List<int>? Ranges; // lo, hi pairs
    private ulong _ascii0, _ascii1;

    internal void AddChar(int cp) => (Chars ??= []).Add(cp);

    internal void AddRange(int lo, int hi)
    {
        Ranges ??= [];
        Ranges.Add(lo);
        Ranges.Add(hi);
    }

    // Precompute the ASCII answers (the common subject).
    internal EreSet Finish()
    {
        for (int cp = 0; cp < 128; cp++)
            if (Slow(cp))
            {
                if (cp < 64) _ascii0 |= 1UL << cp;
                else _ascii1 |= 1UL << (cp - 64);
            }
        return this;
    }

    internal bool Matches(int cp)
    {
        if (cp < 64) return (_ascii0 >> cp & 1) != 0;
        if (cp < 128) return (_ascii1 >> (cp - 64) & 1) != 0;
        return Slow(cp);
    }

    private bool Slow(int cp)
    {
        bool member = Classes != 0 && BashEreClasses.InAny(Classes, cp);
        if (!member && Chars != null) member = Chars.Contains(cp);
        if (!member && Ranges != null)
            for (int i = 0; i < Ranges.Count && !member; i += 2)
                member = Ranges[i] <= cp && cp <= Ranges[i + 1];
        return member != Negate;
    }
}

/// <summary>A node of the parse tree, shaped as glibc's <c>bin_tree_t</c> (parent links, first/next).</summary>
internal sealed class EreTree
{
    internal EreKind Kind;
    internal EreTree? Left, Right, Parent;
    internal int Value; // Char: code point; Anchor: context; BackRef/Subexp/OpenSub/CloseSub: group index
    internal EreSet? Set;
    internal bool Opt;  // opt_subexp
    internal EreTree? First, Next;
    internal int NodeIdx = -1;
}

internal sealed class EreSyntaxException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

/// <summary>
/// glibc 2.39 <c>regcomp</c> for RE_SYNTAX_POSIX_EXTENDED, ported function by function (<c>peek_token</c>,
/// <c>parse_reg_exp</c>, <c>parse_branch</c>, <c>parse_expression</c>, <c>parse_sub_exp</c>, <c>parse_dup_op</c>,
/// <c>parse_bracket_exp</c>, then <c>analyze</c>'s passes), over code points in the C.UTF-8 locale.
/// </summary>
internal sealed class BashEreCompiler
{
    // A regex whose tree grows past this (`(a{1000}){1000}`) is refused (REG_ESPACE) instead of exhausting memory.
    internal const int MaxTreeNodes = 400_000;
    private const int DupMax = 0x7fff; // RE_DUP_MAX

    private enum T : byte
    {
        Char, Alt, Star, Plus, Quest, OpenDup, CloseDup, Open, Close, Bracket, Period, Anchor,
        Word, NotWord, Space, NotSpace, BackRef, BackSlash, End,
        // bracket tokens
        Range, CloseBracket, NonMatch, OpenColl, OpenEquiv, OpenClass,
    }

    private const int WordDelim = 100, NotWordDelim = 101;

    private struct Tok
    {
        public T Type;
        public int C;   // the character (translated unless escaped)
        public int Arg; // anchor context / back-reference index
        public int Len;
    }

    private readonly int[] _raw;
    private readonly int[] _fold;
    private readonly bool _icase;
    private int _pos;
    private int _nsub;
    private uint _completed;
    private bool _hasBackref;
    private int _treeCount;

    private BashEreCompiler(int[] raw, bool icase)
    {
        _raw = raw;
        _icase = icase;
        _fold = icase ? Array.ConvertAll(raw, BashEreClasses.ToUpper) : raw;
    }

    internal static BashEreProgram Compile(string regex, bool icase)
    {
        try
        {
            var c = new BashEreCompiler(BashEreProgram.CodePoints(regex, out _), icase);
            return c.Build();
        }
        catch (EreSyntaxException e) { return BashEreProgram.Failed(e.Code); }
        catch (ParseException) { return BashEreProgram.Failed("REG_ESPACE"); } // nesting past NestingGuard
        catch (InsufficientExecutionStackException) { return BashEreProgram.Failed("REG_ESPACE"); }
        // Any other failure is this compiler's bug, but a regex must never take the host down: status 2.
        catch (Exception e) when (e is not OutOfMemoryException) { return BashEreProgram.Failed("REG_ESPACE"); }
    }

    // ---- tokens -------------------------------------------------------------------------------------------

    private Tok Peek()
    {
        var t = new Tok { Len = 1 };
        if (_pos >= _raw.Length) { t.Type = T.End; t.Len = 0; return t; }
        int c = _fold[_pos];
        t.C = c;
        if (_raw[_pos] == '\\')
        {
            if (_pos + 1 >= _raw.Length) { t.Type = T.BackSlash; return t; }
            int c2 = _raw[_pos + 1];
            // An escaped character is NOT case-translated (re_string_peek_byte_case); a multibyte one effectively is.
            t.C = c2 < 0x80 ? c2 : _fold[_pos + 1];
            t.Len = 2;
            t.Type = T.Char;
            switch (c2)
            {
                case >= '1' and <= '9': t.Type = T.BackRef; t.Arg = c2 - '1'; break;
                case '<': t.Type = T.Anchor; t.Arg = EreAnchor.WordFirst; break;
                case '>': t.Type = T.Anchor; t.Arg = EreAnchor.WordLast; break;
                case 'b': t.Type = T.Anchor; t.Arg = WordDelim; break;
                case 'B': t.Type = T.Anchor; t.Arg = NotWordDelim; break;
                case 'w': t.Type = T.Word; break;
                case 'W': t.Type = T.NotWord; break;
                case 's': t.Type = T.Space; break;
                case 'S': t.Type = T.NotSpace; break;
                case '`': t.Type = T.Anchor; t.Arg = EreAnchor.BufFirst; break;
                case '\'': t.Type = T.Anchor; t.Arg = EreAnchor.BufLast; break;
            }
            return t;
        }
        t.Type = c switch
        {
            '|' => T.Alt,
            '*' => T.Star,
            '+' => T.Plus,
            '?' => T.Quest,
            '{' => T.OpenDup,
            '}' => T.CloseDup,
            '(' => T.Open,
            ')' => T.Close,
            '[' => T.Bracket,
            '.' => T.Period,
            '^' => T.Anchor,
            '$' => T.Anchor,
            _ => T.Char,
        };
        if (c == '^') t.Arg = EreAnchor.LineFirst;
        else if (c == '$') t.Arg = EreAnchor.LineLast;
        return t;
    }

    private void Fetch(ref Tok t)
    {
        t = Peek();
        _pos += t.Len;
    }

    private Tok PeekBracket()
    {
        var t = new Tok { Len = 1 };
        if (_pos >= _raw.Length) { t.Type = T.End; t.Len = 0; return t; }
        int c = _fold[_pos];
        t.C = c;
        if (c >= 0x80) { t.Type = T.Char; return t; }
        if (c == '[')
        {
            int c2 = _pos + 1 < _raw.Length ? _fold[_pos + 1] : 0;
            t.Len = 2;
            t.C = c2;
            switch (c2)
            {
                case '.': t.Type = T.OpenColl; break;
                case '=': t.Type = T.OpenEquiv; break;
                case ':': t.Type = T.OpenClass; break;
                default: t.Type = T.Char; t.C = c; t.Len = 1; break;
            }
            return t;
        }
        t.Type = c switch { '-' => T.Range, ']' => T.CloseBracket, '^' => T.NonMatch, _ => T.Char };
        return t;
    }

    // ---- tree helpers -------------------------------------------------------------------------------------

    private EreTree Create(EreTree? left, EreTree? right, EreKind kind, int value = 0, EreSet? set = null)
    {
        if (++_treeCount > MaxTreeNodes) throw new EreSyntaxException("REG_ESPACE");
        var t = new EreTree { Kind = kind, Left = left, Right = right, Value = value, Set = set };
        if (left != null) left.Parent = t;
        if (right != null) right.Parent = t;
        return t;
    }

    // duplicate_tree: a copy of every node (create_token_tree clears opt_subexp, so the copy is not optional).
    private EreTree Duplicate(EreTree root)
    {
        EreTree? result = null;
        var stack = new Stack<(EreTree Src, EreTree? Parent, bool Left)>();
        stack.Push((root, null, false));
        while (stack.Count > 0)
        {
            var (src, parent, left) = stack.Pop();
            var copy = Create(null, null, src.Kind, src.Value, src.Set);
            copy.Parent = parent;
            if (parent == null) result = copy;
            else if (left) parent.Left = copy;
            else parent.Right = copy;
            if (src.Right != null) stack.Push((src.Right, copy, false));
            if (src.Left != null) stack.Push((src.Left, copy, true));
        }
        return result!;
    }

    private static void PostOrder(EreTree root, Action<EreTree> fn)
    {
        var node = root;
        while (true)
        {
            while (node.Left != null || node.Right != null)
                node = node.Left ?? node.Right!;
            EreTree prev;
            do
            {
                fn(node);
                if (node.Parent == null) return;
                prev = node;
                node = node.Parent;
            }
            while (node.Right == prev || node.Right == null);
            node = node.Right;
        }
    }

    private static void PreOrder(EreTree root, Action<EreTree> fn)
    {
        var node = root;
        while (true)
        {
            fn(node);
            if (node.Left != null) node = node.Left;
            else
            {
                EreTree? prev = null;
                while (node.Right == prev || node.Right == null)
                {
                    prev = node;
                    node = node.Parent!;
                    if (node == null) return;
                }
                node = node.Right;
            }
        }
    }

    // ---- parser -------------------------------------------------------------------------------------------

    private BashEreProgram Build()
    {
        var tok = default(Tok);
        Fetch(ref tok);
        var tree = ParseRegExp(ref tok, 0);
        var end = Create(null, null, EreKind.End);
        var root = tree != null ? Create(tree, end, EreKind.Concat) : end;
        return Analyze(root, end);
    }

    private EreTree? ParseRegExp(ref Tok tok, int nest)
    {
        uint initial = _completed;
        var tree = ParseBranch(ref tok, nest);
        while (tok.Type == T.Alt)
        {
            Fetch(ref tok);
            EreTree? branch = null;
            if (tok.Type != T.Alt && tok.Type != T.End && (nest == 0 || tok.Type != T.Close))
            {
                uint accumulated = _completed;
                _completed = initial;
                branch = ParseBranch(ref tok, nest);
                _completed |= accumulated;
            }
            tree = Create(tree, branch, EreKind.Alt);
        }
        return tree;
    }

    private EreTree? ParseBranch(ref Tok tok, int nest)
    {
        var tree = ParseExpression(ref tok, nest);
        while (tok.Type != T.Alt && tok.Type != T.End && (nest == 0 || tok.Type != T.Close))
        {
            var expr = ParseExpression(ref tok, nest);
            if (tree != null && expr != null) tree = Create(tree, expr, EreKind.Concat);
            else if (tree == null) tree = expr;
        }
        return tree;
    }

    private EreTree? ParseExpression(ref Tok tok, int nest)
    {
        EreTree? tree;
        switch (tok.Type)
        {
            case T.Char:
            case T.Close:    // RE_UNMATCHED_RIGHT_PAREN_ORD: a stray `)` is literal
            case T.CloseDup: // a stray `}` is literal
                tree = Create(null, null, EreKind.Char, tok.C);
                break;
            case T.Anchor:
                if (tok.Arg is WordDelim or NotWordDelim)
                {
                    bool delim = tok.Arg == WordDelim;
                    var first = Create(null, null, EreKind.Anchor, delim ? EreAnchor.WordFirst : EreAnchor.InsideWord);
                    var last = Create(null, null, EreKind.Anchor, delim ? EreAnchor.WordLast : EreAnchor.InsideNotWord);
                    tree = Create(first, last, EreKind.Alt);
                }
                else tree = Create(null, null, EreKind.Anchor, tok.Arg);
                // An anchor cannot take a repetition: `^*` is REG_BADRPT (the `*` starts the next expression).
                Fetch(ref tok);
                return tree;
            case T.Period:
                tree = Create(null, null, EreKind.Period);
                break;
            case T.Word:
            case T.NotWord:
                tree = ClassOp(BashEreClasses.Alnum, '_', tok.Type == T.NotWord);
                break;
            case T.Space:
            case T.NotSpace:
                tree = ClassOp(BashEreClasses.Space, -1, tok.Type == T.NotSpace);
                break;
            case T.Alt:
            case T.End:
                return null;
            case T.BackSlash:
                throw new EreSyntaxException("REG_EESCAPE");
            case T.Open:
                tree = ParseSubExp(ref tok, nest + 1);
                break;
            case T.Bracket:
                tree = ParseBracket();
                break;
            case T.BackRef:
                if ((_completed & (1u << tok.Arg)) == 0) throw new EreSyntaxException("REG_ESUBREG");
                _hasBackref = true;
                tree = Create(null, null, EreKind.BackRef, tok.Arg);
                break;
            case T.OpenDup:
            case T.Star:
            case T.Plus:
            case T.Quest:
                // RE_CONTEXT_INVALID_OPS: a repetition with nothing to repeat.
                throw new EreSyntaxException("REG_BADRPT");
            default:
                throw new EreSyntaxException("REG_BADPAT");
        }
        Fetch(ref tok);
        while (tok.Type is T.Star or T.Plus or T.Quest or T.OpenDup)
            tree = ParseDupOp(tree, ref tok);
        return tree;
    }

    private EreTree ClassOp(int cls, int extra, bool negate)
    {
        var set = new EreSet { Classes = cls, Negate = negate };
        if (extra >= 0) set.AddChar(extra);
        return Create(null, null, EreKind.Set, set: set.Finish());
    }

    private EreTree ParseSubExp(ref Tok tok, int nest)
    {
        using var _ = NestingGuard.Enter();
        int cur = _nsub++;
        Fetch(ref tok);
        EreTree? tree = null;
        if (tok.Type != T.Close)
        {
            tree = ParseRegExp(ref tok, nest);
            if (tok.Type != T.Close) throw new EreSyntaxException("REG_EPAREN");
        }
        if (cur <= 8) _completed |= 1u << cur;
        return Create(tree, null, EreKind.Subexp, cur);
    }

    private int FetchNumber(ref Tok tok)
    {
        int num = -1;
        while (true)
        {
            Fetch(ref tok);
            int c = tok.C;
            if (tok.Type == T.End) return -2;
            if (tok.Type == T.CloseDup || (tok.Type is T.Char && c == ',')) break;
            num = tok.Type != T.Char || c < '0' || c > '9' || num == -2
                ? -2
                : num == -1 ? c - '0' : Math.Min(DupMax + 1, num * 10 + c - '0');
        }
        return num;
    }

    private EreTree? ParseDupOp(EreTree? elem, ref Tok tok)
    {
        int start, end;
        if (tok.Type == T.OpenDup)
        {
            end = 0;
            start = FetchNumber(ref tok);
            if (start == -1)
            {
                if (tok.Type == T.Char && tok.C == ',') start = 0; // {,m} is {0,m}
                else throw new EreSyntaxException("REG_BADBR"); // {}
            }
            if (start != -2)
                end = tok.Type == T.CloseDup ? start
                    : tok.Type == T.Char && tok.C == ',' ? FetchNumber(ref tok) : -2;
            if (start == -2 || end == -2)
                throw new EreSyntaxException(tok.Type == T.End ? "REG_EBRACE" : "REG_BADBR");
            if ((end != -1 && start > end) || tok.Type != T.CloseDup) throw new EreSyntaxException("REG_BADBR");
            if ((end == -1 ? start : end) > DupMax) throw new EreSyntaxException("REG_ESIZE");
        }
        else
        {
            start = tok.Type == T.Plus ? 1 : 0;
            end = tok.Type == T.Quest ? 1 : -1;
        }
        Fetch(ref tok);
        if (elem == null) return null;
        if (start == 0 && end == 0) return null;

        EreTree? oldTree = null;
        EreTree tree;
        if (start > 0)
        {
            tree = elem;
            for (int i = 2; i <= start; i++)
            {
                elem = Duplicate(elem);
                tree = Create(tree, elem, EreKind.Concat);
            }
            if (start == end) return tree;
            elem = Duplicate(elem);
            oldTree = tree;
        }
        if (elem.Kind == EreKind.Subexp) elem.Opt = true; // mark_opt_subexp
        tree = Create(elem, null, end == -1 ? EreKind.Star : EreKind.Alt);
        for (int i = start + 2; i <= end; i++)
        {
            elem = Duplicate(elem);
            tree = Create(tree, elem, EreKind.Concat);
            tree = Create(tree, null, EreKind.Alt);
        }
        if (oldTree != null) tree = Create(oldTree, tree, EreKind.Concat);
        return tree;
    }

    // ---- bracket expressions ------------------------------------------------------------------------------

    private enum ElemKind : byte { SbChar, MbChar, CollSym, EquivClass, CharClass }

    private struct Elem
    {
        public ElemKind Kind;
        public int Ch;
        public string? Name;
        public int NameBytes;
    }

    private EreTree ParseBracket()
    {
        var set = new EreSet();
        var tok = PeekBracket();
        if (tok.Type == T.End) throw new EreSyntaxException("REG_BADPAT");
        if (tok.Type == T.NonMatch)
        {
            set.Negate = true;
            _pos += tok.Len;
            tok = PeekBracket();
            if (tok.Type == T.End) throw new EreSyntaxException("REG_BADPAT");
        }
        if (tok.Type == T.CloseBracket) tok.Type = T.Char; // a first `]` is a member
        bool firstRound = true;
        while (true)
        {
            var startElem = ParseBracketElement(tok, firstRound);
            firstRound = false;
            tok = PeekBracket();
            bool isRange = false;
            var tok2 = default(Tok);
            if (startElem.Kind != ElemKind.CharClass && startElem.Kind != ElemKind.EquivClass)
            {
                if (tok.Type == T.End) throw new EreSyntaxException("REG_EBRACK");
                if (tok.Type == T.Range)
                {
                    _pos += tok.Len;
                    tok2 = PeekBracket();
                    if (tok2.Type == T.End) throw new EreSyntaxException("REG_EBRACK");
                    if (tok2.Type == T.CloseBracket)
                    {
                        _pos -= tok.Len; // a last `-` is a member
                        tok.Type = T.Char;
                    }
                    else isRange = true;
                }
            }
            if (isRange)
            {
                var endElem = ParseBracketElement(tok2, true);
                tok = PeekBracket();
                int lo = RangeEndpoint(startElem), hi = RangeEndpoint(endElem);
                if (lo > hi) throw new EreSyntaxException("REG_ERANGE");
                set.AddRange(lo, hi);
            }
            else
            {
                switch (startElem.Kind)
                {
                    case ElemKind.SbChar:
                    case ElemKind.MbChar:
                        // An invalid byte is a single-byte character whose bit glibc masks out in UTF-8 (bitset_mask
                        // with sb_char): it is never a member (and Matcher.Accepts refuses it to every set).
                        if (!IsRawByte(startElem.Ch)) set.AddChar(startElem.Ch);
                        break;
                    case ElemKind.EquivClass:
                    case ElemKind.CollSym:
                        // C.UTF-8 has no collation rules: only a one-byte name is valid, and it is that character
                        // (an invalid byte is valid but masked out, as above).
                        if (startElem.NameBytes != 1) throw new EreSyntaxException("REG_ECOLLATE");
                        if (!IsRawByte(startElem.Name![0])) set.AddChar(startElem.Name![0]);
                        break;
                    case ElemKind.CharClass:
                        string name = startElem.Name!;
                        if (_icase && name is "upper" or "lower") name = "alpha";
                        int cls = BashEreClasses.ByName(name);
                        if (cls == 0) throw new EreSyntaxException("REG_ECTYPE");
                        set.Classes |= cls;
                        break;
                }
            }
            if (tok.Type == T.End) throw new EreSyntaxException("REG_EBRACK");
            if (tok.Type == T.CloseBracket) break;
        }
        _pos += tok.Len;
        return Create(null, null, EreKind.Set, set: set.Finish());
    }

    // lookup_collation_sequence_value with nrules == 0: a single-byte character (or a one-byte collating symbol) is
    // its own value (collseqmb), an invalid byte included: its byte value, so [a-\xff] is a..U+00FF and
    // [[.\xff.]-z] is REG_ERANGE; a multibyte endpoint is REG_ECOLLATE; a class is REG_ERANGE.
    private static int RangeEndpoint(Elem e) => e.Kind switch
    {
        ElemKind.SbChar => e.Ch,
        ElemKind.MbChar when IsRawByte(e.Ch) => e.Ch - 0xDC00,
        ElemKind.CollSym when e.NameBytes == 1 => IsRawByte(e.Name![0]) ? e.Name![0] - 0xDC00 : e.Name![0],
        ElemKind.EquivClass or ElemKind.CharClass => throw new EreSyntaxException("REG_ERANGE"),
        _ => throw new EreSyntaxException("REG_ECOLLATE"),
    };

    private Elem ParseBracketElement(Tok tok, bool acceptHyphen)
    {
        if (_pos < _raw.Length && _fold[_pos] >= 0x80)
        {
            var mb = new Elem { Kind = ElemKind.MbChar, Ch = _fold[_pos] };
            _pos++;
            return mb;
        }
        _pos += tok.Len;
        if (tok.Type is T.OpenColl or T.OpenClass or T.OpenEquiv) return ParseBracketSymbol(tok);
        if (tok.Type == T.Range && !acceptHyphen)
        {
            // A `-` that is neither a range nor the last member: undefined, glibc says REG_ERANGE.
            if (PeekBracket().Type != T.CloseBracket) throw new EreSyntaxException("REG_ERANGE");
        }
        return new Elem { Kind = ElemKind.SbChar, Ch = tok.C };
    }

    private Elem ParseBracketSymbol(Tok tok)
    {
        int delim = tok.C;
        bool isClass = tok.Type == T.OpenClass;
        if (_pos >= _raw.Length) throw new EreSyntaxException("REG_EBRACK");
        var name = new System.Text.StringBuilder();
        int bytes = 0;
        while (true)
        {
            int ch = isClass ? _raw[_pos] : _fold[_pos]; // a class name is read untranslated
            int len = Utf8Length(ch);
            if (bytes + len > 32) throw new EreSyntaxException("REG_EBRACK"); // BRACKET_NAME_BUF_SIZE
            _pos++;
            if (_pos >= _raw.Length) throw new EreSyntaxException("REG_EBRACK");
            if (ch == delim && _fold[_pos] == ']') break;
            // A lone surrogate (an invalid byte's marker, or one from a broken string) is one UTF-16 unit, which
            // ConvertFromUtf32 would refuse.
            if (ch < 0x10000) name.Append((char)ch);
            else name.Append(char.ConvertFromUtf32(ch));
            bytes += len;
        }
        _pos++;
        return new Elem
        {
            Kind = tok.Type switch { T.OpenColl => ElemKind.CollSym, T.OpenEquiv => ElemKind.EquivClass, _ => ElemKind.CharClass },
            Name = name.ToString(),
            NameBytes = bytes,
        };
    }

    // An invalid byte's marker stands for that one byte.
    private static int Utf8Length(int cp) =>
        cp < 0x80 || IsRawByte(cp) ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4;

    // RawBytes' marker of an invalid UTF-8 byte (CodePoints keeps a lone surrogate as one code point).
    private static bool IsRawByte(int cp) => cp >= RawBytes.FirstMarker && cp <= RawBytes.LastMarker;

    // ---- analyze: the NFA ---------------------------------------------------------------------------------

    private BashEreProgram Analyze(EreTree root, EreTree endLeaf)
    {
        // optimize_subexps: `((x))` keeps one group; the inner index reports the outer one's span.
        var subexpMap = new int[_nsub];
        for (int i = 0; i < _nsub; i++) subexpMap[i] = i;
        PreOrder(root, node =>
        {
            if (node.Kind == EreKind.BackRef) node.Value = subexpMap[node.Value];
            else if (node.Kind == EreKind.Subexp && node.Left is { Kind: EreKind.Subexp } inner)
            {
                node.Left = inner.Left;
                if (node.Left != null) node.Left.Parent = node;
                subexpMap[inner.Value] = subexpMap[node.Value];
            }
        });

        // lower_subexps: SUBEXP -> OPEN body CLOSE.
        PostOrder(root, node =>
        {
            if (node.Left is { Kind: EreKind.Subexp } l)
            {
                node.Left = LowerSubexp(l);
                node.Left.Parent = node;
            }
            if (node.Right is { Kind: EreKind.Subexp } r)
            {
                node.Right = LowerSubexp(r);
                node.Right.Parent = node;
            }
        });

        // calc_first (postorder numbering = glibc's node indices, which order every choice).
        var nodes = new List<EreTree>();
        PostOrder(root, node =>
        {
            if (node.Kind == EreKind.Concat)
            {
                node.First = node.Left!.First;
                node.NodeIdx = node.Left.NodeIdx;
            }
            else
            {
                node.First = node;
                node.NodeIdx = nodes.Count;
                nodes.Add(node);
            }
        });

        // calc_next
        PreOrder(root, node =>
        {
            switch (node.Kind)
            {
                case EreKind.Star:
                    node.Left!.Next = node;
                    break;
                case EreKind.Concat:
                    node.Left!.Next = node.Right!.First;
                    node.Right.Next = node.Next;
                    break;
                default:
                    if (node.Left != null) node.Left.Next = node.Next;
                    if (node.Right != null) node.Right.Next = node.Next;
                    break;
            }
        });

        return BashEreProgram.FromTree(nodes, root.First!.NodeIdx, endLeaf.NodeIdx, _nsub, subexpMap, _hasBackref, _icase);
    }

    private EreTree LowerSubexp(EreTree node)
    {
        var body = node.Left;
        var op = Create(null, null, EreKind.OpenSub, node.Value);
        var cls = Create(null, null, EreKind.CloseSub, node.Value);
        op.Opt = cls.Opt = node.Opt;
        var tree1 = body != null ? Create(body, cls, EreKind.Concat) : cls;
        return Create(op, tree1, EreKind.Concat);
    }
}
