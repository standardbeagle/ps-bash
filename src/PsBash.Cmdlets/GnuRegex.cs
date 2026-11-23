using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

internal enum GnuRegexDialect { Basic, Extended, Emacs }

/// <summary>
/// The syntax bits that separate the GNU tools' regex flavors (glibc's <c>RE_SYNTAX_*</c>), measured against GNU sed
/// 4.9, grep 3.11, findutils 4.9 and coreutils expr. One preset per tool; <see cref="GnuRegex"/> reads only these.
/// </summary>
internal sealed record GnuRegexSyntax(GnuRegexDialect Dialect)
{
    /// <summary>
    /// BRE: <c>\{</c> with nothing before it, and a <c>*</c> or <c>\{</c> right after another repetition, are
    /// "Invalid preceding regular expression"; ERE: a repetition with nothing before it is that error (sed, find). Off
    /// (grep, expr): BRE reads a leading <c>\{1\}</c> as the text <c>{1}</c> and applies a stacked repetition, and grep
    /// -E drops a leading repetition with a warning.
    /// </summary>
    public bool ContextInvalidDup { get; init; }

    /// <summary>ERE: a <c>{</c> that starts no valid interval (<c>a{</c>, <c>a{1,x}</c>) is text (grep -E), not an error.</summary>
    public bool InvalidIntervalIsText { get; init; }

    /// <summary>ERE: a <c>)</c> with no <c>(</c> open is text (grep -E, find), not "Unmatched ) or \)".</summary>
    public bool UnmatchedRightParenIsText { get; init; }

    /// <summary>A bracket of just <c>:name:</c> (<c>[:space:]</c>) is <see cref="GnuRegex.ColonClassMessage"/> (GNU's dfa: grep, sed).</summary>
    public bool ColonClassIsError { get; init; }

    /// <summary>
    /// sed's pattern space: <c>\n</c> is a newline (also in a bracket), and the text is one buffer that may hold
    /// several lines, so <c>.</c> and <c>[^x]</c> match a newline and <c>^</c> / <c>$</c> are only its start and end.
    /// Newline-sensitive (the <c>M</c> flag): <c>.</c> and <c>[^x]</c> do not match a newline, <c>^</c> / <c>$</c> also
    /// match after / before one.
    /// </summary>
    public bool SedBuffer { get; init; }

    public static readonly GnuRegexSyntax SedBasic = new(GnuRegexDialect.Basic) { ContextInvalidDup = true, ColonClassIsError = true, SedBuffer = true };
    public static readonly GnuRegexSyntax SedExtended = new(GnuRegexDialect.Extended) { ContextInvalidDup = true, ColonClassIsError = true, SedBuffer = true };
    public static readonly GnuRegexSyntax GrepBasic = new(GnuRegexDialect.Basic) { ColonClassIsError = true };
    public static readonly GnuRegexSyntax GrepExtended = new(GnuRegexDialect.Extended)
    {
        InvalidIntervalIsText = true, UnmatchedRightParenIsText = true, ColonClassIsError = true,
    };
    public static readonly GnuRegexSyntax ExprBasic = new(GnuRegexDialect.Basic);
    public static readonly GnuRegexSyntax FindEmacs = new(GnuRegexDialect.Emacs);
    public static readonly GnuRegexSyntax FindBasic = new(GnuRegexDialect.Basic) { ContextInvalidDup = true };
    public static readonly GnuRegexSyntax FindExtended = new(GnuRegexDialect.Extended) { ContextInvalidDup = true, UnmatchedRightParenIsText = true };
}

/// <summary>
/// THE translation of a GNU regex (BRE, ERE, emacs) into a .NET pattern, for every tool that takes one: sed, grep, expr,
/// find. An atom-tracking walk, so a repetition knows what it repeats: POSIX anchors are positional in a BRE (<c>a$b</c>
/// is text), a leading <c>*</c> is text, a stacked repetition (<c>a**</c>, <c>a{1}{2}</c>) is wrapped for .NET or is
/// GNU's error, intervals and bracket expressions are validated, and an invalid pattern yields GNU's own message
/// (<c>Unmatched \{</c>, <c>Invalid character class name</c>, ...). Every other escape is the character itself
/// (<c>\d</c> is <c>d</c>, not .NET's digit class). The flavors differ only by <see cref="GnuRegexSyntax"/>.
/// </summary>
internal static class GnuRegex
{
    public const string ColonClassMessage = "character class syntax is [[:space:]], not [:space:]";

    /// <summary>
    /// Translate <paramref name="pattern"/>. False with GNU's message in <paramref name="error"/>. A warning GNU prints
    /// and goes on (grep -E's <c>* at start of expression</c>) is added to <paramref name="warnings"/>.
    /// </summary>
    public static bool TryTranslate(
        string pattern, GnuRegexSyntax syntax, out string net, out string? error,
        bool newlineSensitive = false, List<string>? warnings = null)
    {
        var t = new Walk(pattern, syntax, newlineSensitive, warnings);
        bool ok = t.Run();
        net = ok ? t.Result : "";
        error = t.Error;
        return ok;
    }

    private sealed class Walk
    {
        private readonly string _p;
        private readonly GnuRegexSyntax _syn;
        private readonly bool _nl;
        private readonly List<string>? _warnings;
        private readonly bool _ext, _emacs, _basic;
        private readonly StringBuilder _sb = new();
        private readonly Stack<(int Start, int Number)> _open = new();
        private readonly HashSet<int> _closed = new();
        private int _groups;
        private int _i;
        private bool _atStart = true;      // start of the RE, or just after a group open / alternation (/ a leading ^)
        private int _atom = -1;            // where the repeatable atom just emitted starts in _sb; -1: none to repeat
        private bool _repeated;            // that atom already carries a repetition

        public string? Error;
        public string Result => _sb.ToString();

        public Walk(string p, GnuRegexSyntax syn, bool nl, List<string>? warnings)
        {
            _p = p; _syn = syn; _nl = nl; _warnings = warnings;
            _ext = syn.Dialect == GnuRegexDialect.Extended;
            _emacs = syn.Dialect == GnuRegexDialect.Emacs;
            _basic = syn.Dialect == GnuRegexDialect.Basic;
        }

        private bool Fail(string msg) { Error = msg; return false; }

        private void Atom(string s) { _atom = _sb.Length; _sb.Append(s); _repeated = false; _atStart = false; }

        private void Assertion(string s) { _sb.Append(s); _atom = -1; _repeated = false; _atStart = false; }

        private string Dot => !_syn.SedBuffer ? "." : _nl ? @"[^\n]" : "(?s:.)";

        public bool Run()
        {
            while (_i < _p.Length)
            {
                char c = _p[_i];
                if (c == '\\')
                {
                    if (_i + 1 >= _p.Length) return Fail("Trailing backslash");
                    char n = _p[_i + 1];
                    _i += 2;
                    if (!Escaped(n)) return false;
                    continue;
                }
                _i++;
                if (!Plain(c)) return false;
            }
            if (_open.Count > 0) return Fail(@"Unmatched ( or \(");
            return true;
        }

        private bool Escaped(char n)
        {
            if (!_ext)
            {
                switch (n)
                {
                    case '(': OpenGroup(); return true;
                    case ')': return CloseGroup();
                    case '|': Alternate(); return true;
                }
            }
            if (_basic && n is '+' or '?') return Repeat(n.ToString(), n);
            if (_basic && n == '{') return BreInterval();
            switch (n)
            {
                case 'w': Atom(@"\w"); return true;
                case 'W': Atom(@"\W"); return true;
                case 's' when !_emacs: Atom(@"\s"); return true;
                case 'S' when !_emacs: Atom(@"\S"); return true;
                case 'b': Assertion(@"\b"); return true;
                case 'B': Assertion(@"\B"); return true;
                case '<': Assertion(@"\b(?=\w)"); return true;
                case '>': Assertion(@"\b(?<=\w)"); return true;
                case '`': Assertion(@"\A"); return true;
                case '\'': Assertion(@"\z"); return true;
                case 'n' when _syn.SedBuffer: Atom(@"\n"); return true;
                case >= '1' and <= '9':
                    if (!_closed.Contains(n - '0')) return Fail("Invalid back reference");
                    Atom(@"(?:\" + n + ")");   // grouped: a digit after it is not part of the number
                    return true;
            }
            Atom(Regex.Escape(n.ToString()));
            return true;
        }

        private bool Plain(char c)
        {
            switch (c)
            {
                case '(' when _ext: OpenGroup(); return true;
                case ')' when _ext:
                    if (_open.Count == 0 && _syn.UnmatchedRightParenIsText) { Atom(@"\)"); return true; }
                    return CloseGroup();
                case '|' when _ext: Alternate(); return true;
                case '*': return Repeat("*", '*');
                case '+' when _ext || _emacs: return Repeat("+", '+');
                case '?' when _ext || _emacs: return Repeat("?", '?');
                case '{' when _ext: return EreInterval();
                case '.': Atom(Dot); return true;
                case '[': return Bracket();
                case '^':
                    if (_ext || _atStart)
                    {
                        _sb.Append(!_syn.SedBuffer ? "^" : _nl ? "(?m:^)" : @"\A");
                        _atom = -1; _repeated = false; _atStart = true;   // a `*` after a leading ^ has nothing to repeat
                    }
                    else Atom(@"\^");
                    return true;
                case '$':
                {
                    // BRE/emacs: an anchor only at the end, or before \) / \| (sed 's/a$(b/X/' rewrites "a$(b").
                    bool anchor = _ext || _i >= _p.Length || StartsAt(@"\)") || StartsAt(@"\|");
                    if (anchor) Assertion(!_syn.SedBuffer ? "$" : _nl ? "(?m:$)" : @"\z");
                    else Atom(@"\$");
                    return true;
                }
                default:
                    Atom(Regex.Escape(c.ToString()));
                    return true;
            }
        }

        private bool StartsAt(string t) => string.CompareOrdinal(_p, _i, t, 0, t.Length) == 0;

        private void OpenGroup()
        {
            _open.Push((_sb.Length, ++_groups));
            _sb.Append('(');
            _atom = -1; _repeated = false; _atStart = true;
        }

        private bool CloseGroup()
        {
            if (_open.Count == 0) return Fail(@"Unmatched ) or \)");
            var (start, number) = _open.Pop();
            _sb.Append(')');
            _closed.Add(number);
            _atom = start; _repeated = false; _atStart = false;
            return true;
        }

        private void Alternate()
        {
            _sb.Append('|');
            _atom = -1; _repeated = false; _atStart = true;
        }

        /// <summary>A repetition with nothing to repeat: text (BRE, emacs), or GNU's error / grep -E's dropped-with-a-warning.</summary>
        private bool NothingToRepeat(string text, string warning)
        {
            if (!_ext) { Atom(Regex.Escape(text)); return true; }
            if (_syn.ContextInvalidDup) return Fail("Invalid preceding regular expression");
            _warnings?.Add($"{warning} at start of expression");
            return true;
        }

        private bool Repeat(string op, char kind)
        {
            if (_atom < 0) return NothingToRepeat(kind.ToString(), kind.ToString());
            if (_repeated)
            {
                if (_emacs && kind == '?') { _sb.Append('?'); return true; }   // emacs: *? +? ?? are non-greedy
                if (_basic && _syn.ContextInvalidDup && kind == '*') return Fail("Invalid preceding regular expression");
                Wrap();
            }
            _sb.Append(op);
            _repeated = true;
            _atStart = false;
            return true;
        }

        /// <summary>A second repetition of one atom (<c>a**</c>, <c>a{1}{2}</c>): .NET refuses it bare, so group what is repeated.</summary>
        private void Wrap()
        {
            _sb.Insert(_atom, "(?:");
            _sb.Append(')');
        }

        private bool ApplyInterval(string net, string text)
        {
            if (_atom < 0)
            {
                if (!_ext && _syn.ContextInvalidDup) return Fail("Invalid preceding regular expression");
                return NothingToRepeat(text, "{...}");   // grep/expr BRE: `\{1\}` is the text "{1}"
            }
            if (_repeated)
            {
                if (_basic && _syn.ContextInvalidDup) return Fail("Invalid preceding regular expression");
                Wrap();
            }
            _sb.Append(net);
            _repeated = true;
            _atStart = false;
            return true;
        }

        private bool BreInterval()
        {
            int close = _p.IndexOf(@"\}", _i, StringComparison.Ordinal);
            if (close < 0) return Fail(@"Unmatched \{");
            string body = _p.Substring(_i, close - _i);
            if (Interval(body, out var net) != IntervalShape.Valid) return Fail(@"Invalid content of \{\}");
            _i = close + 2;
            return ApplyInterval(net, "{" + body + "}");
        }

        private bool EreInterval()
        {
            int close = _p.IndexOf('}', _i);
            string body = close < 0 ? "" : _p.Substring(_i, close - _i);
            var shape = close < 0 ? IntervalShape.Malformed : Interval(body, out _);
            if (shape == IntervalShape.Reversed) return Fail(@"Invalid content of \{\}");
            if (shape == IntervalShape.Malformed)
            {
                if (_syn.InvalidIntervalIsText) { Atom(@"\{"); return true; }
                return Fail(close < 0 ? @"Unmatched \{" : @"Invalid content of \{\}");
            }
            Interval(body, out var net);
            _i = close + 1;
            return ApplyInterval(net, "{" + body + "}");
        }

        private enum IntervalShape { Valid, Malformed, Reversed }

        /// <summary><c>m</c>, <c>m,</c>, <c>,n</c>, <c>m,n</c> (GNU: an omitted minimum is 0) as a .NET quantifier.</summary>
        private static IntervalShape Interval(string body, out string net)
        {
            net = "";
            int comma = body.IndexOf(',');
            string lo = comma < 0 ? body : body.Substring(0, comma);
            string? hi = comma < 0 ? null : body.Substring(comma + 1);
            if (!AllDigits(lo) || (hi != null && !AllDigits(hi)) || (lo.Length == 0 && hi == null)) return IntervalShape.Malformed;
            if (!int.TryParse(lo.Length == 0 ? "0" : lo, out int min)) return IntervalShape.Reversed;
            if (hi == null) { net = "{" + min + "}"; return IntervalShape.Valid; }
            if (hi.Length == 0) { net = "{" + min + ",}"; return IntervalShape.Valid; }
            if (!int.TryParse(hi, out int max) || max < min) return IntervalShape.Reversed;
            net = "{" + min + "," + max + "}";
            return IntervalShape.Valid;
        }

        private static bool AllDigits(string s)
        {
            foreach (char ch in s) if (!char.IsAsciiDigit(ch)) return false;
            return true;
        }

        /// <summary>
        /// A bracket expression from just after its <c>[</c>: a leading <c>]</c> is a member, a backslash is a member
        /// (sed: <c>\n</c> is a newline), <c>[:class:]</c> <c>[=c=]</c> <c>[.c.]</c> are units, a range must not run
        /// backwards. Newline-sensitive, a negated set also excludes the newline.
        /// </summary>
        private bool Bracket()
        {
            int j = _i;
            bool neg = false;
            if (j < _p.Length && _p[j] == '^') { neg = true; j++; }
            int contentStart = j;
            var body = new StringBuilder();
            bool first = true, sawRange = false;
            while (true)
            {
                if (j >= _p.Length) return Fail("Unmatched [, [^, [:, [., or [=");
                char c = _p[j];
                if (c == ']' && !first) { j++; break; }
                first = false;
                if (c == '[' && !_emacs && j + 1 < _p.Length && _p[j + 1] is ':' or '=' or '.')
                {
                    char kind = _p[j + 1];
                    int end = _p.IndexOf(kind + "]", j + 2, StringComparison.Ordinal);
                    if (end < 0) return Fail("Unmatched [, [^, [:, [., or [=");
                    string name = _p.Substring(j + 2, end - j - 2);
                    if (kind == ':')
                    {
                        string? cls = name == "word" ? null : PsBash.Core.Parser.BashPattern.PosixClass(name);
                        if (cls is null) return Fail("Invalid character class name");
                        body.Append(cls);
                    }
                    else
                    {
                        if (name.Length != 1) return Fail("Invalid collation character");
                        body.Append(EscSet(name[0]));
                    }
                    j = end + 2;
                    continue;
                }
                char member = c;
                int width = 1;
                if (c == '\\' && _syn.SedBuffer && j + 1 < _p.Length && _p[j + 1] == 'n') { member = '\n'; width = 2; }
                // a range a-z (a '-' that is first or last is a member)
                if (j + width + 1 < _p.Length && _p[j + width] == '-' && _p[j + width + 1] != ']')
                {
                    char hi = _p[j + width + 1];
                    if (hi < member) return Fail("Invalid range end");
                    body.Append(EscSet(member)).Append('-').Append(EscSet(hi));
                    j += width + 2;
                    sawRange = true;
                    continue;
                }
                body.Append(member == '\n' ? @"\n" : EscSet(member));
                j += width;
            }
            // GNU's dfa: `[:space:]` (a bracket that is just `:name:`) is a mistake for `[[:space:]]`.
            int contentEnd = j - 1;
            if (_syn.ColonClassIsError && !sawRange && contentEnd - contentStart >= 3
                && _p[contentStart] == ':' && _p[contentEnd - 1] == ':'
                && _p.AsSpan(contentStart, contentEnd - contentStart).IndexOfAnyExcept(':') >= 0)
                return Fail(ColonClassMessage);
            if (neg && _nl) body.Append(@"\n");
            _i = j;
            Atom("[" + (neg ? "^" : "") + body + "]");
            return true;
        }

        private static string EscSet(char ch) =>
            ch is '\\' or ']' or '[' or '^' or '-' ? "\\" + ch : ch.ToString();
    }
}
