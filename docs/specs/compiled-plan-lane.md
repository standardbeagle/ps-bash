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

Three caveats on reading that table honestly:

- **The absolute figures do not reproduce; the RATIOS do.** A second identical run on the
  same box gave 12.543 s / 36.513 s / 32.131 s / 34.041 s — 30–50 % slower across the
  board. What held stable is what the conclusions rest on: the fused-vs-unfused speedup on
  the sort chain (3.59× then 2.91×) and the *absence* of any effect on the single-command
  producer (0.98× then 1.06×). **Treat the absolutes as one run's indicative numbers, never
  as a regression threshold.** A throughput gate built on these would flake; S1 and S2 must
  each define their own measured baseline immediately before their own change.
- **Fixed overhead is included.** Each timing is a full `ps-bash -c` invocation, carrying
  ~0.4–0.6 s of process start + connect (visible directly in the one-line foreign-producer
  case). That is under 6 % of the smallest cell here — it compresses the gap slightly
  rather than manufacturing it.
- **The foreign-producer row has no meaningful lines/sec** and deliberately shows `—`. That
  shape emits one line, so any derived rate is `1 ÷ fixed-overhead`, not throughput. It is
  in the table to establish that the shape *never enters the lane* (§1.3) and for its object
  count (§1.4), not to be compared against the other rows.

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

### 1.4 The fan-out itself: objects crossing, and bytes allocated

Throughput is the *effect*; this is the *cause*, and it is the metric the epic is named
for. Measured in-process (`-SkipThroughput` section D), 20 000-line producer — objects are
what actually crosses the pipeline, allocation is total managed bytes for the run.

| Shape | Fused: objects / alloc | Unfused: objects / alloc |
|---|---|---|
| `cat f \| grep x \| sort` | **9** / 35.9 MB | **20 000** / 34.0 MB |
| `cat f` (single command) | 20 000 / 4.5 MB | 20 000 / 4.5 MB |
| `Get-ChildItem -Recurse -File src \| grep .cs` | 1 561 / 64.8 MB | 1 561 / 65.1 MB |

**The load-bearing result is row 1: batching cuts objects crossing by ~2 200× and leaves
allocation UNCHANGED (35.9 vs 34.0 MB).** The fused lane declines streaming (no `sort`
core, §1.1) and runs the fallback PowerShell pipeline internally, so every per-line
`PSObject` is still allocated — it simply is not shipped. That makes the two workstreams
**orthogonal, and neither sufficient alone**:

- **S1 (delivery batching)** fixes the *transport* — objects crossing, which is what the
  ~1 ms/line IPC cost is levied on.
- **S2/S3 (streaming cores)** fix the *allocation* — the churn that survives batching
  untouched.

Rows 2 and 3 are the control: fusion is inert for both (identical columns), confirming at
the object level what §1.2 and §1.3 established structurally. Row 3 also shows why foreign
producers are their own problem — 1 561 objects cost 65 MB, an order of magnitude more per
object than our text lines, because they are real `FileInfo` objects. S4 targets that.

**Measurement trap, recorded because it silently produced a wrong table first:**
`PSBASH_FUSED` is read at **transpile** time (`PsEmitter.FusionEnabled`), not at run time.
Transpiling once and then toggling the env var per iteration measures the fused text twice
and yields identical rows. The env var must be set *before* `Transpile`. Section A's
`Add-Type` of `PsBash.Cmdlets.dll` also poisons an in-process `Import-Module PsBash`
("Assembly with same name is already loaded"), which is why section D runs in a child pwsh.

### 1.5 Why phase-2b's "1M lines/sec" cores are invisible end-to-end

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
  This also settles questions this spec does not answer directly: what a `pipe` does when a
  *middle* stage fails, how partial output interleaves, what `$?` reads mid-list. There is
  no need to specify them, and specifying them would risk contradicting the implementation
  — whatever the unfused lane does IS the definition, and a divergence is a bug in the plan
  lane by construction. (`pipefail` is the one exception, because there the unfused lane is
  itself wrong and S12 changes both lanes together.)
- **Unbounded stages must decline.** `InvokeScript` and any batching executor return only
  after completion, so a never-terminating stage would buffer forever where the unfused
  path streams live. `tail -f`/`-F`/`--follow` declines today via `StageIsUnbounded`; this
  generalizes to plan nodes (S8).

  > **Hazard — read before implementing S3.** `StageIsUnbounded` is an *emitter-side check
  > on the command name*. It is currently the ONLY barrier, and it holds today only because
  > `tail` has no streaming core to reach. **S3 gives `tail` a core**, at which point a
  > `tail -f` that slips past the name check reaches an executor that will buffer it
  > forever — a silent hang, not an error, and the unfused lane it replaced streamed fine.
  > The core itself must therefore refuse `-f`/`-F`/`--follow` independently, so the
  > guarantee does not rest on one string comparison in a different project. Belt and
  > braces is correct here: a hang has no error message to debug.

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
| `pipe` | `stages: node[]`, `mergeStderr: bool[]` | Lazy left-to-right composition. Each stage consumes the previous stage's `IEnumerable<string>`. `mergeStderr[i]` marks a `\|&` join. Exit code = last stage's, unless pipefail (§4). | S8 (`\|`) / S9 (`\|&`) |
| `redirect` | `node`, `op`, `fd`, `target` | `>`, `>>`, `2>`, `2>&1`, `<`, and the heredoc/here-string forms `<<`, `<<-`, `<<<` (whose `target` is the literal body, already tab-stripped and variable-expanded by the emitter — the executor never re-expands). | S9 |
| `assign` | `name`, `value`, `scope` | `x=1` as a statement element. **Required, not optional:** without it a `list` or `andor` containing any assignment declines, and `x=1; cat f \| grep y` is an ordinary statement — coverage would be worthless. `scope` distinguishes an environment variable from a function-local (see the `local`-reassignment handling in `PsEmitter`). | S10 |
| `envprefix` | `node`, `pairs: {name,value}[]` | `VAR=x cmd`. MUST save and restore the prior `$env:VAR` — the env-prefix leak was a real fixed bug. | S9 |
| `negate` | `node` | Leading `!`. Inverts the exit code, not the output. | S9 |
| `andor` | `nodes: node[]`, `ops: ("&&"\|"\|\|")[]` | Short-circuit evaluation; `$?` observable between elements. | S10 |
| `list` | `nodes: node[]` | Sequential `;`/newline. Every element runs; exit code = last. | S10 |
| `capture` | `node` | `$(…)`. Produces a string value rather than writing to the terminal, with bash's trailing-newline stripping. | S11 |

### 3.1 Declines by construction — enumerated, not implied

The catch-all in §2 makes anything unlisted *safe*, but safety is not guidance: an
implementer reading "`list` = every element runs" has no stated reason to exclude a
construct, and mis-scoping one is the bug class the `envprefix` row above exists to warn
about. So the decline list is explicit. Each of these MUST decline the whole plan, and a
slice that starts emitting one is changing this spec, not implementing it:

| Construct | Why it declines |
|---|---|
| Background `&` | Changes process lifetime and `$!`; the executor has no async model. |
| `[[ … ]]`, `[ … ]`, `(( … ))` as an operand | Very common (`[[ -f x ]] && cat f`), so this is the biggest coverage hole left open on purpose. It needs a boolean/arith evaluator, which is engine work, not plan work. Revisit only with its own slice. |
| Loops, `if`/`case`, functions, subshells | The reach is *statement*, not script. Adding them makes this a second shell implementation — considered and explicitly not taken (see the epic's risk list). |
| Process substitution `<(…)` / `>(…)` | Routing is classifier-driven today (`emitter-strategy.md` §7); moving it here would silently change which consumers get a temp file vs a stream. |
| Any external or unmapped command | We do not own the producer — the whole reason §1.3 exists. |

**Coverage consequence, stated plainly:** because `[[ … ]]` operands decline, a guarded
statement like `[[ -f f ]] && cat f | grep x` stays on the PowerShell lane in full. S1's
delivery batching is what carries that case, which is a further reason S1 precedes the plan
tree rather than following it.

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
