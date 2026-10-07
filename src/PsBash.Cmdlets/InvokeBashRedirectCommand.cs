using PsBash.Core;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRedirect</c> — the
/// runtime target the emitter pipes stdout into for <c>&gt; file</c> / <c>&gt;&gt; file</c>
/// redirects (<c>... | Invoke-BashRedirect -Path file [-Append]</c>). It collects
/// the pipeline's BashText, joins the lines with <c>\n</c> (one trailing newline
/// when non-empty), and writes or appends to the file. A <c>$null</c> path
/// (e.g. <c>&gt; /dev/null</c>) is a no-op, matching the psm1.
///
/// <c>-Path</c> / <c>-Append</c> are emitter-generated PowerShell-style flags, not
/// bash flags, so they bind directly with no collision. The file path resolves
/// against the process working directory, which the emitted <c>cd</c> keeps in
/// sync with the shell cwd — byte-identical to the psm1's File.WriteAllText.
/// <para>
/// STDERR (<c>2&gt; f</c>, <c>&amp;&gt; f</c>): the emitter merges the command's error stream into
/// its output (<c>2&gt;&amp;1</c>) and pipes both here, so stderr is written by the same
/// byte-faithful writer as stdout — LF, no <c>Invoke-BashLs:</c> prefix (PowerShell's native
/// <c>2&gt; f</c> formats each record with the cmdlet name and CRLF). An <see cref="ErrorRecord"/>
/// goes to <see cref="ErrorPath"/> when bound, is passed on as output with
/// <see cref="PassErrors"/> (<c>2&gt;&amp;1 &gt;f</c>: stderr to the ORIGINAL stdout), and otherwise
/// is written with the stdout records (<c>&amp;&gt; f</c>, <c>&gt;f 2&gt;&amp;1</c>). With no
/// <see cref="Path"/> the stdout records pass through unchanged; <c>-Path $null</c> discards them.
/// </para>
/// <para>
/// SUPERSEDED TARGETS (<c>echo x &gt;a &gt;b</c>): bash opens every redirect in order and the last
/// per fd wins, so <c>a</c> is still created/truncated. <see cref="Truncate"/> / <see cref="Touch"/>
/// open those (truncate, or create-without-truncating for <c>&gt;&gt;</c>) and close them at once.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRedirect")]
public sealed class InvokeBashRedirectCommand : PSCmdlet
{
    [Parameter(Position = 0)]
    public string? Path { get; set; }

    [Parameter]
    public SwitchParameter Append { get; set; }

    /// <summary>The stderr target (<c>2&gt; f</c> / <c>2&gt;&gt; f</c>); <c>$null</c> discards.</summary>
    [Parameter]
    public string? ErrorPath { get; set; }

    [Parameter]
    public SwitchParameter ErrorAppend { get; set; }

    /// <summary>Error records go on as output (stderr dup'd to the original stdout).</summary>
    [Parameter]
    public SwitchParameter PassErrors { get; set; }

    /// <summary>
    /// A bare <c>2&gt;&amp;1</c>: error records go on as ordinary stdout TEXT records. PowerShell's
    /// merge leaves them ErrorRecord objects, and any later stage (an enclosing command's
    /// <c>2&gt; f</c>, a capture) reads an ErrorRecord as stderr — after <c>2&gt;&amp;1</c> it is stdout.
    /// </summary>
    [Parameter]
    public SwitchParameter MergeErrors { get; set; }

    /// <summary>Superseded <c>&gt; f</c> targets: opened (truncated) and closed before the command runs.</summary>
    [Parameter]
    public string[]? Truncate { get; set; }

    /// <summary>Superseded <c>&gt;&gt; f</c> targets: created if missing, never truncated.</summary>
    [Parameter]
    public string[]? Touch { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private FileStream? _stream;
    private FileStream? _errorStream;
    private bool _stdoutToFile;
    private bool _stderrToFile;

    // STREAMING (memory bound): the target is opened up front and every record is written as it
    // arrives through a 64 KB-buffered FileStream, so `big | cmd > f` holds O(buffer), not the
    // whole output. Opening in BeginProcessing also gives bash's ordering: the redirect truncates
    // the target BEFORE the command runs (`cat f > f` leaves f empty, as in bash) and creates it
    // even when the command prints nothing.
    protected override void BeginProcessing()
    {
        _stdoutToFile = MyInvocation.BoundParameters.ContainsKey(nameof(Path));
        _stderrToFile = MyInvocation.BoundParameters.ContainsKey(nameof(ErrorPath));

        foreach (var t in Truncate ?? [])
            if (!TryOpen(t, append: false, out var s)) return; else s?.Dispose();
        foreach (var t in Touch ?? [])
            if (!TryOpen(t, append: true, out var s)) return; else s?.Dispose();
        if (Path is not null && !TryOpen(Path, Append, out _stream)) return;
        if (ErrorPath is not null && !TryOpen(ErrorPath, ErrorAppend, out _errorStream)) return;
    }

    /// <summary>
    /// A redirect target as the OS path to open, resolved like any cmdlet file operand: a target
    /// built by expansion (<c>&gt; $dir/f</c>, dir=/c/Users/...) reaches here unmapped — the emitter
    /// can only rewrite a literal word — so <see cref="FileSystemHelpers.NormalizeOperandPath"/>
    /// maps it. Null for the null device (<c>f=/dev/null; cmd &gt; $f</c>), which operands also
    /// read as empty. NOT <c>/tmp</c>: an expanded <c>/tmp/x</c> operand is not mapped either, and
    /// <c>echo &gt; "$f"; cat "$f"</c> must agree on the file.
    /// </summary>
    internal static string? ResolveTarget(string raw)
        => FileSystemHelpers.IsNullDevice(raw) ? null : FileSystemHelpers.NormalizeOperandPath(raw);

    /// <summary>
    /// Open one target; false (after reporting and stopping upstream) when it cannot be opened.
    /// <paramref name="stream"/> is null for the null device (writes are discarded).
    /// </summary>
    private bool TryOpen(string raw, bool append, out FileStream? stream)
    {
        stream = null;
        string? target = ResolveTarget(raw);
        if (target is null) return true;
        try
        {
            stream = new FileStream(target, append ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite, bufferSize: 64 * 1024);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // bash: a redirection that cannot be opened fails the command (status 1) and the
            // script goes on: `bash: nodir/f: No such file or directory`, `bash: d: Is a directory`.
            // The command must never run (`rm -rf build > /nodir/log` must not delete), but NOT via
            // a terminating error: inside an emitted `try { }` (stdin scope, env prefix, …) one
            // aborts every later statement in the block. UpstreamStop ends the upstream stages the
            // way `Select-Object -First` does — not an error, so the statement just ends with
            // status 1. Under `set -e` stopping the script IS the bash behavior, so terminate then.
            string reason = Directory.Exists(target) ? "Is a directory"
                : ex is UnauthorizedAccessException ? "Permission denied"
                : FileSystemHelpers.ReadErrorMessage(ex);
            string message = $"bash: {raw}: {reason}";
            if (SessionState.PSVariable.GetValue("__BashErrexit") is true)
            {
                FileSystemHelpers.SetLastExitCode(this, 1);
                ThrowTerminatingError(new ErrorRecord(
                    new IOException(message), "BashRedirectError", ErrorCategory.WriteError, raw));
            }
            FileSystemHelpers.WriteBashError(this, message);
            FileSystemHelpers.SetLastExitCode(this, 1);
            _openFailed = true;
            Close();
            UpstreamStop.Throw(this);
            return false; // UpstreamStop unavailable: the cmdlet then just drops its input
        }
    }

    private bool _openFailed;

    protected override void ProcessRecord()
    {
        if (InputObject is null || _openFailed) return;
        if (InputObject.BaseObject is ErrorRecord er)
        {
            if (_stderrToFile) { Write(_errorStream, ErrorPayload(er)); return; }
            // Now stdout data (`2>&1 >f`, a bare `2>&1`): a text record, no longer an ErrorRecord.
            if (PassErrors || MergeErrors)
            {
                foreach (var line in BashRuntime.EmitBashLines(ErrorPayload(er))) WriteObject(line);
                return;
            }
            // Merged into stdout (`&> f`, `>f 2>&1`): written with the stdout records below.
            if (_stdoutToFile) { Write(_stream, ErrorPayload(er)); return; }
            WriteObject(InputObject);
            return;
        }
        if (!_stdoutToFile) { WriteObject(InputObject); return; }
        // Same per-record rule as tee: record boundary "\n" unless the record is marked
        // NoTrailingNewline (printf x / echo -n x), so `printf x > f` writes exactly "x".
        Write(_stream, BashRuntime.RecordFilePayload(InputObject));
    }

    /// <summary>One stderr record as bash writes it: the message only, LF-terminated unless the writer
    /// marked it (<c>printf x &gt;&amp;2 2&gt; f</c> writes exactly <c>x</c>) — <see cref="PsBash.Core.StderrRecord"/>.</summary>
    private static string ErrorPayload(ErrorRecord er)
    {
        string text = er.ErrorDetails?.Message ?? er.Exception?.Message ?? er.ToString();
        return PsBash.Core.StderrRecord.Payload(text, er.TargetObject);
    }

    private static void Write(FileStream? stream, string payload)
    {
        if (stream is null || payload.Length == 0) return;
        // The exact bytes: escaped-byte markers (invalid UTF-8, see RawBytes) are written back as the
        // single original byte, everything else as UTF-8 — `printf '\xe9' > f` is ONE byte.
        var bytes = RawBytes.GetBytes(payload);
        stream.Write(bytes, 0, bytes.Length);
    }

    protected override void EndProcessing()
    {
        Close();
        // A failed open stopped upstream, but a stage that already finished may have reset the
        // status; the failed redirect is the pipeline's status, so `$?` and `set -e` see 1.
        if (_openFailed) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    protected override void StopProcessing() => Close();

    private void Close()
    {
        foreach (var s in new[] { _stream, _errorStream })
            try { s?.Dispose(); } catch { /* best-effort */ }
        _stream = null;
        _errorStream = null;
    }
}
