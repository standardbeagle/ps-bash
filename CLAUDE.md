# ps-bash project instructions

**Navigation: read @CODE_MAP.md first** (projects, key files, where to find X).

## Architecture

`bash → BashLexer → BashParser → PsEmitter (PsBash.Transpiler) → IpcWorker → ps-bash-host/SdkWorker → Invoke-Bash* runtime`

**Passthrough principle:** the emitter maps a command name (`head` → `Invoke-BashHead`) and
forwards every argument unchanged. Flag parsing lives in the runtime cmdlet. Never translate
bash flags to PowerShell parameters in the emitter.

## The Bash tool IS ps-bash (dogfood)

The Bash tool runs `~/.local/bin/bash.exe` = the **installed release, not your build**. If
`echo $BASH_VERSION` shows `4.4.x` it silently fell back to Git Bash (setup: `docs/agent-setup.md`).
- Not WSL, not Git Bash: `$PATH` is a Windows path string; distro-only tools need `wsl.exe -d Ubuntu-24.04`.
- `PSBASH_UNIX_PATHS=1`: `/c/…`, `/mnt/c/…` operands become `C:\…`; `/home/x` is not rewritten.
- Its cwd is separate from the PowerShell tool's — use absolute paths across tools.
- Run `wsl.exe -- bash -lc '…'` from the Bash tool, single-quoted (PowerShell mangles `$`/`\`).

Before reporting a ps-bash bug: check `docs/specs/intentional-differences.md`; confirm against
the oracle `wsl.exe -d Ubuntu-24.04 -- bash -c '<snippet>'`; re-run against the current build.

## Build / test: always `tman`

Never bare `dotnet build` / `dotnet test` (leaks MSBuild nodes/testhosts; two at once corrupt `src/*/bin`).

```bash
tman build | tman test | tman test-proj src/PsBash.Core.Tests [--filter "..."] | tman pester
tman ls --all | tman status <id> | tman kill all
```

Test verbs build first in the same job; all aliases share one serialized lock plus a
machine-wide 2-slot gate (`PSBASH_TMAN_MACHINE_SLOTS`). Quote any `--filter` containing `|`.
Don't pass `-x:y`-style MSBuild switches through tman. Never trust results gathered while
another build ran. Kill only DEV-BUILD `ps-bash-host`/`ps-bash` processes (under repo `bin`)
on MSB3021 — never the `~/.local/bin` ones.

## CI push discipline

Each push fires ~9 CI jobs. Batch commits; don't push fixup loops. `**/*.md`, `docs/spikes/**`,
`docs/solutions/**`, `.worktrack/**` skip CI via `paths-ignore`. Ask before pushing if unsure.

## Release

Use the `/publish` skill (`.claude/commands/publish.md`) — it is the release process.

## Specs (reference, not auto-loaded — read on demand)

Index: `docs/specs/README.md`. Most used: `parser-grammar.md`, `emitter-strategy.md`,
`runtime-functions.md`, `runtime-command-reference.md` (per-command flags), `interactive-completion.md`.
