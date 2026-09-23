# ps-bash project instructions

**Navigation: read @CODE_MAP.md first** — the static structural index (projects, key files, where to find X). A compressed top-of-context map out-navigates on-demand search; see @.claude/rules/findability.md for the doctrine.

## Architecture

```
bash input → BashLexer → BashParser → PsEmitter → IpcWorker → ps-bash-host/SdkWorker → Invoke-Bash* runtime
```
(Parser + emitter live in **PsBash.Transpiler**, not PsBash.Core.)

- **Lexer/Parser**: tokenizes and parses bash into an AST modeled on Oils syntax.asdl
- **Emitter**: maps bash commands to `Invoke-Bash*` functions via **passthrough** — forwards all args, never translates flags
- **Runtime**: PowerShell module (`PsBash.psm1`) with full bash-compatible flag parsing in each function

## The Passthrough Principle

The emitter maps command names (e.g., `head` → `Invoke-BashHead`) and forwards all arguments unchanged. The runtime functions handle all flag parsing. Never translate bash flags to PowerShell parameters in the emitter.

## The Bash tool IS ps-bash (dogfood)

The Bash tool runs `~/.local/bin/ps-bash.exe` — the **installed release, not your build**
(`BASH_VERSION` reports `0.8.0(1)-release`). Consequences:

- **It is not WSL and not Git Bash.** `$PATH` is a native Windows path string
  (`C:\...;C:\...`), never `/mingw64/bin`. Anything installed only inside the distro
  (docker, `wslpath`, `cygpath`) is absent — reach it via `wsl.exe -d Ubuntu-24.04`,
  not the Bash tool.
- **`PSBASH_UNIX_PATHS=1` is set by the wrapper**, so `/c/…` and `/mnt/c/…` operands are
  rewritten to `C:\…` before any cmdlet sees them (`PsEmitter.TryTranslateMsysDrivePath`
  → `WindowsPath.TryMapUnixDrivePath`). Paths with no drive component (`/home/x`) are NOT
  rewritten. Correct for Windows-side tools; wrong if the path is meant to be consumed
  *inside* the distro.
- **cwd persists across Bash-tool calls and is tracked separately from the PowerShell
  tool's cwd.** A relative path built under one and used in the other resolves wrong — and
  `git log -- <bad path>` returns empty rather than erroring, so it reads as "no history".
  Use absolute paths when crossing tools.
- **For `wsl.exe -- bash -lc "…"`, invoke from the Bash tool, not PowerShell.** PowerShell
  interpolates `$VAR` inside double quotes and leaves a bare `\`, producing
  `line N: \: command not found`. ps-bash passes `\$` through correctly. Prefer
  single-quoted `-lc '…'` either way.

### Before reporting a ps-bash bug

1. **Confirm against the oracle**: `wsl.exe -d Ubuntu-24.04 -- bash -c '<snippet>'`.
   Faithful bash behavior is not a bug — e.g. `alias <missing-name>` writing
   `alias: NAME: not found` to stderr is exactly what bash does.
2. **Confirm the symbol/commit exists HERE**: `git cat-file -t <sha>`, `git grep <sym>`.
   Consumer projects that embed PsBash keep their own memory; their notes are not ps-bash
   facts.
3. **Re-run the repro against the current build.** Several long-"known broken" items
   (for-loop pipes, `/c/` path handling) now pass.

## Running Tests

Run builds and tests through **`tman`** (config: `.tman.kdl`). Never bare `dotnet build` /
`dotnet test` — they leak MSBuild worker nodes and testhost processes, and two of them at
once corrupt the shared `src/*/bin` outputs.

```bash
tman build                                        # dotnet build -c Debug -f net10.0
tman test                                         # full suite
tman test-proj src/PsBash.Core.Tests              # one project
tman test-proj src/PsBash.Cmdlets.Tests --filter "FullyQualifiedName~Fused"
tman ls --all | tman status <id> | tman kill all  # inspect / stop runs
```

Why tman and not `dotnet` directly:

1. **Kill-tree on exit** — no orphaned MSBuild/testhost survives a run (the job
   `scripts/test.sh` was written for; that script still works but is not the default).
2. **`max-parallel 1`** — serializes build/test in this directory. Concurrent builds
   against the shared `src/*/bin/Debug/net10.0` outputs are the documented root cause of
   this repo's suite flakiness: a half-written test bin can't start a runspace, so spawned
   hosts die with "stream closed before EXIT sentinel" or hang 30s with no output. Never
   raise this to chase speed.
3. **`stall 5m` / `max-time 45m`** — a wedged run is killed instead of hanging the session.

**Never trust suite results gathered while another build was running.** If you must check,
`tman ls` shows live runs. Quote any `--filter` containing `|` — an unquoted pipe becomes a
shell pipe and hangs.

Orphaned DEV-BUILD `ps-bash-host` / `ps-bash` processes (path under the repo's `bin`) lock
output DLLs and cause MSB3021 on the next build. Kill only those — **never** the
`~/.local/bin` ones, which serve the Bash tool.

## CI Push Discipline

Every push to `main` and every PR fires three workflows (Build, CI Pester, Canary)
across a 3-OS matrix — up to **9 jobs per push**. Multiply by N commits per task
and CI minutes evaporate fast. Rules:

1. **Batch commits.** Don't push after every tiny edit. Group related changes into
   one commit before pushing.
2. **Bookkeeping-only commits are auto-skipped** via `paths-ignore` in
   `.github/workflows/{build,ci,canary}.yml`. Paths that DO NOT trigger CI:
   - `.worktrack/**` (workspace binding `mcp.json` + template manifest `templates.json`)
   - `docs/spikes/**`, `docs/solutions/**`
   - `**/*.md` (READMEs, changelogs, plans)

   Use `[skip ci]` in the commit message ONLY if you also touch a code path and
   know the change is genuinely doc-only (e.g. inline doc comment edits inside a
   .cs file). Default: trust `paths-ignore` and don't add `[skip ci]`.
3. **Concurrency cancels superseded runs** — pushing a new commit cancels the
   in-progress run for the same ref. Don't push hot loops of fixup commits.
4. **Worktrack loop state is not in git.** Claims/leases live in the worktrack DB
   (workspace `ps-bash`, bound by `.worktrack/mcp.json`), so the loop makes no
   bookkeeping commits — only work commits, which touch code and trigger CI.
   Template edits go in `.worktrack/templates.json` and are committed: the daemon
   applies the manifest at start and on a repo's first binding, and it overwrites
   edits made through the template verbs. `worktrack-mcp doctor` reports drift.

If you're unsure whether a change needs CI, ask before pushing.

## Release Process

> Operational checklist: the `/publish` skill (`.claude/commands/publish.md`). Keep the two in sync.

### 1. Update the ReleaseNotes (the only required manual edit)

`publish.yml` **auto-patches every version from the release tag** — `ModuleVersion` in
`PsBash.psd1` and `<Version>` in both `PsBash.Core.csproj` / `PsBash.Transpiler.csproj` — so
you do NOT need to bump versions by hand (bumping is harmless hygiene if you want the source to
match). What you MUST do: prepend a `vX.Y.Z: description.` entry to `ReleaseNotes` in
`src/PsBash.Module/PsBash.psd1`. The whole string is **guard-capped at 10600 chars**
(`ReleaseNotes_UnderPsGalleryLimit`, PSGallery 400s above it) — if you go over, trim your entry
and drop the oldest entries (the trailing version-history URL still links them).

### 2. Run the gate locally — Pester + Core.Tests, not just xunit

The publish gate is the **Pester** suite (`tests/PsBash.Tests.ps1`) + **Core.Tests**; the other
suites and every `Skip report` step are `continue-on-error` (non-fatal). A green
`scripts/test.sh` / xunit run is NOT enough — Pester calls cmdlets directly
(`Invoke-BashEcho -e '...'`), hitting bare-flag binder collisions and manifest invariants xunit
never touches. Run Pester locally first (refresh the gitignored beside-module DLL, then
`Invoke-Pester ./tests/`) — see the **release-pester-gate-local** memory and the `/publish`
skill for the exact commands. Fix any failure before proceeding.

### 3. Commit, tag, push

```bash
git add -A
git commit -m "Release 0.8.2 — <short description>"
git tag v0.8.2
git push origin main --tags
```

### 4. Create GitHub release

```bash
gh release create v0.8.2 --title "v0.8.2" --notes "<description>"
```

This triggers the **Publish Release** workflow which:
- Builds AOT binaries for win-x64, linux-x64, osx-arm64
- Uploads zip archives to the GitHub release
- Runs Pester tests across all platforms
- Publishes the module to PSGallery
- Publishes PsBash.Core NuGet package to nuget.org (requires `NUGET_API_KEY` secret)

### 5. Verify GitHub Actions

```bash
gh run list --workflow=publish.yml --limit 1
```

Check the run status. If in progress, watch it:

```bash
gh run watch
```

All jobs must pass: `build-binaries` (3 matrix), `test` (3 OS matrix), `publish`. A gate failure
shows `publish` as **skipped** (it `needs:` the test job).

If any job fails:
```bash
gh run view <run-id> --log-failed
```

Fix on `main`, then re-cut. Because a skipped `publish` never reached PSGallery/nuget, the
version is reusable: `gh release delete vX.Y.Z --yes --cleanup-tag`, re-tag the SAME version at
the fix commit, push, and re-create the release (no patch number burned).

### 6. Verify PSGallery publication

```powershell
Find-Module PsBash | Select-Object Version
```

Confirm the new version appears. If PSGallery publish failed but binaries succeeded,
you can re-run just the publish job:

```bash
gh workflow run publish.yml -f version=0.8.2
```

## Specs

- @docs/specs/parser-grammar.md — tokens, AST nodes, grammar productions, Oils gap analysis
- @docs/specs/emitter-strategy.md — passthrough principle, pipe mappings, anti-patterns
- @docs/specs/runtime-functions.md — BashObject model, arg-parsing patterns, escape handling, temp files, adding a command
- @docs/specs/runtime-command-reference.md — per-command flag / arg-parsing lookup table
- @docs/specs/interactive-completion.md — interactive shell Tab completion: engine, providers, the no-cursor-map PowerShell bridge, single flag-spec source
- docs/specs/command-assist.md — interactive AI command assist (Ctrl-^): provider config, prompt redaction, output contract, safety classifier, review loop (reference only; not auto-loaded — interactive-only feature, not needed for transpiler/runtime work)
- docs/specs/runtime-migrated-cmdlets.md — REFACTOR-2 binary-cmdlet migration history (reference only; not auto-loaded — it is ~126 KB and would dominate the session context window)
