using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// The text of a command-substitution body (<c>$(cmd)</c>): turns a pipeline of records into the LINES of
/// the byte stream they stand for, honouring the record terminator contract
/// (<see cref="BashRuntime.IsUnterminated"/>): a normal record is a line whose terminator the serializer
/// appends, a <c>NoTrailingNewline</c> record (<c>printf</c>, <c>echo -n</c>, a streaming
/// <c>tr '\n' ,</c>) carries exact bytes and is GLUED to whatever follows it.
///
/// <para>Before this, capture was <c>ForEach-Object { Get-BashText $_ }</c> and the caller joined the
/// elements with a newline, so <c>$(printf a; printf b)</c> was <c>a\nb</c> (bash: <c>ab</c>) and a
/// <c>tr</c> that emitted several exact-bytes records could not stream — it had to buffer the whole
/// input into ONE record so the join would not corrupt it (which hung <c>tail -f f | tr '\n' ' '</c>).
/// With the glue here, the records of such a producer concatenate exactly, so producers may stream.</para>
///
/// <para>Output: one string per logical line (a terminated record, preceded by any exact records pending
/// before it); a pending tail with no terminator is flushed at the end. Callers join the strings with a
/// newline (quoted / assignment context) or splat them as an array (unquoted word-splitting) exactly as
/// they did with the old per-record text.</para>
/// </summary>
[Cmdlet(VerbsData.ConvertTo, "BashCapture")]
[OutputType(typeof(string))]
public sealed class ConvertToBashCaptureCommand : PSCmdlet
{
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// Quoted / assignment context: emit ONE string (lines joined by a newline, trailing newlines stripped, as bash
    /// does) instead of one string per line. The caller used to <c>@(…) -join "`n"</c> then <c>-replace '(\r?\n)+$'</c>,
    /// which held the line array, the joined text and the replaced copy at once (~15x the data at peak for
    /// <c>x=$(cat big)</c>); this keeps only the builder and the final string.
    /// </summary>
    [Parameter]
    public SwitchParameter Join { get; set; }

    private readonly StringBuilder _pending = new();
    private StringBuilder? _joined;
    private bool _hasPending;

    private void Emit(string line)
    {
        if (!Join) { WriteObject(line); return; }
        (_joined ??= new StringBuilder()).Append(line).Append('\n');
    }

    protected override void ProcessRecord()
    {
        if (InputObject is null) return;

        var text = BashRuntime.GetBashText(InputObject);
        _hasPending = true;
        if (BashRuntime.IsUnterminated(InputObject))
        {
            _pending.Append(text);
            return;
        }

        // An exact record that already ends with its own terminator (`printf 'x\n'`) is the line
        // WITHOUT it: the caller's newline join supplies the terminator once.
        if (InputObject.Properties["NoTrailingNewline"]?.Value is true && text.EndsWith('\n'))
            text = text[..^1];
        _pending.Append(text);

        Emit(_pending.ToString());
        _pending.Clear();
        _hasPending = false;
    }

    protected override void EndProcessing()
    {
        if (_hasPending) Emit(_pending.ToString());
        if (!Join) return;

        // Same as -replace '(\r?\n)+$','': drop every trailing newline (and the CR before each).
        var sb = _joined ?? new StringBuilder();
        while (sb.Length > 0 && sb[^1] == '\n')
        {
            sb.Length--;
            if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
        }
        WriteObject(sb.ToString());
    }
}
