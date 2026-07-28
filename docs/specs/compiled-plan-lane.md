# Compiled Plan Lane Specification

The plan IR and execution contract for the compiled statement lane — the C# execution
path that replaces per-line `PSObject` fan-out. Written as the S0 spike of epic
`01KYKGFBY4WCHRNZ10R650H57B`; slices S8–S11 implement it.

Related: [emitter-strategy.md](emitter-strategy.md) §5 (today's fused-pipeline lane),
[runtime-functions.md](runtime-functions.md) (the BashObject model this lane bypasses).

---

## 1. Why — the measured baseline

Measured 2026-07-28 on BEAGLE-AB2, Debug/net10.0, 20 000-line producer, median of 3 runs,
**on a quiet box** (no MSBuild/testhost running; the Roslyn compiler server idles after a
build and is deliberately not treated as busy). Reproduce with
`scripts/profile-fanout-baseline.ps1`; raw output in
`artifacts/worktrack/s0-fanout-baseline-20260728.log`.

| Shape | Fused lane | lines/sec | Unfused | lines/sec |
|---|---|---|---|---|
| `cat f \| grep x \| sort` | 6.868 s | **2 912** | 24.656 s | 811 |
| `cat f` (single command) | 24.933 s | **802** | 24.445 s | 818 |
| `Get-ChildItem \| grep x` | never fuses | — | — | — |

Three structural findings, each of which a slice exists to fix:

### 1.1 The 90% shape attempts streaming and is refused

`cat f | grep x | sort` **does** reach the fused lane and **does** carry a `-Stages` list:

```powershell
Invoke-BashFusedPipeline -Stages @(@('cat','f.txt'), @('grep','x'), @('sort')) `
                         -Fallback { Invoke-BashCat f.txt | Invoke-BashGrep x | Invoke-BashSort }
```

but at runtime `LineStreamRegistry.TryCreate("sort", …)` returns false, so
`TryBuildStreamingStages` fails all-or-nothing and the `-Fallback` scriptblock runs — one
`PSObject` per line, through the PowerShell pipeline engine.

Of the 14 names in `PsEmitter.FusePipelineAllowlist`, only **7** have a streaming core:

| Has a core | **Declines** |
|---|---|
| cat, grep, sed, head, wc, seq, rev | **cut, nl, sort, tac, tail, tr, uniq** |

Any chain containing one of those 7 loses streaming entirely. `sort` sits in the single
most common text chain there is. → **S2** (sort, uniq), **S3** (the rest).

### 1.2 A single command NEVER fuses — the worst case is the commonest one

`PsEmitter.cs:228` — `if (pipeline.Commands.Length < 2) return false;`

So `cat bigfile` emits a bare `Invoke-BashCat f.txt` with no wrapper: no batching, no
streaming, one IPC frame per line. It measured **802 lines/sec, unchanged by
`PSBASH_FUSED`** — the flag is irrelevant because the lane is never entered. This is
~1.2 ms/line, matching the phase-1 profile's "~1 ms/line IPC framing" as bottleneck #1.

This was not anticipated when the epic was planned. It is the strongest argument for
**S1**: batching at the delivery seam is the *only* mechanism that can help a
single-command producer, because there is no pipeline to fuse.

### 1.3 A foreign producer never enters the lane at all

`Get-ChildItem | grep x` emits `Get-ChildItem | Invoke-BashGrep x`. `IsFusablePipeline`
requires **every** stage to be allowlisted, so any unmapped producer — a database query,
any `Get-*`, any external command — is excluded by construction. The plan lane can never
help here: we do not own the producer. Only the delivery seam (**S1**) and consumer-side
wrapper avoidance (**S4**) apply.

### 1.4 Why phase-2b's "1M lines/sec" cores are invisible end-to-end

The phase-2b streaming cores were benchmarked in-process and hit ~1.04M lines/sec (grep)
and ~1.99M (cat). End-to-end the same chains measure ~3k lines/sec. The cores are not
wrong — the **delivery seam caps everything downstream of them**. Until S1 lands, making
more cores faster cannot move end-to-end throughput. This ordering (S1 before the plan
tree) is deliberate.

---

## 2. The decline contract

The single safety property of this lane, inherited from the fused lane and non-negotiable:

> **Any node the executor cannot certify causes the WHOLE plan to decline. The emitter
> always emits a `-Fallback` scriptblock containing the exact PowerShell text today's
> path would have produced, and the executor runs it unchanged.**

Consequences that slices must preserve:

- **All-or-nothing, decided before execution.** Plan building is pure; nothing is executed
  during the decline check, so a late decline can never leave partial output.
- **Partial coverage is always safe.** A slice may implement two node kinds and decline the
  rest. This is what makes S8–S11 individually landable.
- **Declining is silent and non-fatal.** It is a performance outcome, not an error.
- **The fallback is the oracle.** Byte-parity of plan output against fallback output is the
  acceptance criterion for every slice — not parity against a hand-written expectation.
- **Unbounded stages must decline.** `InvokeScript` and any batching executor return only
  after completion, so a never-terminating stage would buffer forever where the unfused
  path streams live. `tail -f`/`-F`/`--follow` declines today via `StageIsUnbounded`; this
  generalizes to plan nodes (S8).

### Kill switch

`PSBASH_PLAN=0` (falsy tokens per `Runtime/EnvFlags.cs`) disables plan emission entirely
and restores today's output. Independent of `PSBASH_FUSED`, which continues to govern the
fused lane until S8 retires it.

---

## 3. Node kinds

The emitter serializes a plan tree; `Invoke-BashPlan` walks it. Every node carries `kind`.
Node kinds are introduced by the slice named in the last column; an executor that meets an
unimplemented kind declines per §2.

| kind | Fields | Semantics | Slice |
|---|---|---|---|
| `command` | `argv: string[]` | One mapped `Invoke-Bash*` stage, resolved to an `ILineStreamStage`. Declines if the name has no core or the argv is outside its certified subset. | S8 |
| `pipe` | `stages: node[]` | Lazy left-to-right composition. Each stage consumes the previous stage's `IEnumerable<string>`. Exit code = last stage's, unless pipefail (§4). | S8 |
| `redirect` | `node`, `op`, `fd`, `target` | Per-stage or whole-node redirect (`>`, `>>`, `2>`, `2>&1`, `<`). | S9 |
| `envprefix` | `node`, `pairs: {name,value}[]` | `VAR=x cmd`. MUST save and restore the prior `$env:VAR` — the env-prefix leak was a real fixed bug. | S9 |
| `negate` | `node` | Leading `!`. Inverts the exit code, not the output. | S9 |
| `andor` | `nodes: node[]`, `ops: ("&&"\|"\|\|")[]` | Short-circuit evaluation; `$?` observable between elements. | S10 |
| `list` | `nodes: node[]` | Sequential `;`/newline. Every element runs; exit code = last. | S10 |
| `capture` | `node` | `$(…)`. Produces a string value rather than writing to the terminal, with bash's trailing-newline stripping. | S11 |

**Deliberately absent — out of scope for this epic:** loops, conditionals, functions,
subshells, process substitution, and any external/unmapped command. These stay on the
PowerShell lane. A slice proposing to add them is scope drift (see the epic's risk list):
the reach is capped at *statement*, not *script*. Adding them would make this a second
shell implementation, which was considered and explicitly not taken.

---

## 4. Semantics the lane is required to fix

Per the epic's decision 2 these must behave **identically in both lanes**, so behavior never
changes because an unrelated edit turned a literal argument into a variable and dropped a
statement out of the compiled lane.

| Behavior | Today | Required | Slice |
|---|---|---|---|
| `PIPESTATUS` / `set -o pipefail` | not implemented; skipped golden at `PipeDifferentialTests.cs:164` | every stage's code recorded; pipefail returns the first non-zero | S12 |
| Broken pipe (`yes \| head -n 3`) | hangs forever; skipped golden at `:198` | terminates. **Exit 141 is deliberately NOT synthesized** — the divergence stays documented | S13 |
| Operand evaluation order | splat temps hoist to a prelude, so `x=5; echo $((x++)) $x` prints `5 5` | strict left-to-right: `5 6` | S14 |

A plan node evaluates its operands in order, which is what makes S14 tractable here and not
in the emitter — see `emitter-strategy.md` §4 for why the prelude hoist was originally left
in place.

---

## 5. Where the code lives

| Concern | Location |
|---|---|
| Plan emission (build the literal, decide decline) | `PsBash.Transpiler/Parser/FusedLane.cs` (extracted from `PsEmitter.cs` by S5) |
| Plan execution | `PsBash.Cmdlets/InvokeBashPlanCommand.cs` (S8) |
| Per-command streaming cores | `PsBash.Cmdlets/LineStream/` (split out of `LineStreamStages.cs` by S6) |
| Delivery batching | `PsBash.Host/Runtime/SdkWorker.cs` (S1) — independent of this lane, helps every shape |

**Renderer duplication warning.** `InvokeBashFusedPipelineCommand.RenderItem` reproduces
`SdkWorker.GetOutputText` by hand and the two **cannot** be merged: the reference graph is
`Host → Core → Cmdlets` with no `Host → Cmdlets` reference (`PsBash.Host.csproj:22`). S7
pins them with a byte-parity guard test. Any change to output rendering must touch both.
