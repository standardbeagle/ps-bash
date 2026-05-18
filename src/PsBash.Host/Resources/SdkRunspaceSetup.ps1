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

# Cmdlets DLL is normally pre-registered in the InitialSessionState by
# SdkRunspace.Create — when that succeeded the C# side sets
# $PsBashCmdletsAlreadyLoaded = $true and the entire probe+import block
# below is skipped (host startup #9: saves ~300 ms cold-runspace JIT cost
# for the first Get-Command call plus the Import-Module body).
# The fallback path stays for callers that bypass ISS pre-reg — e.g. a
# third-party SDK runspace that dot-sources this script directly.
if (-not $PsBashCmdletsAlreadyLoaded -and
    $PsBashCmdletsDllPath -and (Test-Path $PsBashCmdletsDllPath) -and
    -not (Get-Command Invoke-BashBasename -CommandType Cmdlet -ErrorAction SilentlyContinue)) {
    Import-Module $PsBashCmdletsDllPath -Force -ErrorAction SilentlyContinue -DisableNameChecking
}

# RC-8c: bash-style unknown-command handling.
#
# Bash semantics:
#   - Unknown command → exit code 127.
#   - stderr message "bash: <name>: command not found" — suppressible by `2>/dev/null`.
#   - Shell continues to next statement (unless `set -e`).
#
# PowerShell default: CommandNotFoundException is a terminating .NET exception that
# does NOT set $LASTEXITCODE, and the resulting ErrorRecord is written at a scope
# that the transpiled `2>$null` on a simple command does not suppress.
#
# Fix: install a CommandNotFoundAction handler at the runspace level. When PS fails
# to resolve a command, we substitute a scriptblock that:
#   1. Writes a bash-formatted error to the (redirectable) error stream via Write-Error.
#   2. Sets $global:LASTEXITCODE = 127 so `$?` and subsequent `echo $?` see 127.
# The substitute runs in place of the missing command, so `2>$null` on the simple
# command still applies to the Write-Error output — `2>/dev/null` correctly silences it.
$ExecutionContext.InvokeCommand.CommandNotFoundAction = {
    param($CommandName, $EventArgs)
    # Skip if another handler already supplied a substitute.
    if ($EventArgs.StopSearch) { return }
    # Skip empty / null names (e.g. lookup probes from completion).
    if ([string]::IsNullOrEmpty($CommandName)) { return }
    # Capture the name into the scriptblock closure so the substitute knows
    # which command was missing.
    $name = $CommandName
    $EventArgs.CommandScriptBlock = {
        $msg = "bash: ${name}: command not found"
        Write-Error -Message $msg -Category ObjectNotFound -TargetObject $name
        $global:LASTEXITCODE = 127
    }.GetNewClosure()
    $EventArgs.StopSearch = $true
}.GetNewClosure()
