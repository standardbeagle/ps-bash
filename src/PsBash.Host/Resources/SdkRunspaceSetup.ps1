# SdkRunspaceSetup.ps1
#
# Runspace setup script dot-sourced by SdkRunspace.Create after the runspace
# is opened. Pre-loads PsBash.Cmdlets.dll from the installed PsBash module so
# Invoke-BashSource and hook cmdlets are available without triggering the slow
# $PSModulePath auto-loading scan. Silent no-op when PsBash is not installed
# (e.g. in the test environment).
#
# Inputs (session-state variables set by the C# caller BEFORE dot-source):
#   $PsBashCmdletsDllPath  - optional explicit override path to PsBash.Cmdlets.dll
#                            (string, may be $null). When set and the file
#                            exists, the script imports it directly and skips
#                            the Get-Module probe.
#
# Outputs: none (the Cmdlets module is imported into the runspace).
#
# Known gap: PsBash.Cmdlets ships as its own PSGallery module, but the
# workflow change in publish.yml now bundles PsBash.Cmdlets.dll inside the
# staged PsBash.Cmdlets PSGallery package alongside its psd1. The probe below
# currently looks in the PsBash module dir only — the legacy bundled layout —
# so users on the new PSGallery layout still need the dll copied beside
# PsBash.psd1 for the cmdlet path to work. Static-eval inlining in
# PsEmitter.EmitEval covers the cases where PowerShell expanded $() before
# ps-bash saw the input (the user-reported scenario).
#
# Attempting to add a second probe for `PsBash.Cmdlets -ListAvailable` here
# surfaced a host-startup deadlock on Windows pwsh 7.x SDK that wedged even
# simple `echo hi` invocations. Left for a future change with deeper
# investigation; documented here so the gap is not silently rediscovered.

# Path 1: explicit override path passed in from C#. Preferred — skips probe.
if ($PsBashCmdletsDllPath -and (Test-Path $PsBashCmdletsDllPath)) {
    Import-Module $PsBashCmdletsDllPath -Force -ErrorAction SilentlyContinue -DisableNameChecking
    return
}

# Path 2: probe the installed PsBash module dir for PsBash.Cmdlets.dll.
$__psbashModule = Get-Module PsBash -ListAvailable -ErrorAction SilentlyContinue |
    Sort-Object Version -Descending | Select-Object -First 1
if ($__psbashModule) {
    $__psbashCmdletsDll = Join-Path (Split-Path $__psbashModule.Path) 'PsBash.Cmdlets.dll'
    if (Test-Path $__psbashCmdletsDll) {
        Import-Module $__psbashCmdletsDll -Force -ErrorAction SilentlyContinue -DisableNameChecking
    }
}
Remove-Variable -Name __psbashModule, __psbashCmdletsDll -ErrorAction SilentlyContinue
