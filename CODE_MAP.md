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
| **PsBash.Transpiler** | bash → PowerShell front end | `Parser/BashLexer.cs`, `Parser/BashParser.cs`, `Parser/BashToken.cs`, `Parser/Ast/{Commands,Words,Redirects,BashNode}.cs`, `Parser/PsEmitter.cs`, `Transpiler/BashTranspiler.cs` |
| **PsBash.Core** | runtime lib: IPC + module plumbing | `Runtime/IWorker.cs`, `Runtime/IpcWorker.cs`, `Runtime/ModuleExtractor.cs`, `Runtime/OutputCompactor.cs` (compact-output digest), `Runtime/EnvFlags.cs` (shared truthy-env), `Runtime/Ipc/*` (transports, HostProtocol, HostMetadata). Embeds the module + per-TFM Cmdlets DLL as resources. |
| **PsBash.Host** | in-process SDK runspace + interactive shell | `Runtime/SdkRunspace.cs`, `Runtime/SdkWorker.cs`, `Runtime/ICompletionWorker.cs`, `Resources/SdkRunspaceSetup.ps1`; `Shell/{InteractiveShell,LineEditor,CompletionEngine,TabCompleter,CompletionMerge,AliasExpander,Suggester,CtrlRSearch}.cs`, `Shell/{CommandAssistProvider,CommandAssistReview}.cs` (AI command assist), `FlagSpecs.cs` |
| **PsBash.Cmdlets** | binary `Invoke-Bash*` cmdlets + `Format-Styled` | `*Command.cs` (one per migrated cmdlet), `FormatStyledCommand.cs`, `BashRuntime.cs`, `styles/*.css` |
| **PsBash.Module** | psm1 runtime functions (the bulk of `Invoke-Bash*`) | `PsBash.psm1`, `PsBash.psd1`, `BashFlagSpecs.json` (single flag-spec source), `PsBash.Format.ps1xml` |
| **PsBash.Shell** | AOT launcher / CLI | `Program.cs`, `Args.cs`, `Pty/TerminalMode.cs` |
| **PsBash.Testing** | shared test harness | `CanonicalEnv`, `PsBashRunner`, `ProcessSpawn` |

Tests mirror projects: `*.Tests` + `PsBash.Differential.Tests` (bash-oracle), `PsBash.Canary.Tests`, `PsBash.Escalation.Tests`.

## Where to find X

- **Map a bash command → cmdlet** → `PsEmitter.TryEmitMappedCommand` (Transpiler). Passthrough only; flags parsed in the runtime.
- **Implement/Parse a command's flags** → `Invoke-Bash*` in `PsBash.psm1`, or a `*Command.cs` binary cmdlet (Cmdlets).
- **Bash flag specs (completion)** → ONE source: `PsBash.Module/BashFlagSpecs.json` (host embeds it; psm1 loads it).
- **Interactive completion** → `Shell/CompletionEngine.cs` (orchestrator) → `TabCompleter` (static base) + runspace queries. Spec: `docs/specs/interactive-completion.md`.
- **AI command assist (Ctrl-^)** → `Shell/CommandAssistProvider.cs` + `CommandAssistReview.cs`; review loop in `InteractiveShell`. Spec: `docs/specs/command-assist.md`.
- **Compact output (`--compact-output`)** → `Runtime/OutputCompactor.cs` + `IpcWorker` buffering. Spec: `docs/specs/compact-output.md`.
- **Alias expansion** → `Shell/AliasExpander.cs`.
- **Host startup / runspace** → `Runtime/SdkRunspace.cs` + `Resources/SdkRunspaceSetup.ps1` (CommandNotFoundAction, module-autoload recovery).
- **IPC / host lifetime / timeouts** → `Runtime/IpcWorker.cs`, `Runtime/Ipc/*`.
- **Module/cmdlets extraction** → `Runtime/ModuleExtractor.cs`.

## Specs (deep reference)

Index: **`docs/specs/README.md`** — all specs, one line each (guard-enforced complete). The few
auto-loaded ones are `@`-linked in CLAUDE.md (parser-grammar, emitter-strategy, runtime-functions,
runtime-command-reference, interactive-completion); `runtime-migrated-cmdlets` is 126 KB, deliberately
not auto-loaded.

## Path-scoped rules (`.claude/rules/`, load by glob)

`parser.md`/`emitter.md` → Transpiler parser/emitter; `runtime.md`/`temp-files.md` → runtime;
`completion.md` → `Shell/**`; `testing.md`/`qa-rubric.md` → global; `findability.md` → global doctrine.
