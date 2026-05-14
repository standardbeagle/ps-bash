# SdkRunspaceSetup.ps1
#
# Runspace setup script dot-sourced by SdkRunspace.Create after the runspace
# is opened. Imports PsBash.Cmdlets.dll so Invoke-BashSource and the hook
# cmdlets are available without triggering the slow $PSModulePath auto-loading
# scan.
#
# Canonical module-load path (REFACTOR-5): PsBash.Cmdlets.dll is embedded as a
# manifest resource in PsBash.Core and extracted by ModuleExtractor alongside
# PsBash.psm1. SdkRunspace.Create computes that one deterministic path via
# ModuleExtractor.GetCmdletsDllPath and hands it to this script. There is no
# probe — no Get-Module -ListAvailable, no beside-host-binary search. Both of
# those probe styles had a host-startup deadlock history on Windows pwsh 7.x
# SDK (commits f18bedd, 6f264eb) that wedged even simple `echo hi` invocations.
# A known-path Import-Module has no such surface area.
#
# Inputs (session-state variables set by the C# caller BEFORE dot-source):
#   $PsBashCmdletsDllPath  - absolute path to the extracted PsBash.Cmdlets.dll
#                            (string). When the file exists it is imported;
#                            when it does not (e.g. an older Core build with
#                            no embedded cmdlets, or extraction skipped) the
#                            script is a silent no-op so host startup never
#                            blocks on a missing optional dependency.
#
# Outputs: none (the Cmdlets module is imported into the runspace).

if ($PsBashCmdletsDllPath -and (Test-Path $PsBashCmdletsDllPath)) {
    Import-Module $PsBashCmdletsDllPath -Force -ErrorAction SilentlyContinue -DisableNameChecking
}
