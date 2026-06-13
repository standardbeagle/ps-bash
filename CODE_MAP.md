# CODE_MAP — ps-bash structural index

Static top-of-context repo map. Evergreen: edit when a project/responsibility moves, not for
detail (detail lives in `docs/specs/*`, linked from CLAUDE.md). Keep small. Why a map not skills:
a compressed static index out-navigates on-demand retrieval (see `.claude/rules/findability.md`).

## Pipeline (one line)

`bash text → BashLexer → BashParser → AST → PsEmitter → BashTranspiler` (all **PsBash.Transpiler**)
`→ IpcWorker` (**PsBash.Core**) `→ ps-bash-host` (**PsBash.Host**: SdkWorker on one SDK runspace)
`→ Invoke-Bash*` runtime (**PsBash.Module** psm1 + **PsBash.Cmdlets** binary cmdlets).

Two binaries: `ps-bash.exe` launcher (**PsBash.Shell**) talks IPC to `ps-bash-host.exe` (**PsBash.Host**).

## Projects → role → key files

| Project | Role | Key files |
|---|---|---|
| **PsBash.Transpiler** | bash → PowerShell front end | `Parser/BashLexer.cs`, `Parser/BashParser{,.Simple,.Words}.cs` (one partial class: spine+compound / simple-command+redirects+heredoc / word decomposition), `Parser/BashToken.cs`, `Parser/Ast/{Commands,Words,Redirects,BashNode}.cs`, `Parser/PsEmitter.cs`, `Parser/PsBuild.cs` (PS-text builder: quoting/exit-code/void/splat), `Transpiler/BashTranspiler.cs` |
| **PsBash.Core** | runtime lib: IPC + module plumbing | `Runtime/IWorker.cs`, `Runtime/IpcWorker.cs`, `Runtime/ModuleExtractor.cs`, `Runtime/OutputCompactor.cs` (compact-output digest), `Runtime/EnvFlags.cs` (shared truthy-env), `Runtime/Ipc/*` (transports, HostProtocol, HostMetadata). Embeds the module + per-TFM Cmdlets DLL as resources. |
| **PsBash.Host** | in-process SDK runspace + interactive shell | `Runtime/SdkRunspace.cs`, `Runtime/SdkWorker.cs`, `Runtime/ICompletionWorker.cs`, `Resources/SdkRunspaceSetup.ps1`; `Shell/{InteractiveShell,LineEditor,CompletionEngine,TabCompleter,CompletionMerge,AliasExpander,HistoryExpander,Suggester,CtrlRSearch}.cs`, `Shell/{CommandAssistProvider,CommandAssistReview}.cs` (AI command assist), `FlagSpecs.cs` |
| **PsBash.Cmdlets** | binary `Invoke-Bash*` cmdlets + `Format-Styled` | `*Command.cs` (one per migrated cmdlet), `FormatStyledCommand.cs`, `BashRuntime.cs` (`RunChildProcess` = bounded spawn), `FileSystemHelpers.cs` (OS interface: `Delete*Force`/`ClearReadOnly`, version, exit-code), `styles/*.css` |
| **PsBash.Module** | psm1 runtime functions (the bulk of `Invoke-Bash*`) | `PsBash.psm1`, `PsBash.psd1`, `BashFlagSpecs.json` (single flag-spec source), `PsBash.Format.ps1xml` |
| **PsBash.Shell** | AOT launcher / CLI | `Program.cs`, `Args.cs`, `Pty/TerminalMode.cs` |
| **PsBash.Testing** | shared test harness | `CanonicalEnv`, `PsBashRunner`, `ProcessSpawn` |

Tests mirror projects: `*.Tests` + `PsBash.Differential.Tests` (bash-oracle), `PsBash.Canary.Tests`, `PsBash.Escalation.Tests`.

## Where to find X

- **Map a bash command → cmdlet** → `PsEmitter.TryEmitMappedCommand` (Transpiler). Passthrough only; flags parsed in the runtime.
- **Build emitted PowerShell text** (quoting/escaping, exit-code test, `[void]`, subshell, word-split splat, null-safe probe) → `Parser/PsBuild.cs`. NEVER hand-concatenate these in `PsEmitter` — route through `PsBuild` so the seam-escaping/`[void]` fixes stay in one place.
- **Destructive filesystem op** (delete/overwrite for rm/cp/mv/find) → `FileSystemHelpers.Delete{Directory,File,Entry}Force` / `ClearReadOnly` (Cmdlets). NEVER raw `Directory.Delete`/`File.Delete` on a force path — they throw on Windows read-only descendants.
- **Spawn a child process** → `BashRuntime.RunChildProcess` (timeout + kill-tree) for buffered shell-outs. Raw `Process.Start` only for streaming/interactive (traceroute/less) that handle `Stopping` themselves.
- **Implement/Parse a command's flags** → `Invoke-Bash*` in `PsBash.psm1`, or a `*Command.cs` binary cmdlet (Cmdlets).
- **Bash flag specs (completion)** → ONE source: `PsBash.Module/BashFlagSpecs.json` (host embeds it; psm1 loads it).
- **Interactive completion** → `Shell/CompletionEngine.cs` (orchestrator) → `TabCompleter` (static base) + runspace queries. Spec: `docs/specs/interactive-completion.md`.
- **AI command assist (Ctrl-^)** → `Shell/CommandAssistProvider.cs` + `CommandAssistReview.cs`; review loop in `InteractiveShell`. Spec: `docs/specs/command-assist.md`.
- **Frecency dir jump (`z`/`zi`, zoxide-style)** → `Shell/SqliteFrecencyStore.cs` (scoring/aging/prune) + `IFrecencyStore`; `InteractiveShell` records cd visits in `SyncWorkerCwdAsync` and intercepts `z`/`zi` prompt-side → `cd` rewrite; Tab completion in `CompletionEngine`, ghost text in `Shell/FrecencySuggester.cs`. Interactive-only. Spec: `docs/specs/frecency-jump.md`.
- **Compact output (`--compact-output`)** → `Runtime/OutputCompactor.cs` + `IpcWorker` buffering. Spec: `docs/specs/compact-output.md`.
- **Alias expansion** → `Shell/AliasExpander.cs`.
- **History expansion** (`!!`, `!$`, `!n`, `!str`, `^old^new`) → `Shell/HistoryExpander.cs` (pure; REPL runs it pre-alias on the in-session list).
- **Host startup / runspace** → `Runtime/SdkRunspace.cs` + `Resources/SdkRunspaceSetup.ps1` (CommandNotFoundAction, module-autoload recovery).
- **Warm runspace pool** (each `-c` connection gets an isolated runspace, discarded on release → clean session per command + concurrency; warmed on dedicated threads so warm-up can't starve the accept/health loop) → `Runtime/WorkerPool.cs`. Sized via `PSBASH_POOL_WARM`/`PSBASH_POOL_MAX`. Spec: `docs/specs/host-lifecycle-contract.md`.
- **IPC / host lifetime / timeouts** → `Runtime/IpcWorker.cs`, `Runtime/Ipc/*`. **`-c` defaults to `Lifetime.Daemon`** (warm pooled host reused across launchers; opt out with `PSBASH_PER_INVOCATION=1`). The daemon spawn MUST sever stdio inheritance (`IpcWorker.SpawnAndWaitAsync`: redirect+drain host stdout/stderr + clear `HANDLE_FLAG_INHERIT` on Windows / `PSBASH_HOST_DETACH` on POSIX) or a persisted daemon holds the launcher's parent stdout open and hangs it.
- **Concurrent host-spawn arbitration** (Daemon single-flight; stops the cold-start thundering-herd that orphans N-1 runspaces) → `IpcWorker.EnsureHostReachableAsync` + `Runtime/Ipc/HostSpawnLock.cs` (endpoint-scoped exclusive FILE lock, NOT a thread-affine Mutex). Spec: `docs/specs/host-lifecycle-contract.md` §Concurrency.
- **Module/cmdlets extraction** → `Runtime/ModuleExtractor.cs`.

## Specs (deep reference)

Index: **`docs/specs/README.md`** — all specs, one line each (guard-enforced complete). The few
auto-loaded ones are `@`-linked in CLAUDE.md (parser-grammar, emitter-strategy, runtime-functions,
runtime-command-reference, interactive-completion); `runtime-migrated-cmdlets` is 126 KB, deliberately
not auto-loaded.

## Path-scoped rules (`.claude/rules/`, load by glob)

`parser.md`/`emitter.md` → Transpiler parser/emitter (`emitter.md` covers `PsBuild.cs`); `runtime.md`/`temp-files.md` → psm1 runtime;
`os-interface.md` → `Cmdlets/**` (destructive FS + spawn + path helpers); `completion.md` → `Shell/**`;
`testing.md`/`qa-rubric.md` → global; `findability.md` → global doctrine.
