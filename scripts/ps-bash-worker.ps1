#Requires -Version 7.0
param([string]$ModulePath = '')

# Load ps-bash module: explicit path > PSBASH_MODULE env > system ps-bash
$resolvedModule = if ($ModulePath) { $ModulePath }
                  elseif ($env:PSBASH_MODULE) { $env:PSBASH_MODULE }
                  else { $null }

if ($resolvedModule -and (Test-Path $resolvedModule)) {
    Import-Module $resolvedModule -ErrorAction Stop
} else {
    Import-Module ps-bash -ErrorAction SilentlyContinue
}

# Signal ready
[Console]::Out.WriteLine("<<<READY>>>")
[Console]::Out.Flush()

# Command loop
while ($true) {
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
            $result | Out-String -Stream | ForEach-Object {
                [Console]::Out.WriteLine($_)
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
}
