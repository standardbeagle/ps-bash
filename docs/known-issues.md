# Known Issues

## `eval "$(cmd)"` — runtime eval (resolved)

**Status:** supported as of REFACTOR-5a-2. Supersedes the v0.8.14
parse-time-rejection design.

**History:** v0.8.14 resolved `eval` entirely at parse time in
`PsEmitter.EmitEval` and *rejected* dynamic eval bodies — `eval "$(cmd)"`,
`eval \`cmd\``, `eval $((expr))`, `eval <(cmd)` — with a `ParseException`,
because the transpiler could not reach a body computed at runtime. The
rationale was avoiding a nested-ps-bash-subprocess hang.

**Current behavior:** `EmitEval` still folds **static** eval bodies (literals,
quoted literals) inline at parse time via `TryReconstructBashSource` — lossless
and zero-runtime-cost. But when any arg requires runtime expansion (command
sub, arithmetic, process sub, variable, glob, brace expansion) the emitter now
emits an **in-process runtime eval block** instead of rejecting:

```powershell
$__psbash_eval_src = @(<args>) -join ' '
# depth-guarded (limit 5), then:
$__psbash_eval_pwsh = [PsBash.Core.Transpiler.BashTranspiler, PsBash.Transpiler]::Transpile(
    $__psbash_eval_src, [PsBash.Core.Transpiler.TranspileContext, PsBash.Transpiler]::Eval)
Invoke-Expression $__psbash_eval_pwsh
```

The eval body is re-transpiled **in-process** by `BashTranspiler` inside the
ps-bash-host SDK runspace and run via `Invoke-Expression`. No nested ps-bash
subprocess is spawned, so the v0.8.14 hang risk does not return. A depth probe
(`$global:__BashEvalDepth`, limit 5) guards against runaway `eval`-of-`eval`
recursion.

`fnm env --shell bash`, `direnv hook bash`, `ssh-agent -s`, and `dircolors`
shell-init idioms work directly — no manual inlining required.

**Perf:** the fnm-shaped payload (`eval $(printf 'export ...\n...')`) runs well
under the 15 s hang-detection budget — sub-second in local runs. The 15 s
budget on the eval differential tests is a *hang* oracle, not a perf target:
REFACTOR-7 made non-interactive modes spawn a private host per invocation, so
each `-c` pays a single-digit-second host cold-start cost that a 2 s budget
would conflate with a genuine eval hang.

**Regression guards** (in `BashTranspilerTests`):
- `Transpile_EvalWithCommandSubstitution_EmitsRuntimeSubexpression`
- `Transpile_EvalWithMappedCommandSubstitution_TranspilesInner`
- `Transpile_EvalWithBackquoteCommandSub_EmitsRuntimeSubexpression`
- `Transpile_EvalWithArithmeticExpansion_EmitsRuntimeSubexpression`
- `Transpile_EvalWithVariableReference_ForwardsToRuntimeEval`

Differential (bash-oracle) guards in `EvalDifferentialTests`:
- `Differential_Eval_CmdSubMultilineExports_DoesNotHang`
- `Differential_Eval_QuotedCmdSubMultiline_DoesNotHang`
- `Differential_Eval_FnmShapedPayload_DoesNotHang`
- `PsBash_Eval_CmdSubMultiline_WallTimeUnder15000ms`
