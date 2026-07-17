using System.Collections.ObjectModel;
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
/// <b>Bounded-output assumption.</b> <c>InvokeScript</c> only returns after the inner
/// pipeline COMPLETES, so this cmdlet must never be handed a never-terminating stage.
/// The emitter's <c>StageIsUnbounded</c> guard keeps <c>tail -f</c> / <c>--follow</c>
/// (and any future unbounded flag) off the fused lane, so a follow chain streams on the
/// unfused path rather than buffering here forever. The buffer is also unbounded in
/// MEMORY for a very large FINITE output (the whole result Collection is held before the
/// first flush), whereas the unfused lane streams per-line — extreme-scale
/// (100M-line-class) outputs therefore remain best served by the deferred per-stage
/// streaming contract (profile bottleneck #2). This slice fixes bottleneck #1 (the
/// per-line IPC return framing) via output batching.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFusedPipeline")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashFusedPipelineCommand : PSCmdlet
{
    /// <summary>
    /// The transpiled inner pipeline (the exact text the unfused path would have
    /// emitted), wrapped as a scriptblock by the emitter.
    /// </summary>
    [Parameter(Mandatory = true, Position = 0)]
    public ScriptBlock Pipeline { get; set; } = null!;

    /// <summary>
    /// Flush the accumulated output once it reaches this many chars. 32 KiB keeps
    /// the frame count low (few IPC writes) without holding an unbounded buffer.
    /// </summary>
    private const int FlushThresholdChars = 32 * 1024;

    protected override void EndProcessing()
    {
        // Run the inner pipeline in the current scope so $global:LASTEXITCODE the
        // last stage sets is visible to the host.
        Collection<PSObject> results = InvokeCommand.InvokeScript(
            useLocalScope: false,
            scriptBlock: Pipeline,
            input: System.Array.Empty<object>(),
            args: null);

        var sb = new StringBuilder(FlushThresholdChars + 4096);
        foreach (var item in results)
        {
            RenderItem(item, sb);
            if (sb.Length >= FlushThresholdChars)
            {
                Flush(sb);
            }
        }
        Flush(sb);
    }

    private void Flush(StringBuilder sb)
    {
        if (sb.Length == 0) return;
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
