<#
.SYNOPSIS
    Pre-flight audit for PowerShell common-parameter prefix collisions
    in binary cmdlets.

.DESCRIPTION
    PowerShell's parameter binder is case-insensitive and matches by prefix.
    Bash short flags like -d, -e, -v, -w that overlap with common parameters
    (-Debug, -ErrorAction, -Verbose, -WarningAction, etc.) get silently
    routed to the common parameter instead of the cmdlet's intended flag.

    The canonical fix is to declare each colliding flag as an explicit
    single-letter [Parameter] (e.g. `public SwitchParameter D` for -d,
    `public string? C` for -c VALUE). An exact-name match beats a
    common-parameter prefix match in the binder's resolution.

    This script reflection-scans PsBash.Cmdlets.dll, lists every cmdlet's
    declared parameters, cross-references their short forms against the
    common-parameter table, and emits a markdown table of every collision
    plus whether a workaround is already in place.

    Run before migrating a new cmdlet to catch ~60% of post-merge bugs
    that I found by trial-and-error in REFACTOR-2 (touch -d, cut -d:,
    grep -E, sed -E, tar -C, rm -rf bundles, etc.).

.PARAMETER Dll
    Path to PsBash.Cmdlets.dll. Defaults to the project's Debug net8.0 output.

.PARAMETER ShowOk
    Include cmdlets with no collisions in the output (default: only show
    cmdlets that have at least one collision risk).
#>
[CmdletBinding()]
param(
    [string]$Dll,
    [switch]$ShowOk,
    [switch]$IncludeUnreferenced
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# Scan every cmdlet's .cs source for bash-style short flags it actually
# expects (e.g. `case "-c":`, `arg == "-d"`, `if (a == "-rf")`). Without
# this filter we'd flag every common-parameter prefix on every cmdlet,
# even when the bash command doesn't take that flag at all.
$sourceDir = Join-Path $repo 'src/PsBash.Cmdlets'
$referencedFlags = @{}
if (Test-Path -LiteralPath $sourceDir) {
    foreach ($file in Get-ChildItem $sourceDir -Filter 'InvokeBash*Command.cs') {
        $content = Get-Content -Raw -LiteralPath $file.FullName
        # Cmdlet name is derived from the filename:
        # InvokeBashCatCommand.cs -> Invoke-BashCat
        $cmdName = $file.BaseName -replace 'Command$', '' -replace '^InvokeBash', 'Invoke-Bash'
        $set = New-Object System.Collections.Generic.HashSet[string] (
            [System.StringComparer]::OrdinalIgnoreCase)
        # Match "-x" or "-xx" tokens inside string literals.
        $matches = [regex]::Matches($content, '"-([a-zA-Z]{1,3})"')
        foreach ($m in $matches) { [void]$set.Add($m.Groups[1].Value) }
        # Match `--long` long-form references to bash flags too.
        $longMatches = [regex]::Matches($content, '"--([a-zA-Z][a-zA-Z0-9-]*)"')
        foreach ($m in $longMatches) { [void]$set.Add('--' + $m.Groups[1].Value) }
        $referencedFlags[$cmdName] = $set
    }
}

if (-not $Dll) {
    $Dll = Join-Path $repo 'src/PsBash.Cmdlets/bin/Debug/net8.0/PsBash.Cmdlets.dll'
}
if (-not (Test-Path -LiteralPath $Dll)) {
    throw "PsBash.Cmdlets.dll not found at $Dll. Run dotnet build first or pass -Dll."
}

# Reflection-load into a fresh AppDomain-equivalent: just load the assembly
# into the current pwsh and read attributes. ReflectionOnly load isn't
# necessary because we just want the [Cmdlet] / [Parameter] metadata.
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

# PowerShell's common parameters (the ones the binder will prefix-match
# against). Single-letter prefixes derived from these are the collision zone.
$commonParams = @(
    'Verbose', 'Debug',
    'ErrorAction', 'ErrorVariable',
    'WarningAction', 'WarningVariable',
    'InformationAction', 'InformationVariable',
    'OutBuffer', 'OutVariable',
    'PipelineVariable', 'ProgressAction',
    'WhatIf', 'Confirm'
)
# Build a map: prefix-letter -> set of common-parameter names that share it.
$prefixMap = @{}
foreach ($cp in $commonParams) {
    for ($i = 1; $i -le $cp.Length; $i++) {
        $pfx = $cp.Substring(0, $i).ToLowerInvariant()
        if (-not $prefixMap.ContainsKey($pfx)) {
            $prefixMap[$pfx] = New-Object System.Collections.Generic.HashSet[string]
        }
        [void]$prefixMap[$pfx].Add($cp)
    }
}

# PSCmdlet-derived classes only.
$cmdletTypes = $asm.GetTypes() | Where-Object {
    $_.BaseType -and $_.BaseType.FullName -eq 'System.Management.Automation.PSCmdlet'
}

# Known short bash flags worth auditing (a-z A-Z 0-9).
# We treat any one-character [Parameter] name as a potential short flag.
$rows = New-Object System.Collections.Generic.List[object]
foreach ($t in $cmdletTypes) {
    $cmdletAttr = $t.GetCustomAttributes(
        [System.Management.Automation.CmdletAttribute], $false) | Select-Object -First 1
    if (-not $cmdletAttr) { continue }
    $cmdName = "$($cmdletAttr.VerbName)-$($cmdletAttr.NounName)"

    # Collect declared single-letter parameter names (potential bash short flags).
    $declaredShort = New-Object System.Collections.Generic.HashSet[string] (
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($prop in $t.GetProperties([System.Reflection.BindingFlags]::Public -bor
                                       [System.Reflection.BindingFlags]::Instance)) {
        $paramAttr = $prop.GetCustomAttributes(
            [System.Management.Automation.ParameterAttribute], $false) | Select-Object -First 1
        if (-not $paramAttr) { continue }
        if ($prop.Name.Length -eq 1) {
            [void]$declaredShort.Add($prop.Name)
        }
    }

    # Cross-reference: for each single-char prefix in the binder's common-param
    # table, is the corresponding short flag DECLARED on this cmdlet?
    $issues = New-Object System.Collections.Generic.List[object]
    foreach ($letter in 'abcdefghijklmnopqrstuvwxyz'.ToCharArray()) {
        $pfx = $letter.ToString()
        if ($prefixMap.ContainsKey($pfx)) {
            $hitCommon = ($prefixMap[$pfx]) -join ','
            $declared = $declaredShort.Contains($pfx.ToUpperInvariant())
            $issues.Add([pscustomobject]@{
                Letter      = $pfx
                CollidesWith = $hitCommon
                Declared    = $declared
            })
        }
    }

    $rows.Add([pscustomobject]@{
        Cmdlet    = $cmdName
        Type      = $t.FullName
        Issues    = $issues
        Declared  = $declaredShort
    })
}

# Emit markdown table — one row per cmdlet × per collision letter,
# annotated with whether the collision is handled (Declared = $true).
Write-Host '# Binder-collision audit'
Write-Host ''
Write-Host '| Cmdlet | -flag | Common-param prefix-match | Handled? |'
Write-Host '|--------|-------|---------------------------|----------|'

$totalRisks = 0
$totalHandled = 0
$totalSkippedUnreferenced = 0
foreach ($r in $rows) {
    $refs = if ($referencedFlags.ContainsKey($r.Cmdlet)) { $referencedFlags[$r.Cmdlet] } else { $null }
    foreach ($iss in $r.Issues) {
        # Only flag a collision if the cmdlet actually expects that bash flag.
        # The source-scan check filters out 'every cmdlet collides with -v
        # because -Verbose is a common parameter' noise — most cmdlets don't
        # take -v at all.
        $referenced = $refs -and $refs.Contains($iss.Letter)
        if (-not $referenced -and -not $IncludeUnreferenced) {
            $totalSkippedUnreferenced++
            continue
        }
        if (-not $iss.Declared -and -not $ShowOk) {
            Write-Host ('| {0} | -{1} | {2} | **NO** |' -f $r.Cmdlet, $iss.Letter, $iss.CollidesWith)
            $totalRisks++
        } elseif ($iss.Declared) {
            $totalHandled++
            if ($ShowOk) {
                Write-Host ('| {0} | -{1} | {2} | yes |' -f $r.Cmdlet, $iss.Letter, $iss.CollidesWith)
            }
        }
    }
}

Write-Host ''
Write-Host '## Summary'
Write-Host ("- Cmdlets scanned: {0}" -f $rows.Count)
Write-Host ("- Collisions handled (single-letter Parameter declared): {0}" -f $totalHandled)
Write-Host ("- **Unhandled risks**: {0}" -f $totalRisks)
Write-Host ''
Write-Host '> A "NO" row means the cmdlet does NOT declare an explicit short-letter'
Write-Host '> parameter for the bash flag. If the cmdlet expects to receive that'
Write-Host '> flag (check its `Arguments` parsing), it will silently be eaten by the'
Write-Host '> common parameter listed in the table — the fix is to add'
Write-Host '> `[Parameter] public SwitchParameter X` (boolean) or'
Write-Host '> `[Parameter] public string? X` (value-bearing) where X is the bash letter.'
