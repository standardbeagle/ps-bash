using PsBash.Core;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTee</c> function
/// (REFACTOR-2 follow-on). Mirrors GNU coreutils <c>tee</c>: copy pipeline
/// input both to stdout (by passing the original pipeline items through) and
/// to every named file operand.
///
/// STREAMING: every file operand is opened in <see cref="BeginProcessing"/>
/// (truncating, or appending with <c>-a</c>, so an empty pipeline still
/// creates/truncates the file). Each record is then written + flushed to every
/// file and re-emitted in <see cref="ProcessRecord"/>, so downstream stages see
/// output as upstream produces it instead of after upstream completes. Files
/// are closed in <see cref="EndProcessing"/> and <see cref="StopProcessing"/>.
///
/// BYTE FIDELITY: a record's file bytes are its <c>BashText</c> plus a record
/// boundary (<c>\n</c>), UNLESS the record carries <c>NoTrailingNewline</c>
/// (the marker <c>printf</c> / <c>echo -n</c> / <c>EmitBashLines</c> set on
/// output that has no final newline) — then the text is written exactly.
/// So <c>printf x | tee f</c> writes <c>x</c>, <c>printf 'a\nb' | tee f</c>
/// writes <c>a\nb</c>, and <c>printf 'a\n' | tee f</c> writes <c>a\n</c>,
/// matching GNU. A legacy record whose text already ends in <c>\n</c> without
/// the marker is not given a second newline.
///
/// ARGV: parsed by the shared ordered parser (<see cref="ArgParser"/>, spec
/// <see cref="TeeSpec"/>): <c>--</c> ends option parsing and everything after it is a literal
/// file name that is never classified (<c>tee -- -zz</c> creates <c>-zz</c>); bundles
/// (<c>-aa</c>), unique-prefix long options (<c>--app</c>) and options after operands follow
/// GNU getopt_long; unsupported options are refused with exit 2.
///
/// Common-parameter collision: the transpiler single-quotes every dash-leading word for tee
/// (<c>PsEmitter.OrderedArgCommands</c>), so flags arrive in <see cref="Arguments"/> in order.
/// <see cref="A"/>/<see cref="P"/>/<see cref="I"/> stay as decoy switches ONLY for DIRECT calls
/// (<c>Invoke-BashTee -a f</c>, Pester), where the binder would otherwise eat a bare
/// <c>-a</c> (prefix of <c>-Arguments</c>) / <c>-p</c> / <c>-i</c>; they are re-injected before parsing.
///
/// Glob expansion routes through
/// <see cref="FileSystemHelpers.ResolveOperandPaths"/>.
///
/// Directive 12 (injection): operands are bound positionally and resolved by
/// <see cref="SessionState"/> — never concatenated into a script body.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTee")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashTeeCommand : PSCmdlet
{
    /// <summary>
    /// The bash <c>-a</c> (append) switch — declared explicitly because the
    /// bare token <c>-a</c> prefix-matches the cmdlet's own
    /// <see cref="Arguments"/> catch-all under PowerShell parameter binding.
    /// </summary>
    [Parameter]
    public SwitchParameter A { get; set; }

    /// <summary>
    /// Decoy for a DIRECT <c>-p</c> (bare, it prefix-collides with <c>-ProgressAction</c> / <c>-PipelineVariable</c>
    /// and crashed the binder). Re-injected below.
    /// </summary>
    [Parameter]
    public SwitchParameter P { get; set; }

    /// <summary>Decoy for a DIRECT <c>-i</c> (bare, it is ambiguous: <c>-InformationAction</c> /
    /// <c>-InformationVariable</c>). Re-injected below.</summary>
    [Parameter]
    public SwitchParameter I { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private const string OptAppend = "append", OptIgnoreInterrupts = "ignore-interrupts", OptOutputError = "output-error";

    // GNU --output-error MODEs, in GNU's order (the invalid-argument message lists them).
    private static readonly string[] OutputErrorModes = { "warn", "warn-nopipe", "exit", "exit-nopipe" };

    /// <summary>
    /// tee's whole option surface, built once for the shared ordered parser. <c>-i</c> (ignore SIGINT) and
    /// <c>-p</c> / <c>--output-error[=MODE]</c> (how write errors are treated) are accepted: ps-bash's tee is not
    /// killed by an interrupt it does not own, and a closed reader stops it without an error under every MODE.
    /// REGRESSION: both were refused as unsupported (status 2), so GNU scripts using <c>tee -i</c> / <c>tee -p</c>
    /// failed.
    /// </summary>
    private static readonly OptSpecSet TeeSpec = new(
        new[]
        {
            new OptSpec(OptAppend, 'a', "append"),
            new OptSpec(OptIgnoreInterrupts, 'i', "ignore-interrupts"),
            new OptSpec(OptOutputError, 'p', null),
            new OptSpec(OptOutputError, '\0', "output-error", OptKind.OptionalValue),
        },
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, TeeSpec);

    private readonly List<(string Display, RedirectFileWriter Stream)> _sinks = new();
    private bool _done;
    // An operand tee could not open or write: status 1 at the END. Set only in BeginProcessing it was lost — the
    // upstream stage (`echo x | tee /no/such/f`) runs after it and resets $LASTEXITCODE to 0. Same latch as cat.
    private bool _hadError;

    private void Fail(string message)
    {
        _hadError = true;
        FileSystemHelpers.WriteBashError(this, message);
    }

    protected override void BeginProcessing()
    {
        // Re-inject the decoy-bound flags (a DIRECT call such as `Invoke-BashTee -a f` binds A/P
        // instead of reaching Arguments; the transpiler force-quotes them so they arrive in
        // Arguments in order). The decoys are prepended, which is fine: tee's options are
        // order-insensitive and a decoy can only be bound BEFORE any `--`.
        var args = BashRuntime.PrependDecoys(Arguments, (A.IsPresent, "-a"), (P.IsPresent, "-p"), (I.IsPresent, "-i"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "tee", args)) { _done = true; return; }
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "tee"))
            {
                WriteObject(line);
            }
            _done = true;
            return;
        }

        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "tee", parsed)) { _done = true; return; }
        if (FileSystemHelpers.TryHandleInfoOptions(this, "tee", parsed)) { _done = true; return; }

        foreach (var t in parsed.All(OptOutputError))
        {
            if (t.Value is { } mode && Array.IndexOf(OutputErrorModes, mode) < 0)
            {
                var msg = new System.Text.StringBuilder($"tee: invalid argument {GnuQuote.Locale(mode)} for {GnuQuote.Locale("--output-error")}\nValid arguments are:");
                foreach (var m in OutputErrorModes) msg.Append("\n  - ").Append(GnuQuote.Locale(m));
                if (FileSystemHelpers.UsageHint("tee") is { } hint) msg.Append('\n').Append(hint);
                Fail(msg.ToString()); // status 1 at the end (an upstream stage resets it before then)
                _done = true;
                return;
            }
        }

        bool append = parsed.Has(OptAppend);
        var operands = parsed.Operands();

        foreach (var rawPath in operands.Where(o => !string.IsNullOrEmpty(o)))
        {
            // /dev/null (or NUL) as a tee target: discard the write (bash sends it to the
            // null device) but still pass stdin through to stdout. Without this the
            // resolved path (e.g. C:\dev\null on Windows) has no parent dir.
            // The same for every device name (DevicePath): `tee /dev/stderr` and `tee /dev/stdout` write tee's own
            // streams, `tee /dev/fd/3` descriptor 3. RedirectFileWriter resolves the device; no file is involved.
            if (DevicePath.IsDevice(rawPath))
            {
                try { _sinks.Add((rawPath, RedirectFileWriter.OpenFile(rawPath, append, this))); }
                catch (IOException ex) { Fail($"tee: {rawPath}: {ex.Message}"); }
                continue;
            }

            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, rawPath))
            {
                if (FileSystemHelpers.IsNullDevice(filePath)) continue;
                string normalized = BashPath.ToShell(filePath);
                // GNU names the operand as typed (quotef), never the resolved Windows path.
                string shown = GnuQuote.IfNeeded(rawPath);
                string? parentDir;
                try { parentDir = Path.GetDirectoryName(filePath); }
                catch { parentDir = null; }

                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    Fail($"tee: {shown}: No such file or directory");
                    continue;
                }
                if (Directory.Exists(filePath))
                {
                    Fail($"tee: {shown}: Is a directory");
                    continue;
                }

                try
                {
                    // The shared streaming policy (RedirectFileWriter): written as tee receives it, bursts buffered.
                    _sinks.Add((normalized, RedirectFileWriter.OpenFile(filePath, append)));
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    Fail($"tee: {shown}: {FileSystemHelpers.ReadErrorMessage(ex)}");
                }
            }
        }
    }

    protected override void ProcessRecord()
    {
        if (_done || InputObject == null) return;

        string payload = BashRuntime.RecordFilePayload(InputObject);

        if (_sinks.Count > 0 && payload.Length > 0)
        {
            byte[] bytes = RawBytes.GetBytes(payload);
            for (int i = _sinks.Count - 1; i >= 0; i--)
            {
                var (display, stream) = _sinks[i];
                try
                {
                    stream.Write(bytes);
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    Fail($"tee: {display}: {ex.Message}");
                    try { stream.Dispose(); } catch { /* best-effort */ }
                    _sinks.RemoveAt(i);
                }
            }
        }

        // Pass through the ORIGINAL object so typed properties (and the
        // NoTrailingNewline marker) reach stdout unchanged.
        WriteObject(InputObject);
    }

    protected override void EndProcessing()
    {
        CloseSinks();
        if (_hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    protected override void StopProcessing() => CloseSinks();

    private void CloseSinks()
    {
        foreach (var (_, stream) in _sinks)
        {
            try { stream.Dispose(); } catch { /* best-effort */ }
        }
        _sinks.Clear();
    }
}
