# --- sleep ---

function Invoke-BashSleep {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'sleep' }

    if ($Arguments.Count -eq 0) {
        Write-BashError -Message 'sleep: missing operand'
        return
    }

    $totalSeconds = 0.0
    foreach ($arg in $Arguments) {
        $multiplier = 1.0
        $numStr = $arg
        if ($arg -match '^([\d.]+)([smhd])$') {
            $numStr = $Matches[1]
            switch ($Matches[2]) {
                's' { $multiplier = 1.0 }
                'm' { $multiplier = 60.0 }
                'h' { $multiplier = 3600.0 }
                'd' { $multiplier = 86400.0 }
            }
        }
        $val = 0.0
        if (-not [double]::TryParse($numStr, [ref]$val)) {
            Write-BashError -Message "sleep: invalid time interval '$arg'"
            return
        }
        if ($val -lt 0) {
            Write-BashError -Message "sleep: invalid time interval '$arg'"
            return
        }
        $totalSeconds += $val * $multiplier
    }

    if ($totalSeconds -gt 0) {
        $ms = [int]([Math]::Ceiling($totalSeconds * 1000))
        Start-Sleep -Milliseconds $ms
    }
}

# --- time ---

function Invoke-BashTime {
    [OutputType('PsBash.TimeOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'time' }

    if ($Arguments.Count -eq 0) {
        Write-BashError -Message 'time: missing command'
        return
    }

    $cmd = $Arguments[0]
    $cmdArgs = @()
    if ($Arguments.Count -gt 1) {
        $cmdArgs = $Arguments[1..($Arguments.Count - 1)]
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $exitCode = 0
    $outputText = ''
    try {
        $output = @(& $cmd @cmdArgs 2>&1)
        $sw.Stop()
        $errors = @($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $normal = @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
        foreach ($e in $errors) { Write-BashError -Message "$e" }
        if ($errors.Count -gt 0) { $exitCode = 1 }
    } catch {
        $sw.Stop()
        Write-BashError -Message $_.Exception.Message
        $exitCode = 1
        $errors = @($_)
        $normal = @()
    }
    try {
        $textParts = @(foreach ($item in $normal) {
            if ($item.PSObject.Properties['BashText']) { $item.BashText } else { "$item" }
        })
        $outputText = $textParts -join "`n"
    } catch {
        $sw.Stop()
        $exitCode = 1
        Write-BashError -Message "$_"
    }

    $realTime = $sw.Elapsed
    $formatted = 'real    {0:N3}s' -f $realTime.TotalSeconds
    [Console]::Error.WriteLine($formatted)

    $obj = [PSCustomObject]@{
        PSTypeName = 'PsBash.TimeOutput'
        RealTime   = $realTime
        Command    = $cmd
        ExitCode   = $exitCode
        BashText   = $outputText
    }
    Set-BashDisplayProperty $obj
}

# --- which ---

function Invoke-BashWhich {
    [OutputType('PsBash.WhichOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'which' }

    $showAll = $false
    $operands = [System.Collections.Generic.List[string]]::new()
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-a') { $showAll = $true }
        else { $operands.Add($arg) }
    }

    if ($operands.Count -eq 0) {
        Write-BashError -Message 'which: missing operand'
        return
    }

    foreach ($name in $operands) {
        $cmds = @(Get-Command $name -ErrorAction SilentlyContinue)
        if ($cmds.Count -eq 0) {
            Write-BashError -Message "which: no $name in PATH"
            continue
        }

        $toShow = if ($showAll) { $cmds } else { @($cmds[0]) }
        foreach ($c in $toShow) {
            $path = if ($c.Source) { $c.Source }
                    elseif ($c.Definition) { $c.Definition }
                    else { $name }
            $type = $c.CommandType.ToString().ToLower()
            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.WhichOutput'
                Command    = $name
                Path       = $path
                Type       = $type
                BashText   = $path
            }
            Set-BashDisplayProperty $obj
        }
    }
}

# --- alias / unalias (module mode: dynamic function creation) ---

$script:BashUserAliases = [System.Collections.Generic.Dictionary[string,string]]::new(
    [System.StringComparer]::Ordinal
)

function Invoke-BashAlias {
    [OutputType('PsBash.AliasOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'alias' }

    $unaliasMode = $false
    $removeAll = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-u') {
            $unaliasMode = $true
        } elseif ($arg -ceq '-a' -and $unaliasMode) {
            $removeAll = $true
        } elseif ($arg -ceq '-p') {
        } else {
            $operands.Add($arg)
        }
        $i++
    }

    if ($unaliasMode) {
        if ($removeAll) {
            foreach ($name in @($script:BashUserAliases.Keys)) {
                Invoke-Expression "Remove-Item 'Function:\$name' -Force -ErrorAction SilentlyContinue"
            }
            $script:BashUserAliases.Clear()
            return
        }
        foreach ($name in $operands) {
            if (-not $script:BashUserAliases.ContainsKey($name)) {
                Write-BashError -Message "unalias: ${name}: not found"
                continue
            }
            Invoke-Expression "Remove-Item 'Function:\$name' -Force -ErrorAction SilentlyContinue"
            $script:BashUserAliases.Remove($name) | Out-Null
        }
        return
    }

    if ($operands.Count -eq 0) {
        foreach ($kvp in $script:BashUserAliases.GetEnumerator()) {
            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.AliasOutput'
                Name       = $kvp.Key
                Value      = $kvp.Value
                BashText   = "alias $($kvp.Key)='$($kvp.Value)'"
            }
            Set-BashDisplayProperty $obj
        }
        return
    }

    foreach ($arg in $operands) {
        if ($arg -match '^([^=]+)=(.*)$') {
            $aliasName = $Matches[1]
            $aliasValue = $Matches[2]
            $script:BashUserAliases[$aliasName] = $aliasValue
            $body = [scriptblock]::Create("& $aliasValue @args")
            Invoke-Expression "function global:$aliasName { & $aliasValue @args }"
        } else {
            if ($script:BashUserAliases.ContainsKey($arg)) {
                $val = $script:BashUserAliases[$arg]
                $obj = [PSCustomObject]@{
                    PSTypeName = 'PsBash.AliasOutput'
                    Name       = $arg
                    Value      = $val
                    BashText   = "alias $arg='$val'"
                }
                Set-BashDisplayProperty $obj
            } else {
                Write-BashError -Message "alias: ${arg}: not found"
            }
        }
    }
}

# --- Prompt and Chpwd Hooks ---
# Emitter translates PROMPT_COMMAND='cmd' -> Register-BashPromptHook -Name 'prompt-command' -ScriptBlock { ... }
# and trap '...' DEBUG -> Register-BashChpwdHook -Name '...' -ScriptBlock { ... }.
# InteractiveShell calls Invoke-BashPromptHooks after each command and Invoke-BashChpwdHooks when cwd changes.

$script:BashPromptHooks = [System.Collections.Generic.Dictionary[string,scriptblock]]::new(
    [System.StringComparer]::Ordinal
)

$script:BashChpwdHooks = [System.Collections.Generic.Dictionary[string,scriptblock]]::new(
    [System.StringComparer]::Ordinal
)

function Register-BashPromptHook {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$ScriptBlock)
    $script:BashPromptHooks[$Name] = $ScriptBlock
}

function Unregister-BashPromptHook {
    param([Parameter(Mandatory)][string]$Name)
    [void]$script:BashPromptHooks.Remove($Name)
}

function Invoke-BashPromptHooks {
    foreach ($hook in @($script:BashPromptHooks.Values)) {
        try { & $hook } catch { [Console]::Error.WriteLine("ps-bash: PROMPT_COMMAND: $($_.Exception.Message)") }
    }
}

function Register-BashChpwdHook {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$ScriptBlock)
    $script:BashChpwdHooks[$Name] = $ScriptBlock
}

function Unregister-BashChpwdHook {
    param([Parameter(Mandatory)][string]$Name)
    [void]$script:BashChpwdHooks.Remove($Name)
}

function Invoke-BashChpwdHooks {
    foreach ($hook in @($script:BashChpwdHooks.Values)) {
        try { & $hook } catch { [Console]::Error.WriteLine("ps-bash: chpwd hook: $($_.Exception.Message)") }
    }
}

# --- trap ---

$script:BashTrapHandlers = [System.Collections.Generic.Dictionary[string,object]]::new(
    [System.StringComparer]::OrdinalIgnoreCase
)

function Invoke-BashTrap {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'trap' }

    if ($Arguments.Count -eq 0) {
        foreach ($kvp in $script:BashTrapHandlers.GetEnumerator()) {
            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.TrapOutput'
                Signal     = $kvp.Key
                Action     = $kvp.Value
                BashText   = "trap -- '$($kvp.Value)' $($kvp.Key)"
            }
            Set-BashDisplayProperty $obj
        }
        return
    }

    if ($Arguments.Count -eq 1 -and $Arguments[0] -ceq '-l') {
        $signals = @('EXIT', 'ERR', 'INT', 'TERM', 'HUP', 'QUIT', 'PIPE', 'ALRM', 'USR1', 'USR2')
        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.TrapOutput'
            Signal     = $null
            Action     = $null
            BashText   = ($signals -join ' ')
        }
        Set-BashDisplayProperty $obj
        return
    }

    $action = $null
    $signals = [System.Collections.Generic.List[string]]::new()
    $resetMode = $false

    if ($Arguments[0] -ceq '-' -or $Arguments[0] -ceq '--') {
        $resetMode = $true
        for ($i = 1; $i -lt $Arguments.Count; $i++) {
            $signals.Add($Arguments[$i].ToUpper())
        }
    } else {
        $action = $Arguments[0]
        for ($i = 1; $i -lt $Arguments.Count; $i++) {
            $signals.Add($Arguments[$i].ToUpper())
        }
    }

    if ($signals.Count -eq 0) {
        $signals.Add('EXIT')
    }

    foreach ($signal in $signals) {
        if ($resetMode -or ($action -eq '')) {
            if ($script:BashTrapHandlers.ContainsKey($signal)) {
                if ($signal -ceq 'EXIT') {
                    $global:__BashTrapEXIT = $null
                }
                $script:BashTrapHandlers.Remove($signal) | Out-Null
            }
            continue
        }

        switch ($signal) {
            'EXIT' {
                $sb = [scriptblock]::Create($action)
                $global:__BashTrapEXIT = $sb
                $script:BashTrapHandlers['EXIT'] = $action
            }
            'ERR' {
                $sb = [scriptblock]::Create($action)
                $script:BashTrapHandlers['ERR'] = $action
                $global:__BashTrapERR = $sb
            }
            default {
                $script:BashTrapHandlers[$signal] = $action
            }
        }
    }
}

# --- read ---

function Invoke-BashRead {
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $prompt = $null
    $promptSet = $false
    $varNames = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-r') {
            $i++; continue
        }
        if ($arg -ceq '-p' -and ($i + 1) -lt $Arguments.Count) {
            $prompt = $Arguments[$i + 1]
            $promptSet = $true
            $i += 2; continue
        }
        if ($arg -ceq '-a' -and ($i + 1) -lt $Arguments.Count) {
            # read -a arr: read into array
            $varNames.Add($Arguments[$i + 1])
            $i += 2; continue
        }
        $varNames.Add($arg)
        $i++
    }

    if ($varNames.Count -eq 0) { return }

    # Determine input source: pipeline or interactive
    $inputLine = $null
    if ($pipelineInput.Count -gt 0) {
        # Collect text from pipeline input
        $allText = [System.Text.StringBuilder]::new()
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text) { [void]$allText.Append($text) }
        }
        $inputLine = $allText.ToString() -replace "`r`n", "`n" -replace "`n$", ''
    } else {
        # Interactive: use Read-Host
        if ($promptSet) {
            $inputLine = Read-Host $prompt
        } else {
            $inputLine = Read-Host
        }
    }

    if ($null -eq $inputLine) { return }

    if ($varNames.Count -eq 1) {
        # Single variable: assign entire line in the caller's scope
        Set-Variable -Name $varNames[0] -Value $inputLine -Scope 1
    } else {
        # Multiple variables: split by whitespace
        $parts = $inputLine -split '\s+'
        for ($j = 0; $j -lt $varNames.Count; $j++) {
            if ($j -lt $parts.Count - 1) {
                Set-Variable -Name $varNames[$j] -Value $parts[$j] -Scope 1
            } elseif ($j -eq $varNames.Count - 1) {
                # Last variable gets remaining text
                $remaining = ($parts[$j..($parts.Count - 1)] -join ' ')
                Set-Variable -Name $varNames[$j] -Value $remaining -Scope 1
            } else {
                Set-Variable -Name $varNames[$j] -Value '' -Scope 1
            }
        }
    }
}

# --- mapfile / readarray ---

function Invoke-BashMapfile {
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $count = $null
    $origin = 0
    $stripTrailing = $false
    $varName = 'MAPFILE'

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-t') {
            $stripTrailing = $true
            $i++; continue
        }
        if ($arg -ceq '-n' -and ($i + 1) -lt $Arguments.Count) {
            $count = [int]$Arguments[$i + 1]
            $i += 2; continue
        }
        if ($arg.StartsWith('-n') -and $arg.Length -gt 2) {
            $count = [int]$arg.Substring(2)
            $i++; continue
        }
        if ($arg -ceq '-O' -and ($i + 1) -lt $Arguments.Count) {
            $origin = [int]$Arguments[$i + 1]
            $i += 2; continue
        }
        if ($arg.StartsWith('-O') -and $arg.Length -gt 2) {
            $origin = [int]$arg.Substring(2)
            $i++; continue
        }
        # -d DELIM: custom delimiter (consumed but currently splits on \n only)
        if ($arg -ceq '-d' -and ($i + 1) -lt $Arguments.Count) {
            $i += 2; continue
        }
        if ($arg.StartsWith('-d') -and $arg.Length -gt 2) {
            $i++; continue
        }
        # Non-flag argument is the variable name
        if (-not $arg.StartsWith('-')) {
            $varName = $arg
        }
        $i++
    }

    # Collect input: pipeline or stdin
    $lines = [System.Collections.Generic.List[string]]::new()

    if ($pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text) {
                foreach ($line in ($text -replace "`r`n", "`n" -split "`n")) {
                    if ($line -ne '') { $lines.Add($line) }
                }
            }
        }
    }

    # Apply count limit
    if ($null -ne $count -and $lines.Count -gt $count) {
        $lines = $lines.GetRange(0, $count)
    }

    # Strip trailing delimiter if requested
    if ($stripTrailing) {
        for ($j = 0; $j -lt $lines.Count; $j++) {
            $lines[$j] = $lines[$j].TrimEnd("`n"[0], "`r"[0])
        }
    }

    # Build result array with origin offset
    if ($origin -gt 0) {
        $result = @(1..$origin | ForEach-Object { '' })
        $result += @($lines)
    } else {
        $result = @($lines)
    }

    Set-Variable -Name $varName -Value $result
}

# --- readlink ---

function Invoke-BashReadlink {
    [OutputType('PsBash.ReadlinkOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'readlink' }

    $canonicalize = $false
    $operands = [System.Collections.Generic.List[string]]::new()
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-f') { $canonicalize = $true }
        else { $operands.Add($arg) }
    }

    if ($operands.Count -eq 0) {
        Write-Error 'readlink: missing operand' -ErrorAction Continue
        return
    }

    foreach ($path in $operands) {
        if ($canonicalize) {
            $resolved = (Resolve-Path -Path $path -ErrorAction SilentlyContinue)
            if (-not $resolved) {
                Write-Error "readlink: ${path}: No such file or directory" -ErrorAction Continue
                continue
            }
            $text = $resolved.Path
        } else {
            $item = Get-Item -Path $path -ErrorAction SilentlyContinue
            if (-not $item) {
                Write-Error "readlink: ${path}: No such file or directory" -ErrorAction Continue
                continue
            }
            $text = if ($item.Target) { $item.Target } else { $item.FullName }
        }
        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.ReadlinkOutput'
            Path       = $text
            BashText   = $text
        }
        Set-BashDisplayProperty $obj
    }
}

# --- mktemp ---

function Invoke-BashMktemp {
    [OutputType('PsBash.MktempOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'mktemp' }

    $makeDir = $false
    $template = $null
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-d') { $makeDir = $true }
        else { $template = $arg }
    }

    $subDir = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'ps-bash', 'proc-sub')
    [void][System.IO.Directory]::CreateDirectory($subDir)

    $name = [System.IO.Path]::GetRandomFileName()
    if ($template) {
        $prefix = $template -replace 'X+$', ''
        $prefix = [System.IO.Path]::GetFileName($prefix)
        $name = $prefix + [System.IO.Path]::GetRandomFileName()
    }

    $fullPath = [System.IO.Path]::Combine($subDir, $name)

    if ($makeDir) {
        [void][System.IO.Directory]::CreateDirectory($fullPath)
    } else {
        [void][System.IO.File]::WriteAllText($fullPath, '')
    }

    $obj = [PSCustomObject]@{
        PSTypeName = 'PsBash.MktempOutput'
        Path       = $fullPath
        BashText   = $fullPath
    }
    Set-BashDisplayProperty $obj
}

# --- type ---

$script:BashShoptOptions = @{
    'extglob'            = $false
    'globstar'           = $true
    'dotglob'            = $false
    'nullglob'           = $false
    'nocaseglob'         = $false
    'expand_aliases'     = $true
    'cmdhist'            = $true
    'histappend'         = $true
    'checkwinsize'      = $true
    'progcomp'           = $true
    'login_shell'        = $false
    'interactive_comments' = $true
    'sourcepath'         = $true
    'hostcomplete'       = $true
}

function Invoke-BashShopt {
    param()
    $Arguments = [string[]]$args

    $setMode = $false
    $unsetMode = $false
    $printMode = $false
    $queryMode = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    foreach ($arg in $Arguments) {
        switch ($arg) {
            '-s' { $setMode = $true }
            '-u' { $unsetMode = $true }
            '-p' { $printMode = $true }
            '-q' { $queryMode = $true }
            default { $operands.Add($arg) }
        }
    }

    if ($printMode -and $operands.Count -eq 0) {
        foreach ($kv in $script:BashShoptOptions.GetEnumerator() | Sort-Object Key) {
            $val = if ($kv.Value) { 'on' } else { 'off' }
            Emit-BashLine -Text "shopt -s $($kv.Name)"
        }
        return
    }

    foreach ($opt in $operands) {
        if ($setMode) {
            $script:BashShoptOptions[$opt] = $true
        } elseif ($unsetMode) {
            $script:BashShoptOptions[$opt] = $false
        } else {
            if ($script:BashShoptOptions.ContainsKey($opt)) {
                $val = if ($script:BashShoptOptions[$opt]) { 'on' } else { 'off' }
                Emit-BashLine -Text "$opt $val"
            } else {
                Write-BashError -Message "bash: shopt: ${opt}: invalid shell option name"
            }
        }
    }
}

function Invoke-BashType {
    [OutputType('PsBash.TypeOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'type' }

    $typeOnly = $false
    $showAll = $false
    $printMode = $false
    $operands = [System.Collections.Generic.List[string]]::new()
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-t') { $typeOnly = $true }
        elseif ($arg -ceq '-a' -or $arg -ceq '--all') { $showAll = $true }
        elseif ($arg -ceq '-p') { $printMode = $true }
        else { $operands.Add($arg) }
    }

    if ($operands.Count -eq 0) {
        Write-Error 'type: missing operand' -ErrorAction Continue
        return
    }

    $builtins = @('echo', 'printf', 'type', 'cd', 'exit', 'return', 'export',
                   'unset', 'set', 'shift', 'read', 'eval', 'source', 'trap',
                   'alias', 'unalias', 'test', '[', 'true', 'false')

    foreach ($name in $operands) {
        if ($printMode) {
            $val = Get-Variable -Name $name -Scope Global -ValueOnly -ErrorAction SilentlyContinue
            $source = 'variable'
            if ($null -eq $val) {
                $envVal = [System.Environment]::GetEnvironmentVariable($name)
                if ($null -ne $envVal) { $val = $envVal; $source = 'environment' }
            }
            if ($null -ne $val) {
                if ($val -is [System.Collections.IDictionary]) {
                    Emit-BashLine -Text "declare -A $name=$($val | ConvertTo-Json -Compress)"
                } elseif ($val -is [array] -or $val -is [System.Collections.IList]) {
                    Emit-BashLine -Text "declare -a $name=$($val | ConvertTo-Json -Compress)"
                } else {
                    Emit-BashLine -Text "declare -- $name=`"$val`""
                }
            } else {
                Write-Error "bash: declare: ${name}: not found" -ErrorAction Continue
            }
            continue
        }

        $isBuiltin = $builtins -contains $name
        if ($isBuiltin) {
            $kind = 'builtin'
            $text = if ($typeOnly) { $kind } else { "$name is a shell builtin" }
            if (-not $showAll) {
                $obj = [PSCustomObject]@{
                    PSTypeName = 'PsBash.TypeOutput'
                    Command    = $name
                    Kind       = $kind
                    BashText   = $text
                }
                Set-BashDisplayProperty $obj
                continue
            }
        }

        $results = [System.Collections.Generic.List[PSObject]]::new()
        if ($isBuiltin -and $showAll) {
            $results.Add(([PSCustomObject]@{
                PSTypeName = 'PsBash.TypeOutput'
                Command    = $name
                Kind       = 'builtin'
                BashText   = if ($typeOnly) { 'builtin' } else { "$name is a shell builtin" }
            }))
        }

        $alias = Get-Alias $name -ErrorAction SilentlyContinue
        if ($alias) {
            if ($alias.Definition -match '^Invoke-Bash|^Get-Bash|^Set-Bash|^ConvertFrom-') {
                $results.Add(([PSCustomObject]@{
                    PSTypeName = 'PsBash.TypeOutput'
                    Command    = $name
                    Kind       = 'alias'
                    BashText   = if ($typeOnly) { 'alias' } else { "$name is aliased to ``$($alias.Definition)''" }
                }))
            }
        }

        $cmd = Get-Command $name -CommandType Application,Cmdlet,Function -ErrorAction SilentlyContinue
        if ($cmd -and -not $isBuiltin) {
            switch ($cmd.CommandType) {
                'Alias'    { $k = 'alias'; $t = if ($typeOnly) { $k } else { "$name is aliased to ``$($cmd.Definition)''" } }
                'Function' { $k = 'function'; $t = if ($typeOnly) { $k } else { "$name is a function" } }
                default    { $k = 'file'; $t = if ($typeOnly) { $k } else { "$name is $($cmd.Source)" } }
            }
            $results.Add(([PSCustomObject]@{
                PSTypeName = 'PsBash.TypeOutput'
                Command    = $name
                Kind       = $k
                BashText   = $t
            }))
        }

        if ($results.Count -eq 0) {
            Write-Error "bash: type: ${name}: not found" -ErrorAction Continue
            continue
        }

        if (-not $showAll -and -not $isBuiltin) {
            $r = $results[0]
            Set-BashDisplayProperty $r
            continue
        }

        foreach ($r in $results) {
            Set-BashDisplayProperty $r
        }
    }
}

function Invoke-BashBash {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'bash' }

    # Resolve the ps-bash executable: prefer the parent process path (exact binary),
    # fall back to Get-Command ps-bash.
    $psBashExe = $null
    $__pid = Get-Variable -Name __parentPid -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    if ($__pid -and $__pid -gt 0) {
        try {
            $parent = [System.Diagnostics.Process]::GetProcessById($__pid)
            $psBashExe = $parent.MainModule.FileName
        } catch {}
    }
    if (-not $psBashExe) {
        $found = Get-Command ps-bash -ErrorAction SilentlyContinue
        if ($found) { $psBashExe = $found.Source }
    }
    if (-not $psBashExe) {
        # Try the same directory as the current PowerShell executable
        $psBashExe = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName), 'ps-bash')
        if ($IsWindows) { $psBashExe += '.exe' }
        if (-not (Test-Path $psBashExe)) {
            Write-BashError -Message 'bash: ps-bash executable not found'
            return
        }
    }

    # Handle --version: print ps-bash version info
    if ($Arguments -contains '--version') {
        $version = $null
        if ($null -ne (Get-Variable MyInvocation -ValueOnly -ErrorAction SilentlyContinue)) {
            $mod = $MyInvocation.MyCommand.Module
            if ($mod) { $version = $mod.Version.ToString() }
        }
        if (-not $version) { $version = '0.7.6' }
        $text = "ps-bash, version $version`nBash-to-PowerShell transpiler"
        Emit-BashLine -Text $text
        return
    }

    # Forward all arguments to ps-bash executable
    $output = & $psBashExe @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $global:LASTEXITCODE = $exitCode

    $errors = @($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
    $normal = @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })

    foreach ($e in $errors) {
        [Console]::Error.WriteLine("$e")
    }

    foreach ($item in $normal) {
        $text = if ($item.PSObject.Properties['BashText']) { $item.BashText } else { "$item" }
        Emit-BashLine -Text $text
    }
}

