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
- Oracle: `wsl.exe -d Ubuntu-24.04 -e bash -c '<snippet>'` (or `-e bash file.sh`). Use `-e`, NEVER
  `--`: `--` re-parses the line in the Linux login shell first (`$?`/`$HOME` expand, `\` stripped).

Before reporting a ps-bash bug: check `docs/specs/intentional-differences.md`; confirm against
the oracle; re-run against the current build (`tman dev`).

## Build / test: always `tman`

Never bare `dotnet build` / `dotnet test` (leaks MSBuild nodes/testhosts; two at once corrupt `src/*/bin`).

```bash
tman build | tman test | tman test-proj src/PsBash.Core.Tests [--filter "..."] | tman pester
tman dev -c '<snippet>' | tman dev script.sh      # run THIS checkout's dev ps-bash
tman ls --all | tman status <id> | tman kill all
```

Never run `src/*/bin/**/ps-bash.exe` directly: it takes no lock, so a concurrent build swaps DLLs
under it, and its leftover daemon holds `bin/` (next build MSB3021). `tman dev` builds, takes the
checkout lock, and uses a per-invocation host. In an agent worktree without it: set
`PSBASH_PER_INVOCATION=1` and probe only between tman jobs.

Test verbs build first in the same job; all aliases (and `tman dev`) share one serialized lock
plus a machine-wide 1-slot gate (`PSBASH_TMAN_MACHINE_SLOTS`): builds and tests never overlap,
across every checkout and agent worktree. Queued jobs wait; don't work around the queue. Quote any `--filter` containing `|`.
Don't pass `-x:y`-style MSBuild switches through tman. Never trust results gathered while
another build ran. Kill only DEV-BUILD `ps-bash-host`/`ps-bash` processes (under repo `bin`)
on MSB3021 — never the `~/.local/bin` ones.

## Linux builds / tests: fleet boxes, through tman's queue

Linux runs go to **build3** (32 cores) first and **build2** (= beagle-inspiron, 8 cores) second, always through
`scripts/remote-test.sh` (`--help`; no pwsh there, so `<cmd>` is plain bash, not a tman alias):

```bash
scripts/remote-test.sh build3 -- 'dotnet test src/PsBash.Core.Tests -c Debug --filter "FullyQualifiedName~Sed"'
scripts/remote-test.sh --dirty build3 -- 'dotnet build ps-bash.sln -c Debug'   # ship uncommitted work
scripts/remote-test.sh done build3        # LAST step of every agent's work: removes its remote worktree
scripts/remote-test.sh ls build3 | scripts/remote-test.sh prune [--dry-run] build3   # idle >= 24h, never live
```

- `<cmd>` runs as `~/.local/bin/tman run --queue default --label agent=<name>` in `~/work/scratch/ps-bash/<name>`,
  a worktree off one shared bare repo per host fed by `git push`. tman admits by measured cores and memory and
  queues the rest: never read `/proc/loadavg`, sleep, or hop hosts to find a quiet moment.
- Start it with the Bash tool's `run_in_background` and wait for the completion notification. No `sleep` loops,
  no polling marker files or logs. Exit = `<cmd>`'s status; 75 = tman stopped it (queue timeout, `tman kill`): re-run.
- No clones, rsyncs, tarballs or bundles into your own `~/work/agent-*`. One worktree per agent (`--name`, default
  this checkout's directory name), reused across runs so builds stay incremental; `--rm` for a one-off.
- Any other ssh work on a fleet box goes under `~/.local/bin/tman run --queue default --label agent=<name> -- …`
  (full path: `~/.local/bin` is not on a non-interactive ssh PATH).
- Windows-only gates (Pester, spec, Shell/Host) stay on beagle-ab via `docs/spikes/xfail-tools/fleet/fleet-gate.ps1`.

## CI push discipline

Each push fires ~9 CI jobs. Batch commits; don't push fixup loops. `**/*.md`, `docs/spikes/**`,
`docs/solutions/**`, `.worktrack/**` skip CI via `paths-ignore`. Ask before pushing if unsure.

## Release

Use the `/publish` skill (`.claude/commands/publish.md`) — it is the release process.

## Specs (reference, not auto-loaded — read on demand)

Index: `docs/specs/README.md`. Most used: `parser-grammar.md`, `emitter-strategy.md`,
`runtime-functions.md`, `runtime-command-reference.md` (per-command flags), `interactive-completion.md`.
