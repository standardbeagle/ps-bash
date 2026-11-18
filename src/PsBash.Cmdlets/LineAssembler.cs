using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Turns a pipeline's records back into LINES for a command that reads its input line by line (sed, awk, grep,
/// cut, sort, head …). A record that ends mid-line (<see cref="BashRuntime.IsUnterminated"/>: <c>printf a</c>,
/// <c>echo -n</c>, a file without a final newline) is not a line: bash's reader sees the bytes, so
/// <c>{ printf a; printf 'b\n'; } | sed s/x/y/</c> reads ONE line <c>ab</c>. Before this, each command took each
/// record as its own line(s): sed printed <c>a</c> and <c>b</c> on two lines, <c>grep -c .</c> counted pieces,
/// <c>sort</c> sorted fragments (spec <c>runtime-functions.md</c> §Pipeline record kinds, "Partial lines").
///
/// <para><b>Use:</b> the cmdlet's <c>ProcessRecord</c> feeds each <c>InputObject</c> and processes what comes
/// back (nothing while a line is still open); <c>EndProcessing</c> processes <see cref="Flush"/> first, the
/// stream's unterminated last line. A record that completes nothing pending passes through AS IS, so a filter
/// still emits the upstream object (<c>text-commands-output-strings</c>); only a joined line is a fresh text
/// record. At most one record comes back per record fed.</para>
///
/// <para><b>Terminator.</b> <c>'\n'</c> by default; a command reading NUL-terminated records (<c>-z</c>) sets
/// <c>'\0'</c>, and then a record is complete when its bytes end in NUL (<c>printf 'a\0'</c>, <c>find -print0</c>).
/// A command reading raw bytes (<c>cat</c>, <c>tr</c>, <c>od</c>, <c>wc</c>) does not use it: there no byte ends a
/// record. Guard: <c>LineAssemblerGuardTests</c> (every line consumer feeds one).</para>
/// </summary>
public sealed class LineAssembler
{
    private readonly StringBuilder _pending = new();
    private bool _hasPending;

    /// <summary>The byte that ends a record: <c>'\n'</c> (lines) or <c>'\0'</c> (<c>-z</c>).</summary>
    public char Terminator { get; set; } = '\n';

    /// <summary>
    /// True for a mode that counts BYTES (<c>head -c</c>, <c>tail -c</c>): every record is handed on as is. Holding a
    /// piece until its newline would never end on an endless stream without one (<c>yes | tr '\n' x | head -c 5</c>).
    /// </summary>
    public bool PassThrough { get; set; }

    /// <summary>True while an incomplete piece is held for the next record.</summary>
    public bool HasPending => _hasPending;

    /// <summary>
    /// Feed one pipeline record. Returns the record to process now: <paramref name="item"/> itself when nothing
    /// was pending and it ends its record; a fresh text record when it completes a held piece; <c>null</c> when
    /// the record is still open (the piece is held) or <paramref name="item"/> is null.
    /// </summary>
    public PSObject? Feed(PSObject? item)
    {
        if (item is null) return null;
        if (PassThrough) return item;
        string text = BashRuntime.GetBashText(item);
        // The record's bytes: its text, plus the boundary newline a terminated record renders with.
        bool boundary = !BashRuntime.IsUnterminated(item) && !text.EndsWith('\n');
        bool complete = Terminator == '\n'
            ? boundary || text.EndsWith('\n')
            : !boundary && text.EndsWith(Terminator);

        if (!_hasPending)
        {
            if (complete) return item;
            _pending.Append(text);
            if (boundary) _pending.Append('\n');
            _hasPending = true;
            return null;
        }

        _pending.Append(text);
        if (boundary) _pending.Append('\n');
        if (!complete) return null;

        string joined = _pending.ToString();
        _pending.Clear();
        _hasPending = false;
        if (Terminator == '\n')
            return AsPso(BashRuntime.TextRecord(joined[..^1], unterminated: false));
        return AsPso(BashRuntime.TextRecord(joined, unterminated: true));
    }

    /// <summary>
    /// End of input: the held piece as an UNTERMINATED record (the stream's last record had no terminator), or
    /// <c>null</c> when nothing is held. Call first in <c>EndProcessing</c>.
    /// </summary>
    public PSObject? Flush()
    {
        if (!_hasPending) return null;
        string text = _pending.ToString();
        _pending.Clear();
        _hasPending = false;
        return AsPso(BashRuntime.TextRecord(text, unterminated: true));
    }

    private static PSObject AsPso(object record) => record as PSObject ?? PSObject.AsPSObject(record);
}
