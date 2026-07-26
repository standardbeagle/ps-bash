# Bug: the shipped host publish contained no PowerShell — `PrivateAssets` on the SDK reference

**Found:** 2026-07-26, while installing a local build over the stale `~/.local/bin` one
**Severity:** High — the documented release command produced a host that could not run a single command
**Fixed:** removed `<PrivateAssets>all</PrivateAssets>` from `Microsoft.PowerShell.SDK` in `PsBash.Host.csproj`

## Symptom

`dotnet publish src/PsBash.Host -c Release -r win-x64 --self-contained true` — the exact
command `publish.yml` ships with (lines 56-61) — emitted a host directory with **no
`System.Management.Automation.dll` at all**. The resulting host starts and binds its
endpoint, then fails every command with:

```
ps-bash-host worker failure: Could not load file or assembly
'System.Management.Automation, Version=7.4.6.500, ...'. The system cannot find the file specified.
```

From the launcher's side that surfaces as the far less helpful
`ps-bash: ps-bash-host did not accept connections within 20s.` — the health handshake never
completes, so the real error is only visible if you connect to a manually started host.

## Root cause

`PsBash.Host.csproj` carried `<PrivateAssets>all</PrivateAssets>` on the
`Microsoft.PowerShell.SDK` reference. On the SDK meta-package that **excludes its runtime
assets from publish output** — the published `ps-bash-host.deps.json` had zero mentions of
`Microsoft.PowerShell` or `System.Management.Automation` (the installed 0.10.19 had 12).
Only the package's RID-specific *content* leaked through, which is why the publish tree
contained `runtimes/unix/lib/net8.0/Modules/Microsoft.PowerShell.*/*.psd1` but none of the
managed assemblies.

The attribute is idiomatic on a bare `System.Management.Automation` PackageReference, which
this used to be (`edebbfc`). When `bb8291f` upgraded it to `Microsoft.PowerShell.SDK` —
whose commit message says the point was "so all runtime DLLs are included in the output" —
the attribute came along and silently defeated exactly that.

## Why no test or build caught it

Every ordinary build and test run is blind to this:

- A dev/CI `dotnet build` resolves SMA from the NuGet cache through
  `runtimeconfig.dev.json` probing paths, so `bin/Debug/**` works fine *without* SMA beside
  the binary (it genuinely is not there — 57 DLLs, no SMA).
- The in-process host tests get SMA from the **test project's own** SDK reference.
- `PublishHostSidecar` in `PsBash.Shell.csproj` builds the host `SelfContained=true` and
  copies that BUILD output, which does include SMA — so publishing the **Shell** always
  worked. Only the standalone host publish was broken.
- The release archives are produced by `build-binaries`, which the `publish` job does not
  `need:` (see `release-build-binaries-warnaserror`), so a broken archive ships silently
  while PSGallery/NuGet succeed.

## Fix

One line removed, plus a comment in the csproj explaining why it must not come back.
`dotnet publish src/PsBash.Host --self-contained` now emits 264 DLLs including
`System.Management.Automation.dll` (7623480 bytes).

Verified by running `publish.yml`'s exact recipe end to end — publish launcher, publish
host, stage the launcher exe, copy host output over it — then executing a command through
the staged archive: `recipe-works | 3`, exit 0.

(The local run used `PublishAot=false` because this box cannot AOT-compile — ILCompiler's
native link step cannot find `vswhere.exe`. That deviation is in the LAUNCHER only, so it
also meant staging the launcher's `.dll` alongside its apphost; `publish.yml` correctly
stages only `ps-bash.exe` because a real AOT launcher is a single self-contained file. The
host publish, which is what changed, was exercised unmodified.)

## Blast radius and cleanup (2026-07-26)

**Every** release archive of the two-binary era was non-functional, not just the recent ones.
Audited all 28 releases that had assets, by reading each `win-x64` zip's entry list:
**28/28 contained `ps-bash-host` but no `System.Management.Automation.dll`.** Cross-platform
spot-checks at the earliest, middle and latest points (`v0.9.1`, `v0.10.13`, `v0.10.22` ×
`linux-x64`, `osx-arm64`) were all broken too — expected, since the fault is in one host
publish command per workflow run and is therefore RID-independent, but verified rather than
assumed.

Range: **v0.9.1 → v0.10.22**. The two-binary layout began at v0.9.0 (`19df856`, 2026-05-01)
and `PrivateAssets` predates it (`edebbfc`, 2026-04-29), so no two-binary archive was ever
correct. v0.7.x/v0.8.x archives are a single `ps-bash.exe` with no host and are unaffected —
`v0.8.20`'s zip has exactly one entry.

**All 84 assets were deleted** (28 releases × 3 platforms, zero failures) on the owner's
instruction, leaving those releases with notes and no downloads. Repairing them was rejected
on provenance grounds: an archive attached to tag `vX` must be built from `vX`'s source, and
every affected tag still contains the bug, so a `gh workflow run publish.yml -f version=X
--ref main` re-upload would hang main-built binaries off an old tag. The honest options were
empty or misleading; empty won.

Releases with no assets at all — v0.9.3-5, v0.9.8, v0.9.12, v0.10.5, v0.10.6, v0.10.17 —
were already missing them, consistent with the historically flaky `build-binaries` legs (see
`release-build-binaries-warnaserror`, `release-nu1903-warnaserror-binaries`).

## Confirmed: the shipped archives really were broken

Not a theoretical risk. Downloaded the published `ps-bash-v0.10.22-win-x64.zip` (the latest
release, 2026-07-18) straight from GitHub:

| | correct build | shipped v0.10.22 |
|---|---|---|
| DLLs at archive root | 265 | **195** |
| `System.Management.Automation.dll` | present (7623480 B) | **absent** |
| `ps-bash -c 'echo …'` | works | **exit 125**, `ps-bash-host did not accept connections within 20s` |

So every GitHub release archive from the introduction of the attribute through v0.10.22 was
non-functional — a user unzipping it could not run a single command. PSGallery and NuGet were
unaffected: the `publish` job builds the module separately and does not `need:` the
`build-binaries` job, which is exactly why nothing surfaced.

## Regression guard

`PsBash.Core.Tests/PublishRecipeGuardTests.cs` asserts the SDK PackageReference carries
neither `PrivateAssets` nor `ExcludeAssets`. It inspects only that one element, so the
attribute stays legal elsewhere (notably the Host **ProjectReference** in
`PsBash.Shell.csproj`, where it is correct). Verified red with the attribute restored, green
without. Core.Tests is a publish gate, so this now blocks a release rather than shipping.

A csproj-text assertion alone would be thin — it only catches this one spelling of the
mistake — so the archive is now also smoke-tested for real (below).

## Automatic archive check (`scripts/smoke-archive.ps1`)

No workflow executed what it packaged. `build.yml` published, ran `package-slim` /
`package-full`, and uploaded. `publish.yml` published, zipped, and uploaded. `ci.yml` runs
Pester in-process against the module. `canary.yml` uses `dotnet build` output, which resolves
SMA from the NuGet cache and is therefore structurally incapable of seeing a publish fault.
So nothing anywhere ran a packaged artifact.

`scripts/smoke-archive.ps1` is now that check, shared by both workflows (and runnable
locally) so it cannot drift between them:

- `publish.yml` → **Smoke-test staged archive** on `./publish/stage`, after packaging and
  *before* either upload step, on every RID.
- `build.yml` → **Smoke-test packaged archives** on `dist/slim/<rid>` and `dist/full/<rid>`,
  before the artifact uploads. This runs on every push, so a regression surfaces there long
  before a release.

It:

1. asserts the staged launcher and host binaries exist;
2. asserts `System.Management.Automation.dll` is present, failing with a message that names
   the `PrivateAssets`/`ExcludeAssets` cause — so a packaging regression is diagnosed rather
   than surfacing as an opaque connection timeout;
3. runs the archive the way a user would — `ps-bash -c 'echo smoke-ok; seq 1 3 | wc -l'`,
   letting the launcher spawn the host out of the same directory — and requires exit 0 plus
   both expected outputs. The endpoint is randomized so it can never adopt a stray host.

Verified both directions locally against the real artifacts: the script FAILS the downloaded,
broken v0.10.22 archive (exit 1, with the actionable message) and PASSES a correct staged
build (`smoke-ok`, `3`, exit 0).

## The same bug was in every local packaging script

`pack-local.ps1`, `package-slim.ps1` and `package-full.ps1` all consume a **separately
published** `PsBash.Host` (`dist/host/<rid>`), i.e. the identical broken pattern — so they
produced broken output too, and the same one-line csproj fix repairs all of them.

This also explains how a WORKING build came to be installed at `~/.local/bin` while every
release archive was broken: its `ps-bash-host.deps.json` is 88829 bytes with 12
`System.Management.Automation` mentions — the same as a fixed build, versus 33721 bytes and 0
mentions for a broken one. So it came from a path that included the SDK assets (publishing
`PsBash.Shell`, whose `PublishHostSidecar` builds the host `SelfContained=true` and copies
that output), never from the standalone host publish those scripts use.
