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
/// writes a bash-style <c>ps-bash: …</c> diagnostic to stderr and sets exit
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
public sealed class InvokeBashArithmeticCommand : PSCmdlet
{
    [Parameter(Position = 0, ValueFromRemainingArguments = true)]
    public string[]? Tokens { get; set; }

    protected override void EndProcessing()
    {
        // The transpiler emits a single quoted argument, but accept multiple
        // tokens (joined with spaces) so an unquoted expression still works.
        var expr = Tokens is { Length: > 0 } ? string.Join(" ", Tokens) : "0";
        // An empty / blank expression is 0 in bash (`$(( ))`, `[[ "" -eq 0 ]]`).
        if (string.IsNullOrWhiteSpace(expr)) expr = "0";

        // Set directly, not through SetLastExitCode: that would clear the mark an EARLIER word of the same
        // command left (`echo $((1/0)) $((1+1))` is still abandoned).
        SessionState.PSVariable.Set(new PSVariable("global:LASTEXITCODE", 0));
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
            FileSystemHelpers.WriteBashError(this, $"{ex.Token}: expression recursion level exceeded (error token is \"{ex.Token}\")");
            if (IsWordExpansion()) FileSystemHelpers.MarkExpansionFailed(this);
        }
        catch (BashArith.BashArithException ex)
        {
            // A failed WORD abandons the command it belongs to (see PsBuild.AbandonIfExpansionFailed, echo).
            FileSystemHelpers.WriteBashError(this, $"{expr}: {ex.Message}");   // status 1
            // Marked LAST: reporting the error goes through SetLastExitCode, which ends any mark.
            if (IsWordExpansion()) FileSystemHelpers.MarkExpansionFailed(this);
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
            FileSystemHelpers.WriteStderr(this, $"ps-bash: {name}: unbound variable");
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
        return BashArith.Evaluate(trimmed,
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

    // An associative array's key: the subscript text with `$k` / `${k}` replaced by the variable's value.
    // A quoted key drops its quotes (`(( x = A['spam'] ))` reads key spam); a single-quoted one is not expanded.
    private string ExpandKey(string subscript)
    {
        string key = subscript.Trim();
        if (key.Length >= 2 && key[0] == '\'' && key[^1] == '\'' && key.IndexOf('\'', 1) == key.Length - 1) return key[1..^1];
        if (key.Length >= 2 && key[0] == '"' && key[^1] == '"' && key.IndexOf('"', 1) == key.Length - 1) key = key[1..^1];
        return System.Text.RegularExpressions.Regex.Replace(key, @"\$\{?(\w+)\}?", m => ReadRawVar(m.Groups[1].Value) ?? "");
    }

    // An indexed array's subscript is itself an arithmetic expression; negative counts from the end.
    private long ElementIndex(string subscript, int count)
    {
        long index = BashArith.Evaluate(subscript, n => ReadVar(n), WriteVar, ReadRawVar);
        return index < 0 ? index + count : index;
    }

    /// <summary>
    /// A subscript read from a VALUE (<c>x='a[$(cmd)]'; $(( x ))</c>) with a command substitution in it: bash expands it
    /// as a word first, so the command runs (<see cref="BashSubscriptExpansion"/>). A literal <c>$(( a[$(cmd)] ))</c> in
    /// the script never gets here with one; the emitter substitutes it before the expression reaches this cmdlet.
    /// </summary>
    private bool TryExpandSubscript(ref string subscript)
    {
        if (!BashSubscriptExpansion.NeedsWordExpansion(subscript)) return false;
        subscript = BashSubscriptExpansion.Expand(InvokeCommand, subscript)
            ?? throw new BashArith.BashArithException($"{subscript}: bad substitution");
        return true;
    }

    private string? ReadElement(string baseName, string subscript)
    {
        bool expanded = TryExpandSubscript(ref subscript);
        object? holder = SessionState.PSVariable.GetValue(baseName);
        if (holder is PSObject wrapped) holder = wrapped.BaseObject;
        if (holder is System.Collections.IDictionary dict)
        {
            string key = expanded ? subscript : ExpandKey(subscript);
            return dict.Contains(key) ? dict[key]?.ToString() ?? "" : null;
        }
        if (holder is System.Collections.IList list)
        {
            long index = ElementIndex(subscript, list.Count);
            return index >= 0 && index < list.Count ? list[(int)index]?.ToString() ?? "" : null;
        }
        // A scalar behaves as a one-element array.
        string? scalar = BashVariableStore.Get(baseName);
        return scalar is not null && ElementIndex(subscript, 1) == 0 ? scalar : null;
    }

    private void WriteElement(string baseName, string subscript, string value)
    {
        bool expanded = TryExpandSubscript(ref subscript);
        object? holder = SessionState.PSVariable.GetValue(baseName);
        if (holder is PSObject wrapped) holder = wrapped.BaseObject;
        if (holder is BashSparseArray sparse)
        {
            long at = ElementIndex(subscript, sparse.Count == 0 ? 0 : (int)sparse.NextIndex);
            if (at >= 0) sparse[at] = value;
            return;
        }
        if (holder is System.Collections.IDictionary dict)
        {
            dict[expanded ? subscript : ExpandKey(subscript)] = value;
            return;
        }
        var items = new List<object?>();
        if (holder is System.Collections.IEnumerable seq && holder is not string)
            foreach (var item in seq) items.Add(item);
        long index = ElementIndex(subscript, items.Count);
        if (index < 0) return;
        if (index > items.Count)
        {
            // A gap: a dense array can't hold it (bash keeps the holes unset), so it becomes a sparse one.
            var sp = BashSparseArray.FromList(items);
            sp[index] = value;
            if (SessionState.PSVariable.Get(baseName) is { } o) o.Value = sp; else SessionState.PSVariable.Set(baseName, sp);
            return;
        }
        while (items.Count <= index) items.Add("");
        items[(int)index] = value;
        if (SessionState.PSVariable.Get(baseName) is { } owner) owner.Value = items.ToArray();
        else SessionState.PSVariable.Set(baseName, items.ToArray());
    }

    private string? ReadRawVar(string name)
    {
        // BASH_LINENO is the runtime's call-line stack, not a stored variable (`$((BASH_LINENO))` is element 0).
        if (name == "BASH_LINENO")
        {
            var lines = VarFrameStack.BashLineNo(SessionState.PSVariable.GetValue("global:BASH_SOURCE"));
            return lines.Length > 0 ? lines[0] : null;
        }
        if (TrySplitElement(name, out var elemBase, out var elemSub)) return ReadElement(elemBase, elemSub);
        // PowerShell variable first (loop variables are emitted as $name).
        try
        {
            var variable = SessionState.PSVariable.Get(name);
            if (variable is not null)
            {
                // An array / associative array used as a plain name reads element 0 (`a=(1 2 3); $((a + 5))` is 6).
                object? held = variable.Value is PSObject w ? w.BaseObject : variable.Value;
                if (held is System.Collections.IDictionary dict)
                    return dict.Contains("0") ? dict["0"]?.ToString() ?? "" : "";
                if (held is System.Collections.IList list && held is not string)
                    return list.Count > 0 ? list[0]?.ToString() ?? "" : "";
                return held?.ToString() ?? string.Empty;
            }
        }
        catch { /* not a readable PS variable in scope */ }

        // Ordinary bash variable (ps-bash stores them as $env:NAME).
        return BashVariableStore.Get(name);
    }

    /// <summary>
    /// Persist an assignment. Writes to the bash-variable store and, when a
    /// PowerShell variable of that name already exists in scope, updates it too
    /// so a loop variable stays consistent.
    /// </summary>
    private void WriteVar(string name, long value)
    {
        var s = value.ToString(CultureInfo.InvariantCulture);
        if (TrySplitElement(name, out var elemBase, out var elemSub)) { WriteElement(elemBase, elemSub, s); return; }
        try
        {
            // `a=(1 10); (( a++ ))` / `let m=7` on an array or associative array writes element 0 (key "0"), leaving
            // the other elements; it replaced the whole array with the number.
            object? held = GetVariableValue(name);
            if (held is PSObject w) held = w.BaseObject;
            if (held is System.Collections.IDictionary || (held is System.Collections.IList && held is not string))
            {
                WriteElement(name, "0", s);
                return;
            }
        }
        catch { /* not a readable PS variable in scope */ }
        BashVariableStore.Set(name, s);
        try
        {
            if (GetVariableValue(name) is not null)
                SessionState.PSVariable.Set(name, value);
        }
        catch { /* best effort — env write above is the canonical store */ }
    }
}
