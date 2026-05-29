# Compact Output Specification

An opt-in mode that replaces a non-interactive command's raw output stream with a
**bounded, summarized digest** — built for agent contexts that want a small, scannable
result instead of thousands of lines. Off by default; faithful streaming is the norm.

Source files:
- `src/PsBash.Core/Runtime/OutputCompactor.cs` — the pure summarizer (no I/O).
- `src/PsBash.Core/Runtime/IpcWorker.cs` — buffers frames and emits the digest.
- `src/PsBash.Core/Runtime/EnvFlags.cs` — shared truthy-env parsing.
- `src/PsBash.Shell/Args.cs`, `Program.cs` — CLI flag → env normalization.

---

## 1. Enabling it

| Surface | Effect |
|---------|--------|
| `--compact-output` / `--caveman` / `--wenyan` | enable (the latter two are aliases) |
| `--no-compact-output` | disable, even if the env var is set |
| `PSBASH_COMPACT_OUTPUT` | enable when truthy (`1`/`true`/`yes`/`on`, via `EnvFlags.IsTruthy`) |
| `PSBASH_COMPACT_COMMAND` | override the command label shown in the digest header |

**Precedence:** the CLI flag wins over the env var. `Program.cs` resolves
`shellArgs.CompactOutput ?? EnvFlags.IsTruthy("PSBASH_COMPACT_OUTPUT")` and re-writes
`PSBASH_COMPACT_OUTPUT=1|0` so downstream layers read one stable switch.

**Scope:** only the non-interactive path (`IpcWorker.QueryAsync` — i.e. `-c`, stdin
pipe, script file). The interactive REPL is unaffected.

---

## 2. The digest format (`OutputCompactor.CompactCommandOutput`)

Signature: `CompactCommandOutput(command, exitCode, timedOut, frames, maxLines = 120)`.

Output is a header line followed by stream-prefixed body lines:

```
ps-bash compact-output: exit=1 stdout_lines=12 stderr_lines=3 command="dotnet build"
[out] Restored /repo/x.csproj
[err] error CS0103: the name 'Foo' does not exist
...
```

(`timeout=true` is inserted into the header when `timedOut`.)

### Pipeline
1. **SplitFrames** — each frame's text is normalized (`\r\n`/`\r` → `\n`) and split into
   lines; a trailing empty line is dropped. stdout/stderr line counts are taken here (the
   header reports the *pre-collapse* totals).
2. **CollapseRuns** — a run of consecutive identical lines (same stream + text) collapses
   to the first line plus `... repeated N more times: <line truncated to 120>`.
3. **SelectLines** — if `<= maxLines`, keep all. Otherwise:
   - **failure** (`exitCode != 0` or `timedOut`): take "important" lines (§3) up to
     `maxLines/2`, then fill the remainder from the tail.
   - **success**: keep a head of `max(8, maxLines/4)` lines, then a
     `... omitted N compacted lines ...` marker (`N = max(0, postCollapseLineCount - maxLines)`),
     then fill from the tail.
   - A final `Take(maxLines)` hard-caps the result.

### §3 IsImportant
A line is "important" (surfaced first in the failure path) when it is on **stderr**, or
its text matches (case-insensitive)
`error|failed|failure|exception|timeout|denied|fatal|warning`, a `:line(:col)` location,
`line <n>`, or a stack-frame ` at …(…:n)`.

---

## 3. IpcWorker integration & two intentional departures

When `PSBASH_COMPACT_OUTPUT` is truthy, `IpcWorker.QueryAsync` routes every response
frame into a `List<OutputFrame>` instead of the normal per-frame console/callback write,
then calls `EmitCompactedOutput` once the command finishes (or on idle-timeout, with
`exitCode = 124, timedOut = true`). `EmitCompactedOutput` sends the digest to
`OutputCallback` if set, else `Console.Write`.

This deviates from the normal REFACTOR-4 frame routing in two deliberate ways (both
commented at the buffering site in `IpcWorker.cs`):

1. **No streaming.** Output is held until the command exits, so memory grows with the
   line count. Only the *emitted* digest is bounded (`maxLines`), **not** the intake
   buffer — a command that prints gigabytes will buffer all of it. This is acceptable for
   the opt-in agent use case but is a real large-input risk
   (cf. `.claude/rules/qa-rubric.md` Directive 3, axis 2). Do not enable compact mode for
   unbounded producers.
2. **Stderr folds into stdout.** Both streams go into the one buffer (normal mode keeps
   stderr on `Console.Error`, never folded into `OutputCallback`). The stream distinction
   is preserved *textually* by the `[out]` / `[err]` prefixes the compactor writes — useful
   for agents consuming a single combined stream.

---

## 4. Notes

- `OutputCompactor` is pure and unit-tested (`PsBash.Core.Tests/Runtime/OutputCompactorTests.cs`);
  it does no I/O, so failure/large/collapse paths are tested deterministically.
- `EnvFlags.IsTruthy` is the single truthy-env parser shared by `Program.cs` (launcher)
  and `IpcWorker` (core) — previously each kept its own copy.
- The failure path reorders output (important lines pulled to the front); this is a
  summary, not a faithful transcript. Use normal mode when byte-exact ordering matters.
