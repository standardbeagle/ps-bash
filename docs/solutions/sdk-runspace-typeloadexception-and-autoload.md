---
name: sdk-runspace-typeloadexception-and-autoload
title: SDK runspace PSSnapIn TypeLoadException + module auto-load divergence (tnc)
description: Why the ps-bash SDK host force-loads Microsoft.PowerShell.Commands.* and pre-registers cmdlets, the real root cause of the v5 PSSnapIn TypeLoadException, and why `tnc` (and other auto-loadable aliases) resolve in plain pwsh but not in ps-bash.
tags: [host, sdk-runspace, powershell, module-autoload, typeloadexception, command-resolution]
date: 2026-05-23
status: root-cause-confirmed; tnc alias auto-load FIXED (CommandNotFoundAction discovery fallback); TypeLoadException cleaner fix still recommended (not yet implemented)
---

# SDK runspace: PSSnapIn TypeLoadException + module auto-load divergence

## Symptoms

1. **Original workaround trigger** (`SdkRunspace.RegisterSdkCmdlets`): without it, the first
   in-process cmdlet call (e.g. `Write-Output`) returns empty output with an error stream
   reading *"command was found in module 'Microsoft.PowerShell.Utility' but the module could
   not be loaded"* — a `TypeLoadException` on `PSSnapIn`.

2. **User-visible (Issue A):** `tnc worktrack -port 433` → `bash: get-tnc: command not found`;
   `test-networkconnection localhost` → `bash: test-networkconnection: command not found`.
   But `Test-NetConnection` (proper case) works.

## Root cause #1 — the PSSnapIn TypeLoadException (confirmed)

Chain of events:

1. `PsBash.Host.csproj` references `Microsoft.PowerShell.SDK` with `<PrivateAssets>all</PrivateAssets>`.
   This (plus the SDK package layout) means the cmdlet **implementation** assemblies
   `Microsoft.PowerShell.Commands.{Utility,Management,Security}.dll` are NOT copied to the
   top-level host bin folder — they live only under `runtimes/{rid}/lib/{tfm}/`.
2. `Assembly.Load(simpleName)` does **not** probe `runtimes/{rid}/lib/{tfm}/` — only the
   top-level app base. So when SMA needs `Microsoft.PowerShell.Commands.Utility` (to back
   `Write-Output`), the load fails.
3. SMA then falls back to **module auto-loading** for the missing command, walking the
   inherited `$PSModulePath`, which on Windows includes
   `C:\WINDOWS\System32\WindowsPowerShell\v1.0\Modules`.
4. There it finds the **Windows PowerShell 5.1** `Microsoft.PowerShell.Utility` manifest:
   - `PowerShellVersion = 5.1`
   - `CompatiblePSEditions = Desktop`
   - `NestedModules = Microsoft.PowerShell.Commands.Utility.dll` (the **v5** binary)
5. That v5 binary references `System.Management.Automation.PSSnapIn`, a type **removed in
   SMA 7.x** → `TypeLoadException`.

So the trigger is the SDK packaging (factor A: Commands.* unreachable by simple-name load),
and the *fatal* part is the inherited v5 Desktop module path (factor B).

### Current workaround (`SdkRunspace.cs`)

`RegisterSdkCmdlets` locates SMA via `typeof(PSObject).Assembly.Location`, `Assembly.LoadFrom`s
the three `Microsoft.PowerShell.Commands.*` DLLs from the SDK runtime store, then scans the
AppDomain and pre-registers every discovered cmdlet into a custom `InitialSessionState`. The
user runspace therefore never has to auto-load the core cmdlets, so it never hits the v5
manifest. It treats factor A by brute force (hard-codes 3 assembly names) and sidesteps factor B.

### Recommended cleaner fix (not yet implemented)

Install an `AssemblyLoadContext.Default.Resolving` (or `AppDomain.CurrentDomain.AssemblyResolve`)
handler in the host that resolves **any** `Microsoft.PowerShell.Commands.*` / SMA-sibling
assembly from the SDK's `runtimes/{rid}/lib/{tfm}/` directory. Then `Assembly.Load(simpleName)`
succeeds, SMA never falls back to the v5 manifest, and the broad pre-registration scan can be
reduced. Advantages over the current code: covers every SDK cmdlet assembly (not just the 3
hard-coded ones), and stops fighting module auto-loading in general.

**Risk:** host startup has a documented deadlock history (commits f18bedd, 6f264eb) around
module probing on Windows pwsh 7.x. Any change here must be tested against
`PsBash.Host.Tests` (the `QueryAsync_*` suite reproduced the original TypeLoadException) on
all three OSes before replacing the working workaround.

## Root cause #2 — `tnc` auto-load divergence (Issue A)

This is **related but distinct** from the TypeLoadException; the workaround does not cause it.

Findings (verified against `pwsh -NoProfile` as the oracle):

| Command | plain pwsh | ps-bash | Note |
|---|---|---|---|
| `Test-NetConnection` (proper) | works | works | parity |
| `test-networkconnection` (lower) | not recognized | not found | **parity** — PowerShell's own behaviour, not a ps-bash bug |
| `tnc` / `TNC` (alias) | works | `get-tnc` not found | **divergence** |

- NetTCPIP's manifest exports the alias **explicitly**: `AliasesToExport = gip, TNC`, and
  `Test-NetConnection` is in `FunctionsToExport`. So both are first-class exports.
- In ps-bash, after `Import-Module NetTCPIP` (or after any proper-cased command auto-loads
  the module), `tnc` resolves and runs (`Get-Command tnc` → `TNC`). So the module loads fine —
  only **alias-triggered auto-load never fires** in the SDK runspace, for either case.
- Function auto-load in the SDK runspace is **case-sensitive until the on-disk module analysis
  cache warms up**: early in testing, lowercase `test-networkconnection` failed while
  `Test-NetConnection` worked; later, after repeated loads populated
  `%LOCALAPPDATA%\Microsoft\Windows\PowerShell\ModuleAnalysisCache`, lowercase
  `get-netroute` resolved. Plain pwsh is case-insensitive from the start because it reads that
  cache; the SDK runspace's cold-cache fallback is an ordinal (case-sensitive) export scan.
- The `get-tnc` text is **PowerShell's own** noun-resolution artifact (no ps-bash code prepends
  `Get-`; the transpiled output is a clean `tnc localhost`).

### Fix shipped (option 2 — `CommandNotFoundAction` discovery fallback)

`SdkRunspaceSetup.ps1` now recovers from a command miss before emitting "command not found":

1. `Resolve-BashAutoloadModule` builds a **lazy, case-insensitive** index `command-name → module`
   from `Get-Module -ListAvailable` manifest exports (`ExportedAliases` / `ExportedFunctions` /
   `ExportedCmdlets`). Reading manifests does not load the modules, so it cannot trigger the
   PSSnapIn crash. Built once per runspace on the first miss; cached thereafter.
2. The handler tries the name as typed **plus** the de-mangled noun when PowerShell prepended
   the default `Get-` verb (`tnc` → `Get-tnc`). On an index hit it `Import-Module`s the owner,
   `Get-Command`s the name, and substitutes `& $resolved @args`.
3. A re-entrancy guard (`$script:BashAutoloadResolving`) prevents the index build / probe from
   recursing back into the handler.

Result (verified end-to-end + Host.Tests `ExecuteAsync_AutoloadableAlias_ResolvesViaDiscoveryFallback`):
`tnc` → `Test-NetConnection`, `gip` → `Get-NetIPAddress`, lowercase correct names resolve
(case-insensitive index, better than plain pwsh's cold cache), and genuine unknowns still exit 127
(`ExecuteAsync_GenuineUnknownCommand_StillExits127`). NOTE: `test-networkconnection` is a *misspelling*
(the cmdlet is `Test-NetConnection` — "Net", not "Network"), so it correctly stays unresolved.

**Cost:** the first unknown command per runspace pays a one-time `Get-Module -ListAvailable` scan
(~1–2 s cold). Subsequent misses are O(1). A future optimization could pre-build the index off the
hot path. The lowercase failures of the *correctly-spelled* name are now fixed; misspellings remain
faithful "command not found".

### Still recommended: cleaner TypeLoadException fix

The discovery fallback fixes the user-visible symptom but the TypeLoadException workaround
(`RegisterSdkCmdlets` force-load + pre-register-everything) remains. Replacing it with an
`AssemblyLoadContext.Resolving` handler (root cause #1 above) is still the cleaner long-term fix;
it touches host startup and needs the Host.Tests + 3-OS matrix gate.

## Verification commands

```powershell
$ps = "$env:USERPROFILE\.local\bin\ps-bash.exe"
& $ps --ps -c "Test-NetConnection localhost -InformationLevel Quiet"          # True
& $ps --ps -c "tnc localhost -InformationLevel Quiet"                          # get-tnc not found (bug)
& $ps --ps -c "Import-Module NetTCPIP; tnc localhost -InformationLevel Quiet"  # True (module loads fine)
```
