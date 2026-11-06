using System.Globalization;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Operand of a <c>[ a -eq b ]</c> numeric test: a plain decimal integer (optional sign, surrounding blanks;
/// no octal / hex / base#, no arithmetic). Anything else reports <c>[: X: integer expression expected</c>,
/// raises <c>$global:__BashTestBad</c> and yields 0 — the emitted statement form turns the flag into status 2,
/// while inside an <c>if</c>/<c>&amp;&amp;</c> the comparison is simply false/true on 0 like bash's error path.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTestInt")]
[OutputType(typeof(long))]
public sealed class InvokeBashTestIntCommand : PSCmdlet
{
    [Parameter(Position = 0, ValueFromPipeline = true)]
    [AllowNull]
    [AllowEmptyString]
    public object? Value { get; set; }

    /// <summary>The right-hand operand: it does not reset the flag, and stays quiet after a bad left operand.</summary>
    [Parameter]
    public SwitchParameter Second { get; set; }

    protected override void ProcessRecord()
    {
        string text = Value?.ToString() ?? "";
        if (!Second.IsPresent) SessionState.PSVariable.Set("global:__BashTestBad", false);
        if (long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            WriteObject(n);
            return;
        }
        // bash stops at the first bad operand: report one error per test.
        if (SessionState.PSVariable.GetValue("__BashTestBad") is not true)
            FileSystemHelpers.WriteBashError(this, $"[: {text}: integer expression expected");
        SessionState.PSVariable.Set("global:__BashTestBad", true);
        WriteObject(0L);
    }
}
