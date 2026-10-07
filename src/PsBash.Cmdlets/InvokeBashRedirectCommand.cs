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
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRedirect")]
public sealed class InvokeBashRedirectCommand : PSCmdlet
{
    [Parameter(Position = 0)]
    public string? Path { get; set; }

    [Parameter]
    public SwitchParameter Append { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private FileStream? _stream;

    // STREAMING (memory bound): the target is opened up front and every record is written as it
    // arrives through a 64 KB-buffered FileStream, so `big | cmd > f` holds O(buffer), not the
    // whole output. Opening in BeginProcessing also gives bash's ordering: the redirect truncates
    // the target BEFORE the command runs (`cat f > f` leaves f empty, as in bash) and creates it
    // even when the command prints nothing.
    protected override void BeginProcessing()
    {
        if (Path is null) return;
        // A target built by expansion (`> $dir/f`, dir=/c/Users/...) reaches here unmapped — the
        // emitter can only rewrite a literal `/c/...` word — so map it like any cmdlet operand.
        string target = FileSystemHelpers.NormalizeOperandPath(Path);
        try
        {
            _stream = new FileStream(target, Append ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite, bufferSize: 64 * 1024);
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
            string message = $"bash: {Path}: {reason}";
            if (SessionState.PSVariable.GetValue("__BashErrexit") is true)
            {
                FileSystemHelpers.SetLastExitCode(this, 1);
                ThrowTerminatingError(new ErrorRecord(
                    new IOException(message), "BashRedirectError", ErrorCategory.WriteError, Path));
            }
            FileSystemHelpers.WriteBashError(this, message);
            FileSystemHelpers.SetLastExitCode(this, 1);
            _openFailed = true;
            UpstreamStop.Throw(this);
        }
    }

    private bool _openFailed;

    protected override void ProcessRecord()
    {
        if (InputObject is null || _stream is null) return;
        // Same per-record rule as tee: record boundary "\n" unless the record is marked
        // NoTrailingNewline (printf x / echo -n x), so `printf x > f` writes exactly "x".
        var payload = BashRuntime.RecordFilePayload(InputObject);
        if (payload.Length == 0) return;
        // The exact bytes: escaped-byte markers (invalid UTF-8, see RawBytes) are written back as the
        // single original byte, everything else as UTF-8 — `printf '\xe9' > f` is ONE byte.
        var bytes = RawBytes.GetBytes(payload);
        _stream.Write(bytes, 0, bytes.Length);
    }

    protected override void EndProcessing()
    {
        Close();
        // The upstream command still ran (and set its own status) after BeginProcessing; the
        // failed redirect is the pipeline's status, so `$?` and `set -e` see 1.
        if (_openFailed) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    protected override void StopProcessing() => Close();

    private void Close()
    {
        var s = _stream;
        _stream = null;
        try { s?.Dispose(); } catch { /* best-effort */ }
    }
}
