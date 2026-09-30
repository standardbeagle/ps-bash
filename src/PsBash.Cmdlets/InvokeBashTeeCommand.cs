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
/// <see cref="A"/>/<see cref="P"/> stay as decoy switches ONLY for DIRECT calls
/// (<c>Invoke-BashTee -a f</c>, Pester), where the binder would otherwise eat a bare
/// <c>-a</c> (prefix of <c>-Arguments</c>) / <c>-p</c>; they are re-injected before parsing.
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
    /// Decoy for the valid-but-unsupported <c>-p</c> (diagnose write errors). Bare
    /// <c>-p</c> prefix-collides with <c>-ProgressAction</c> and crashed the binder
    /// before the classifier. Re-injected below so it fires exit 2.
    /// </summary>
    [Parameter]
    public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private const string OptAppend = "append";

    // Valid GNU tee flags not implemented by ps-bash. -a / --append IS implemented.
    // (A string[] field on purpose: CommonParameterCollisionGuardTests enumerates the
    // static string sets of every cmdlet to find short flags the binder could eat.)
    private static readonly string[] TeeValidButUnsupported =
    {
        "-i", "--ignore-interrupts",
        "-p", "--output-error",
    };

    /// <summary>tee's whole option surface, built once for the shared ordered parser.</summary>
    private static readonly OptSpecSet TeeSpec = new(
        new[] { new OptSpec(OptAppend, 'a', "append") },
        validButUnsupported: TeeValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, TeeSpec);

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(false);

    private readonly List<(string Display, FileStream Stream)> _sinks = new();
    private bool _done;

    protected override void BeginProcessing()
    {
        // Re-inject the decoy-bound flags (a DIRECT call such as `Invoke-BashTee -a f` binds A/P
        // instead of reaching Arguments; the transpiler force-quotes them so they arrive in
        // Arguments in order). The decoys are prepended, which is fine: tee's options are
        // order-insensitive and a decoy can only be bound BEFORE any `--`.
        var args = BashRuntime.PrependDecoys(Arguments, (A.IsPresent, "-a"), (P.IsPresent, "-p"));

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

        bool append = parsed.Has(OptAppend);
        var operands = parsed.Operands();

        foreach (var rawPath in operands.Where(o => !string.IsNullOrEmpty(o)))
        {
            // /dev/null (or NUL) as a tee target: discard the write (bash sends it to the
            // null device) but still pass stdin through to stdout. Without this the
            // resolved path (e.g. C:\dev\null on Windows) has no parent dir.
            if (FileSystemHelpers.IsNullDevice(rawPath)) continue;

            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, rawPath))
            {
                if (FileSystemHelpers.IsNullDevice(filePath)) continue;
                string normalized = FileSystemHelpers.ToBashPath(filePath);
                string? parentDir;
                try { parentDir = Path.GetDirectoryName(filePath); }
                catch { parentDir = null; }

                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    FileSystemHelpers.WriteBashError(
                        this, $"tee: {normalized}: No such file or directory");
                    continue;
                }

                try
                {
                    var fs = new FileStream(
                        filePath,
                        append ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    _sinks.Add((normalized, fs));
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(
                        this, $"tee: {normalized}: {ex.Message}");
                }
            }
        }
    }

    protected override void ProcessRecord()
    {
        if (_done || InputObject == null) return;

        string text = BashRuntime.GetBashText(InputObject);
        bool noNl = InputObject.Properties["NoTrailingNewline"]?.Value is true;
        string payload = noNl || text.EndsWith('\n') ? text : text + "\n";

        if (_sinks.Count > 0 && payload.Length > 0)
        {
            byte[] bytes = Utf8NoBom.GetBytes(payload);
            for (int i = _sinks.Count - 1; i >= 0; i--)
            {
                var (display, stream) = _sinks[i];
                try
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(this, $"tee: {display}: {ex.Message}");
                    try { stream.Dispose(); } catch { /* best-effort */ }
                    _sinks.RemoveAt(i);
                }
            }
        }

        // Pass through the ORIGINAL object so typed properties (and the
        // NoTrailingNewline marker) reach stdout unchanged.
        WriteObject(InputObject);
    }

    protected override void EndProcessing() => CloseSinks();

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
