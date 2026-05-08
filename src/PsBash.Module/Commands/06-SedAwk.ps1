# --- sed Command ---

function Invoke-BashSed {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'sed' }

    # Parse flags and expressions
    $suppressDefault = $false
    $inPlace = $false
    $extendedRegex = $false
    $scriptFile = $null
    $expressions = [System.Collections.Generic.List[string]]::new()
    $operands = [System.Collections.Generic.List[string]]::new()
    $pastDoubleDash = $false

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($pastDoubleDash) {
            $operands.Add($arg)
            $i++
            continue
        }

        if ($arg -eq '--') {
            $pastDoubleDash = $true
            $i++
            continue
        }

        if ($arg -ceq '-e') {
            $i++
            if ($i -lt $Arguments.Count) {
                $expressions.Add($Arguments[$i])
            }
            $i++
            continue
        }

        if ($arg -ceq '-f') {
            $i++
            if ($i -lt $Arguments.Count) {
                $scriptFile = $Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            $fConsumed = $false
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'n' { $suppressDefault = $true }
                    'i' { $inPlace = $true }
                    'E' { $extendedRegex = $true }
                    'r' { $extendedRegex = $true }
                    'f' {
                        if (-not $fConsumed) {
                            $i++
                            if ($i -lt $Arguments.Count) {
                                $scriptFile = $Arguments[$i]
                                $fConsumed = $true
                            }
                        }
                    }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Read script file if -f was specified
    if ($null -ne $scriptFile) {
        $resolved = Resolve-Path $scriptFile -ErrorAction SilentlyContinue
        if ($null -eq $resolved) {
            Write-BashError -Message "sed: can't read $scriptFile" -ExitCode 2
            return
        }
        $scriptText = [System.IO.File]::ReadAllText($resolved.Path)
        # Each non-empty line in the script file is a separate sed command
        foreach ($scriptLine in $scriptText -split "`n") {
            $trimmed = $scriptLine.Trim()
            if ($trimmed.Length -gt 0) {
                $expressions.Add($trimmed)
            }
        }
    }

    # First operand is the expression if no -e was used
    if ($expressions.Count -eq 0 -and $operands.Count -gt 0) {
        $expressions.Add($operands[0])
        $operands.RemoveAt(0)
    }

    if ($expressions.Count -eq 0) {
        Write-BashError -Message 'sed: usage: sed [options] expression [file ...]' -ExitCode 2
        return
    }

    # Parse sed commands from expressions
    $commands = [System.Collections.Generic.List[hashtable]]::new()
    foreach ($expr in $expressions) {
        $parsed = ConvertFrom-SedExpression -Expression $expr -ExtendedRegex $extendedRegex
        if ($null -eq $parsed) { return }
        $commands.Add($parsed)
    }

    $simpleSubstitution = $false
    $simpleSubCommand = $null
    if (-not $suppressDefault -and $commands.Count -eq 1) {
        $candidate = $commands[0]
        if ($candidate.Type -eq 's' -and $null -eq $candidate.Address -and -not ([string]$candidate.Replacement).Contains("`n")) {
            $simpleSubstitution = $true
            $simpleSubCommand = $candidate
        }
    }

    # Process all lines through sed commands, handling multi-line pattern space
    # $lines is [string[]], returns [string[]] of output lines
    $processLines = {
        param([string[]]$inputLines)

        $outputLines = [System.Collections.Generic.List[string]]::new()
        $totalLines = $inputLines.Count
        $li = 0

        while ($li -lt $totalLines) {
            $patternSpace = $inputLines[$li]
            $lineNum = $li + 1
            $deleted = $false
            $quit = $false
            $quitExitCode = 0
            $replaced = $false

            $restartCycle = $true
            while ($restartCycle) {
                $restartCycle = $false
                $printedLines = [System.Collections.Generic.List[string]]::new()
                $appendTexts = [System.Collections.Generic.List[string]]::new()
                $insertTexts = [System.Collections.Generic.List[string]]::new()
                $deleted = $false
                $replaced = $false

                foreach ($cmd in $commands) {
                    if ($deleted) { break }
                    if ($quit -and $cmd.Type -ne 'q') { continue }

                    # For address matching, use the first line of pattern space
                    $firstLine = if ($patternSpace.Contains("`n")) {
                        $patternSpace.Substring(0, $patternSpace.IndexOf("`n"))
                    } else {
                        $patternSpace
                    }

                    if (-not (Test-SedAddress -Cmd $cmd -Line $firstLine -LineNum $lineNum -TotalLines $totalLines -AllLines $inputLines)) {
                        continue
                    }

                    switch -CaseSensitive ($cmd.Type) {
                        's' {
                            $regex = $cmd.Regex
                            if ($cmd.Global) {
                                $patternSpace = $regex.Replace($patternSpace, $cmd.Replacement)
                            } else {
                                $patternSpace = $regex.Replace($patternSpace, $cmd.Replacement, 1)
                            }
                        }
                        'd' {
                            $deleted = $true
                        }
                        'D' {
                            $nlIdx = $patternSpace.IndexOf("`n")
                            if ($nlIdx -ge 0) {
                                $patternSpace = $patternSpace.Substring($nlIdx + 1)
                            } else {
                                $deleted = $true
                                $patternSpace = ''
                            }
                            if (-not $deleted -and $patternSpace.Length -gt 0) {
                                $restartCycle = $true
                            } elseif ($deleted) {
                                $restartCycle = $false
                            }
                            # Break out of foreach, will check restartCycle below
                            break
                        }
                        'p' {
                            $printedLines.Add($patternSpace)
                        }
                        'P' {
                            $nlIdx = $patternSpace.IndexOf("`n")
                            if ($nlIdx -ge 0) {
                                $printedLines.Add($patternSpace.Substring(0, $nlIdx))
                            } else {
                                $printedLines.Add($patternSpace)
                            }
                        }
                        'N' {
                            $li++
                            if ($li -lt $totalLines) {
                                $patternSpace += "`n" + $inputLines[$li]
                            }
                        }
                        'q' {
                            $quit = $true
                            $quitExitCode = $cmd.ExitCode
                        }
                        'a' {
                            $appendTexts.Add($cmd.Text)
                        }
                        'i' {
                            $insertTexts.Add($cmd.Text)
                        }
                        'c' {
                            $deleted = $true
                            $replaced = $true
                            $appendTexts.Add($cmd.Text)
                        }
                        'y' {
                            $sb = [System.Text.StringBuilder]::new($patternSpace.Length)
                            foreach ($ch in $patternSpace.ToCharArray()) {
                                $idx = $cmd.Source.IndexOf($ch)
                                if ($idx -ge 0) {
                                    [void]$sb.Append($cmd.Dest[$idx])
                                } else {
                                    [void]$sb.Append($ch)
                                }
                            }
                            $patternSpace = $sb.ToString()
                        }
                    }

                    if ($restartCycle) { break }
                }

                if ($restartCycle) { continue }

                # Emit insert texts (before current line)
                foreach ($insText in $insertTexts) {
                    $outputLines.Add($insText)
                }

                # Emit printed lines (from p/P commands)
                foreach ($pLine in $printedLines) {
                    $outputLines.Add($pLine)
                }

                # Emit default output
                if (-not $deleted) {
                    if (-not $suppressDefault) {
                        # Pattern space may have embedded newlines from N
                        if ($patternSpace.Contains("`n")) {
                            foreach ($psLine in $patternSpace.Split("`n")) {
                                $outputLines.Add($psLine)
                            }
                        } else {
                            $outputLines.Add($patternSpace)
                        }
                    }
                }

                # Emit append texts (after current line)
                foreach ($appText in $appendTexts) {
                    $outputLines.Add($appText)
                }
            }

            $li++

            if ($quit) {
                # q prints the current pattern space first (already done above), then stops
                break
            }
        }

        $outputLines.ToArray()
    }

    # --- File mode (including in-place) ---
    if ($operands.Count -gt 0) {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $rawText = Read-BashFileBytes -Path $filePath -Command 'sed'
            if ($null -eq $rawText) { continue }
            $hadTrailingNewline = $rawText.EndsWith("`n")
            if ($hadTrailingNewline) {
                $rawText = $rawText.Substring(0, $rawText.Length - 1)
            }
            $lines = $rawText.Split("`n")

            $outputLines = & $processLines $lines

            if ($inPlace) {
                $outText = ($outputLines -join "`n")
                if ($hadTrailingNewline) { $outText += "`n" }
                if (-not (Write-BashFileText -Path $filePath -Text $outText -Command 'sed')) { return }
            } else {
                foreach ($outLine in $outputLines) {
                    New-BashObject -BashText "$outLine`n"
                }
            }
        }
        return
    }

    # --- Pipeline mode ---
    if ($pipelineInput.Count -eq 0) { return }

    if ($simpleSubstitution) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $outLine = if ($simpleSubCommand.Global) {
                        $simpleSubCommand.Regex.Replace($subLine, $simpleSubCommand.Replacement)
                    } else {
                        $simpleSubCommand.Regex.Replace($subLine, $simpleSubCommand.Replacement, 1)
                    }
                    New-BashObject -BashText "$outLine`n"
                }
            } else {
                $outLine = if ($simpleSubCommand.Global) {
                    $simpleSubCommand.Regex.Replace($text.TrimEnd("`n".ToCharArray()), $simpleSubCommand.Replacement)
                } else {
                    $simpleSubCommand.Regex.Replace($text.TrimEnd("`n".ToCharArray()), $simpleSubCommand.Replacement, 1)
                }
                if ($item -isnot [string] -and $null -ne $item.PSObject -and $null -ne $item.PSObject.Properties['BashText']) {
                    $item.BashText = "$outLine`n"
                    $item
                } else {
                    New-BashObject -BashText "$outLine`n"
                }
            }
        }
        return
    }

    $allLines = [System.Collections.Generic.List[string]]::new()
    $origItems = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $pipelineInput) {
        $text = Get-BashText -InputObject $item
        if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
            foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                $allLines.Add($subLine)
                $origItems.Add($null)
            }
        } else {
            $allLines.Add(($text.TrimEnd("`n".ToCharArray())))
            $origItems.Add($item)
        }
    }

    $inputArray = [string[]]$allLines.ToArray()
    $outputLines = @(& $processLines $inputArray)

    # Emit output preserving original objects where possible
    for ($oi = 0; $oi -lt $outputLines.Count; $oi++) {
        if ($oi -lt $origItems.Count) {
            $orig = $origItems[$oi]
            if ($null -ne $orig -and $orig -isnot [string] -and $null -ne $orig.PSObject -and $null -ne $orig.PSObject.Properties['BashText']) {
                $orig.BashText = "$($outputLines[$oi])`n"
                $orig
                continue
            }
        }
        New-BashObject -BashText "$($outputLines[$oi])`n"
    }
}

function ConvertFrom-SedExpression {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [string]$Expression,

        [Parameter()]
        [bool]$ExtendedRegex = $false
    )

    $addr = $null
    $pos = 0

    # Parse address prefix
    if ($Expression.Length -gt 0 -and $Expression[$pos] -eq '/') {
        # /pattern/ address
        $pos++
        $endSlash = $Expression.IndexOf('/', $pos)
        if ($endSlash -lt 0) { Write-BashError -Message 'sed: unterminated address regex' -ExitCode 2; return $null }
        $addr = @{ Type = 'regex'; Pattern = $Expression.Substring($pos, $endSlash - $pos) }
        $pos = $endSlash + 1

        # Check for range: /start/,/end/
        if ($pos -lt $Expression.Length -and $Expression[$pos] -eq ',') {
            $pos++
            if ($pos -lt $Expression.Length -and $Expression[$pos] -eq '/') {
                $pos++
                $endSlash2 = $Expression.IndexOf('/', $pos)
                if ($endSlash2 -lt 0) { Write-BashError -Message 'sed: unterminated address regex' -ExitCode 2; return $null }
                $addr = @{
                    Type         = 'range_regex'
                    StartPattern = $addr.Pattern
                    EndPattern   = $Expression.Substring($pos, $endSlash2 - $pos)
                }
                $pos = $endSlash2 + 1
            }
        }
    } elseif ($Expression.Length -gt 0 -and $Expression[$pos] -match '^\d') {
        # Numeric address
        $numStr = ''
        while ($pos -lt $Expression.Length -and $Expression[$pos] -match '\d') {
            $numStr += $Expression[$pos]
            $pos++
        }
        $startNum = [int]$numStr

        if ($pos -lt $Expression.Length -and $Expression[$pos] -eq ',') {
            $pos++
            if ($pos -lt $Expression.Length -and $Expression[$pos] -eq '$') {
                $addr = @{ Type = 'range_num'; Start = $startNum; End = [int]::MaxValue }
                $pos++
            } else {
                $numStr2 = ''
                while ($pos -lt $Expression.Length -and $Expression[$pos] -match '\d') {
                    $numStr2 += $Expression[$pos]
                    $pos++
                }
                $addr = @{ Type = 'range_num'; Start = $startNum; End = [int]$numStr2 }
            }
        } else {
            $addr = @{ Type = 'line'; Line = $startNum }
        }
    }

    $remaining = $Expression.Substring($pos)

    # Parse command
    if ($remaining.Length -eq 0) {
        Write-BashError -Message 'sed: missing command' -ExitCode 2
        return $null
    }

    $cmdChar = $remaining[0]

    switch -CaseSensitive ($cmdChar) {
        's' {
            $delim = $remaining[1]
            $parts = [System.Collections.Generic.List[string]]::new()
            $current = [System.Text.StringBuilder]::new()
            $escaped = $false
            for ($ci = 2; $ci -lt $remaining.Length; $ci++) {
                $c = $remaining[$ci]
                if ($escaped) {
                    if ($c -ne $delim) { [void]$current.Append('\') }
                    [void]$current.Append($c)
                    $escaped = $false
                    continue
                }
                if ($c -eq '\') {
                    $escaped = $true
                    continue
                }
                if ($c -eq $delim) {
                    $parts.Add($current.ToString())
                    $current = [System.Text.StringBuilder]::new()
                    continue
                }
                [void]$current.Append($c)
            }
            $parts.Add($current.ToString())

            if ($parts.Count -lt 2) { Write-BashError -Message 'sed: bad substitution' -ExitCode 2; return $null }

            $searchPattern = $parts[0]
            $replacement = $parts[1]
            $flags = if ($parts.Count -gt 2) { $parts[2] } else { '' }
            $global = $flags.Contains('g')

            $replacement = $replacement -replace '\\(\d)', '$$$1'
            $replacement = $replacement -replace '\\&', '$$0'

            $regexOpts = [System.Text.RegularExpressions.RegexOptions]::None
            if ($flags.Contains('I') -or $flags.Contains('i')) {
                $regexOpts = $regexOpts -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
            }

            if (-not $ExtendedRegex) {
                $searchPattern = $searchPattern -replace '(?<!\\)\(', '\(' -replace '(?<!\\)\)', '\)' -replace '(?<!\\)\{', '\{' -replace '(?<!\\)\}', '\}' -replace '(?<!\\)\|', '\|' -replace '(?<!\\)\+', '\+' -replace '(?<!\\)\?', '\?'
            }

            $regex = [regex]::new($searchPattern, $regexOpts)

            @{
                Type        = 's'
                Address     = $addr
                Regex       = $regex
                Replacement = $replacement
                Global      = $global
            }
        }
        'd' {
            @{
                Type    = 'd'
                Address = $addr
            }
        }
        'D' {
            @{
                Type    = 'D'
                Address = $addr
            }
        }
        'p' {
            @{
                Type    = 'p'
                Address = $addr
            }
        }
        'P' {
            @{
                Type    = 'P'
                Address = $addr
            }
        }
        'N' {
            @{
                Type    = 'N'
                Address = $addr
            }
        }
        'q' {
            $exitCode = 0
            if ($remaining.Length -gt 1) {
                $qArg = $remaining.Substring(1).Trim()
                if ($qArg.Length -gt 0 -and $qArg -match '^\d+$') {
                    $exitCode = [int]$qArg
                }
            }
            @{
                Type     = 'q'
                Address  = $addr
                ExitCode = $exitCode
            }
        }
        'a' {
            $text = if ($remaining.Length -gt 1) { $remaining.Substring(1) } else { '' }
            $text = $text.TrimStart('\').TrimStart()
            @{
                Type    = 'a'
                Address = $addr
                Text    = $text
            }
        }
        'i' {
            $text = if ($remaining.Length -gt 1) { $remaining.Substring(1) } else { '' }
            $text = $text.TrimStart('\').TrimStart()
            @{
                Type    = 'i'
                Address = $addr
                Text    = $text
            }
        }
        'c' {
            $text = if ($remaining.Length -gt 1) { $remaining.Substring(1) } else { '' }
            $text = $text.TrimStart('\').TrimStart()
            @{
                Type    = 'c'
                Address = $addr
                Text    = $text
            }
        }
        'y' {
            $delim = $remaining[1]
            $parts = $remaining.Substring(2).Split($delim)
            if ($parts.Count -lt 2) { Write-BashError -Message 'sed: bad transliteration' -ExitCode 2; return $null }
            $source = $parts[0]
            $dest = $parts[1]
            if ($source.Length -ne $dest.Length) {
                Write-BashError -Message 'sed: y: source and dest must be the same length' -ExitCode 2; return $null
            }
            @{
                Type    = 'y'
                Address = $addr
                Source  = $source
                Dest    = $dest
            }
        }
        default {
            Write-BashError -Message "sed: unsupported command '$cmdChar'" -ExitCode 2; return $null
        }
    }
}

function Test-SedAddress {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Cmd,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Line,

        [Parameter(Mandatory)]
        [int]$LineNum,

        [Parameter(Mandatory)]
        [int]$TotalLines,

        [Parameter()]
        [string[]]$AllLines
    )

    $addr = $Cmd.Address
    if ($null -eq $addr) { return $true }

    switch ($addr.Type) {
        'regex' {
            return [regex]::IsMatch($Line, $addr.Pattern)
        }
        'line' {
            return $LineNum -eq $addr.Line
        }
        'range_num' {
            return ($LineNum -ge $addr.Start -and $LineNum -le $addr.End)
        }
        'range_regex' {
            $inRange = $false
            $rangeActive = $false
            for ($ri = 0; $ri -lt $AllLines.Count; $ri++) {
                if (-not $rangeActive) {
                    if ([regex]::IsMatch($AllLines[$ri], $addr.StartPattern)) {
                        $rangeActive = $true
                    }
                }
                if ($rangeActive -and ($ri + 1) -eq $LineNum) {
                    $inRange = $true
                }
                if ($rangeActive -and ($ri + 1) -ne $LineNum -and
                    [regex]::IsMatch($AllLines[$ri], $addr.EndPattern)) {
                    $rangeActive = $false
                }
                if ($rangeActive -and ($ri + 1) -eq $LineNum -and
                    [regex]::IsMatch($AllLines[$ri], $addr.EndPattern)) {
                    $rangeActive = $false
                }
                if (($ri + 1) -gt $LineNum) { break }
            }
            return $inRange
        }
    }
    return $false
}

# --- awk Command ---

function Invoke-BashAwk {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'awk' }

    # Parse flags: -F FS, -v VAR=VAL, -f FILE
    $fieldSep = ' '
    $fieldSepIsDefault = $true
    $variables = @{}
    $programText = $null
    $programFiles = [System.Collections.Generic.List[string]]::new()
    $i = 0

    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -ceq '-F') {
            $i++
            if ($i -lt $Arguments.Count) {
                $fieldSep = $Arguments[$i] -replace '\\t', "`t"
                $fieldSepIsDefault = $false
            }
            $i++
            continue
        }

        if ($arg.Length -gt 2 -and $arg.StartsWith('-F')) {
            $fieldSep = $arg.Substring(2) -replace '\\t', "`t"
            $fieldSepIsDefault = $false
            $i++
            continue
        }

        if ($arg -ceq '-v') {
            $i++
            if ($i -lt $Arguments.Count) {
                $eqIdx = $Arguments[$i].IndexOf('=')
                if ($eqIdx -gt 0) {
                    $vName = $Arguments[$i].Substring(0, $eqIdx)
                    $vVal = $Arguments[$i].Substring($eqIdx + 1)
                    $variables[$vName] = $vVal
                }
            }
            $i++
            continue
        }

        if ($arg -ceq '-f' -or $arg -ceq '--file') {
            $i++
            if ($i -lt $Arguments.Count) {
                $programFiles.Add($Arguments[$i])
            }
            $i++
            continue
        }

        if ($null -eq $programText) {
            $programText = $arg
        }
        $i++
    }

    # If -f was used, read program from file
    if ($programFiles.Count -gt 0) {
        $fileText = [System.Text.StringBuilder]::new()
        foreach ($pf in $programFiles) {
            if (-not (Test-Path $pf)) {
                Write-BashError -Message "awk: can't open source file ${pf}: No such file or directory" -ExitCode 2
                return
            }
            [void]$fileText.Append([System.IO.File]::ReadAllText($pf))
        }
        $programText = $fileText.ToString()
    }

    if ($null -eq $programText) {
        Write-BashError -Message 'awk: usage: awk [options] program [file ...]' -ExitCode 2
        return
    }

    # Parse program into rules (pattern/action pairs)
    $rules = ConvertFrom-AwkProgram -Program $programText

    # Apply FS/OFS from variables or BEGIN blocks
    if ($variables.ContainsKey('FS')) {
        $fieldSep = $variables['FS']
        $fieldSepIsDefault = $false
    }
    if (-not $variables.ContainsKey('OFS')) {
        $variables['OFS'] = ' '
    }
    if (-not $variables.ContainsKey('NR')) {
        $variables['NR'] = 0
    }

    # Execute BEGIN rules first
    $beginOutput = [System.Collections.Generic.List[string]]::new()
    foreach ($rule in $rules) {
        if ($rule.Pattern -eq 'BEGIN') {
            Invoke-AwkAction -Action $rule.Action -Fields @('') -Variables $variables -Output $beginOutput -FieldSep $fieldSep
            # BEGIN can set FS/OFS
            if ($variables.ContainsKey('FS') -and $fieldSepIsDefault) {
                $fieldSep = $variables['FS']
                $fieldSepIsDefault = $false
            }
        }
    }

    # Emit BEGIN output
    foreach ($line in $beginOutput) {
        New-BashObject -BashText "$line`n"
    }

    # Process input lines
    if ($pipelineInput.Count -eq 0) {
        # Still run END blocks
        $endOutput = [System.Collections.Generic.List[string]]::new()
        foreach ($rule in $rules) {
            if ($rule.Pattern -eq 'END') {
                Invoke-AwkAction -Action $rule.Action -Fields @('') -Variables $variables -Output $endOutput -FieldSep $fieldSep
            }
        }
        foreach ($line in $endOutput) {
            New-BashObject -BashText "$line`n"
        }
        return
    }

    $printfBuffer = [System.Text.StringBuilder]::new()
    $allLines = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $pipelineInput) {
        $text = Get-BashText -InputObject $item
        if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
            foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                $allLines.Add($subLine)
            }
        } else {
            $allLines.Add(($text.TrimEnd("`n".ToCharArray())))
        }
    }
    for ($idx = 0; $idx -lt $allLines.Count; $idx++) {
        $text = $allLines[$idx]
        $variables['NR'] = $idx + 1

        # Split into fields
        $fields = Split-AwkFields -Line $text -FieldSep $fieldSep -IsDefault $fieldSepIsDefault
        $variables['NF'] = $fields.Count - 1

        $lineOutput = [System.Collections.Generic.List[string]]::new()
        $matched = $false

        foreach ($rule in $rules) {
            if ($rule.Pattern -eq 'BEGIN' -or $rule.Pattern -eq 'END') { continue }

            if (Test-AwkPattern -Pattern $rule.Pattern -Fields $fields -Variables $variables) {
                $matched = $true
                if ($null -ne $rule.Action -and $rule.Action.Length -gt 0) {
                    Invoke-AwkAction -Action $rule.Action -Fields $fields -Variables $variables -Output $lineOutput -FieldSep $fieldSep -PrintfBuffer $printfBuffer
                } else {
                    $lineOutput.Add($fields[0])
                }
            }
        }

        foreach ($outLine in $lineOutput) {
            New-BashObject -BashText "$outLine`n"
        }
    }

    # Flush printf buffer
    if ($printfBuffer.Length -gt 0) {
        New-BashObject -BashText "$($printfBuffer.ToString())`n"
    }

    # Execute END rules
    $endOutput = [System.Collections.Generic.List[string]]::new()
    foreach ($rule in $rules) {
        if ($rule.Pattern -eq 'END') {
            Invoke-AwkAction -Action $rule.Action -Fields @('') -Variables $variables -Output $endOutput -FieldSep $fieldSep
        }
    }
    foreach ($line in $endOutput) {
        New-BashObject -BashText "$line`n"
    }
}

function Split-AwkFields {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [string]$Line,
        [string]$FieldSep,
        [bool]$IsDefault
    )

    if ($IsDefault) {
        # Default awk behavior: split on runs of whitespace, trim leading/trailing
        $parts = $Line.Trim() -split '\s+'
        if ($parts.Count -eq 1 -and $parts[0] -eq '') {
            return @($Line)
        }
    } else {
        $escaped = [regex]::Escape($FieldSep)
        $parts = $Line -split $escaped
    }

    $result = [string[]]::new($parts.Count + 1)
    $result[0] = $Line
    for ($j = 0; $j -lt $parts.Count; $j++) {
        $result[$j + 1] = $parts[$j]
    }
    return $result
}

function ConvertFrom-AwkProgram {
    [CmdletBinding()]
    [OutputType([hashtable[]])]
    param(
        [Parameter(Mandatory)]
        [string]$Program
    )

    $rules = [System.Collections.Generic.List[hashtable]]::new()
    $pos = 0
    $len = $Program.Length

    while ($pos -lt $len) {
        # Skip whitespace and semicolons between rules
        while ($pos -lt $len -and ($Program[$pos] -match '[\s;]')) { $pos++ }
        if ($pos -ge $len) { break }

        $pattern = ''
        $action = $null

        # Check for BEGIN/END
        if ($pos + 5 -le $len -and $Program.Substring($pos, 5) -eq 'BEGIN') {
            $pattern = 'BEGIN'
            $pos += 5
            while ($pos -lt $len -and $Program[$pos] -match '\s') { $pos++ }
            if ($pos -lt $len -and $Program[$pos] -eq '{') {
                $action = Read-AwkBlock -Program $Program -Pos ([ref]$pos)
            }
            $rules.Add(@{ Pattern = $pattern; Action = $action })
            continue
        }

        if ($pos + 3 -le $len -and $Program.Substring($pos, 3) -eq 'END') {
            $afterEnd = $pos + 3
            if ($afterEnd -ge $len -or $Program[$afterEnd] -match '[\s{]') {
                $pattern = 'END'
                $pos = $afterEnd
                while ($pos -lt $len -and $Program[$pos] -match '\s') { $pos++ }
                if ($pos -lt $len -and $Program[$pos] -eq '{') {
                    $action = Read-AwkBlock -Program $Program -Pos ([ref]$pos)
                }
                $rules.Add(@{ Pattern = $pattern; Action = $action })
                continue
            }
        }

        # Check for /regex/ pattern
        if ($Program[$pos] -eq '/') {
            $endSlash = $pos + 1
            while ($endSlash -lt $len) {
                if ($Program[$endSlash] -eq '\') { $endSlash += 2; continue }
                if ($Program[$endSlash] -eq '/') { break }
                $endSlash++
            }
            $pattern = $Program.Substring($pos, $endSlash - $pos + 1)
            $pos = $endSlash + 1
            while ($pos -lt $len -and $Program[$pos] -match '\s') { $pos++ }
            if ($pos -lt $len -and $Program[$pos] -eq '{') {
                $action = Read-AwkBlock -Program $Program -Pos ([ref]$pos)
            }
            $rules.Add(@{ Pattern = $pattern; Action = $action })
            continue
        }

        # Check for action-only rule {action}
        if ($Program[$pos] -eq '{') {
            $action = Read-AwkBlock -Program $Program -Pos ([ref]$pos)
            $rules.Add(@{ Pattern = ''; Action = $action })
            continue
        }

        # Expression pattern (e.g. $2 > 8, NR > 1, $1 == "value")
        $exprStart = $pos
        while ($pos -lt $len -and $Program[$pos] -ne '{' -and -not ($pos -gt $exprStart -and $Program[$pos] -match '[\s]' -and $pos + 1 -lt $len -and $Program[$pos + 1] -eq '{')) {
            if ($Program[$pos] -eq '"') {
                $pos++
                while ($pos -lt $len -and $Program[$pos] -ne '"') {
                    if ($Program[$pos] -eq '\') { $pos++ }
                    $pos++
                }
                if ($pos -lt $len) { $pos++ }
                continue
            }
            $pos++
        }
        # Trim trailing whitespace from pattern
        $patEnd = $pos
        while ($patEnd -gt $exprStart -and $Program[$patEnd - 1] -match '\s') { $patEnd-- }
        $pattern = $Program.Substring($exprStart, $patEnd - $exprStart)

        while ($pos -lt $len -and $Program[$pos] -match '\s') { $pos++ }
        if ($pos -lt $len -and $Program[$pos] -eq '{') {
            $action = Read-AwkBlock -Program $Program -Pos ([ref]$pos)
        }
        $rules.Add(@{ Pattern = $pattern; Action = $action })
    }

    return , $rules.ToArray()
}

function Read-AwkBlock {
    param(
        [string]$Program,
        [ref]$Pos
    )

    $start = $Pos.Value + 1
    $depth = 1
    $p = $start

    while ($p -lt $Program.Length -and $depth -gt 0) {
        $ch = $Program[$p]
        if ($ch -eq '"') {
            $p++
            while ($p -lt $Program.Length -and $Program[$p] -ne '"') {
                if ($Program[$p] -eq '\') { $p++ }
                $p++
            }
        } elseif ($ch -eq '/') {
            # Could be regex in gsub/sub context - skip to closing /
            $prev = if ($p -gt 0) { $Program[$p - 1] } else { '' }
            if ($prev -eq '(' -or $prev -eq ',') {
                $p++
                while ($p -lt $Program.Length -and $Program[$p] -ne '/') {
                    if ($Program[$p] -eq '\') { $p++ }
                    $p++
                }
            }
        } elseif ($ch -eq '{') {
            $depth++
        } elseif ($ch -eq '}') {
            $depth--
        }
        $p++
    }

    $result = $Program.Substring($start, $p - $start - 1).Trim()
    $Pos.Value = $p
    return $result
}

function Test-AwkPattern {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [string]$Pattern,
        [string[]]$Fields,
        [hashtable]$Variables
    )

    if ($Pattern -eq '' -or $null -eq $Pattern) { return $true }

    # Regex pattern /pattern/
    if ($Pattern.StartsWith('/') -and $Pattern.EndsWith('/') -and $Pattern.Length -gt 2) {
        $regex = $Pattern.Substring(1, $Pattern.Length - 2)
        return [regex]::IsMatch($Fields[0], $regex)
    }

    # Expression pattern
    $val = Resolve-AwkExpression -Expr $Pattern -Fields $Fields -Variables $Variables
    if ($val -is [bool]) { return $val }
    if ($val -is [double] -or $val -is [int]) { return $val -ne 0 }
    if ($val -is [string]) { return $val.Length -gt 0 }
    return [bool]$val
}

function Resolve-AwkExpression {
    param(
        [string]$Expr,
        [string[]]$Fields,
        [hashtable]$Variables
    )

    $e = $Expr.Trim()

    # String literal
    if ($e.StartsWith('"') -and $e.EndsWith('"')) {
        return Expand-AwkString -Str $e.Substring(1, $e.Length - 2)
    }

    # Comparison operators (scan for >, <, >=, <=, ==, !=, ~ outside strings)
    $opPos = -1
    $opLen = 0
    $opType = ''
    $depth = 0
    $inStr = $false

    for ($ci = 0; $ci -lt $e.Length; $ci++) {
        $ch = $e[$ci]
        if ($ch -eq '"') { $inStr = -not $inStr; continue }
        if ($inStr) { continue }
        if ($ch -eq '(') { $depth++; continue }
        if ($ch -eq ')') { $depth--; continue }
        if ($depth -gt 0) { continue }

        if ($ci + 1 -lt $e.Length) {
            $two = $e.Substring($ci, 2)
            if ($two -eq '==' -or $two -eq '!=' -or $two -eq '>=' -or $two -eq '<=') {
                $opPos = $ci; $opLen = 2; $opType = $two; break
            }
        }
        if ($ch -eq '>' -and ($ci + 1 -ge $e.Length -or $e[$ci + 1] -ne '=')) {
            $opPos = $ci; $opLen = 1; $opType = '>'; break
        }
        if ($ch -eq '<' -and ($ci + 1 -ge $e.Length -or $e[$ci + 1] -ne '=')) {
            $opPos = $ci; $opLen = 1; $opType = '<'; break
        }
    }

    if ($opPos -gt 0) {
        $left = Resolve-AwkExpression -Expr $e.Substring(0, $opPos) -Fields $Fields -Variables $Variables
        $right = Resolve-AwkExpression -Expr $e.Substring($opPos + $opLen) -Fields $Fields -Variables $Variables

        # Try numeric comparison
        $leftNum = 0.0
        $rightNum = 0.0
        $bothNumeric = [double]::TryParse("$left", [ref]$leftNum) -and [double]::TryParse("$right", [ref]$rightNum)

        switch ($opType) {
            '==' { if ($bothNumeric) { return $leftNum -eq $rightNum } else { return "$left" -eq "$right" } }
            '!=' { if ($bothNumeric) { return $leftNum -ne $rightNum } else { return "$left" -ne "$right" } }
            '>'  { if ($bothNumeric) { return $leftNum -gt $rightNum } else { return "$left" -gt "$right" } }
            '<'  { if ($bothNumeric) { return $leftNum -lt $rightNum } else { return "$left" -lt "$right" } }
            '>=' { if ($bothNumeric) { return $leftNum -ge $rightNum } else { return "$left" -ge "$right" } }
            '<=' { if ($bothNumeric) { return $leftNum -le $rightNum } else { return "$left" -le "$right" } }
        }
    }

    # Arithmetic: + - * / % (scan right-to-left for +/- then */% for precedence)
    $depth = 0; $inStr = $false
    for ($ci = $e.Length - 1; $ci -ge 1; $ci--) {
        $ch = $e[$ci]
        if ($ch -eq '"') { $inStr = -not $inStr; continue }
        if ($inStr) { continue }
        if ($ch -eq ')') { $depth++; continue }
        if ($ch -eq '(') { $depth--; continue }
        if ($depth -gt 0) { continue }

        if (($ch -eq '+' -or $ch -eq '-') -and $ci -gt 0) {
            $left = Resolve-AwkExpression -Expr $e.Substring(0, $ci) -Fields $Fields -Variables $Variables
            $right = Resolve-AwkExpression -Expr $e.Substring($ci + 1) -Fields $Fields -Variables $Variables
            $lv = 0.0; $rv = 0.0
            [void][double]::TryParse("$left", [ref]$lv)
            [void][double]::TryParse("$right", [ref]$rv)
            if ($ch -eq '+') { return $lv + $rv } else { return $lv - $rv }
        }
    }

    $depth = 0; $inStr = $false
    for ($ci = $e.Length - 1; $ci -ge 1; $ci--) {
        $ch = $e[$ci]
        if ($ch -eq '"') { $inStr = -not $inStr; continue }
        if ($inStr) { continue }
        if ($ch -eq ')') { $depth++; continue }
        if ($ch -eq '(') { $depth--; continue }
        if ($depth -gt 0) { continue }

        if ($ch -eq '*' -or $ch -eq '/' -or $ch -eq '%') {
            $left = Resolve-AwkExpression -Expr $e.Substring(0, $ci) -Fields $Fields -Variables $Variables
            $right = Resolve-AwkExpression -Expr $e.Substring($ci + 1) -Fields $Fields -Variables $Variables
            $lv = 0.0; $rv = 0.0
            [void][double]::TryParse("$left", [ref]$lv)
            [void][double]::TryParse("$right", [ref]$rv)
            if ($ch -eq '*') { return $lv * $rv }
            if ($ch -eq '/' -and $rv -ne 0) { return $lv / $rv }
            if ($ch -eq '%' -and $rv -ne 0) { return $lv % $rv }
            return 0
        }
    }

    # Parenthesized expression
    if ($e.StartsWith('(') -and $e.EndsWith(')')) {
        return Resolve-AwkExpression -Expr $e.Substring(1, $e.Length - 2) -Fields $Fields -Variables $Variables
    }

    # Function call: name(args)
    $funcMatch = [regex]::Match($e, '^(length|substr|tolower|toupper|sprintf|match|strftime|systime|index|split|rand|srand|sin|cos|atan2|exp|log|sqrt|int)\s*\((.*)$')
    if ($funcMatch.Success) {
        $fName = $funcMatch.Groups[1].Value
        $rest = $funcMatch.Groups[2].Value
        # Find matching closing paren
        $pd = 1; $pi = 0
        $inQ = $false
        while ($pi -lt $rest.Length -and $pd -gt 0) {
            if ($rest[$pi] -eq '"') { $inQ = -not $inQ }
            if (-not $inQ) {
                if ($rest[$pi] -eq '(') { $pd++ }
                if ($rest[$pi] -eq ')') { $pd-- }
            }
            if ($pd -gt 0) { $pi++ }
        }
        $argText = $rest.Substring(0, $pi)
        $fArgs = @(Split-AwkFuncArgs -Text $argText)
        return Resolve-AwkStringFunc -FuncName $fName -FuncArgs $fArgs -Fields $Fields -Variables $Variables
    }

    # Field reference $N or $NF
    if ($e.StartsWith('$')) {
        $fieldExpr = $e.Substring(1)
        if ($fieldExpr -eq 'NF') {
            $idx = $Fields.Count - 1
        } else {
            $idx = 0
            [void][int]::TryParse($fieldExpr, [ref]$idx)
        }
        if ($idx -ge 0 -and $idx -lt $Fields.Count) {
            return $Fields[$idx]
        }
        return ''
    }

    # Numeric literal
    $numVal = 0.0
    if ([double]::TryParse($e, [ref]$numVal)) {
        return $numVal
    }

    # Built-in variable or user variable
    if ($Variables.ContainsKey($e)) {
        return $Variables[$e]
    }

    return $e
}

function Expand-AwkString {
    param([string]$Str)
    $Str = $Str -replace '\\n', "`n"
    $Str = $Str -replace '\\t', "`t"
    $Str = $Str -replace '\\\\', '\'
    return $Str
}

function Invoke-AwkAction {
    param(
        [string]$Action,
        [string[]]$Fields,
        [hashtable]$Variables,
        [System.Collections.Generic.List[string]]$Output,
        [string]$FieldSep,
        [System.Text.StringBuilder]$PrintfBuffer = $null
    )

    # Split action into statements by semicolons (respecting strings and parens)
    $statements = @(Split-AwkStatements -Text $Action)

    foreach ($stmt in $statements) {
        $s = $stmt.Trim()
        if ($s.Length -eq 0) { continue }

        # Assignment: var = expr (but not ==)
        $assignMatch = [regex]::Match($s, '^([A-Za-z_]\w*)\s*=\s*(.+)$')
        if ($assignMatch.Success -and -not $s.Contains('==')) {
            $vName = $assignMatch.Groups[1].Value
            $vVal = Resolve-AwkExpression -Expr $assignMatch.Groups[2].Value -Fields $Fields -Variables $Variables
            $Variables[$vName] = $vVal
            if ($vName -eq 'OFS' -or $vName -eq 'FS') {
                # These are tracked via the variables hashtable
            }
            continue
        }

        # delete array[key] or delete array (clear all)
        if ($s -match '^delete\s+') {
            $delTarget = $s.Substring(7).Trim()
            $delMatch = [regex]::Match($delTarget, '^([A-Za-z_]\w*)\[(.+)\]$')
            if ($delMatch.Success) {
                $arrName = $delMatch.Groups[1].Value
                $keyExpr = $delMatch.Groups[2].Value
                $key = Resolve-AwkExpression -Expr $keyExpr -Fields $Fields -Variables $Variables
                $keyStr = "$key"
                $keysToRemove = @($Variables.Keys | Where-Object { $_ -like "$arrName[*]" })
                foreach ($k in $keysToRemove) {
                    $kKey = $k -replace "^$([regex]::Escape($arrName))\[(.+)\]$", '$1'
                    if ($kKey -eq $keyStr) {
                        $Variables.Remove($k)
                    }
                }
            } else {
                $arrName = $delTarget
                $keysToRemove = @($Variables.Keys | Where-Object { $_ -like "$arrName[*]" })
                foreach ($k in $keysToRemove) {
                    $Variables.Remove($k)
                }
                if ($Variables.ContainsKey($arrName)) {
                    $Variables.Remove($arrName)
                }
            }
            continue
        }

        # gsub(/regex/, "replacement") or gsub(/regex/, "replacement", target)
        if ($s -match '^gsub\s*\(') {
            $argsStr = $s.Substring($s.IndexOf('(') + 1)
            $argsStr = $argsStr.Substring(0, $argsStr.LastIndexOf(')'))
            $gsubArgs = @(Split-AwkFuncArgs -Text $argsStr)
            if ($gsubArgs.Count -ge 2) {
                $regex = $gsubArgs[0].Trim()
                if ($regex.StartsWith('/') -and $regex.EndsWith('/')) {
                    $regex = $regex.Substring(1, $regex.Length - 2)
                }
                $repl = Resolve-AwkExpression -Expr $gsubArgs[1].Trim() -Fields $Fields -Variables $Variables
                $Fields[0] = [regex]::Replace($Fields[0], $regex, "$repl")
                # Re-split fields
                $newFields = Split-AwkFields -Line $Fields[0] -FieldSep $FieldSep -IsDefault ($FieldSep -eq ' ')
                for ($fi = 0; $fi -lt $newFields.Count -and $fi -lt $Fields.Count; $fi++) {
                    $Fields[$fi] = $newFields[$fi]
                }
            }
            continue
        }

        # sub(/regex/, "replacement")
        if ($s -match '^sub\s*\(') {
            $argsStr = $s.Substring($s.IndexOf('(') + 1)
            $argsStr = $argsStr.Substring(0, $argsStr.LastIndexOf(')'))
            $subArgs = @(Split-AwkFuncArgs -Text $argsStr)
            if ($subArgs.Count -ge 2) {
                $regex = $subArgs[0].Trim()
                if ($regex.StartsWith('/') -and $regex.EndsWith('/')) {
                    $regex = $regex.Substring(1, $regex.Length - 2)
                }
                $repl = Resolve-AwkExpression -Expr $subArgs[1].Trim() -Fields $Fields -Variables $Variables
                $Fields[0] = [regex]::new($regex).Replace($Fields[0], "$repl", 1)
                $newFields = Split-AwkFields -Line $Fields[0] -FieldSep $FieldSep -IsDefault ($FieldSep -eq ' ')
                for ($fi = 0; $fi -lt $newFields.Count -and $fi -lt $Fields.Count; $fi++) {
                    $Fields[$fi] = $newFields[$fi]
                }
            }
            continue
        }

        # printf "fmt", args...
        if ($s -match '^printf\s+') {
            $printfArgs = $s.Substring(6).Trim()
            $parts = @(Split-AwkFuncArgs -Text $printfArgs)
            if ($parts.Count -ge 1) {
                $fmt = Resolve-AwkExpression -Expr $parts[0].Trim() -Fields $Fields -Variables $Variables
                $fmtStr = "$fmt"
                $argVals = @()
                for ($ai = 1; $ai -lt $parts.Count; $ai++) {
                    $argVals += Resolve-AwkExpression -Expr $parts[$ai].Trim() -Fields $Fields -Variables $Variables
                }
                $formatted = Format-AwkPrintf -Format $fmtStr -FormatArgs $argVals
                if ($null -ne $PrintfBuffer) {
                    [void]$PrintfBuffer.Append($formatted)
                    # Emit complete lines from buffer
                    $bufStr = $PrintfBuffer.ToString()
                    while ($bufStr.Contains("`n")) {
                        $nlIdx = $bufStr.IndexOf("`n")
                        $Output.Add($bufStr.Substring(0, $nlIdx))
                        $bufStr = $bufStr.Substring($nlIdx + 1)
                    }
                    [void]$PrintfBuffer.Clear()
                    [void]$PrintfBuffer.Append($bufStr)
                } else {
                    $Output.Add($formatted)
                }
            }
            continue
        }

        # print expr, expr, ...
        if ($s -match '^print\s*(.*)$') {
            $printArgs = $Matches[1].Trim()
            if ($printArgs.Length -eq 0) {
                $Output.Add($Fields[0])
            } else {
                $ofs = if ($Variables.ContainsKey('OFS')) { "$($Variables['OFS'])" } else { ' ' }
                $parts = @(Split-AwkFuncArgs -Text $printArgs)
                $vals = [System.Collections.Generic.List[string]]::new()
                foreach ($part in $parts) {
                    $val = Resolve-AwkExpression -Expr $part.Trim() -Fields $Fields -Variables $Variables
                    $numCheck = 0.0
                    if ($val -is [double]) {
                        $intVal = [int]$val
                        if ([double]$intVal -eq [double]$val) {
                            $vals.Add("$intVal")
                        } else {
                            $vals.Add("$val")
                        }
                    } else {
                        $vals.Add("$val")
                    }
                }
                $Output.Add($vals -join $ofs)
            }
            continue
        }

        # Bare print (no arguments, just "print")
        if ($s -eq 'print') {
            $Output.Add($Fields[0])
            continue
        }

        # Bare function call: match(...), srand(), etc.
        $funcMatch = [regex]::Match($s, '^(gsub|sub|match|srand)\s*\(')
        if ($funcMatch.Success) {
            $funcName = $funcMatch.Groups[1].Value
            $argsStr = $s.Substring($s.IndexOf('(') + 1)
            $argsStr = $argsStr.Substring(0, $argsStr.LastIndexOf(')'))
            $fArgs = @(Split-AwkFuncArgs -Text $argsStr)
            $funcResult = Resolve-AwkStringFunc -FuncName $funcName -FuncArgs $fArgs -Fields $Fields -Variables $Variables
            # gsub/sub need field re-splitting
            if ($funcName -eq 'gsub' -or $funcName -eq 'sub') {
                if ($funcName -eq 'gsub' -and $fArgs.Count -ge 2) {
                    $regex = $fArgs[0].Trim()
                    if ($regex.StartsWith('/') -and $regex.EndsWith('/')) { $regex = $regex.Substring(1, $regex.Length - 2) }
                    $repl = Resolve-AwkExpression -Expr $fArgs[1].Trim() -Fields $Fields -Variables $Variables
                    $target = if ($fArgs.Count -ge 3) {
                        $tExpr = $fArgs[2].Trim()
                        $tVal = Resolve-AwkExpression -Expr $tExpr -Fields $Fields -Variables $Variables
                        "$tVal"
                    } else { $Fields[0] }
                    $newVal = [regex]::Replace($target, $regex, "$repl")
                    if ($fArgs.Count -lt 3) { $Fields[0] = $newVal }
                    $newFields = Split-AwkFields -Line $Fields[0] -FieldSep $FieldSep -IsDefault ($FieldSep -eq ' ')
                    for ($fi = 0; $fi -lt $newFields.Count -and $fi -lt $Fields.Count; $fi++) { $Fields[$fi] = $newFields[$fi] }
                } elseif ($funcName -eq 'sub' -and $fArgs.Count -ge 2) {
                    $regex = $fArgs[0].Trim()
                    if ($regex.StartsWith('/') -and $regex.EndsWith('/')) { $regex = $regex.Substring(1, $regex.Length - 2) }
                    $repl = Resolve-AwkExpression -Expr $fArgs[1].Trim() -Fields $Fields -Variables $Variables
                    $target = if ($fArgs.Count -ge 3) {
                        $tExpr = $fArgs[2].Trim()
                        $tVal = Resolve-AwkExpression -Expr $tExpr -Fields $Fields -Variables $Variables
                        "$tVal"
                    } else { $Fields[0] }
                    $newVal = [regex]::new($regex).Replace($target, "$repl", 1)
                    if ($fArgs.Count -lt 3) { $Fields[0] = $newVal }
                    $newFields = Split-AwkFields -Line $Fields[0] -FieldSep $FieldSep -IsDefault ($FieldSep -eq ' ')
                    for ($fi = 0; $fi -lt $newFields.Count -and $fi -lt $Fields.Count; $fi++) { $Fields[$fi] = $newFields[$fi] }
                }
            }
            continue
        }
    }
}

function Split-AwkStatements {
    param([string]$Text)

    $results = [System.Collections.Generic.List[string]]::new()
    $current = [System.Text.StringBuilder]::new()
    $inStr = $false
    $depth = 0

    for ($ci = 0; $ci -lt $Text.Length; $ci++) {
        $ch = $Text[$ci]
        if ($ch -eq '"' -and ($ci -eq 0 -or $Text[$ci - 1] -ne '\')) {
            $inStr = -not $inStr
            [void]$current.Append($ch)
            continue
        }
        if ($inStr) { [void]$current.Append($ch); continue }
        if ($ch -eq '(') { $depth++; [void]$current.Append($ch); continue }
        if ($ch -eq ')') { $depth--; [void]$current.Append($ch); continue }
        if ($ch -eq ';' -and $depth -eq 0) {
            $results.Add($current.ToString())
            [void]$current.Clear()
            continue
        }
        [void]$current.Append($ch)
    }
    if ($current.Length -gt 0) { $results.Add($current.ToString()) }
    return $results.ToArray()
}

function Split-AwkFuncArgs {
    param([string]$Text)

    $results = [System.Collections.Generic.List[string]]::new()
    $current = [System.Text.StringBuilder]::new()
    $inStr = $false
    $depth = 0
    $inRegex = $false

    for ($ci = 0; $ci -lt $Text.Length; $ci++) {
        $ch = $Text[$ci]
        if ($ch -eq '/' -and -not $inStr) {
            if (-not $inRegex -and ($ci -eq 0 -or $Text[$ci - 1] -match '[,(]')) {
                $inRegex = $true
                [void]$current.Append($ch)
                continue
            } elseif ($inRegex) {
                $inRegex = $false
                [void]$current.Append($ch)
                continue
            }
        }
        if ($inRegex) { [void]$current.Append($ch); continue }
        if ($ch -eq '"' -and ($ci -eq 0 -or $Text[$ci - 1] -ne '\')) {
            $inStr = -not $inStr
            [void]$current.Append($ch)
            continue
        }
        if ($inStr) { [void]$current.Append($ch); continue }
        if ($ch -eq '(') { $depth++; [void]$current.Append($ch); continue }
        if ($ch -eq ')') { $depth--; [void]$current.Append($ch); continue }
        if ($ch -eq ',' -and $depth -eq 0) {
            $results.Add($current.ToString())
            [void]$current.Clear()
            continue
        }
        [void]$current.Append($ch)
    }
    if ($current.Length -gt 0) { $results.Add($current.ToString()) }
    return $results.ToArray()
}

function Format-AwkPrintf {
    param(
        [string]$Format,
        [array]$FormatArgs
    )

    $result = [System.Text.StringBuilder]::new()
    $argIdx = 0
    $i = 0

    while ($i -lt $Format.Length) {
        $ch = $Format[$i]
        if ($ch -eq '%' -and ($i + 1) -lt $Format.Length) {
            $i++
            # Read flags, width, precision
            $fmtSpec = [System.Text.StringBuilder]::new()
            while ($i -lt $Format.Length -and $Format[$i] -match '[-+ 0#]') {
                [void]$fmtSpec.Append($Format[$i])
                $i++
            }
            while ($i -lt $Format.Length -and $Format[$i] -match '\d') {
                [void]$fmtSpec.Append($Format[$i])
                $i++
            }
            if ($i -lt $Format.Length -and $Format[$i] -eq '.') {
                [void]$fmtSpec.Append($Format[$i])
                $i++
                while ($i -lt $Format.Length -and $Format[$i] -match '\d') {
                    [void]$fmtSpec.Append($Format[$i])
                    $i++
                }
            }
            if ($i -lt $Format.Length) {
                $conv = $Format[$i]
                $argVal = if ($argIdx -lt $FormatArgs.Count) { $FormatArgs[$argIdx] } else { '' }
                $argIdx++
                switch ($conv) {
                    's' { [void]$result.Append("$argVal") }
                    'd' {
                        $nv = 0; [void][int]::TryParse("$argVal", [ref]$nv)
                        [void]$result.Append($nv)
                    }
                    'f' {
                        $nv = 0.0; [void][double]::TryParse("$argVal", [ref]$nv)
                        [void]$result.Append($nv.ToString('F6'))
                    }
                    '%' { [void]$result.Append('%'); $argIdx-- }
                    default { [void]$result.Append($conv) }
                }
                $i++
            }
        } elseif ($ch -eq '\' -and ($i + 1) -lt $Format.Length) {
            $i++
            switch ($Format[$i]) {
                'n' { [void]$result.Append("`n") }
                't' { [void]$result.Append("`t") }
                '\' { [void]$result.Append('\') }
                default { [void]$result.Append('\'); [void]$result.Append($Format[$i]) }
            }
            $i++
        } else {
            [void]$result.Append($ch)
            $i++
        }
    }

    return $result.ToString()
}

# String function support in Resolve-AwkExpression
# Extend Resolve-AwkExpression to handle function calls
function Resolve-AwkStringFunc {
    param(
        [string]$FuncName,
        [string[]]$FuncArgs,
        [string[]]$Fields,
        [hashtable]$Variables
    )

    switch ($FuncName) {
        'length' {
            $val = if ($FuncArgs.Count -gt 0) {
                Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            } else { $Fields[0] }
            return "$val".Length
        }
        'substr' {
            if ($FuncArgs.Count -ge 2) {
                $str = "$(Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables)"
                $start = 0; [void][int]::TryParse("$(Resolve-AwkExpression -Expr $FuncArgs[1] -Fields $Fields -Variables $Variables)", [ref]$start)
                $start-- # awk is 1-based
                if ($start -lt 0) { $start = 0 }
                if ($FuncArgs.Count -ge 3) {
                    $len = 0; [void][int]::TryParse("$(Resolve-AwkExpression -Expr $FuncArgs[2] -Fields $Fields -Variables $Variables)", [ref]$len)
                    if ($start + $len -gt $str.Length) { $len = $str.Length - $start }
                    return $str.Substring($start, $len)
                }
                return $str.Substring($start)
            }
            return ''
        }
        'tolower' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            return "$val".ToLower()
        }
        'toupper' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            return "$val".ToUpper()
        }
        'sprintf' {
            if ($FuncArgs.Count -ge 1) {
                $fmt = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
                $fmtStr = "$fmt"
                $argVals = @()
                for ($ai = 1; $ai -lt $FuncArgs.Count; $ai++) {
                    $argVals += Resolve-AwkExpression -Expr $FuncArgs[$ai] -Fields $Fields -Variables $Variables
                }
                return Format-AwkPrintf -Format $fmtStr -FormatArgs $argVals
            }
            return ''
        }
        'match' {
            if ($FuncArgs.Count -ge 2) {
                $str = "$(Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables)"
                $regexArg = $FuncArgs[1].Trim()
                if ($regexArg.StartsWith('/') -and $regexArg.EndsWith('/')) {
                    $regexArg = $regexArg.Substring(1, $regexArg.Length - 2)
                }
                $m = [regex]::Match($str, $regexArg)
                if ($m.Success) {
                    $Variables['RSTART'] = $m.Index + 1
                    $Variables['RLENGTH'] = $m.Length
                    return $m.Index + 1
                }
                $Variables['RSTART'] = 0
                $Variables['RLENGTH'] = -1
                return 0
            }
            $Variables['RSTART'] = 0
            $Variables['RLENGTH'] = -1
            return 0
        }
        'strftime' {
            $fmtVal = if ($FuncArgs.Count -ge 1) {
                Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            } else {
                '%Y-%m-%d %H:%M:%S'
            }
            $timestamp = if ($FuncArgs.Count -ge 2) {
                Resolve-AwkExpression -Expr $FuncArgs[1] -Fields $Fields -Variables $Variables
            } else {
                $null
            }
            $epoch = [DateTimeOffset]::UnixEpoch
            $dt = if ($null -ne $timestamp -and "$timestamp" -ne '') {
                $ts = 0.0; [void][double]::TryParse("$timestamp", [ref]$ts)
                $epoch.AddSeconds($ts).DateTime
            } else {
                [DateTimeOffset]::UtcNow.DateTime
            }
            $fmtStr = "$fmtVal"
            # Map C/awk strftime specifiers to .NET format strings
            # Protect %% first to avoid double-replacement
            $fmtStr = $fmtStr -replace '%%', [char]0x01
            $fmtStr = $fmtStr -replace '%Y', $dt.ToString('yyyy')
            $fmtStr = $fmtStr -replace '%m', $dt.ToString('MM')
            $fmtStr = $fmtStr -replace '%d', $dt.ToString('dd')
            $fmtStr = $fmtStr -replace '%H', $dt.ToString('HH')
            $fmtStr = $fmtStr -replace '%M', $dt.ToString('mm')
            $fmtStr = $fmtStr -replace '%S', $dt.ToString('ss')
            $fmtStr = $fmtStr -replace '%j', $dt.DayOfYear.ToString('000')
            $fmtStr = $fmtStr -replace '%w', "$([int]$dt.DayOfWeek)"
            $fmtStr = $fmtStr -replace '%a', $dt.ToString('ddd')
            $fmtStr = $fmtStr -replace '%A', $dt.ToString('dddd')
            $fmtStr = $fmtStr -replace '%b', $dt.ToString('MMM')
            $fmtStr = $fmtStr -replace '%B', $dt.ToString('MMMM')
            $fmtStr = $fmtStr -replace '%p', $dt.ToString('tt')
            $fmtStr = $fmtStr -replace '%I', $dt.ToString('hh')
            $fmtStr = $fmtStr -replace [char]0x01, '%'
            return $fmtStr
        }
        'systime' {
            return [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        }
        'index' {
            if ($FuncArgs.Count -ge 2) {
                $str = "$(Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables)"
                $substr = "$(Resolve-AwkExpression -Expr $FuncArgs[1] -Fields $Fields -Variables $Variables)"
                $idx = $str.IndexOf($substr)
                return if ($idx -ge 0) { $idx + 1 } else { 0 }
            }
            return 0
        }
        'split' {
            if ($FuncArgs.Count -ge 2) {
                $str = "$(Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables)"
                $sepExpr = $FuncArgs[1].Trim()
                $sep = if ($sepExpr.StartsWith('/') -and $sepExpr.EndsWith('/')) {
                    $sepExpr.Substring(1, $sepExpr.Length - 2)
                } else {
                    "$(Resolve-AwkExpression -Expr $sepExpr -Fields $Fields -Variables $Variables)"
                }
                $parts = if ($sep.Length -eq 1 -and $sep -notmatch '[\[\]\(\)\{\}\.\+\*\?\^\$\|]') {
                    $str.Split([char]$sep[0])
                } else {
                    [regex]::Split($str, $sep)
                }
                if ($FuncArgs.Count -ge 3) {
                    $arrName = $FuncArgs[2].Trim()
                    $arr = @()
                    for ($ai = 0; $ai -lt $parts.Count; $ai++) {
                        $arr += "$($parts[$ai])"
                        $Variables["$arrName[$($ai + 1)]"] = "$($parts[$ai])"
                    }
                    $variables[$arrName] = $arr
                }
                return $parts.Count
            }
            return 0
        }
        'int' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            return [int][math]::Truncate($num)
        }
        'rand' { return ($script:AwkRand ?? [System.Random]::Shared).NextDouble() }
        'srand' {
            if ($FuncArgs.Count -ge 1) {
                $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
                $seed = 0; [void][int]::TryParse("$val", [ref]$seed)
                $script:AwkRand = [System.Random]::new($seed)
            } else {
                $script:AwkRand = [System.Random]::new()
            }
            return [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        }
        'sin' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            return [math]::Sin($num)
        }
        'cos' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            return [math]::Cos($num)
        }
        'atan2' {
            if ($FuncArgs.Count -ge 2) {
                $y = 0.0; $x = 0.0
                $yv = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
                $xv = Resolve-AwkExpression -Expr $FuncArgs[1] -Fields $Fields -Variables $Variables
                [void][double]::TryParse("$yv", [ref]$y)
                [void][double]::TryParse("$xv", [ref]$x)
                return [math]::Atan2($y, $x)
            }
            return 0
        }
        'exp' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            return [math]::Exp($num)
        }
        'log' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            if ($num -gt 0) { return [math]::Log($num) }
            return 0
        }
        'sqrt' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            if ($num -ge 0) { return [math]::Sqrt($num) }
            return 0
        }
        default { return '' }
    }
}

