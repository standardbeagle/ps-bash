# Parity follow-ups (found 2026-06-17 oracle sweeps)

Two real bugs surfaced by the file-based WSL-oracle sweeps that were left for
dedicated follow-up (each needs more care than a quick emitter/cmdlet fix). The
many other bugs found in the same session were fixed and committed
(`af2a01a`, `9fc78d2`, `02baebf`, `33a580c`, `3eaaee2`, `7e7d75a`, `cddbe58`).

> **RESOLVED 2026-06-17.** Both fixed. The original #1 diagnosis (IPC output
> framing) was WRONG — see the "Actual root cause" note below.

## 1. printf trailing newline dropped when another command follows

**Symptom:**
```
printf "%s\n" b            -> "b\n"        (correct, alone)
printf "%s\n" b; echo c    -> "bc\n"       (WRONG — bash: "b\nc\n"); the printf
                                            newline vanishes only when a frame follows
```
`printf` (and any cmdlet using `noTrailingNewline: true`) emits a single
`BashText` that already contains its own trailing `\n`. `SdkWorker.GetOutputText`
(SdkWorker.cs:577) correctly returns that text verbatim for a no-trailing-newline
object. The loss is **downstream in the host→launcher IPC output framing**: when
a no-trailing-newline frame is followed by another output frame, the first
frame's embedded trailing `\n` is dropped (the line-based `HostProtocol` /
`Connection` output sink appears to treat each delivery as a newline-terminated
"line" and strip/normalize the trailing newline). Alone it survives because
there is no following frame to merge with.

**Why deferred:** the fix is in the IPC output-framing layer (`Connection` output
sink / `HostProtocol.WriteResponseLineAsync` / `OutputCompactor`), which carries
EVERY command's output — `noTrailingNewline` is referenced across ~90 files
(printf, cat, head, tail, sort, …). It needs its own differential pass to avoid
regressing echo/ls/cat newline behavior (there are existing "blank line between
ls entries" / "double newline" guards). Not a safe late-session change.

**Repro:** `ps-bash -c 'printf "%s\n" b; echo c'` → `bc` (expect `b`/`c` on two lines).

**Actual root cause (the deferred IPC theory was wrong).** Raw-byte capture showed
`printf "%s\n" b` ALONE also dropped its newline (emits just `b`, byte `98`) — so the
loss was never "only when a frame follows," and never in the IPC framing (frames are
base64-encoded, so an embedded `\n` survives the wire intact). The real bug was in
`BashRuntime.NewBashObject` (`src/PsBash.Cmdlets/BashRuntime.cs`): it called
`NormalizeBashText` UNCONDITIONALLY, stripping one trailing `\n` even for a
`noTrailingNewline:true` object. printf builds the full literal output (`"b\n"`) and
passes `noTrailingNewline:true` to mean "emit these bytes verbatim, add no boundary" —
but the strip turned `"b\n"` into `"b"`, deleting printf's own newline. Alone the
terminal still looked plausible; followed by `echo c` the two frames concatenated to
`bc`.

**Fix:** skip `NormalizeBashText` when `noTrailingNewline` is set (the flag means "exact
bytes"). printf is the only `noTrailingNewline:true` caller, and `EmitBashLines` never
passes a trailing-`\n` string with the no-newline marker, so the blast radius is printf.
Regression tests: `BashRuntimeTests.NewBashObject_NoTrailingNewline_PreservesTrailingNewlineVerbatim`,
and `NewFlagSupportTests.Printf_RecyclesFormatUntilArgsExhausted` updated to expect the
preserved trailing newline (its old comment falsely claimed the streamed output kept it).

## 2. ${!arr[@]} on an EMPTY array prints a benign runtime error

After the `cddbe58` fix, `${!arr[@]}` works for populated indexed/associative
arrays, but on an empty array:
```
arr=(); echo ${!arr[@]}    -> "Object reference not set to an instance of an object."
                              on stderr (exit 0); bash prints an empty line
```
The emitted expression
`$(if ($arr -is [System.Collections.IDictionary]) {...} elseif ($arr.Count -gt 0)
{...} else { @() })` evaluates to `@()` cleanly in a plain pwsh runspace, so the
NPE is in the runtime echo/`$()` path for an empty expansion in the SDK runspace,
not the if-expression itself. Non-fatal (exit 0, benign stderr line); rare.

**Repro:** `ps-bash -c 'arr=(); echo ${!arr[@]}'`.

**Root cause + fix.** The bare-argument expansion used the command-substitution form
`$(if ... else { @() })`. A `$(...)` that yields an empty collection still binds ONE
`$null` positional argument, which `ConvertFromBashArgs` then NPE'd on
(`arg.StartsWith` on null). The emitter (`PsEmitter.EmitBracedVar`) now emits the ARRAY-
subexpression `@(...)` for the bare-argument case — `@()` unrolls to ZERO args (bash
parity: a blank line), populated unrolls to N separate args. Inside double quotes the
expansion is interpolated into the string, so `$(...)` is kept there. Regression tests:
`PsEmitterTests.Transpile_ArrayKeys_BareArg_UsesArraySubexprNotDollarSubexpr` and
`Transpile_ArrayKeys_InsideDoubleQuotes_UsesDollarSubexpr`.
