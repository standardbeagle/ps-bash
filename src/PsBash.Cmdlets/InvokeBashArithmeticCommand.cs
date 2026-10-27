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

        FileSystemHelpers.SetLastExitCode(this, 0);
        try
        {
            long result = BashArith.Evaluate(expr, ReadVar, WriteVar, ReadRawVar);
            WriteObject(result);
        }
        catch (UnboundVariableException)
        {
            // already reported; no value
        }
        catch (BashArith.BashArithException ex)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
            FileSystemHelpers.WriteBashError(this, $"{expr}: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolve a bare identifier to a long. Order: live PowerShell variable
    /// (loop vars / builtins), then environment variable, then 0. A non-numeric
    /// value is recursively evaluated as an arithmetic expression (bash
    /// semantics), guarded against reference cycles.
    /// </summary>
    private sealed class UnboundVariableException : Exception { }

    private long ReadVar(string name) => ReadVar(name, new HashSet<string>(StringComparer.Ordinal));

    private long ReadVar(string name, HashSet<string> visiting)
    {
        if (!visiting.Add(name)) return 0; // cycle: bash also stops at 0

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
        if (string.IsNullOrEmpty(raw)) return 0;

        var trimmed = raw.Trim();
        if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var direct))
            return direct;

        // Non-numeric: evaluate the value itself as an arithmetic expression.
        try
        {
            return BashArith.Evaluate(trimmed,
                n => ReadVar(n, visiting),
                WriteVar,
                ReadRawVar);
        }
        catch (BashArith.BashArithException)
        {
            return 0; // unparseable value reads as 0, matching bash's leniency
        }
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
    private string ExpandKey(string subscript) =>
        System.Text.RegularExpressions.Regex.Replace(subscript.Trim(), @"\$\{?(\w+)\}?", m => ReadRawVar(m.Groups[1].Value) ?? "");

    // An indexed array's subscript is itself an arithmetic expression; negative counts from the end.
    private long ElementIndex(string subscript, int count)
    {
        long index = BashArith.Evaluate(subscript, n => ReadVar(n), WriteVar, ReadRawVar);
        return index < 0 ? index + count : index;
    }

    private string? ReadElement(string baseName, string subscript)
    {
        object? holder = SessionState.PSVariable.GetValue(baseName);
        if (holder is PSObject wrapped) holder = wrapped.BaseObject;
        if (holder is System.Collections.IDictionary dict)
        {
            string key = ExpandKey(subscript);
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
        object? holder = SessionState.PSVariable.GetValue(baseName);
        if (holder is PSObject wrapped) holder = wrapped.BaseObject;
        if (holder is System.Collections.IDictionary dict)
        {
            dict[ExpandKey(subscript)] = value;
            return;
        }
        var items = new List<object?>();
        if (holder is System.Collections.IEnumerable seq && holder is not string)
            foreach (var item in seq) items.Add(item);
        long index = ElementIndex(subscript, items.Count);
        if (index < 0) return;
        while (items.Count <= index) items.Add("");
        items[(int)index] = value;
        if (SessionState.PSVariable.Get(baseName) is { } owner) owner.Value = items.ToArray();
        else SessionState.PSVariable.Set(baseName, items.ToArray());
    }

    private string? ReadRawVar(string name)
    {
        if (TrySplitElement(name, out var elemBase, out var elemSub)) return ReadElement(elemBase, elemSub);
        // PowerShell variable first (loop variables are emitted as $name).
        try
        {
            var variable = SessionState.PSVariable.Get(name);
            if (variable is not null)
                return variable.Value?.ToString() ?? string.Empty;
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
        BashVariableStore.Set(name, s);
        try
        {
            if (GetVariableValue(name) is not null)
                SessionState.PSVariable.Set(name, value);
        }
        catch { /* best effort — env write above is the canonical store */ }
    }
}
