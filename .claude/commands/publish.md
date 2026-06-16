Publish a new version of PsBash to PSGallery + NuGet.

Detailed reference: the "Release Process" section of CLAUDE.md. This command is the
operational checklist; keep the two in sync.

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
   - Build + refresh the gitignored beside-module DLLs the psm1 probes first, or the Pester
     run loads stale code and reproduces nothing:
     `dotnet build src/PsBash.Cmdlets/PsBash.Cmdlets.csproj -c Debug`, then copy
     `bin/Debug/net8.0/{PsBash.Cmdlets,PsBash.Transpiler,Parlot}.dll` → `src/PsBash.Module/`.
   - Pester: `pwsh -NoProfile -c "Import-Module Pester; Invoke-Pester ./tests/ -Output Minimal"`
     (self-isolates via `tests/EnsureCleanRunspace.ps1`). Expect 0 failed.
   - Core.Tests (the ReleaseNotes/manifest guards): `dotnet test src/PsBash.Core.Tests`.
   - (scripts/test.sh is the canonical runner but is often blocked in this env — see the
     running-tests memory; the two commands above are the gate-equivalent subset.)
   Abort if either gate has a failure.

3. **Pick the version.** `gh release list --repo standardbeagle/ps-bash --limit 1`. Use the
   user's version if given, else bump the patch. Confirm no stale draft release for that tag.

4. **Tag + release** (the workflow patches `ModuleVersion` in the psd1 AND `<Version>` in both
   PsBash.Core/PsBash.Transpiler csprojs from the tag at publish time, so you do NOT need to
   edit any version by hand — only the ReleaseNotes in step 1). Commit the ReleaseNotes edit,
   then `git tag vX.Y.Z && git push origin main --tags`, then
   `gh release create vX.Y.Z --repo standardbeagle/ps-bash --title "vX.Y.Z" --notes "<notes>"`
   (release notes derived from `git log <last-tag>..HEAD --oneline`, grouped fix/feat/etc.).

5. **Watch publish.yml to completion**:
   `gh run watch <id> --exit-status` (or `gh run list --workflow=publish.yml --limit 1`).
   All build-binaries (×3) + test (×3) + publish must be green. Verify
   `Find-Module PsBash | Select Version` shows the new version.

## If the gate fails (fix-forward, no version burned)

When `publish` was **skipped**, nothing reached PSGallery/nuget, so the version is reusable:
diagnose from `gh run view --job <id> --log`, fix on `main`, then
`gh release delete vX.Y.Z --yes --cleanup-tag`, re-tag the SAME version at the fix commit,
push, and re-create the release. Each attempt usually surfaces one real failure (e.g. a
direct-cmdlet-call binder collision, or ReleaseNotes overflow).
