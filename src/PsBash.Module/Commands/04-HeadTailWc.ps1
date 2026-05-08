# --- head Command ---

function Invoke-BashHead {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'head' }

    # Manual arg parsing for value-bearing -n and -c flags
    $count = 10
    $byteCount = $null
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

        if ($arg -cmatch '^-n(\d+)$') {
            $count = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-n') {
            $i++
            if ($i -lt $Arguments.Count) {
                $count = [int]$Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-c(\d+)$') {
            $byteCount = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-c') {
            $i++
            if ($i -lt $Arguments.Count) {
                $byteCount = [int]$Arguments[$i]
            }
            $i++
            continue
        }

        # Legacy -N shorthand (e.g., head -5)
        if ($arg -cmatch '^-(\d+)$') {
            $count = [int]$Matches[1]
            $i++
            continue
        }

        # Bare positional number (e.g., head 5)
        if ($arg -cmatch '^\d+$' -and $operands.Count -eq 0) {
            $count = [int]$arg
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Pipeline mode
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        if ($null -ne $byteCount) {
            if ($byteCount -le 0) { return }
            # Byte mode: concatenate all input and take first N bytes
            $allText = ($pipelineInput | ForEach-Object { Get-BashText -InputObject $_ }) -join "`n"
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($allText)
            $take = [System.Math]::Min($bytes.Length, $byteCount)
            [System.Text.Encoding]::UTF8.GetString($bytes, 0, $take)
            return
        }
        $emitted = 0
        foreach ($item in $pipelineInput) {
            if ($emitted -ge $count) { break }
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    if ($emitted -ge $count) { break }
                    $subLine
                    $emitted++
                }
            } else {
                $item
                $emitted++
            }
        }
        return
    }

    # File mode — resolve globs, stream lines/bytes with early exit
    $resolvedFiles = Resolve-BashGlob -Paths $operands
    foreach ($filePath in $resolvedFiles) {
        if ($null -ne $byteCount) {
            if ($byteCount -le 0) { continue }
            try {
                $bytes = [System.IO.File]::ReadAllBytes($filePath)
                $take = [System.Math]::Min($bytes.Length, $byteCount)
                [System.Text.Encoding]::UTF8.GetString($bytes, 0, $take)
            } catch {
                Write-BashError -Message "head: cannot read '$filePath': $($_.Exception.Message)"
            }
            continue
        }

        $reader = Open-BashFileReader -Path $filePath -Command 'head'
        if ($null -eq $reader) { continue }

        try {
            $li = 0
            while ($li -lt $count -and $null -ne ($line = $reader.ReadLine())) {
                $li++
                $obj = [PSCustomObject]@{
                    PSTypeName = 'PsBash.CatLine'
                    LineNumber = $li
                    Content    = $line
                    FileName   = $filePath
                    BashText   = $line
                }
                Set-BashDisplayProperty $obj
            }
        } finally {
            $reader.Dispose()
        }
    }
}

# --- tail Command ---

function Invoke-BashTail {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'tail' }

    # Manual arg parsing for value-bearing flags
    $count = 10
    $byteCount = $null
    $fromLine = $false
    $followFile = $false     # -f / --follow: follow file for new content
    $sleepInterval = 1.0     # -s / --sleep-interval: poll interval in seconds
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

        if ($arg -ceq '-f' -or $arg -ceq '--follow') {
            $followFile = $true
            $i++
            continue
        }

        if ($arg -ceq '--sleep-interval') {
            $i++
            if ($i -lt $Arguments.Count) {
                $sleepInterval = [double]$Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-s([\d.]+)$') {
            $sleepInterval = [double]$Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-s') {
            $i++
            if ($i -lt $Arguments.Count) {
                $sleepInterval = [double]$Arguments[$i]
            }
            $i++
            continue
        }

        # -c +N syntax (from byte N onward)
        if ($arg -cmatch '^-c\+(\d+)$') {
            $byteCount = [int]$Matches[1]
            $fromLine = $true
            $i++
            continue
        }

        if ($arg -cmatch '^-c(\d+)$') {
            $byteCount = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-c' -or $arg -ceq '--bytes') {
            $i++
            if ($i -lt $Arguments.Count) {
                $val = $Arguments[$i]
                if ($val.StartsWith('+')) {
                    $byteCount = [int]$val.Substring(1)
                    $fromLine = $true
                } else {
                    $byteCount = [int]$val
                }
            }
            $i++
            continue
        }

        # -n +N syntax (from line N onward)
        if ($arg -cmatch '^-n\+(\d+)$') {
            $count = [int]$Matches[1]
            $fromLine = $true
            $i++
            continue
        }

        if ($arg -cmatch '^-n(\d+)$') {
            $count = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-n') {
            $i++
            if ($i -lt $Arguments.Count) {
                $val = $Arguments[$i]
                if ($val.StartsWith('+')) {
                    $count = [int]$val.Substring(1)
                    $fromLine = $true
                } else {
                    $count = [int]$val
                }
            }
            $i++
            continue
        }

        # Legacy -N shorthand (e.g., tail -5)
        if ($arg -cmatch '^-(\d+)$') {
            $count = [int]$Matches[1]
            $i++
            continue
        }

        # Bare positional number (e.g., tail 5)
        if ($arg -cmatch '^\d+$' -and $operands.Count -eq 0) {
            $count = [int]$arg
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Pipeline mode
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        if ($fromLine) {
            # +N mode: skip first N-1 items, emit the rest
            $skip = $count - 1
            $idx = 0
            foreach ($item in $pipelineInput) {
                $text = Get-BashText -InputObject $item
                if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                    foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                        if ($idx -ge $skip) {
                            $subLine
                        }
                        $idx++
                    }
                } else {
                    if ($idx -ge $skip) {
                        $item
                    }
                    $idx++
                }
            }
        } else {
            # -N mode: circular buffer — only keep last N items in memory
            $buf = [object[]]::new($count)
            $bufLen = 0
            $pos = 0

            foreach ($item in $pipelineInput) {
                $text = Get-BashText -InputObject $item
                if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                    foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                        $wrapped = $subLine
                        $buf[$pos] = $wrapped
                        $pos = ($pos + 1) % $count
                        if ($bufLen -lt $count) { $bufLen++ }
                    }
                } else {
                    $buf[$pos] = $item
                    $pos = ($pos + 1) % $count
                    if ($bufLen -lt $count) { $bufLen++ }
                }
            }

            # Emit the circular buffer in order (oldest first)
            $start = if ($bufLen -lt $count) { 0 } else { $pos }
            for ($i = 0; $i -lt $bufLen; $i++) {
                $buf[($start + $i) % $count]
            }
        }
        return
    }

    # File mode — resolve globs
    $resolvedFiles = @(Resolve-BashGlob -Paths $operands)

    if ($resolvedFiles.Count -eq 0) {
        return
    }

    $filePath = $resolvedFiles[0]

    # -c bytes mode
    if ($null -ne $byteCount) {
        $rawText = Read-BashFileBytes -Path $filePath -Command 'tail'
        if ($null -eq $rawText) { return }

        if ($fromLine) {
            # -c +N: output starting from byte offset N
            $startIdx = [System.Math]::Min($byteCount, $rawText.Length)
            Emit-BashLine -Text $rawText.Substring($startIdx)
        } else {
            # -c N: output last N bytes
            $startIdx = [System.Math]::Max(0, $rawText.Length - $byteCount)
            Emit-BashLine -Text $rawText.Substring($startIdx)
        }
        return
    }

    if ($followFile) {
        # Follow mode: continuously monitor file for new lines using polling
        try {
            $lines = Read-BashFileLines -Path $filePath -Command 'tail'
            if ($null -eq $lines) { $lines = @() }

            if ($fromLine) {
                $lastOutputLine = $count - 1
            } else {
                $lastOutputLine = [System.Math]::Max(-1, $lines.Count - $count - 1)
            }

            # Output initial lines
            for ($li = $lastOutputLine + 1; $li -lt $lines.Count; $li++) {
                Emit-BashLine -Text $lines[$li]
            }

            # Track file length to avoid re-reading entire file each poll
            $filePos = [System.IO.FileInfo]::new($filePath).Length

            # Monitor file for new content
            while ($true) {
                Start-Sleep -Seconds $sleepInterval
                $info = [System.IO.FileInfo]::new($filePath)
                if ($info.Length -gt $filePos) {
                    $newContent = $null
                    try {
                        $fs = [System.IO.FileStream]::new($filePath, 'Open', 'Read', 'ReadWrite')
                        try {
                            $sr = [System.IO.StreamReader]::new($fs)
                            try {
                                $null = $fs.Seek($filePos, 'Begin')
                                $newContent = $sr.ReadToEnd()
                                $filePos = $fs.Position
                            } finally {
                                $sr.Dispose()
                            }
                        } finally {
                            $fs.Dispose()
                        }
                    } catch {
                        continue
                    }
                    if ($null -ne $newContent -and $newContent.Length -gt 0) {
                        if ($newContent.EndsWith("`n")) {
                            $newContent = $newContent.Substring(0, $newContent.Length - 1)
                        }
                        $newContent = $newContent -replace "`r`n", "`n"
                        foreach ($line in $newContent.Split("`n")) {
                            Emit-BashLine -Text $line
                        }
                    }
                } elseif ($info.Length -lt $filePos) {
                    # File was truncated or rotated — reset position
                    $filePos = 0
                }
            }
        } catch {
            Write-BashError -Message "tail: cannot follow file: $_" -ExitCode 1
        }
    } else {
        # Normal mode: output last N lines
        foreach ($filePath in $resolvedFiles) {
            if ($fromLine) {
                # Stream and skip first N-1 lines without buffering
                $reader = Open-BashFileReader -Path $filePath -Command 'tail'
                if ($null -eq $reader) { continue }

                try {
                    $li = 0
                    while ($null -ne ($line = $reader.ReadLine())) {
                        $li++
                        if ($li -ge $count) {
                            $obj = [PSCustomObject]@{
                                PSTypeName = 'PsBash.CatLine'
                                LineNumber = $li
                                Content    = $line
                                FileName   = $filePath
                                BashText   = $line
                            }
                            Set-BashDisplayProperty $obj
                        }
                    }
                } finally {
                    $reader.Dispose()
                }
            } else {
                # Circular buffer — stream lines, keep only last N in memory
                $reader = Open-BashFileReader -Path $filePath -Command 'tail'
                if ($null -eq $reader) { continue }

                try {
                    $buf = [string[]]::new($count)
                    $bufLen = 0
                    $total = 0
                    $pos = 0

                    while ($null -ne ($line = $reader.ReadLine())) {
                        $buf[$pos] = $line
                        $pos = ($pos + 1) % $count
                        if ($bufLen -lt $count) { $bufLen++ }
                        $total++
                    }

                    # Emit the circular buffer in order (oldest first)
                    $start = if ($bufLen -lt $count) { 0 } else { $pos }
                    $lineNumOffset = $total - $bufLen
                    for ($i = 0; $i -lt $bufLen; $i++) {
                        $idx = ($start + $i) % $count
                        $obj = [PSCustomObject]@{
                            PSTypeName = 'PsBash.CatLine'
                            LineNumber = $lineNumOffset + $i + 1
                            Content    = $buf[$idx]
                            FileName   = $filePath
                            BashText   = $buf[$idx]
                        }
                        Set-BashDisplayProperty $obj
                    }
                } finally {
                    $reader.Dispose()
                }
            }
        }
    }
}

# --- wc Command ---

function Invoke-BashWc {
    [OutputType('PsBash.WcResult')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'wc' }

    $defs = New-FlagDefs -Entries @(
        '-l', 'line count only'
        '-w', 'word count only'
        '-c', 'byte count only'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs
    $linesOnly = $parsed.Flags['-l']
    $wordsOnly = $parsed.Flags['-w']
    $bytesOnly = $parsed.Flags['-c']
    $noFlags = -not $linesOnly -and -not $wordsOnly -and -not $bytesOnly

    $operands = $parsed.Operands

    $emitResult = {
        param([long]$Lines, [long]$Words, [long]$Bytes, [string]$FileName)

        $parts = [System.Collections.Generic.List[string]]::new()
        if ($linesOnly)      { $parts.Add($Lines.ToString().PadLeft(7)) }
        elseif ($wordsOnly)  { $parts.Add($Words.ToString().PadLeft(7)) }
        elseif ($bytesOnly)  { $parts.Add($Bytes.ToString().PadLeft(7)) }
        else {
            $parts.Add($Lines.ToString().PadLeft(7))
            $parts.Add($Words.ToString().PadLeft(8))
            $parts.Add($Bytes.ToString().PadLeft(8))
        }
        if ($FileName -ne '') { $parts.Add(" $FileName") }

        $bashText = ($parts -join '') -replace '^\s+', ' '
        $bashText = $bashText.TrimStart()

        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.WcResult'
            Lines      = $Lines
            Words      = $Words
            Bytes      = $Bytes
            FileName   = $FileName
            BashText   = $bashText
        }
        Set-BashDisplayProperty $obj
    }

    $countWords = {
        param([string]$Text)

        $count = 0
        $inWord = $false
        for ($i = 0; $i -lt $Text.Length; $i++) {
            $ch = $Text[$i]
            $isWs = $ch -eq ' ' -or $ch -eq "`t" -or $ch -eq "`n" -or $ch -eq "`r"
            if ($isWs) {
                $inWord = $false
            } elseif (-not $inWord) {
                $count++
                $inWord = $true
            }
        }
        $count
    }

    # Pipeline mode
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        [long]$totalLines = 0
        [long]$totalWords = 0
        [long]$totalBytes = 0

        foreach ($item in $pipelineInput) {
            $text = if ($null -eq $item) {
                ''
            } elseif ($item -is [string]) {
                $item
            } elseif ($null -ne $item.PSObject -and $null -ne $item.PSObject.Properties['BashText']) {
                [string]$item.BashText
            } else {
                "$item"
            }

            $end = $text.Length
            while ($end -gt 0 -and $text[$end - 1] -eq "`n") { $end-- }
            $lineText = if ($end -eq $text.Length) { $text } else { $text.Substring(0, $end) }
            [long]$lineCount = 1
            if ($lineText.Length -gt 0) {
                for ($i = 0; $i -lt $lineText.Length; $i++) {
                    if ($lineText[$i] -eq "`n") { $lineCount++ }
                }
            }
            $totalLines += $lineCount
            if ($wordsOnly -or $noFlags) {
                $totalWords += & $countWords $lineText
            }
            if ($bytesOnly -or $noFlags) {
                $totalBytes += [System.Text.Encoding]::UTF8.GetByteCount($lineText.Replace("`n", '')) + $lineCount
            }
        }

        & $emitResult $totalLines $totalWords $totalBytes ''
        return
    }

    # File mode
    $totalLines = 0
    $totalWords = 0
    $totalBytes = 0
    $multipleFiles = $operands.Count -gt 1

    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-BashError -Message "wc: ${filePath}: No such file or directory"
            continue
        }

        $rawText = Read-BashFileBytes -Path $filePath -Command 'wc'
        if ($null -eq $rawText) { continue }

        # Byte count: file size minus BOM (Read-BashFileBytes handles BOM for text)
        $fileBytes = [System.IO.FileInfo]::new($filePath).Length
        try {
            $fs = [System.IO.File]::OpenRead($filePath)
            $bom = [byte[]]::new(3)
            if ($fs.Read($bom, 0, 3) -ge 3 -and $bom[0] -eq 0xEF -and $bom[1] -eq 0xBB -and $bom[2] -eq 0xBF) {
                $fileBytes -= 3
            }
            $fs.Dispose()
        } catch {
            # If BOM peek fails, use raw file size
        }
        [long]$lineCount = 0
        foreach ($c in [char[]]$rawText) { if ($c -eq "`n") { $lineCount++ } }
        $wordCount = & $countWords $rawText

        $totalLines += $lineCount
        $totalWords += $wordCount
        $totalBytes += $fileBytes

        & $emitResult $lineCount $wordCount $fileBytes $filePath
    }

    if ($multipleFiles) {
        & $emitResult $totalLines $totalWords $totalBytes 'total'
    }
}

