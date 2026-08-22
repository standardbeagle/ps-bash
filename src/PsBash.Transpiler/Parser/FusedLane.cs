using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// The fused-pipeline lane (PERF phase 2): the detection half of
/// <c>Invoke-BashFusedPipeline</c>. Extracted from <see cref="PsEmitter"/> so the
/// emitter file stays navigable — this is pure predicate/builder logic with no
/// emitter state of its own.
/// <para>
/// <b>State ownership.</b> The two <c>[ThreadStatic]</c> depths the lane's decision
/// depends on (<c>_captureDepth</c>, and the test seam
/// <c>PsEmitter.FusionEnabledOverride</c>) stay on <see cref="PsEmitter"/>, which is
/// what mutates them. They are passed IN — <see cref="IsFusionEnabled"/> takes the
/// override, and the capture-depth gate is applied by the caller — so this type
/// reaches back for nothing and the "when does fusion apply" answer cannot drift by
/// having two homes for the same flag.
/// </para>
/// </summary>
internal static class FusedLane
{
    /// <summary>
    /// Line-oriented commands whose pipelines are eligible for the fused lane.
    /// Every stage of an all-mapped pipeline must be one of these for the emitter
    /// to wrap the whole pipeline in <c>Invoke-BashFusedPipeline { … }</c> (which
    /// batches the terminal flush — the phase-1 profile's dominant bottleneck).
    /// The runtime cmdlet runs the SAME <c>Invoke-Bash*</c> stages, so fidelity is
    /// guaranteed by construction; this set is intentionally the line-oriented
    /// text subset (no ls/find/awk/jq typed-object producers, whose boundary
    /// object shape must survive `ls | grep .txt`).
    /// <para>
    /// As of S3 of the fan-out epic EVERY name here has a streaming core in
    /// <c>LineStreamRegistry</c> (S1: cat/seq/rev/head/wc/grep/sed; S2: sort/uniq
    /// + the <c>cat FILE</c> producer; S3: tr/cut/tail/tac/nl), so a chain no longer
    /// declines because of an ARBITRARY missing stage. The lane is still
    /// all-or-nothing PER ARGV: a core accepts only its certified argv subset and
    /// declines the rest to the real cmdlet, and this list is only the first gate.
    /// </para>
    /// </summary>
    internal static readonly HashSet<string> FusePipelineAllowlist = new(StringComparer.Ordinal)
    {
        "cat", "grep", "sed", "head", "tail", "wc",
        "sort", "uniq", "tr", "cut", "seq", "rev", "tac", "nl",
    };

    /// <summary>
    /// The fused lane is ON by default; <c>PSBASH_FUSED</c> set to a falsy token
    /// (<c>0</c> / <c>false</c> / <c>no</c> / <c>off</c>) disables detection so the
    /// old PowerShell-pipeline path is used. (Read here rather than via
    /// <c>PsBash.Core.Runtime.EnvFlags</c> because PsBash.Transpiler is a leaf
    /// project that must not reference PsBash.Core — it would be a reference cycle.)
    /// </summary>
    /// <param name="overrideValue">
    /// The caller's <c>[ThreadStatic]</c> test seam (<c>PsEmitter.FusionEnabledOverride</c>),
    /// passed in rather than read from here so the thread-affine state has exactly one home.
    /// </param>
    internal static bool IsFusionEnabled(bool? overrideValue)
    {
        if (overrideValue is bool forced) return forced;
        return !IsFusionDisabledByEnvValue(
            Environment.GetEnvironmentVariable("PSBASH_FUSED"));
    }

    /// <summary>
    /// Pure kill-switch parse (env-value → disabled?), testable without touching
    /// the process environment. A null/empty/unrecognized value keeps the lane ON
    /// (default); only an explicit falsy token disables it.
    /// </summary>
    internal static bool IsFusionDisabledByEnvValue(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return false;
        return v.Equals("0", StringComparison.OrdinalIgnoreCase)
            || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when every stage of <paramref name="pipeline"/> maps to an allowlisted
    /// line-oriented command with no per-stage redirect / heredoc / env-prefix, the
    /// pipeline is a plain <c>|</c> chain (no <c>|&amp;</c>) of at least two stages,
    /// and it is not negated — the conditions under which
    /// <c>Invoke-BashFusedPipeline</c> is byte-identical to today's path.
    /// </summary>
    internal static bool IsFusablePipeline(Command.Pipeline pipeline)
    {
        if (pipeline.Negated) return false;
        if (pipeline.Commands.Length < 2) return false;
        foreach (var op in pipeline.Ops)
            if (op != "|") return false; // |& stderr-merge falls back this slice
        foreach (var stage in pipeline.Commands)
        {
            if (stage is not Command.Simple s) return false;
            if (!s.Redirects.IsEmpty || !s.HereDocs.IsEmpty || !s.EnvPairs.IsEmpty)
                return false;
            var name = s.Words.IsEmpty ? null : PsEmitter.GetLiteralValue(s.Words[0]);
            if (name is null || !FusePipelineAllowlist.Contains(name))
                return false;
            // Unbounded/streaming stage guard: the fused cmdlet runs the inner
            // pipeline via InvokeScript, which only returns after the pipeline
            // COMPLETES — so a never-ending stage (e.g. `tail -f`) would batch-buffer
            // forever and hang silently where the unfused lane streams live. Any such
            // stage forces the fallback. See StageIsUnbounded.
            if (StageIsUnbounded(name, s.Words))
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when an allowlisted stage's args put it into an UNBOUNDED / never-terminating
    /// mode that the batched fused lane cannot serve (it buffers until the inner pipeline
    /// completes). The fused chain must never hang where the unfused chain streams, so any
    /// such stage forces the PowerShell-pipeline fallback.
    /// <para>
    /// General deny seam keyed by command so future allowlist additions inherit the check.
    /// Today only <c>tail</c> has an unbounded flag (<c>-f</c>/<c>-F</c>/<c>--follow</c>).
    /// For <c>tail</c>, a non-literal arg (variable / command-sub / glob) is treated as
    /// potentially <c>-f</c> and is conservatively unsafe — correctness (no hang) over the
    /// perf win on an uncommon shape.
    /// </para>
    /// <para>
    /// <b>This is one of TWO independent barriers, deliberately.</b> Until S3 it was the
    /// only one, and it held partly by ACCIDENT: <c>tail</c> had no streaming core, so a
    /// follow argv had nothing to reach. S3 gave <c>tail</c> a core, so
    /// <c>TailStage.IsFollowToken</c> now refuses every follow spelling itself. Keep both:
    /// a hang produces no error message to debug, and this check also covers the
    /// phase-2a scriptblock lane, which never consults a core at all.
    /// </para>
    /// </summary>
    internal static bool StageIsUnbounded(string command, ImmutableArray<CompoundWord> words)
    {
        if (command != "tail") return false;

        // words[0] is the command name; scan the operands/flags after it.
        for (int i = 1; i < words.Length; i++)
        {
            var lit = PsEmitter.GetLiteralValue(words[i]);
            if (lit is null)
                return true; // non-literal arg could expand to -f → be safe, fall back

            if (lit == "--follow" || lit.StartsWith("--follow=", StringComparison.Ordinal))
                return true;
            if (lit == "--")
                break; // end of flags — remaining tokens are operands, not -f

            // Short-flag group (`-f`, `-F`, `-qf`, `-fn`, …): any 'f'/'F' flag char = follow.
            if (lit.Length >= 2 && lit[0] == '-' && lit[1] != '-'
                && (lit.IndexOf('f') >= 0 || lit.IndexOf('F') >= 0))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Build the runtime <c>-Stages</c> array for a fusable pipeline — one
    /// <c>@('cmd', 'arg', …)</c> element per stage — or <c>null</c> when ANY stage
    /// carries a non-literal argument. Only compile-time-static args
    /// (<c>PsEmitter.TryGetStaticArgValue</c>: literals, single/double-quoted literals,
    /// escaped chars) are emitted; each is a PS single-quoted literal so the value
    /// the streaming core receives is exactly what the unfused cmdlet's
    /// <c>Arguments</c> would get. A variable / command-sub / glob / brace-expansion
    /// arg returns null, so the pipeline falls back to the phase-2a scriptblock lane
    /// (which evaluates those correctly). Called only on an already-validated
    /// <see cref="IsFusablePipeline"/> pipeline (every stage is a mapped
    /// <see cref="Command.Simple"/>).
    /// </summary>
    internal static string? TryBuildFusedStages(Command.Pipeline pipeline)
    {
        var stageStrs = new List<string>(pipeline.Commands.Length);
        foreach (var stageCmd in pipeline.Commands)
        {
            if (stageCmd is not Command.Simple s || s.Words.IsEmpty) return null;
            var name = PsEmitter.GetLiteralValue(s.Words[0]);
            if (name is null) return null;
            var parts = new List<string>(s.Words.Length) { PsBuild.SingleQuote(name) };
            for (int i = 1; i < s.Words.Length; i++)
            {
                var val = PsEmitter.TryGetStaticArgValue(s.Words[i]);
                if (val is null) return null; // non-literal arg → no stage list, use fallback
                // The fallback path runs the arg through TransformWordPath (operand
                // path rewrites: /tmp/… → $env:TEMP\…, MSYS drive paths). The stage
                // list carries the RAW literal, so if the transform would change the
                // value the two paths would diverge — decline (conservatively also for
                // a quoted path that the fallback would leave literal; correctness over
                // the perf win on that rare shape).
                if (PsEmitter.TransformWordPath(val) != val) return null;
                parts.Add(PsBuild.SingleQuote(val));
            }
            stageStrs.Add("@(" + string.Join(", ", parts) + ")");
        }
        return "@(" + string.Join(", ", stageStrs) + ")";
    }
}
