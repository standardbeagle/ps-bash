Publish a new version of PsBash to PSGallery + NuGet.

This is the single source for the release process (CLAUDE.md only points here).

## The gate (know it before you tag)

`publish.yml` runs on the GitHub *release*. Its `publish` job `needs:` exactly TWO
blocking suites — the **Pester** suite (`tests/PsBash.Tests.ps1`) and **PsBash.Core.Tests**
(xunit). Everything else — Cmdlets.Tests, Shell.Tests, Differential.Tests, and every
`Skip report` step — is `continue-on-error: true` (non-fatal; a red skip-report does NOT
fail the gate and "errors" even on green releases). If the gate fails, the `publish` job
shows **skipped** and nothing reaches the feeds.

CRITICAL: a green local `dotnet test` is NOT enough. The Pester gate exercises paths the
xunit Cmdlets.Tests doesn't — **direct cmdlet calls** (`Invoke-BashEcho -e '...'`, which can
hit bare-flag binder collisions the transpiler's force-quoting hides) and **manifest
invariants** (`ReleaseNotes_UnderPsGalleryLimit`, cap 10600 chars). Run Pester locally first.

## Steps

1. **Update the psd1 ReleaseNotes** (this is the only manual psd1 edit that matters — the
   workflow auto-patches every *version*, see step 4). Prepend a `vX.Y.Z: ...` entry to
   `PrivateData.PSData.ReleaseNotes` in `src/PsBash.Module/PsBash.psd1`. The whole string is
   guard-capped at **10600 chars** — if you go over, trim your entry AND drop the oldest
   entries (the trailing version-history URL still links them). Verify:
   `(Import-PowerShellDataFile src/PsBash.Module/PsBash.psd1).PrivateData.PSData.ReleaseNotes.Length`.

2. **Run both blocking gates locally** (NOT just xunit):
   - **Pester (easy path): `./scripts/pester.ps1`** — builds PsBash.Cmdlets, refreshes the
     gitignored beside-module DLLs the psm1 probes for, and runs Invoke-Pester in a fresh
     child pwsh that first strips any INSTALLED PsBash from PSModulePath (so a stale
     user-scope copy can't shadow the source tree — otherwise dozens of ls/cat/grep tests
     fail locally for reasons unrelated to your change). Flags: `-Detailed`, `-Filter
     <wildcard>`, `-SkipBuild` (psm1-only edits load from source → instant re-run). Green
     baseline ≈ 1068 passed / 0 failed. Expect 0 failed.
   - Manual fallback: `dotnet build src/PsBash.Cmdlets/PsBash.Cmdlets.csproj -c Debug`, copy
     `bin/Debug/net8.0/{PsBash.Cmdlets,PsBash.Transpiler,Parlot}.dll` → `src/PsBash.Module/`,
     then `pwsh -NoProfile -c "Import-Module Pester; Invoke-Pester ./tests/ -Output Minimal"`.
   - Core.Tests (the ReleaseNotes/manifest guards): `dotnet test src/PsBash.Core.Tests`.
   - (scripts/test.sh is the canonical xunit runner but is often blocked in this env — see the
     running-tests memory; the commands above are the gate-equivalent subset.)
   Abort if either gate has a failure.

3. **Pick the version.** `gh release list --repo standardbeagle/ps-bash --limit 1`. Use the
   user's version if given, else bump the patch. Confirm no stale draft release for that tag.

4. **Tag = deploy** (the workflow patches `ModuleVersion` in the psd1 AND `<Version>` in both
   PsBash.Core/PsBash.Transpiler csprojs from the tag at publish time, so you do NOT need to
   edit any version by hand — only the ReleaseNotes in step 1). Commit the ReleaseNotes edit,
   then push the branch and ONLY the new tag:
   `git tag vX.Y.Z && git push origin main && git push origin vX.Y.Z`.
   - NEVER `git push --tags`: it pushes every local tag, and `publish.yml` runs on ANY `v*` tag
     push — a stale local tag (v0.10.24, 2026-10-08) started a publish of an old commit.
   - Do NOT `gh release create`: the workflow's `release-tag` job creates the release for the
     pushed tag. A release created by hand fires the `release` event = a SECOND publish run of
     the same version (racing PSGallery / asset uploads). Set the notes AFTER the run creates
     the release: `gh release edit vX.Y.Z --repo standardbeagle/ps-bash --notes-file notes.md`
     (derived from `git log <last-tag>..HEAD --oneline`, grouped fix/feat/etc.).

5. **Watch publish.yml to completion** — exactly ONE run for the tag (`event: push`):
   `gh run list --repo standardbeagle/ps-bash --workflow=publish.yml --limit 3` then
   `gh run watch <id> --exit-status`. A second run for the same version (`event: release`) or a run
   for another tag means step 4 went wrong — `gh run cancel` it before its `publish` job starts.
   All build-binaries (×3) + test (×3) + publish must be green. Verify
   `Find-Module PsBash | Select Version` shows the new version.

## If the gate fails (fix-forward, no version burned)

When `publish` was **skipped**, nothing reached PSGallery/nuget, so the version is reusable:
diagnose from `gh run view --job <id> --log`, fix on `main`, then
`gh release delete vX.Y.Z --yes --cleanup-tag`, `git tag -d vX.Y.Z`, re-tag the SAME version
at the fix commit, and push main + that tag (the tag push re-creates the release; then re-apply
the notes with `gh release edit`). Before tagging, build locally: Transpiler/Core generate XML
docs and the binaries job builds with `/warnaserror`, so a stale `<param>`/`<paramref>` or a
missing `<param>` fails all three binaries. Each attempt usually surfaces one real failure (e.g. a
direct-cmdlet-call binder collision, or ReleaseNotes overflow).

If binaries succeeded but only the PSGallery publish failed, re-run just that job:
`gh workflow run publish.yml -f version=X.Y.Z`.
