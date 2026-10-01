using System.Collections.ObjectModel;
using System.Linq;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// The fused-pipeline lane (PERF task 01KXQ0KMG5C26BWXNVPZXBVA6H, phase 2). When
/// EVERY stage of a bash pipeline maps to one of our own <c>Invoke-Bash*</c>
/// line-oriented commands, the transpiler wraps the WHOLE emitted pipeline in a
/// single <c>Invoke-BashFusedPipeline { … }</c> call instead of letting each
/// per-line object cross the host→launcher IPC boundary individually.
///
/// <para>
/// Why this exists — the phase-1 profile ranked the bottleneck as (1, DOMINANT)
/// the per-output-line IPC framing / console write back to the launcher
/// (~1 ms/line), (2) per-line <c>BashObject</c> allocation, (3) the fixed warm
/// invocation floor. The design implication was explicit: fusing stages without
/// batching the terminal flush leaves bottleneck #1 untouched. This cmdlet
/// attacks #1 directly: it runs the inner all-mapped pipeline host-side (its
/// stages are the SAME real <c>Invoke-Bash*</c> cmdlets, so behaviour — exit
/// codes, ordering, byte output — is identical to the unfused path) and coalesces
/// the result into a few large, newline-preserving frames rather than one frame
/// per line. The number of objects that cross IPC drops from N (one per line) to
/// ~N·avgLineLen/<see cref="FlushThresholdChars"/>.
/// </para>
///
/// <para>
/// <b>Byte-fidelity:</b> the host's <c>SdkWorker.GetOutputText</c> renders each
/// pipeline object as <c>BashText + Environment.NewLine</c> (unless the object
/// carries <c>NoTrailingNewline</c>), a bare string as <c>string +
/// Environment.NewLine</c>, and anything else as <c>ToString() +
/// Environment.NewLine</c>. <see cref="RenderItem"/> reproduces that exactly, so
/// the concatenated batch is byte-for-byte what the launcher would have received
/// as N separate frames. Each emitted batch carries <c>NoTrailingNewline</c> so
/// the host appends nothing further.
/// </para>
///
/// <para>
/// <b>Exit code:</b> the inner pipeline's last stage owns
/// <c>$global:LASTEXITCODE</c> (e.g. grep sets 1 on no-match). The scriptblock is
/// invoked in the CURRENT scope (<c>useNewScope: false</c>) so that write is
/// visible to the host, and this cmdlet never resets it.
/// </para>
///
/// <para>
/// <b>No pipeline input:</b> the emitter only wraps a COMPLETE top-level pipeline,
/// never a pipe target, and the host invokes the transpiled expression with a null
/// input pipeline (<c>SdkWorker</c> — <c>_ps.Invoke(null, …)</c>). A fused pipeline
/// therefore always begins with its own producer stage and is never fed external
/// stdin, so this cmdlet takes no pipeline input.
/// </para>
///
/// <para>
/// Not fused (the transpiler keeps today's PowerShell pipeline): any pipeline with
/// a non-allowlisted / external stage, per-stage redirects, heredocs, env-prefixes,
/// a <c>|&amp;</c> stderr-merge, or a leading <c>!</c> negation; and any pipeline
/// nested inside a command / process substitution (there is no IPC return path to
/// batch there). The kill switch <c>PSBASH_FUSED=0</c> disables detection
/// entirely.
/// </para>
///
/// <para>
/// <b>Streaming, and why unbounded stages stay unfused.</b> The fallback pipeline is piped into a
/// sink instance of this cmdlet (<see cref="Sink"/>) that renders each record into a ~32 KiB batch
/// and writes the frame as soon as it is full, so retained output is one batch, not the whole
/// result (it used to be <c>InvokeScript</c>'s full Collection, held until the pipeline completed;
/// the host drains by removal the same way). That fixes MEMORY. It does not make a never-terminating
/// stage fusible: frames are cut by SIZE, so the lines of a live follower
/// (<c>tail -f log | grep x</c>) would sit in a half-full batch until 32 KiB accumulate, where the
/// unfused lane prints each line at once; flushing on idle would need a timer thread, and
/// <c>WriteObject</c> is only legal on the pipeline thread. The emitter's <c>StageIsUnbounded</c>
/// guard therefore stays: <c>tail -f</c> / <c>--follow</c> (and any future unbounded flag) keep the
/// unfused lane.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFusedPipeline", DefaultParameterSetName = RunSet)]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashFusedPipelineCommand : PSCmdlet
{
    /// <summary>
    /// The transpiled inner pipeline (the exact text the unfused path would have
    /// emitted), wrapped as a scriptblock by the emitter. In phase-2b this is the
    /// FALLBACK path: it runs when <see cref="Stages"/> is absent or any stage's
    /// argv is outside its streaming core's certified subset.
    /// </summary>
    [Parameter(Mandatory = true, Position = 0, ParameterSetName = RunSet)]
    [Alias("Fallback")]
    public ScriptBlock Pipeline { get; set; } = null!;

    private const string RunSet = "Run";
    private const string SinkSet = "Sink";

    /// <summary>
    /// Structured stage list emitted by <c>PsEmitter</c> when every stage's args are
    /// plain literals (phase-2b): each element is an <c>object[]</c> of
    /// <c>[commandName, arg1, arg2, …]</c>. When present AND every stage resolves to
    /// a streaming core (<see cref="LineStreamRegistry.TryCreate"/>), the fused
    /// pipeline runs the lazy line→line chain directly — no per-line
    /// <c>PSCustomObject</c>, no PowerShell pipeline dispatch (profile bottleneck #2).
    /// Any stage that declines forces the <see cref="Pipeline"/> fallback, so
    /// correctness is preserved for every case the streaming cores do not (yet) cover.
    /// </summary>
    [Parameter(ParameterSetName = RunSet)]
    public object[]? Stages { get; set; }

    /// <summary>
    /// Internal: when the fallback pipeline runs, its output is piped into a SECOND instance of this cmdlet
    /// bound to this parameter set — a sink that hands every record to the OUTER instance (this parameter
    /// is that instance), which renders it and writes a ~32 KiB frame as soon as one is full. Not for users.
    /// </summary>
    [Parameter(Mandatory = true, ParameterSetName = SinkSet, DontShow = true)]
    public object? Sink { get; set; }

    /// <summary>Sink-instance pipeline input (the fallback pipeline's records).</summary>
    [Parameter(ValueFromPipeline = true, ParameterSetName = SinkSet, DontShow = true)]
    public PSObject? InputObject { get; set; }

    // The batch being filled by the OUTER instance (fed from the sink instance, same thread).
    private StringBuilder? _batch;

    /// <summary>Frames written by the fallback lane so far (test seam: &gt; 1 means output left before the inner pipeline ended).</summary>
    internal int FramesWritten { get; private set; }

    // The wrapper runs in its OWN scope (useLocalScope: true), so its two parameters leak nothing into the
    // caller. `& $pipeline` is a CALL, so the pipeline text sees a fresh empty $args exactly as the old
    // InvokeScript(args: null) gave it. The pipeline streams into the sink: each record reaches it as it is
    // produced, so nothing is collected. (Not `$args[0]`: how $args reaches a non-local-scope invocation
    // differs between hosts; a parameter binds identically everywhere.)
    // Built per call, not cached in a static: the host runs several runspaces on several threads, and a
    // ScriptBlock is compiled lazily and bound to the session state that first runs it.
    private const string SinkWrapperText =
        "param($__psbPipeline, $__psbSink) & $__psbPipeline | Invoke-BashFusedPipeline -Sink $__psbSink";

    protected override void ProcessRecord()
    {
        if (ParameterSetName != SinkSet || InputObject is null) return;
        var outer = (Sink is PSObject p ? p.BaseObject : Sink) as InvokeBashFusedPipelineCommand;
        outer?.Accept(InputObject);
    }

    private void Accept(PSObject item)
    {
        var sb = _batch ??= new StringBuilder(FlushThresholdChars + 4096);
        RenderItem(item, sb);
        if (sb.Length >= FlushThresholdChars) Flush(sb);
    }

    /// <summary>
    /// Flush the accumulated output once it reaches this many chars. 32 KiB keeps
    /// the frame count low (few IPC writes) without holding an unbounded buffer.
    /// </summary>
    private const int FlushThresholdChars = 32 * 1024;

    protected override void EndProcessing()
    {
        // The sink instance only forwards records (ProcessRecord); it has nothing to run or flush.
        if (ParameterSetName == SinkSet) return;

        // Phase-2b streaming lane: when the emitter supplied a plain-arg stage list
        // and every stage has a certified streaming core, run the composed lazy
        // line→line chain directly (no per-line PSObject). Any decline → fall through
        // to the phase-2a scriptblock path below (byte-identical, always correct).
        if (Stages is { Length: > 0 } && TryBuildStreamingStages(out var stages))
        {
            RunStreaming(stages);
            return;
        }

        // Fallback (phase-2a): run the inner pipeline in the current scope so
        // $global:LASTEXITCODE the last stage sets is visible to the host. The pipeline STREAMS
        // into the sink (see SinkWrapperText): each ~32 KiB frame is written the moment it is full,
        // instead of the whole result Collection being held until the pipeline completes.
        _batch = new StringBuilder(FlushThresholdChars + 4096);
        InvokeCommand.InvokeScript(
            useLocalScope: true,
            scriptBlock: ScriptBlock.Create(SinkWrapperText),
            input: System.Array.Empty<object>(),
            args: new object[] { Pipeline, this });
        Flush(_batch);
    }

    /// <summary>
    /// Resolve every element of <see cref="Stages"/> to a streaming core. All-or-nothing:
    /// returns false (and the caller uses the fallback) if any stage's command/argv is
    /// not in a streaming core's certified subset. No stage is executed here — building
    /// is pure, so a late decline never leaves partial output.
    /// </summary>
    private bool TryBuildStreamingStages(out List<ILineStreamStage> stages)
    {
        stages = new List<ILineStreamStage>();
        foreach (var element in Stages!)
        {
            var argv = ToArgv(element);
            if (argv is null || argv.Length == 0) return false;
            var name = argv[0];
            var rest = argv.Length > 1 ? argv[1..] : System.Array.Empty<string>();
            if (!LineStreamRegistry.TryCreate(name, rest, out var stage, ResolveOperandPath)) return false;
            stages.Add(stage);
        }
        return stages.Count > 0;
    }

    /// <summary>
    /// Resolve a relative file operand exactly as the cmdlet the stage stands in for would:
    /// through PowerShell's current location, not the process working directory.
    /// <para>The two used to be assumed equal, upheld only by the convention that every
    /// writer of the working directory moves BOTH halves. That convention broke three
    /// times — <c>pushd</c>, the subshell <c>Pop-Location</c>, and the module-mode
    /// <c>cd</c> alias — and each break was a SILENT wrong-file read at exit 0. Resolving
    /// through <see cref="PSCmdlet.SessionState"/> makes the two agree by construction, so
    /// a future location-moving writer cannot reintroduce the bug.</para>
    /// <para>Falls back to the process cwd if the provider path is unavailable (a rare host
    /// state); a stage that resolves to a missing file simply declines, so the worst case is
    /// the fallback lane, never wrong output.</para>
    /// </summary>
    private string ResolveOperandPath(string operand)
    {
        try
        {
            return SessionState.Path.GetUnresolvedProviderPathFromPSPath(operand);
        }
        catch
        {
            return System.IO.Path.GetFullPath(operand);
        }
    }

    /// <summary>Coerce one stage element (a PS <c>@('cmd','a','b')</c> literal) into a
    /// <c>string[]</c> of command-name + args. Returns null on an unexpected shape.</summary>
    private static string[]? ToArgv(object? element)
    {
        if (element is PSObject pso) element = pso.BaseObject;
        switch (element)
        {
            case string[] sa:
                return sa;
            case object[] oa:
                return oa.Select(o => (o is PSObject p ? p.BaseObject : o)?.ToString() ?? string.Empty).ToArray();
            case System.Collections.IEnumerable en when element is not string:
                return en.Cast<object?>()
                    .Select(o => (o is PSObject p ? p.BaseObject : o)?.ToString() ?? string.Empty)
                    .ToArray();
            default:
                return null;
        }
    }

    /// <summary>
    /// Run the composed streaming chain and batch its output exactly like the
    /// phase-2a fallback (one <c>NoTrailingNewline</c> frame per ~32 KiB). Each
    /// yielded line renders as <c>line + Environment.NewLine</c> — byte-identical to
    /// the unfused per-object serialization. The LAST stage's exit code becomes
    /// <c>$global:LASTEXITCODE</c>, matching an unfused pipe's exit semantics.
    /// </summary>
    private void RunStreaming(List<ILineStreamStage> stages)
    {
        IEnumerable<string> cur = System.Array.Empty<string>();
        foreach (var stage in stages) cur = stage.Run(cur);

        var nl = System.Environment.NewLine;
        var sb = new StringBuilder(FlushThresholdChars + 4096);
        foreach (var line in cur)
        {
            sb.Append(line).Append(nl);
            if (sb.Length >= FlushThresholdChars)
            {
                Flush(sb);
            }
        }
        Flush(sb);

        // Exit code is valid only after the chain is fully enumerated (grep sets it
        // during iteration). Propagate the terminal stage's code, like a real pipe.
        FileSystemHelpers.SetLastExitCode(this, stages[^1].ExitCode);
    }

    private void Flush(StringBuilder sb)
    {
        if (sb.Length == 0) return;
        FramesWritten++;
        // NoTrailingNewline: RenderItem already appended every record boundary, so
        // the host must emit these bytes verbatim and add nothing.
        WriteObject(BashRuntime.NewBashObject(
            sb.ToString(), "PsBash.TextOutput", noTrailingNewline: true));
        sb.Clear();
    }

    /// <summary>
    /// Reproduces <c>SdkWorker.GetOutputText</c> so a batched frame is byte-for-byte
    /// what the launcher would have received as one frame per line.
    /// </summary>
    private static void RenderItem(PSObject? item, StringBuilder sb)
    {
        if (item is null) return;

        var bashText = item.Properties["BashText"]?.Value;
        if (bashText is not null)
        {
            sb.Append(bashText.ToString() ?? "");
            bool noNewline = item.Properties["NoTrailingNewline"]?.Value is true;
            if (!noNewline) sb.Append(System.Environment.NewLine);
            return;
        }

        if (item.BaseObject is string s)
        {
            sb.Append(s).Append(System.Environment.NewLine);
            return;
        }

        sb.Append(item.ToString()).Append(System.Environment.NewLine);
    }
}
