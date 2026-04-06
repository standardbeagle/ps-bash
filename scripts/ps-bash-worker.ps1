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

# Signal ready
[Console]::Out.WriteLine("<<<READY>>>")
[Console]::Out.Flush()

# Command loop
while ($true) {
    # Parent death detection: if the parent process is gone, self-terminate
    if ($ParentPid -gt 0) {
        try {
            $null = [System.Diagnostics.Process]::GetProcessById($ParentPid)
        } catch {
            exit 0
        }
    }

    $lines = [System.Collections.Generic.List[string]]::new()

    while ($true) {
        $line = [Console]::In.ReadLine()
        if ($null -eq $line) { exit 0 }
        if ($line -eq '<<<END>>>') { break }
        $lines.Add($line)
    }

    $command = $lines -join "`n"

    try {
        # Agent-internal only: commands come from the transpiler, not user input
        $result = Invoke-Expression $command
        if ($null -ne $result) {
            foreach ($item in @($result)) {
                if ($null -ne $item.PSObject -and $null -ne $item.PSObject.Properties['BashText']) {
                    $text = [string]$item.BashText -replace "`n$", ''
                    [Console]::Out.WriteLine($text)
                } else {
                    $item | Out-String -Stream | ForEach-Object {
                        [Console]::Out.WriteLine($_)
                    }
                }
            }
        }
        $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 0 }
        [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 1 }
        [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
    } finally {
        [Console]::Out.Flush()
    }

    # Memory watchdog: self-terminate if working set exceeds limit
    $ws = [System.Diagnostics.Process]::GetCurrentProcess().WorkingSet64
    $maxBytes = if ($env:PSBASH_MAX_MEMORY) { [long]$env:PSBASH_MAX_MEMORY * 1MB } else { 512MB }
    if ($ws -gt $maxBytes) {
        [Console]::Error.WriteLine("ps-bash: worker exceeded memory limit ($([math]::Round($ws/1MB))MB > $([math]::Round($maxBytes/1MB))MB)")
        exit 137
    }
}
