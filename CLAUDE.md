# ps-bash project instructions

## Architecture

```
bash input → BashLexer → BashParser → PsEmitter → IpcWorker → ps-bash-host/SdkWorker → Invoke-Bash* runtime
```

- **Lexer/Parser**: tokenizes and parses bash into an AST modeled on Oils syntax.asdl
- **Emitter**: maps bash commands to `Invoke-Bash*` functions via **passthrough** — forwards all args, never translates flags
- **Runtime**: PowerShell module (`PsBash.psm1`) with full bash-compatible flag parsing in each function

## The Passthrough Principle

The emitter maps command names (e.g., `head` → `Invoke-BashHead`) and forwards all arguments unchanged. The runtime functions handle all flag parsing. Never translate bash flags to PowerShell parameters in the emitter.

## Running Tests

Always use `scripts/test.sh` instead of `dotnet test` directly.
It shuts down MSBuild server nodes and testhost processes on exit.

```bash
./scripts/test.sh                          # all tests
./scripts/test.sh --filter "MyTest"        # specific test
./scripts/test.sh src/PsBash.Core.Tests    # specific project
```

Do NOT use bare `dotnet test ...` — it leaks MSBuild worker nodes and testhost processes.

## CI Push Discipline

Every push to `main` and every PR fires three workflows (Build, CI Pester, Canary)
across a 3-OS matrix — up to **9 jobs per push**. Multiply by N commits per task
and CI minutes evaporate fast. Rules:

1. **Batch commits.** Don't push after every tiny edit. Group related changes into
   one commit before pushing.
2. **Bookkeeping-only commits are auto-skipped** via `paths-ignore` in
   `.github/workflows/{build,ci,canary}.yml`. Paths that DO NOT trigger CI:
   - `.dartai/**`, `.dartai-locks.json` (loop state, claims)
   - `docs/spikes/**`, `docs/solutions/**`
   - `**/*.md` (READMEs, changelogs, plans)

   Use `[skip ci]` in the commit message ONLY if you also touch a code path and
   know the change is genuinely doc-only (e.g. inline doc comment edits inside a
   .cs file). Default: trust `paths-ignore` and don't add `[skip ci]`.
3. **Concurrency cancels superseded runs** — pushing a new commit cancels the
   in-progress run for the same ref. Don't push hot loops of fixup commits.
4. **Loop driver commit pattern** (claim → work → release): the claim and release
   commits touch only `.dartai-locks.json` and are filtered out automatically. Only
   the work commit (which touches actual code) triggers CI. Keep it that way — do
   not add other paths to claim/release commits.

If you're unsure whether a change needs CI, ask before pushing.

## Release Process

### 1. Bump version and update notes

Edit `src/PsBash.Module/PsBash.psd1`:
- Update `ModuleVersion` (e.g. `'0.8.1'` → `'0.8.2'`)
- Prepend new entry to `ReleaseNotes` (format: `v0.8.2: description. v0.8.1: ...`)

Edit `src/PsBash.Core/PsBash.Core.csproj`:
- Update `<Version>` to match the module manifest version

Alternatively, run `pwsh scripts/pack-local.ps1` which syncs the csproj version from the manifest and packs into `dist/`.

### 2. Run all tests

```bash
./scripts/test.sh
```

Fix any failures before proceeding.

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

All three jobs must pass: `build-binaries` (3 matrix jobs), `test` (3 OS matrix), `publish`.

If any job fails:
```bash
gh run view <run-id> --log-failed
```

Fix the issue, bump to a new patch version, and re-release.

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
- @docs/specs/runtime-migrated-cmdlets.md — REFACTOR-2 binary-cmdlet migration history
