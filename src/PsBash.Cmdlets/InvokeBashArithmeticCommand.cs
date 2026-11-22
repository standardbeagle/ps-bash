using System.Globalization;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Evaluates a bash arithmetic expression (<c>$(( ))</c> / <c>(( ))</c>) with
/// faithful integer semantics via <see cref="BashArith"/>, and writes the
/// 64-bit integer result to the pipeline. The transpiler emits
/// <c>$(Invoke-BashArith '&lt;expr&gt;')</c> in place of the old
/// pass-the-string-to-PowerShell approach, which mistranslated <c>**</c>,
/// integer division, bitwise/shift operators, C-style comparisons/logicals
/// (1/0 vs True/False), the ternary operator, number bases, and more.
///
/// Variable resolution mirrors the transpiler's model: a bare name resolves to
/// a live PowerShell variable when one exists (loop variables, builtins), else
/// the environment variable (ordinary bash variables are stored as
/// <c>$env:NAME</c>), else <c>0</c> (bash treats unset as zero). A value that is
/// itself an arithmetic string is evaluated recursively, as in bash
/// (<c>a=b; b=5; echo $((a))</c> → 5), with a cycle guard. Assignments
/// (<c>$((x=…))</c>, <c>$((x+=…))</c>, <c>++</c>/<c>--</c>) persist to the
/// environment so subsequent <c>$env:NAME</c> reads see them.
///
/// On a malformed or runtime-invalid expression (division by zero, bad base) it
/// writes bash's diagnostic to stderr -- the shell's prefix (<see cref="ShellDiagnostic"/>), the
/// <see cref="Label"/>, the expression and the error token:
/// <c>s.sh: line 3: ((: 1 / 0 : division by 0 (error token is "0 ")</c> -- and sets exit
/// code 1, emitting no value — matching bash, where a failed arithmetic
/// evaluation aborts the expansion.
///
/// Security (Directive 12): the expression is parsed by a dedicated lexer and
/// evaluated numerically — it is NEVER passed to <c>Invoke-Expression</c> or a
/// scriptblock, so a value containing <c>;</c>, <c>$()</c>, or backticks cannot
/// be re-parsed as PowerShell.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashArith")]
[OutputType(typeof(long))]
public sealed class InvokeBashArithmeticCommand : BashCmdlet
{
    [Parameter(Position = 0, ValueFromRemainingArguments = true)]
    public string[]? Tokens { get; set; }

    /// <summary>
    /// The command evaluating the expression, which bash names after the prefix: <c>let</c> (Invoke-BashLet),
    /// <c>((</c> (the arithmetic command and the clauses of <c>for ((…))</c>), <c>[[</c> (an arithmetic operand of
    /// <c>[[ … -eq … ]]</c>): <c>s.sh: line 3: let: 1+: syntax error: operand expected (error token is "+")</c>.
    /// Without it (<c>$(( … ))</c>, a subscript) the report is <c>s.sh: line 3: EXPR: MESSAGE</c>.
    /// </summary>
    [Parameter]
    public string? Label { get; set; }

    /// <summary>The expression is an array subscript (<c>${a[1/0]}</c>, <c>a[1/0]=x</c>): see
    /// <see cref="FileSystemHelpers.MarkExpansionFailed(PSCmdlet, bool)"/>.</summary>
    [Parameter]
    public SwitchParameter Subscript { get; set; }

    /// <summary>Every arithmetic error is the shell's diagnostic: its prefix, then the label (<see cref="ShellDiagnostic"/>).</summary>
    private void ReportError(string body)
        => FileSystemHelpers.WriteBashError(this, ShellDiagnostic.Prefix(SessionState) + (Label is null ? "" : Label + ": ") + body);

    protected override void EndProcessing()
    {
        // The transpiler emits a single quoted argument, but accept multiple
        // tokens (joined with spaces) so an unquoted expression still works.
        // bash names the expression without its leading blanks but WITH its trailing ones (`(( 1/0 ))` reports
        // `((: 1/0 : division by 0 (error token is "0 ")`), so the emitter passes the text right-untrimmed.
        var expr = Tokens is { Length: > 0 } ? string.Join(" ", Tokens).TrimStart() : "0";
        // An empty / blank expression is 0 in bash (`$(( ))`, `[[ "" -eq 0 ]]`).
        if (string.IsNullOrWhiteSpace(expr)) expr = "0";

        // Set directly, not through SetLastExitCode: that would clear the mark an EARLIER word of the same
        // command left (`echo $((1/0)) $((1+1))` is still abandoned).
        FileSystemHelpers.SetLastExitCode(this, 0);
        try
        {
            long result = BashArith.Evaluate(SubstituteExpressionVariables(expr), ReadVar, WriteVar, ReadRawVar);
            WriteObject(result);
        }
        catch (UnboundVariableException)
        {
            // already reported; no value
        }
        catch (ArithRecursionException ex)
        {
            ReportError($"{ex.Token}: expression recursion level exceeded (error token is \"{ex.Token}\")");
            if (IsWordExpansion()) FileSystemHelpers.MarkExpansionFailed(this);
        }
        catch (BashArith.BashArithException ex)
        {
            // A failed WORD abandons the command it belongs to (see PsBuild.AbandonIfExpansionFailed, echo).
            ReportError(ex.Describe(expr));   // status 1
            // Marked LAST: reporting the error goes through SetLastExitCode, which ends any mark.
            bool inSubscript = Subscript.IsPresent || ex.InSubscript;
            if (inSubscript || IsWordExpansion()) FileSystemHelpers.MarkExpansionFailed(this, inSubscript);
        }
    }

    /// <summary>
    /// True for <c>$(( expr ))</c>, which the emitter writes as <c>$(Invoke-BashArith ...)</c> (under nounset:
    /// <c>$__psbash_av = Invoke-BashArith ...</c>). The command and condition forms (<c>(( expr ))</c>,
    /// <c>[[ a -eq b ]]</c>: <c>@(Invoke-BashArith ...)</c>, <c>[long](Invoke-BashArith ...)</c>) are not words:
    /// only a failed WORD abandons the command it belongs to.
    /// </summary>
    private bool IsWordExpansion()
    {
        var before = (MyInvocation.Line ?? "").AsSpan();
        int at = MyInvocation.OffsetInLine - 1;
        if (at < 0 || at > before.Length) return false;
        before = before[..at];
        return before.EndsWith("$(") || before.EndsWith("$__psbash_av = ");
    }

    /// <summary>
    /// Resolve a bare identifier to a long. Order: live PowerShell variable
    /// (loop vars / builtins), then environment variable, then 0. A non-numeric
    /// value is recursively evaluated as an arithmetic expression (bash
    /// semantics), guarded against reference cycles.
    /// </summary>
    private sealed class UnboundVariableException : Exception { }

    /// <summary>
    /// bash expands <c>$x</c> / <c>${x}</c> TEXTUALLY before parsing, so x='1 + 2' gives <c>$(( $x * 3 ))</c> = 7,
    /// not 9. Only a value that is more than one plain integer needs this; a plain integer reads the same either way.
    /// </summary>
    private string SubstituteExpressionVariables(string expr)
    {
        if (expr.IndexOf('$') < 0) return expr;
        return System.Text.RegularExpressions.Regex.Replace(expr, @"\$(?:\{([A-Za-z_]\w*)\}|([A-Za-z_]\w*))(?![\w\[])", m =>
        {
            string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            string? raw = ReadRawVar(name);
            if (string.IsNullOrEmpty(raw) || System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\s*-?\d+\s*$")) return m.Value;
            return " " + raw + " ";
        });
    }

    /// <summary>bash's MAX_EXPR_RECURSION_LEVEL: a value that is an expression naming itself (<c>loop='i<=100&&(s+=i,i++,loop)'</c>)
    /// recurses until this depth, then the expansion fails (<c>a=b b=a; $((a))</c>: "expression recursion level exceeded").</summary>
    private const int MaxRecursionDepth = 1024;

    /// <summary>A variable's value nested past <see cref="MaxRecursionDepth"/> (or past what the stack holds).</summary>
    private sealed class ArithRecursionException(string token) : Exception(token)
    {
        public string Token { get; } = token;
    }

    private long ReadVar(string name) => ReadVar(name, 0);

    private long ReadVar(string name, int depth)
    {
        if (depth >= MaxRecursionDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new ArithRecursionException(name);

        string? raw = ReadRawVar(name);
        // `set -u`: an unset name in an arithmetic expression ends the script like any other unbound read.
        if (raw is null && SessionState.PSVariable.GetValue("__BashNounset") is true)
        {
            // Reported on the error stream here; the emitted wrapper (PsBuild.ArithNounsetGuard) sees the
            // global and ends the script -- a cmdlet exception would only end this statement.
            FileSystemHelpers.WriteStderr(this, ShellDiagnostic.Prefix(SessionState) + $"{name}: unbound variable");
            FileSystemHelpers.SetLastExitCode(this, 1);
            SessionState.PSVariable.Set("global:__BashUnbound", name);
            throw new UnboundVariableException();
        }
        // An empty or blank value is 0 (x=' '; $((x)) is 0 in bash).
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return 0;
        // A leading 0 is octal (x=010 is 8, x=08 is an error), so only a plain decimal is read directly.
        if (!HasLeadingZero(trimmed)
            && long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var direct))
            return direct;

        // Otherwise evaluate the value itself as an arithmetic expression. A value that is not one (x='a b',
        // x='a[<(echo)]=1') fails the whole expansion, as in bash ("syntax error in expression", status 1); it read as 0.
        // bash names the value's expression with its trailing blanks (x=' 5 / 0  ': `5 / 0  : division by 0`).
        return BashArith.Evaluate(raw!.TrimStart(),
            n => ReadVar(n, depth + 1),
            WriteVar,
            ReadRawVar);
    }

    private static bool HasLeadingZero(string s)
    {
        int i = s.Length > 0 && s[0] is '-' or '+' ? 1 : 0;
        return s.Length > i + 1 && s[i] == '0';
    }

    /// <summary>`name[subscript]` as the evaluator passes an array element.</summary>
    private static bool TrySplitElement(string name, out string baseName, out string subscript)
    {
        int open = name.IndexOf('[');
        if (open > 0 && name.EndsWith(']'))
        {
            baseName = name[..open];
            subscript = name[(open + 1)..^1];
            return true;
        }
        baseName = subscript = "";
        return false;
    }

    private BashArrayElement.Vars ElementVars => new(ReadVar, WriteVar, ReadRawVar);

    /// <summary>
    /// A subscript read from a VALUE (<c>x='a[$(cmd)]'; $(( x ))</c>) with a command substitution in it: bash expands it
    /// as a word first, so the command runs (<see cref="BashSubscriptExpansion"/>). A literal <c>$(( a[$(cmd)] ))</c> in
    /// the script never gets here with one; the emitter substitutes it before the expression reaches this cmdlet.
    /// </summary>
    private string ExpandSubscript(string subscript, out bool expanded)
        => BashArrayElement.ExpandSubscript(InvokeCommand, subscript, out expanded)
           ?? throw new BashArith.BashArithException($"{subscript}: bad substitution");

    private string? ReadElement(string baseName, string subscript)
    {
        subscript = ExpandSubscript(subscript, out bool expanded);
        object? holder = BashVariableLookup.ArrayHolder(SessionState, baseName);
        if (holder is System.Collections.IDictionary dict)
        {
            string key = BashArrayElement.AssocKey(subscript, expanded, ReadRawVar);
            return dict.Contains(key) ? dict[key]?.ToString() ?? "" : null;
        }
        try
        {
            if (holder is System.Collections.IList list)
            {
                long index = BashArrayElement.Index(subscript, list.Count, ElementVars);
                return index >= 0 && index < list.Count ? list[(int)index]?.ToString() ?? "" : null;
            }
            // A scalar behaves as a one-element array.
            string? scalar = BashVariableStore.Get(baseName);
            return scalar is not null && BashArrayElement.Index(subscript, 1, ElementVars) == 0 ? scalar : null;
        }
        catch (BashArith.BashArithException ex) { ex.InSubscript = true; throw; }
    }

    private void WriteElement(string baseName, string subscript, string value)
    {
        subscript = ExpandSubscript(subscript, out bool expanded);
        try { BashArrayElement.Write(SessionState, baseName, subscript, expanded, value, ElementVars); }
        catch (BashArith.BashArithException ex) { ex.InSubscript = true; throw; }
    }

    private string? ReadRawVar(string name)
    {
        // `local -n c=$1; (( c++ ))`: a nameref reads its target. REGRESSION: it read a variable named c.
        name = BashShellState.ResolveNameref(name);
        // BASH_LINENO is the runtime's call-line stack, not a stored variable (`$((BASH_LINENO))` is element 0).
        if (name == "BASH_LINENO")
        {
            var lines = VarFrameStack.BashLineNo(SessionState.PSVariable.GetValue("global:BASH_SOURCE"));
            return lines.Length > 0 ? lines[0] : null;
        }
        if (TrySplitElement(name, out var elemBase, out var elemSub)) return ReadElement(elemBase, elemSub);
        // The shell variable, or element 0 of the array the name holds (`a=(1 2 3); $((a + 5))` is 6). Never a scalar
        // PowerShell variable: that was a caller's loop variable seen through PowerShell's scope chain, a stale `read`
        // copy, or PowerShell's own `$input` (environment-model.md section 8).
        return BashVariableLookup.Scalar(SessionState, name);
    }

    /// <summary>
    /// Persist an assignment: the shell variable, or element 0 of the array the name holds (`a=(1 10); (( a++ ))` /
    /// `let m=7` on an array writes element 0 and leaves the others).
    /// </summary>
    private void WriteVar(string name, long value)
    {
        var s = value.ToString(CultureInfo.InvariantCulture);
        name = BashShellState.ResolveNameref(name);   // a nameref writes its target
        if (TrySplitElement(name, out var elemBase, out var elemSub)) { WriteElement(elemBase, elemSub, s); return; }
        if (BashVariableLookup.ArrayHolder(SessionState, name) is not null) { WriteElement(name, "0", s); return; }
        BashVariableStore.Set(name, s);
    }
}
