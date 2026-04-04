#Requires -Version 7.0
param()

# Load ps-bash module
$modulePath = Join-Path $PSScriptRoot "Modules" "ps-bash"
if (Test-Path $modulePath) {
    Import-Module $modulePath -ErrorAction Stop
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
        [Console]::Out.WriteLine("<<<EXIT:0>>>")
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        [Console]::Out.WriteLine("<<<EXIT:1>>>")
    } finally {
        [Console]::Out.Flush()
    }
}
