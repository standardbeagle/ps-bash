---
name: pester
description: Run the release-blocking Pester gate (tests/PsBash.Tests.ps1) the reliable way — build cmdlets, refresh beside-module DLLs, isolate PSModulePath from a stale installed PsBash, run Invoke-Pester.
---

# PESTER GATE. One of the TWO release-blocking suites (the other is Core.Tests). Ref: [[release-pester-gate-local]] memory, `.claude/commands/publish.md`.

文言：一命跑之——`./scripts/pester.ps1`；建cmdlets、刷旁DLL、去陳裝PsBash之PSModulePath、跑之。psm1獨改用 `-SkipBuild`。綠準1068/0。

## RUN

`./scripts/pester.ps1`  — does the whole dance (build PsBash.Cmdlets → copy `net8.0/{PsBash.Cmdlets,PsBash.Transpiler,Parlot}.dll` beside the module → Invoke-Pester in a fresh child pwsh that STRIPS any installed PsBash from PSModulePath first).

Flags: `-SkipBuild` (psm1-only edits load from source → instant re-run), `-Detailed`, `-Filter '<wildcard>'`.

## WHY (not a bare Invoke-Pester)

1. The psm1 loads the binary cmdlets from **gitignored DLLs beside the module** (`src/PsBash.Module/*.dll`). A stale copy → the run loads old code and reproduces nothing.
2. A **stale installed PsBash** on the user module path (OneDrive `…\PowerShell\Modules\PsBash`) auto-loads and shadows the source tree — dozens of ls/cat/grep tests fail for reasons unrelated to your change (58 red before isolation, 0 after). The script strips it BEFORE Pester runs; `tests/EnsureCleanRunspace.ps1` alone can leak it in at discovery time. See [[stale-installed-psbash-shadows-tests]].

## EXPECT

Green baseline ≈ **1068 passed, 0 failed, 6 skipped** (~54 s). Any failure = abort (do not tag). Then also `dotnet test src/PsBash.Core.Tests` (the ReleaseNotes/manifest guard — the OTHER blocking gate).

## GOTCHA

The Pester gate exercises **direct cmdlet calls** (`Invoke-BashEcho -e …`) and **manifest invariants** that xunit never touches, so a green `dotnet test` is NOT enough to release. And a per-line "improvement" to a widely-consumed psm1 helper can break the gate wholesale — e.g. making `Show-BashHelp` emit per-line records broke every `--help` test. Keep single-object contracts single-object.
