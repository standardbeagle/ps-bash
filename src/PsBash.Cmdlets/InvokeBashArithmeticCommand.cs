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
            long result = BashArith.Evaluate(expr, ReadVar, WriteVar);
            WriteObject(result);
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
    private long ReadVar(string name) => ReadVar(name, new HashSet<string>(StringComparer.Ordinal));

    private long ReadVar(string name, HashSet<string> visiting)
    {
        if (!visiting.Add(name)) return 0; // cycle: bash also stops at 0

        string? raw = null;
        // PowerShell variable first (loop variables are emitted as $name).
        try
        {
            var psv = GetVariableValue(name);
            if (psv is not null) raw = psv.ToString();
        }
        catch { /* not a readable PS variable in scope */ }

        // Environment variable (ordinary bash vars live in $env:NAME).
        if (string.IsNullOrEmpty(raw))
            raw = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrEmpty(raw)) return 0;

        var trimmed = raw.Trim();
        if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var direct))
            return direct;

        // Non-numeric: evaluate the value itself as an arithmetic expression.
        try
        {
            return BashArith.Evaluate(trimmed,
                n => ReadVar(n, visiting),
                WriteVar);
        }
        catch (BashArith.BashArithException)
        {
            return 0; // unparseable value reads as 0, matching bash's leniency
        }
    }

    /// <summary>
    /// Persist an assignment. Writes to the environment (ps-bash's store for
    /// ordinary variables) and, when a PowerShell variable of that name already
    /// exists in scope, updates it too so a loop variable stays consistent.
    /// </summary>
    private void WriteVar(string name, long value)
    {
        var s = value.ToString(CultureInfo.InvariantCulture);
        Environment.SetEnvironmentVariable(name, s);
        try
        {
            if (GetVariableValue(name) is not null)
                SessionState.PSVariable.Set(name, value);
        }
        catch { /* best effort — env write above is the canonical store */ }
    }
}
