# REFACTOR-2 Migration Playbook

**Prompt template + procedure for migrating one `Invoke-Bash*` function from `PsBash.psm1` into a binary cmdlet in `PsBash.Cmdlets.dll`.**

Designed for execution by a fresh subagent with no prior session context. Each migration is a self-contained task: read the psm1 function, write a C# cmdlet, write parity tests, remove the psm1 function, update docs, commit, push.

Companion: [`docs/refactor-2-roadmap.md`](./refactor-2-roadmap.md) — the queue of remaining functions with sizing and tier.

## Prompt template

Paste verbatim into a subagent's prompt. Replace `<FUNCTION>` and `<COMMAND-NAME>` with the chosen entry from the roadmap's Tier 1 list.

> Migrate the `Invoke-Bash<FUNCTION>` script function from `src/PsBash.Module/PsBash.psm1` to a compiled C# cmdlet in `src/PsBash.Cmdlets/`.
>
> Context: this is REFACTOR-2 follow-on work. Twelve such migrations have already landed (whoami, hostname, yes, realpath, md5sum, sha1sum, sha256sum, mkdir, rmdir, cp, mv, rm, touch, ln, sleep, which). The established pattern is documented in `docs/refactor-2-migration-playbook.md` — read that file first, then `docs/specs/runtime-functions.md` (the architectural spec), then the closest existing reference cmdlet in `src/PsBash.Cmdlets/`.
>
> Procedure (the playbook spells out the details):
>
> 1. Locate `function Invoke-Bash<FUNCTION>` in the psm1 — read it end to end.
> 2. Identify the closest existing reference cmdlet from the playbook's "Reference patterns" table.
> 3. Create `src/PsBash.Cmdlets/InvokeBash<FUNCTION>Command.cs` following the reference pattern. Use the established `[Cmdlet(VerbsLifecycle.Invoke, "Bash<FUNCTION>")]` attribute. Declare any short flag whose name is a prefix of a PowerShell common parameter (`-Verbose`, `-Debug`, `-Confirm`, `-WhatIf`, `-Error*`, `-Warning*`, `-Information*`, `-Out*`, `-Progress*`, `-PipelineVariable`) as an explicit `SwitchParameter`. The catch-all `Arguments` parameter uses `ValueFromRemainingArguments = true`.
> 4. Delegate `--help` to psm1 `Show-BashHelp` via parameter-bound `InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "<COMMAND-NAME>")`. Delegate any errors to psm1 `Write-BashError` via `FileSystemHelpers.WriteBashError(this, "<message>")`.
> 5. For path operands that may contain globs, use `FileSystemHelpers.ResolveOperandPaths(this, raw)`. For path resolution without globbing, use `SessionState.Path.GetUnresolvedProviderPathFromPSPath(raw)`.
> 6. For output, prefer `BashRuntime.NewBashObject(text)` (returns bare string for the default `PsBash.TextOutput` type). For typed output objects, construct a `PSObject`, push the type name with `TypeNames.Insert(0, "PsBash.X")`, and add `PSNoteProperty` entries.
> 7. Create `src/PsBash.Cmdlets.Tests/InvokeBash<FUNCTION>CommandTests.cs` following the closest reference test file. Cover the same Directive-3 axes the psm1 oracle handled: empty input, missing operand, alias resolution, `--help`, plus at least one injection probe per Directive 12.
> 8. Build: `dotnet build src/PsBash.Cmdlets/PsBash.Cmdlets.csproj` and `dotnet build src/PsBash.Shell/PsBash.Shell.csproj`. Both must be 0 errors.
> 9. Run the new tests: `dotnet test src/PsBash.Cmdlets.Tests --filter "InvokeBash<FUNCTION>CommandTests"`. All must pass.
> 10. Run a direct smoke test: `& src/PsBash.Shell/bin/Debug/net10.0/ps-bash.exe -c "<COMMAND-NAME> <typical-args>"`. Exit 0, expected output.
> 11. Remove the `function Invoke-Bash<FUNCTION>` block from `src/PsBash.Module/PsBash.psm1` (and any nearby `# --- <COMMAND-NAME> Command ---` header comment). Replace with a single-line breadcrumb comment pointing at the new C# file.
> 12. Add an entry to the migrated-cmdlets table in `docs/specs/runtime-functions.md` (the table starting `| Command | Cmdlet class | Phase | Notes |`). Use the established prose density.
> 13. `git add` the four touched files (cmdlet, test, psm1, runtime-functions.md). Commit with the template below. **Do not push** — return the worktree branch name and a short summary so the orchestrator can review and integrate.
>
> Commit message template:
>
> ```
> refactor: migrate Invoke-Bash<FUNCTION> to binary cmdlet
>
> REFACTOR-2 follow-on. Removes a <N>-line psm1 function in favor of
> InvokeBash<FUNCTION>Command.cs. <one paragraph on key behaviors
> preserved from the oracle and any flag-binding hazards handled>.
>
> Parity tests in InvokeBash<FUNCTION>CommandTests.cs cover <list>. All
> <N> green. Direct smoke test of <typical-invocation> succeeds end-to-end.
>
> Net impact:
>   psm1 line count step: <BEFORE> -> <AFTER>
>   Functions remaining to migrate: <BEFORE-1> -> <AFTER-1>
> ```
>
> **Stop conditions:** if (a) the psm1 function depends on a helper that is not yet migrated and is not in `BashRuntime` or `FileSystemHelpers`, OR (b) the function has shared mutable state with other Invoke-Bash* functions (job table, dir stack), OR (c) a flag-binding collision cannot be resolved by declaring a `SwitchParameter` — STOP, do not commit, return a "blocked" status with the specific blocker. Do not improvise around helper migrations; the orchestrator decides scope.

## Reference patterns

Pick the closest match before writing your cmdlet:

| Migration shape | Reference cmdlet | Reference test |
|---|---|---|
| Trivial — one .NET call, no flags besides `--help` | `InvokeBashWhoamiCommand.cs`, `InvokeBashHostnameCommand.cs` | `InvokeBashWhoamiHostnameCommandTests.cs` |
| Path-resolver with silent flag-skip | `InvokeBashRealpathCommand.cs` | `InvokeBashRealpathCommandTests.cs` |
| Infinite producer (`PSCmdlet.Stopping`) | `InvokeBashYesCommand.cs` | `InvokeBashYesCommandTests.cs` |
| File + pipeline dual mode with typed PSObject output | `InvokeBashChecksumCommands.cs` (md5/sha1/sha256) | `InvokeBashChecksumCommandTests.cs` |
| Filesystem mutator with `-v` verbose output | `InvokeBashMkdirCommand.cs`, `InvokeBashRmdirCommand.cs` | `InvokeBashFileSystemMutatorTests.cs` |
| Filesystem mutator with glob + multi-operand | `InvokeBashCpCommand.cs`, `InvokeBashMvCommand.cs`, `InvokeBashRmCommand.cs` | same |
| Date/time parsing + filesystem | `InvokeBashTouchCommand.cs` | (test deferred — add for your migration) |
| `-s`/`-f`/`-v` link-creator | `InvokeBashLnCommand.cs` | (test deferred — add for your migration) |
| Numeric arg parsing with unit suffix + `Stopping`-polled loop | `InvokeBashSleepCommand.cs` | (test deferred) |
| Command resolution via `Get-Command` delegation | `InvokeBashWhichCommand.cs` | (test deferred) |
| File-bytes hashing | `ChecksumEngine` static helper in `InvokeBashChecksumCommands.cs` | — |

## Architectural invariants (do not break)

These hold for every migrated cmdlet:

1. **AOT-safe.** No `ScriptBlock` construction in C# (use `InvokeCommand.InvokeScript` with a fixed-string body and parameter-bound `$args`). No `Add-Type` from C#. No reflection over user-provided types.
2. **Quoting safety (Directive 12).** User-controlled tokens never get concatenated into a PowerShell script body. They bind positionally through `InvokeScript`'s `args` parameter. Every test file has at least one injection probe.
3. **psm1 helper boundary.** Three psm1 helpers stay in psm1 by design: `Show-BashHelp` (reads module-scope help-spec hashtables), `Write-BashError` (reads `$script:BashErrorMode`), `Resolve-BashGlob` (the legacy psm1 path for non-filesystem PSDrives — Tier-1 cmdlets should bypass this via `FileSystemHelpers.ResolveOperandPaths` which uses `SessionState.Path` directly, not the psm1 helper).
4. **Aliases stay in psm1.** Don't remove `Set-Alias <command-name> -Value 'Invoke-Bash<X>'` lines. They resolve to the cmdlet because the cmdlet class is imported via `PsBash.Cmdlets.dll` before psm1 runs.
5. **Bash output shape.** Path strings get backslash-to-slash normalization for verbose / display output (`FileSystemHelpers.ToBashPath`). Trailing newlines on individual emitted strings follow the psm1 oracle exactly.
6. **Exit code.** When the psm1 oracle set `$global:LASTEXITCODE = 1` on an error, use `FileSystemHelpers.SetLastExitCode(this, 1)`.

## "Definition of done" checklist

A migration is complete when **all** of these are true:

- [ ] `src/PsBash.Cmdlets/InvokeBash<FUNCTION>Command.cs` exists
- [ ] `src/PsBash.Cmdlets.Tests/InvokeBash<FUNCTION>CommandTests.cs` exists
- [ ] `function Invoke-Bash<FUNCTION>` no longer appears in `src/PsBash.Module/PsBash.psm1`
- [ ] `Set-Alias <command-name> ...` line for this command **still exists** in psm1
- [ ] Entry added to the migrated-cmdlets table in `docs/specs/runtime-functions.md`
- [ ] `dotnet build src/PsBash.Cmdlets/PsBash.Cmdlets.csproj`: 0 errors
- [ ] `dotnet build src/PsBash.Shell/PsBash.Shell.csproj`: 0 errors
- [ ] `dotnet test src/PsBash.Cmdlets.Tests --filter "InvokeBash<FUNCTION>CommandTests"`: all green
- [ ] Direct smoke test: `ps-bash -c "<command-name> <typical-args>"` produces expected output and exits 0
- [ ] Commit created with the template message (orchestrator pushes)

## Failure modes to surface, not work around

A subagent should **stop and return "blocked"** rather than improvise on any of these:

- The function calls a psm1 helper that doesn't have a C# equivalent in `BashRuntime` or `FileSystemHelpers` (e.g. `Get-BashFileInfo`, `Format-StatString`, `Read-BashFileLines`, `Get-BashItem`). Migrating the helper is a separate decision.
- The function reads or writes `$global:Bash<X>` state shared with other functions (job table, dir stack, positional params). These migrate as batches with a shared design decision.
- The function has more than one short flag that prefix-collides with PowerShell common parameters and the collision can't be resolved by declaring `SwitchParameter`s (the `Invoke-BashEcho` case — `-e` AND `-E` both ambiguous with `-ErrorAction`). Per `runtime-functions.md`, this means the function stays psm1.
- The function shells out to a native binary via `Get-Command -CommandType Application` with PSDrive-style path mangling. The native-vs-fallback split needs careful thinking about Windows-vs-POSIX behavior.
- Differential test breakage. If the new cmdlet's output differs byte-for-byte from the psm1 oracle on any differential case, the cmdlet is wrong — don't update the golden.
