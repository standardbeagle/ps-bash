# ps-bash project instructions

## Architecture

```
bash input → BashLexer → BashParser → PsEmitter → PwshWorker → Invoke-Bash* runtime
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

## Release Process

### 1. Bump version and update notes

Edit `src/PsBash.Module/PsBash.psd1`:
- Update `ModuleVersion` (e.g. `'0.8.1'` → `'0.8.2'`)
- Prepend new entry to `ReleaseNotes` (format: `v0.8.2: description. v0.8.1: ...`)

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
- @docs/specs/runtime-functions.md — BashObject model, command reference, temp files
