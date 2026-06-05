using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashLet</c> function
/// (REFACTOR-2 follow-on, vars batch). Implements the bash <c>let</c>
/// builtin: evaluate one or more arithmetic expressions and assign the
/// result to the named variable in the caller's scope.
///
/// <para><b>Security note — Directive 12 hardening:</b> the psm1 oracle
/// delegated evaluation to <c>Invoke-Expression</c> on lightly munged user
/// input — a serious quoting hazard at the bash/PowerShell seam. This
/// cmdlet implements a small purpose-built integer-arithmetic parser
/// instead. User-controlled tokens are never re-parsed as PowerShell;
/// <c>$(throw 'pwn')</c> or <c>;rm -rf</c> in the expression text yields
/// a "let: ... expression error" exit-1 result, not code execution.</para>
///
/// Supported grammar (subset matching the bash arithmetic builtin's common
/// surface):
/// <list type="bullet">
/// <item><c>NAME=EXPR</c> — assign EXPR's value to NAME in the caller's
/// scope. NAME must be a bash identifier ([A-Za-z_][A-Za-z0-9_]*).</item>
/// <item><c>EXPR</c> — evaluate without assignment.</item>
/// <item>Operators: <c>+ - * / % ** ( )</c> with standard precedence
/// (<c>**</c> right-associative, then <c>* / %</c>, then <c>+ -</c>).
/// Unary <c>+</c> / <c>-</c> on the leading term.</item>
/// <item>Operands: decimal integer literals OR bash identifiers (looked up
/// via <see cref="PSVariableIntrinsics.GetValue(string)"/> in the caller's
/// scope; missing / non-integer variables resolve to 0, matching bash).</item>
/// </list>
///
/// Exit code: matches the oracle byte-for-byte — <c>$global:LASTEXITCODE</c>
/// is set to 1 if any evaluated EXPR is zero (the bash "false" convention)
/// OR if a syntax / lookup error fires; 0 otherwise. A syntax error also
/// emits a bash-style error via <see cref="FileSystemHelpers.WriteBashError"/>
/// and returns early (oracle parity — the rest of the operand list is
/// skipped).
///
/// No stdout output (variable side-effect only).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashLet")]
public sealed class InvokeBashLetCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "let", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "let"))
            {
                WriteObject(line);
            }
            return;
        }

        bool anyZero = false;
        foreach (var raw in args)
        {
            if (string.IsNullOrEmpty(raw)) continue;

            string? targetName = null;
            string exprText;

            // Optional NAME=EXPR prefix. The first '=' splits; an empty LHS
            // or non-identifier LHS falls through to "expression only" mode
            // (so a stray "=1" is a parse error like the oracle's would be).
            int eq = raw.IndexOf('=');
            // Skip == (comparison, not assignment). bash let supports comparison
            // operators returning 0/1, but the oracle's IE-based path treated
            // = as PowerShell assignment, never comparison. Preserve that.
            if (eq > 0 && (eq + 1 >= raw.Length || raw[eq + 1] != '='))
            {
                var lhs = raw.Substring(0, eq).Trim();
                if (IsIdentifier(lhs))
                {
                    targetName = lhs;
                    exprText = raw.Substring(eq + 1);
                }
                else
                {
                    exprText = raw;
                }
            }
            else
            {
                exprText = raw;
            }

            long result;
            try
            {
                var parser = new LetParser(exprText, this);
                result = parser.ParseAndConsume();
            }
            catch (FormatException)
            {
                FileSystemHelpers.WriteBashError(
                    this, $"let: {raw} : expression error");
                SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
                return;
            }
            catch (DivideByZeroException)
            {
                FileSystemHelpers.WriteBashError(
                    this, $"let: {raw} : expression error");
                SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
                return;
            }
            catch (OverflowException)
            {
                FileSystemHelpers.WriteBashError(
                    this, $"let: {raw} : expression error");
                SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
                return;
            }

            if (targetName != null)
            {
                // Set the variable in the caller's scope. The oracle relied on
                // PowerShell's `Invoke-Expression "NAME=EXPR"` which assigns in
                // the local scope of the calling function. The runspace-scope
                // PSVariable.Set has the same observable effect from a cmdlet.
                SessionState.PSVariable.Set(targetName, result);
            }

            if (result == 0) anyZero = true;
        }

        SessionState.PSVariable.Set("global:LASTEXITCODE", anyZero ? 1 : 0);
    }

    private static bool IsIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        for (int i = 1; i < s.Length; i++)
        {
            if (!char.IsLetterOrDigit(s[i]) && s[i] != '_') return false;
        }
        return true;
    }

    /// <summary>
    /// Tiny recursive-descent parser for the let-supported arithmetic subset.
    /// All evaluation runs in C# — user tokens are never fed to a PowerShell
    /// script body (Directive 12).
    /// </summary>
    private sealed class LetParser
    {
        private readonly string _src;
        private readonly PSCmdlet _cmdlet;
        private int _pos;

        public LetParser(string src, PSCmdlet cmdlet)
        {
            _src = src;
            _cmdlet = cmdlet;
            _pos = 0;
        }

        public long ParseAndConsume()
        {
            SkipWhitespace();
            var v = ParseAdditive();
            SkipWhitespace();
            if (_pos < _src.Length) throw new FormatException("trailing tokens");
            return v;
        }

        private long ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _src.Length) return left;
                char c = _src[_pos];
                if (c == '+') { _pos++; left = checked(left + ParseMultiplicative()); }
                else if (c == '-') { _pos++; left = checked(left - ParseMultiplicative()); }
                else return left;
            }
        }

        private long ParseMultiplicative()
        {
            var left = ParsePower();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _src.Length) return left;
                char c = _src[_pos];
                if (c == '*' && (_pos + 1 >= _src.Length || _src[_pos + 1] != '*'))
                {
                    _pos++;
                    left = checked(left * ParsePower());
                }
                else if (c == '/')
                {
                    _pos++;
                    var r = ParsePower();
                    if (r == 0) throw new DivideByZeroException();
                    left = left / r;
                }
                else if (c == '%')
                {
                    _pos++;
                    var r = ParsePower();
                    if (r == 0) throw new DivideByZeroException();
                    left = left % r;
                }
                else return left;
            }
        }

        private long ParsePower()
        {
            var b = ParseUnary();
            SkipWhitespace();
            if (_pos + 1 < _src.Length && _src[_pos] == '*' && _src[_pos + 1] == '*')
            {
                _pos += 2;
                var exp = ParsePower();   // right-associative
                if (exp < 0) throw new FormatException("negative exponent");
                long result = 1;
                for (long i = 0; i < exp; i++) result = checked(result * b);
                return result;
            }
            return b;
        }

        private long ParseUnary()
        {
            SkipWhitespace();
            if (_pos >= _src.Length) throw new FormatException("unexpected end");
            char c = _src[_pos];
            if (c == '+') { _pos++; return ParseUnary(); }
            if (c == '-') { _pos++; return checked(-ParseUnary()); }
            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _src.Length) throw new FormatException("unexpected end");
            char c = _src[_pos];
            if (c == '(')
            {
                _pos++;
                var v = ParseAdditive();
                SkipWhitespace();
                if (_pos >= _src.Length || _src[_pos] != ')')
                    throw new FormatException("unbalanced paren");
                _pos++;
                return v;
            }
            if (char.IsDigit(c))
            {
                int start = _pos;
                while (_pos < _src.Length && char.IsDigit(_src[_pos])) _pos++;
                if (!long.TryParse(_src.Substring(start, _pos - start), out var n))
                    throw new FormatException("bad integer");
                return n;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = _pos;
                while (_pos < _src.Length &&
                       (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_')) _pos++;
                var name = _src.Substring(start, _pos - start);
                // Look up the variable in the caller's scope; missing or
                // non-integer variables resolve to 0 (bash arithmetic
                // convention).
                var val = _cmdlet.SessionState.PSVariable.GetValue(name);
                if (val == null) return 0;
                if (val is long lv) return lv;
                if (val is int iv) return iv;
                var s = val.ToString();
                if (string.IsNullOrEmpty(s)) return 0;
                if (long.TryParse(s, out var parsed)) return parsed;
                return 0;
            }
            throw new FormatException($"unexpected char '{c}'");
        }

        private void SkipWhitespace()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
        }
    }
}
