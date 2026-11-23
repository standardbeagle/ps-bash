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
public sealed class InvokeBashRedirectCommand : BashCmdlet, IStatusTransparentStage
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

    /// <summary><c>&gt;|</c>: truncate even under <c>set -C</c> (noclobber).</summary>
    [Parameter]
    public SwitchParameter Force { get; set; }

    /// <summary><c>1&lt;&gt; f</c>: -Path is opened for reading and writing - created if missing, never truncated, written
    /// from its start (bash turns <c>abcdef</c> into <c>XY\ndef</c> with <c>echo XY 1&lt;&gt; f</c>).</summary>
    [Parameter]
    public SwitchParameter ReadWrite { get; set; }

    /// <summary><c>2&lt;&gt; f</c>: -ErrorPath opened as <see cref="ReadWrite"/> opens -Path (created, never truncated).</summary>
    [Parameter]
    public SwitchParameter ErrorReadWrite { get; set; }

    /// <summary><c>2&gt;|</c>: the stderr target's noclobber override.</summary>
    [Parameter]
    public SwitchParameter ErrorForce { get; set; }

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

    /// <summary>
    /// The order bash opens the targets in and where each open error is reported (<see cref="RedirectOpenPlan"/>);
    /// absent, the stage opens -Truncate, -Touch, -Path, -ErrorPath and reports on stderr.
    /// </summary>
    [Parameter]
    public string? OpenPlan { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private RedirectFileWriter? _stream;
    private RedirectFileWriter? _errorStream;
    private bool _stdoutToFile;
    private bool _stderrToFile;
    // The device a target names (DevicePath: the one list). Stdout / Stderr are this command's own streams on every
    // platform - on Windows they were opened as files under C:\dev ("No such file", or where that directory existed,
    // output silently written into a file), and on Linux opening them reaches the HOST process's descriptors, not the
    // command's stdout/stderr that the pipeline captures. /dev/full fails every write; any other device is opened by
    // RedirectFileWriter.OpenFile, never as a file.
    // Held in a private class, not as fields of this exported type: a value-type field from
    // PsBash.Transpiler would load that assembly when PowerShell lists the module's types at import
    // (guard: ExportedTypeLoadGuardTests).
    private sealed class DeviceTargets { internal DeviceKind Out; internal DeviceKind Err; }
    private readonly DeviceTargets _devices = new();
    private DeviceKind _outDevice { get => _devices.Out; set => _devices.Out = value; }
    private DeviceKind _errDevice { get => _devices.Err; set => _devices.Err = value; }
    private bool _fullReported;

    /// <summary>
    /// True when this stage keeps the error records it receives through PowerShell's <c>2&gt;&amp;1</c> in the same
    /// stream as the output records — a bash merge (<c>2&gt;&amp;1</c>, <c>&amp;&gt;f</c>, <c>&gt;f 2&gt;&amp;1</c>, <c>2&gt;/dev/stdout</c>) —
    /// and false when it splits them (<c>2&gt;f</c> → <c>-ErrorPath</c>; <c>2&gt;&amp;1 &gt;f</c> → <c>-Path f -PassErrors</c>).
    /// <see cref="StderrMerge.JoinsStdout"/> asks the stage downstream of a spawning cmdlet. Valid once parameters are
    /// bound, which PowerShell does for the whole pipeline before any command begins.
    /// </summary>
    internal bool KeepsStderrWithStdout()
    {
        if (MergeErrors) return true;
        if (MyInvocation.BoundParameters.ContainsKey(nameof(ErrorPath))) return DevicePath.Classify(ErrorPath) == DeviceKind.Stdout;
        if (PassErrors) return !MyInvocation.BoundParameters.ContainsKey(nameof(Path));
        return true;
    }

    // STREAMING (memory bound): the target is opened up front and every record is written as it
    // arrives through a 64 KB-buffered FileStream, so `big | cmd > f` holds O(buffer), not the
    // whole output. The buffer never holds output back for long: RedirectFileWriter writes a slow
    // producer's records at once and a burst's tail on an idle timer, and flushes before any ps-bash
    // file read (`{ echo a; cat f; } > f` sees `a`, as in bash). Opening in BeginProcessing also
    // gives bash's ordering: the redirect truncates the target BEFORE the command runs (`cat f > f` leaves f empty, as in bash) and creates it
    // even when the command prints nothing.
    protected override void BeginProcessing()
    {
        OpenTargets();
        if (!_openFailed) RegisterTargets();
    }

    // The shell's descriptor table (BashFdTable): while the command runs, its fd 1 / fd 2 IS this stage's target, so an
    // `exec 3>&1` inside copies it (`{ exec 3>&1; } >f; echo x >&3` writes x to f) and an `exec >g` inside moves what
    // this stage would receive (ProcessRecord). Only a file or the null device: a stream device is not a new target.
    private BashFdTable.Registration? _outReg, _errReg;

    private void RegisterTargets()
    {
        if (_stdoutToFile && _outDevice is DeviceKind.None or DeviceKind.Null)
        {
            _outReg = BashFdTable.Register(1, BashFdTable.ForStage(_stream));
        }
        if (_stderrToFile && _errDevice is DeviceKind.None or DeviceKind.Null)
        {
            _errReg = BashFdTable.Register(2, BashFdTable.ForStage(_errorStream));
        }
    }

    /// <summary>
    /// A record for fd <paramref name="fd"/> after the command moved that descriptor (<c>{ exec &gt;g; echo x; } &gt;f</c>
    /// writes x to g): written where the table says. True when it was; false when the descriptor is still this stage's.
    /// </summary>
    private bool TryDivert(int fd, string payload)
    {
        if ((fd == 1 ? _outReg : _errReg) is not { } reg) return false;
        var current = reg.Current;
        if (ReferenceEquals(current, reg.Handle)) return false;
        int std = fd;
        try
        {
            if (current is not null) std = BashFdTable.WriteHandle(current, payload);
        }
        catch (InvalidOperationException) { return true; }   // a closed descriptor: the write fails
        if (std == 1) foreach (var line in BashRuntime.EmitBashLines(payload)) WriteObject(line);
        else if (std == 2) FileSystemHelpers.WriteStderrBytes(this, payload);
        return true;
    }

    private void OpenTargets()
    {
        _outDevice = DevicePath.Classify(Path);
        _errDevice = DevicePath.Classify(ErrorPath);
        // A stream device is not a file: `> /dev/stdout` leaves the records where they were going, `> /dev/stderr`
        // sends them to stderr (ProcessRecord). /dev/full counts as a file target whose every write fails.
        _stdoutToFile = MyInvocation.BoundParameters.ContainsKey(nameof(Path))
                        && _outDevice is not (DeviceKind.Stdout or DeviceKind.Stderr);
        _stderrToFile = MyInvocation.BoundParameters.ContainsKey(nameof(ErrorPath))
                        && _errDevice is not (DeviceKind.Stdout or DeviceKind.Stderr);

        // `echo hi > file$((1/0))`: the target word failed to expand, so bash abandons the command (status 1)
        // without opening anything. Same path as a target that cannot be opened.
        if (FileSystemHelpers.TakeExpansionFailed(this))
        {
            MarkOpenFailed();
            UpstreamStop.Throw(this);
            return;
        }

        if (RedirectOpenPlan.Parse(OpenPlan, Truncate?.Length ?? 0, Touch?.Length ?? 0, Path is not null, ErrorPath is not null)
            is { } plan)
        {
            OpenInPlanOrder(plan);
            return;
        }
        foreach (var t in Truncate ?? [])
            if (!TryOpen(t, append: false, force: false, out var s)) return; else s?.Dispose();
        foreach (var t in Touch ?? [])
            if (!TryOpen(t, append: true, force: false, out var s)) return; else s?.Dispose();
        if (Path is not null)
        {
            if (!TryOpen(Path, Append, Force, out var s, readWrite: ReadWrite)) return;
            _stream = s;
        }
        if (ErrorPath is not null)
        {
            if (!TryOpen(ErrorPath, ErrorAppend, ErrorForce, out var s, readWrite: ErrorReadWrite)) return;
            _errorStream = s;
        }
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
    /// Open the targets in <paramref name="plan"/>'s order (bash's), each open error reported where fd 2 pointed when
    /// that redirect was reached. A failure leaves the later targets unopened (bash never reaches them). The targets
    /// opened so far stay open until then, so a report can go into one (<c>2&gt;e &gt;nodir/f</c>).
    /// </summary>
    private void OpenInPlanOrder(IReadOnlyList<RedirectOpenPlan.Step> plan)
    {
        var held = new Dictionary<string, (string Raw, RedirectFileWriter? Stream)>(StringComparer.Ordinal);
        try
        {
            foreach (var step in plan)
            {
                var (raw, append, force) = RoleTarget(step.Role);
                if (!TryOpen(raw, append, force, out var s, message => ReportTo(step.Report, message, held), readWrite: step.Role switch { "p" => ReadWrite, "e" => ErrorReadWrite, _ => false })) return;
                held[step.Role] = (raw, s);
            }
            if (held.Remove("p", out var p)) _stream = p.Stream;
            if (held.Remove("e", out var e)) _errorStream = e.Stream;
        }
        finally
        {
            // Superseded targets (and everything, after a failure) are closed once the opens are done.
            foreach (var (_, s) in held.Values)
                try { s?.Dispose(); } catch { /* best-effort */ }
        }
    }

    private (string Raw, bool Append, bool Force) RoleTarget(string role) => role switch
    {
        "p" => (Path!, Append, Force),
        "e" => (ErrorPath!, ErrorAppend, ErrorForce),
        _ when role[0] == 't' => (Truncate![int.Parse(role.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)], false, false),
        _ => (Touch![int.Parse(role.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)], true, false),
    };

    /// <summary>
    /// An open error, reported where fd 2 pointed: stderr (<paramref name="report"/> null), nowhere (<c>2&gt;/dev/null</c>
    /// first), the original stdout (<c>2&gt;&amp;1</c> first), or a target opened earlier (<c>2&gt;e</c> first). Through a
    /// target that names a device, the device's stream.
    /// </summary>
    private void ReportTo(string? report, string message, Dictionary<string, (string Raw, RedirectFileWriter? Stream)> held)
    {
        if (report is null) { FileSystemHelpers.WriteBashError(this, message); return; }
        if (report == RedirectOpenPlan.Discard) return;
        DeviceKind device = DeviceKind.Stdout;
        RedirectFileWriter? stream = null;
        if (report != RedirectOpenPlan.Stdout && held.TryGetValue(report, out var target))
        {
            device = DevicePath.Classify(target.Raw);
            stream = target.Stream;
        }
        if (device == DeviceKind.Stderr) FileSystemHelpers.WriteStderr(this, message);
        else if (device == DeviceKind.Stdout)
            foreach (var line in BashRuntime.EmitBashLines(message + "\n")) WriteObject(line);
        else Write(stream, message + "\n");
    }

    /// <summary>
    /// Open one target; false (after reporting and stopping upstream) when it cannot be opened.
    /// <paramref name="stream"/> is null for the null device (writes are discarded). The report is bash's shell
    /// diagnostic (<c>s.sh: line 3: nodir/f: No such file or directory</c>, <see cref="ShellDiagnostic"/>), written on
    /// stderr unless <paramref name="report"/> sends it elsewhere.
    /// </summary>
    private bool TryOpen(string raw, bool append, bool force, out RedirectFileWriter? stream, Action<string>? report = null, bool readWrite = false)
    {
        stream = null;
        // `> $x` that expanded to several words or none: bash's "$x: ambiguous redirect", a failed open like any other.
        if (RedirectTarget.IsAmbiguous(raw, out string shown)) return FailOpen(shown, "ambiguous redirect", report);
        // `> ''`: no file is named (bash: ": No such file or directory"; .NET's own text named its parameter).
        if (raw.Length == 0) return FailOpen(raw, "No such file or directory", report);
        // Opening a stream device, /dev/null or /dev/full always succeeds and creates nothing (null stream: written
        // elsewhere, or discarded).
        var device = DevicePath.Classify(raw);
        if (device is DeviceKind.Stdout or DeviceKind.Stderr or DeviceKind.Full or DeviceKind.Null) return true;
        string? target = ResolveTarget(raw);
        if (target is null) return true;
        try
        {
            // Any other device (`> /dev/fd/3`, `> /dev/zero`, `> /dev/tty`): the shared writer resolves it, never a file.
            if (device != DeviceKind.None)
            {
                stream = RedirectFileWriter.OpenFile(raw, append, this);
                return true;
            }
            // `set -C`: a truncating `>` never overwrites an existing regular file (`>|` and `>>` do).
            if (!append && !force && !readWrite && File.Exists(target)
                && SessionState.PSVariable.GetValue("__BashNoclobber") is true)
                throw new IOException("cannot overwrite existing file");
            FileSystemHelpers.ThrowIfNotWritableName(target);   // `> twenty/`: "Is a directory"
            stream = new RedirectFileWriter(new FileStream(target, append ? FileMode.Append : readWrite ? FileMode.OpenOrCreate : FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite, bufferSize: 64 * 1024));
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
            // status 1. `set -e` is the emitted statement check's (FailOpen).
            string reason = Directory.Exists(target) ? "Is a directory"
                : ex is UnauthorizedAccessException ? "Permission denied"
                : FileSystemHelpers.ReadErrorMessage(ex);
            return FailOpen(raw, reason, report);
        }
    }

    /// <summary>
    /// A redirect that cannot be opened: bash's <c>s.sh: line 3: SHOWN: REASON</c>, status 1, and the command does not
    /// run. Always false (the open failed). Only a STATUS: whether <c>set -e</c> ends the shell is the one emitted
    /// errexit check's to decide, which knows what this cmdlet cannot - that the command is an <c>||</c> / <c>if</c>
    /// operand (<c>__BashErrexitSuppress</c>), or runs in a subshell whose own status is what the parent checks.
    /// REGRESSION: a terminating error here ended the whole script from <c>echo hi &gt; /nodir/x || echo caught</c> and
    /// from <c>( set -e; { echo x; } &gt; /nodir/f )</c>, and its text escaped the subshell's <c>2&gt;/dev/null</c>.
    /// </summary>
    private bool FailOpen(string shown, string reason, Action<string>? report)
    {
        string message = ShellDiagnostic.Prefix(SessionState) + $"{shown}: {reason}";
        if (report is null) FileSystemHelpers.WriteBashError(this, message);
        else report(message);
        MarkOpenFailed();
        Close();
        UpstreamStop.Throw(this);
        return false; // UpstreamStop unavailable: the cmdlet then just drops its input
    }

    private bool _openFailed;

    /// <summary>Name of the global a failed open sets. A compound command's status is its body's, so the emitter
    /// cannot tell a failed redirect from it; after a redirected compound it runs the ERR trap / errexit on this
    /// (<c>{ ...; } &gt; /nodir/f</c> fires the trap once, a failing body inside a good redirect does not).</summary>
    public const string RedirectFailedVariable = "__BashRedirectFailed";

    private void MarkOpenFailed()
    {
        FileSystemHelpers.SetLastExitCode(this, 1);
        _openFailed = true;
        SessionState.PSVariable.Set(new PSVariable("global:" + RedirectFailedVariable, true));
    }

    protected override void ProcessRecord()
    {
        if (InputObject is null || _openFailed) return;
        if (InputObject.BaseObject is ErrorRecord er)
        {
            if (_errReg is not null && TryDivert(2, ErrorPayload(er))) return;
            if (_stderrToFile) { WriteTarget(_errorStream, _errDevice, ErrorPayload(er)); return; }
            // `2> /dev/stderr`: stays stderr (as stderr, not an ErrorRecord object a downstream stage would read as a
            // line). `2> /dev/stdout`: stdout, i.e. like `2>&1` at that point.
            if (_errDevice == DeviceKind.Stderr) { FileSystemHelpers.WriteStderrBytes(this, ErrorPayload(er)); return; }
            if (_errDevice == DeviceKind.Stdout)
            {
                if (_stdoutToFile) { WriteTarget(_stream, _outDevice, ErrorPayload(er)); return; }
                if (_outDevice == DeviceKind.Stderr) { FileSystemHelpers.WriteStderrBytes(this, ErrorPayload(er)); return; }
                foreach (var line in BashRuntime.EmitBashLines(ErrorPayload(er))) WriteObject(line);
                return;
            }
            // Now stdout data (`2>&1 >f`, a bare `2>&1`): a text record, no longer an ErrorRecord.
            if (PassErrors || MergeErrors)
            {
                foreach (var line in BashRuntime.EmitBashLines(ErrorPayload(er))) WriteObject(line);
                return;
            }
            // Merged into stdout (`&> f`, `>f 2>&1`): written with the stdout records below.
            if (_stdoutToFile) { WriteTarget(_stream, _outDevice, ErrorPayload(er)); return; }
            // `&> /dev/stdout`: the merged stderr is stdout text now.
            if (_outDevice == DeviceKind.Stdout)
            {
                foreach (var line in BashRuntime.EmitBashLines(ErrorPayload(er))) WriteObject(line);
                return;
            }
            WriteObject(InputObject);
            return;
        }
        // `echo msg > /dev/stderr`: the exact bytes on the command's stderr.
        if (_outDevice == DeviceKind.Stderr) { FileSystemHelpers.WriteStderrBytes(this, BashRuntime.RecordFilePayload(InputObject)); return; }
        if (!_stdoutToFile) { WriteObject(InputObject); return; }
        if (_outReg is not null && TryDivert(1, BashRuntime.RecordFilePayload(InputObject))) return;
        // Same per-record rule as tee: record boundary "\n" unless the record is marked
        // NoTrailingNewline (printf x / echo -n x), so `printf x > f` writes exactly "x".
        WriteTarget(_stream, _outDevice, BashRuntime.RecordFilePayload(InputObject));
    }

    /// <summary>
    /// A write to a file target, or to <c>/dev/full</c>, where every non-empty write fails with ENOSPC: bash reports
    /// <c>write error: No space left on device</c> once and the command's status is 1 (<c>true &gt; /dev/full</c>
    /// writes nothing and keeps 0).
    /// </summary>
    private void WriteTarget(RedirectFileWriter? stream, DeviceKind device, string payload)
    {
        if (device != DeviceKind.Full) { Write(stream, payload); return; }
        if (payload.Length == 0) return;
        if (!_fullReported)
        {
            _fullReported = true;
            FileSystemHelpers.WriteStderr(this, ShellDiagnostic.Prefix(SessionState) + "write error: No space left on device");
        }
    }

    /// <summary>One stderr record as bash writes it: the message only, LF-terminated unless the writer
    /// marked it (<c>printf x &gt;&amp;2 2&gt; f</c> writes exactly <c>x</c>) — <see cref="PsBash.Core.StderrRecord"/>.</summary>
    private static string ErrorPayload(ErrorRecord er)
    {
        string text = er.ErrorDetails?.Message ?? er.Exception?.Message ?? er.ToString();
        return PsBash.Core.StderrRecord.Payload(text, er.TargetObject);
    }

    private static void Write(RedirectFileWriter? stream, string payload)
    {
        if (stream is null || payload.Length == 0) return;
        // The exact bytes: escaped-byte markers (invalid UTF-8, see RawBytes) are written back as the
        // single original byte, everything else as UTF-8 — `printf '\xe9' > f` is ONE byte.
        stream.Write(RawBytes.GetBytes(payload));
    }

    protected override void EndProcessing()
    {
        Close();
        // A failed open stopped upstream, but a stage that already finished may have reset the
        // status; the failed redirect is the pipeline's status, so `$?` and `set -e` see 1.
        // Same for a write to /dev/full: the upstream command sets its own status after its last record.
        if (_openFailed || _fullReported) FileSystemHelpers.SetLastExitCode(this, 1);
        // This redirect opened and its command ran: a nested redirect that failed inside the body
        // (`{ echo > /nodir/x; } > log`) already ran its own trap and is not this command's failure.
        else if (SessionState.PSVariable.GetValue(RedirectFailedVariable) is true)
            SessionState.PSVariable.Set(new PSVariable("global:" + RedirectFailedVariable, false));
    }

    protected override void StopProcessing() => Close();

    // A terminating error upstream (a discarded line: `{ echo ${!undef}; } 2>>f`) ends the pipeline without
    // EndProcessing; PowerShell still disposes the cmdlet, so what was written is flushed and the file is let go.
    protected override void DisposeResources() => Close();

    private void Close()
    {
        // The table gets back the fd 1 / 2 the command had before; the target closes once no descriptor names it.
        if (_outReg is { } outReg)
        {
            _outReg = null;
            BashFdTable.Unregister(outReg);
            _stream = null;
        }
        if (_errReg is { } errReg)
        {
            _errReg = null;
            BashFdTable.Unregister(errReg);
            _errorStream = null;
        }
        foreach (var s in new[] { _stream, _errorStream })
            try { s?.Dispose(); } catch { /* best-effort */ }
        _stream = null;
        _errorStream = null;
    }
}
