#Requires -Version 7.0
param([string]$ModulePath = '', [int]$ParentPid = 0)

# Load ps-bash module: explicit path > PSBASH_MODULE env > system ps-bash
$resolvedModule = if ($ModulePath) { $ModulePath }
                  elseif ($env:PSBASH_MODULE) { $env:PSBASH_MODULE }
                  else { $null }

if ($resolvedModule -and (Test-Path $resolvedModule)) {
    Import-Module $resolvedModule -ErrorAction Stop -DisableNameChecking
} else {
    Import-Module ps-bash -ErrorAction SilentlyContinue -DisableNameChecking
}

$global:LASTEXITCODE = 0
$global:__parentPid = $ParentPid
$global:__BashErrexit = $false
$global:__BashTrapEXIT = $null
$global:__BashTrapERR = $null

# Signal ready
[Console]::Out.WriteLine("<<<READY>>>")
[Console]::Out.Flush()

function Invoke-BashExitTraps {
    try {
        $mod = if ($resolvedModule -and (Test-Path $resolvedModule)) {
            Get-Module -Name (Get-Item $resolvedModule).BaseName -ErrorAction SilentlyContinue
        } else {
            Get-Module -Name 'PsBash' -ErrorAction SilentlyContinue
        }
        if (-not $mod) {
            # Fallback: find by path match
            $mod = Get-Module | Where-Object { $_.Path -and $_.Path.Contains('PsBash') } | Select-Object -First 1
        }
        if (-not $mod) { return }
        $handlers = $mod.Invoke({ $script:BashTrapHandlers })
        if (-not $handlers -or -not ($handlers -is [System.Collections.IDictionary])) { return }
        $exitAction = $handlers['EXIT']
        if (-not $exitAction) { return }
        if ($exitAction -is [System.Management.Automation.PSEventSubscriber]) {
            Unregister-Event -SubscriptionId $exitAction.SubscriptionId -ErrorAction SilentlyContinue
            $cmd = $exitAction.Action.Command
            if ($cmd) { Invoke-Expression $cmd }
        } elseif ($exitAction -is [string]) {
            Invoke-Expression $exitAction
        } elseif ($exitAction -is [scriptblock]) {
            & $exitAction
        }
    } catch {
        [Console]::Error.WriteLine("trap EXIT: $($_.Exception.Message)")
    }
}

# Command loop
while ($true) {
    # Parent death detection: if the parent process is gone, self-terminate
    if ($global:__parentPid -gt 0) {
        try {
            $null = [System.Diagnostics.Process]::GetProcessById($global:__parentPid)
        } catch {
            Invoke-BashExitTraps
            exit 0
        }
    }

    $lines = [System.Collections.Generic.List[string]]::new()

    while ($true) {
        $line = [Console]::In.ReadLine()
        if ($null -eq $line) {
            Invoke-BashExitTraps
            exit 0
        }
        if ($line -eq '<<<END>>>') { break }
        $lines.Add($line)
    }

    $command = $lines -join "`n"

    # Handle explicit exit so it terminates the worker process
    if ($command -match '^\s*exit\s+(\d+)\s*$') {
        exit [int]$matches[1]
    } elseif ($command -match '^\s*exit\s*$') {
        exit 0
    }

    try {
        # Agent-internal only: commands come from the transpiler, not user input
        $result = Invoke-Expression $command
        $__partialLine = $false
        if ($null -ne $result) {
            foreach ($item in @($result)) {
                if ($item -is [string]) {
                    [Console]::Out.WriteLine($item)
                    $__partialLine = $false
                } elseif ($null -ne $item.PSObject -and $null -ne $item.PSObject.Properties['BashText']) {
                    $raw = [string]$item.BashText
                    # Strip any trailing \n — BashText is stored clean; the serializer owns line endings.
                    # NoTrailingNewline property signals partial-line output (printf "%d ", echo -n, etc.)
                    $isPartial = $null -ne $item.PSObject.Properties['NoTrailingNewline'] -and [bool]$item.NoTrailingNewline
                    $text = if ($raw.EndsWith("`n")) { $raw.Substring(0, $raw.Length - 1) } else { $raw }
                    if ($isPartial) {
                        [Console]::Out.Write($text)
                        $__partialLine = $true
                    } else {
                        [Console]::Out.WriteLine($text)
                        $__partialLine = $false
                    }
                } else {
                    $item | Out-String -Stream | ForEach-Object {
                        [Console]::Out.WriteLine($_)
                    }
                    $__partialLine = $false
                }
            }
        }
        if ($__partialLine) { [Console]::Out.WriteLine() }
        $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 0 }
        if ($global:__BashErrexit -and $exitCode -ne 0) {
            $global:LASTEXITCODE = $exitCode
            [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
            exit $exitCode
        }
        $global:LASTEXITCODE = $exitCode
        [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        $exitCode = if ($LASTEXITCODE -ge 1) { $LASTEXITCODE } else { 1 }
        if ($global:__BashErrexit -and $exitCode -ne 0) {
            $global:LASTEXITCODE = $exitCode
            [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
            exit $exitCode
        }
        $global:LASTEXITCODE = $exitCode
        [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
    } finally {
        [Console]::Out.Flush()
    }

    # Memory watchdog: self-terminate if working set exceeds limit
    $ws = [System.Diagnostics.Process]::GetCurrentProcess().WorkingSet64
    $maxBytes = if ($env:PSBASH_MAX_MEMORY) { [long]$env:PSBASH_MAX_MEMORY * 1MB } else { 512MB }
    if ($ws -gt $maxBytes) {
        [Console]::Error.WriteLine("ps-bash: worker exceeded memory limit ($([math]::Round($ws/1MB))MB > $([math]::Round($maxBytes/1MB))MB)")
        Invoke-BashExitTraps
        exit 137
    }
}
