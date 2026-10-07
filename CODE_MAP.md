# CODE_MAP — ps-bash structural index

Evergreen nav map (loaded every session — keep small; edit on structure moves only). Detail → `docs/specs/*`.

## Pipeline

`bash → BashLexer → BashParser → AST → PsEmitter → BashTranspiler` (**PsBash.Transpiler**)
`→ IpcWorker` (**Core**) `→ ps-bash-host` (**Host**: SdkWorker, one SDK runspace) `→ Invoke-Bash*` (**Module** psm1 + **Cmdlets**).
Launcher `ps-bash.exe` (**Shell**) talks IPC to `ps-bash-host.exe` (**Host**).

## Projects

| Project | Role | Key files |
|---|---|---|
| **Transpiler** | bash → PS | `Parser/BashLexer.cs`, `Parser/BashParser{,.Simple,.Words}.cs`, `Parser/Ast/*`, `Parser/PsEmitter.cs`, `Parser/FusedLane.cs`, `Parser/PsBuild.cs`, `Transpiler/BashTranspiler.cs` |
| **Core** | IPC + module plumbing | `Runtime/IpcWorker.cs`, `Runtime/ModuleExtractor.cs`, `Runtime/Compaction/*`, `Runtime/Ipc/*` |
| **Host** | SDK runspace + interactive shell | `Runtime/{SdkRunspace,SdkWorker,WorkerPool}.cs`, `Resources/SdkRunspaceSetup.ps1`, `Shell/*` (LineEditor, CompletionEngine, …) |
| **Cmdlets** | ~100 binary `Invoke-Bash*` (**commands live here**) | `*Command.cs`, `Args/*`, `LineStream/*`, `BashRuntime.cs`, `FileSystemHelpers.cs`, `Media/*` |
| **Module** | psm1: aliases, BashObject, job control | `PsBash.psm1`, `BashFlagSpecs.json` (single flag-spec source) |
| **Shell** | AOT launcher | `Program.cs`, `Args.cs` |
| **Testing** | harness | `CanonicalEnv`, `PsBashRunner`, `ProcessSpawn` |

Tests: `*.Tests`, `Differential.Tests` (bash oracle), `Canary.Tests`, `Escalation.Tests`.

## Where to find X

- **bash cmd → cmdlet**: `PsEmitter.TryEmitMappedCommand` (passthrough only).
- **Emitted PS text** (quoting, exit-code, `[void]`, splat): `Parser/PsBuild.cs` — never hand-concat in PsEmitter.
- **Command flags**: `Cmdlets/*Command.cs`; shared argv parser `Cmdlets/Args/{ArgParser,OptSpec,ParsedArgs}.cs` + emitter opt-in `PsEmitter.OrderedArgCommands`.
- **Destructive FS / spawn**: `FileSystemHelpers.Delete*Force`/`ClearReadOnly`; `BashRuntime.RunChildProcess`.
- **Glob**: `PsEmitter.IsGlobWord` → `ConvertToBashGlobCommand.cs` → `BashGlob.cs`; shopt in `InvokeBashShoptCommand`.
- **Compound-command stdin**: `PsEmitter._inStdinScope`, `PsBuild.StdinScope`, `Parser/StdinReaders.cs`, `Cmdlets/SharedStdin.cs`; natives get it as process stdin via `Cmdlets/NativeStdinBridge.cs`.
- **Launcher stdin into `-c`**: `Shell/Program.cs`, `HostProtocol` STDIN frames, `Server/LauncherStdinFeed.cs`, `Transpiler/StdinCursor.cs`.
- **Fused pipeline**: `FusedLane.cs` → `InvokeBashFusedPipelineCommand.cs` → `LineStreamStages.cs` + `LineStream/*`.
- **External-tool wrapper** (psav/ffmpeg ref): `InvokeBash{Tool}Command.cs` + `Media/*` (plan first, run second).
- **diff**: `DiffEngine.cs`, `DiffFormat.cs`, `DiffPlan.cs`. **jq**: `Jq*.cs`. **sort**: `SortEngine.cs`. **cut**: `CutPlan.cs`.
- **Completion**: `Shell/CompletionEngine.cs` → `TabCompleter`. **Aliases**: `Shell/AliasExpander.cs`. **History `!!`**: `Shell/HistoryExpander.cs`.
- **AI assist**: `Shell/CommandAssist*.cs`. **z/zi**: `Shell/SqliteFrecencyStore.cs`.
- **Compact output**: `Runtime/Compaction/OutputCompactor.cs`.
- **Host startup / autoload**: `SdkRunspace.cs` + `SdkRunspaceSetup.ps1`.
- **Host must survive input** (recursion bound, host log, stuck-command poison): `Parser/NestingGuard.cs` (every recursive descent enters it), `Host/Server/HostLog.cs`, `Host/Runtime/StuckCommandWatchdog.cs`. Spec: `host-lifecycle-contract.md` §No input may take the host down.
- **Daemon / IPC / pool / nesting / spawn lock**: `IpcWorker.cs`, `Ipc/*` (`WindowsHostSpawn.cs` = no handle inheritance, `HostSpawnLock.cs`), `WorkerPool.cs`. Spec: `host-lifecycle-contract.md`.
- **Bytes vs text**: `Transpiler/RawBytes.cs` at every text↔bytes boundary — never `Encoding.UTF8` on data; streams `Cmdlets/ByteRecordStreams.cs`.
- **Embedded DLL extraction**: `ModuleExtractor.cs` (list = `EmbedCmdletsDll` in `PsBash.Core.csproj`).
- **Strata**: `src/Strata.props`.

## Specs & rules

Specs index: `docs/specs/README.md` (read on demand, never `@`-link). Path-scoped rules in `.claude/rules/` load by glob.
