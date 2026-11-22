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
public sealed class ConvertToBashCaptureCommand : BashCmdlet
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
    private bool _warnedNul;

    private void Emit(string line)
    {
        if (!Join) { WriteObject(line); return; }
        (_joined ??= new StringBuilder()).Append(line).Append('\n');
    }

    // The substitution IS fd 1 of the command inside it (BashFdTable): `x=$(exec 3>&1; echo a >&3)` captures a, and
    // `x=$(exec >f; echo a)` writes a to f. What a table descriptor writes here arrives on another command's call, so it
    // is queued and taken in order with the records (DrainWritten).
    private BashFdTable.Registration? _reg;
    private readonly object _writtenGate = new();
    private StringBuilder? _written;

    protected override void BeginProcessing()
    {
        _reg = BashFdTable.Register(1, BashFdTable.ForCapture(text =>
        {
            lock (_writtenGate) (_written ??= new StringBuilder()).Append(text);
        }));
    }

    private void DrainWritten()
    {
        string? text = null;
        lock (_writtenGate)
        {
            if (_written is { Length: > 0 }) { text = _written.ToString(); _written.Clear(); }
        }
        if (text is not null) Add(text);
    }

    private void Unregister()
    {
        if (_reg is not { } reg) return;
        _reg = null;
        BashFdTable.Unregister(reg);
    }

    protected override void StopProcessing() => Unregister();

    protected override void DisposeResources() => Unregister();

    protected override void ProcessRecord()
    {
        if (InputObject is null) return;
        DrainWritten();

        // The record's bytes (the one rule, BashRuntime.RecordBytes): a plain record is its text plus a newline even
        // when that text already ends in one (`$(jq -r . <<<'"a\n"'; echo .)` is "a\n\n.").
        var text = BashRuntime.RecordBytes(InputObject);
        // The command moved its fd 1 away from this substitution (`exec >f`, `exec >&2`): the record goes there.
        if (_reg is { } reg && reg.Current is var current && !ReferenceEquals(current, reg.Handle))
        {
            int std;
            try { std = current is null ? 1 : BashFdTable.WriteHandle(current, text); }
            catch (InvalidOperationException) { return; }   // a closed descriptor: the write fails
            if (std == 2) { FileSystemHelpers.WriteStderrBytes(this, text); return; }
            // The shell's original stdout; with no host attached there is nowhere else for it.
            if (std == 0 || PsBash.Core.Stdio.ShellStdio.WriteTerminal(1, text)) return;
        }
        Add(text);
    }

    private void Add(string text)
    {
        // bash drops every NUL byte of a substitution's output, with one warning: `$(printf '.\000.')` is `..`. Kept, a
        // NUL ended the value at the first one (an environment variable is a C string).
        if (text.Contains('\0'))
        {
            text = text.Replace("\0", "");
            if (!_warnedNul)
            {
                _warnedNul = true;
                FileSystemHelpers.WriteStderr(this, ShellDiagnostic.Prefix(SessionState) + "warning: command substitution: ignored null byte in input");
            }
        }
        _hasPending = true;
        if (!text.EndsWith('\n'))
        {
            _pending.Append(text);
            return;
        }

        // Bytes ending in a newline complete a line: the line WITHOUT that newline (the caller's newline join
        // supplies it once).
        _pending.Append(text, 0, text.Length - 1);

        Emit(_pending.ToString());
        _pending.Clear();
        _hasPending = false;
    }

    protected override void EndProcessing()
    {
        DrainWritten();
        Unregister();
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
