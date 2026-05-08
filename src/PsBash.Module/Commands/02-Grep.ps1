# --- grep Command ---

function Invoke-BashGrep {
    [OutputType('PsBash.GrepMatch')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'grep' }

    # Parse arguments manually because grep has value-bearing flags (-A, -B, -C, -m, -e)
    $ignoreCase = $false
    $invertMatch = $false
    $showLineNumbers = $false
    $countOnly = $false
    $quietMode = $false          # -q: suppress output, exit 0 if match, 1 if no match
    $recursive = $false
    $filesOnly = $false
    $extendedRegex = $false
    $fixedString = $false
    $wholeWord = $false
    $outputMatchOnly = $false
    $forceFileName = $false      # -H: always show filename
    $suppressFileName = $false   # -h: never show filename
    $maxMatches = [int]::MaxValue
    $afterContext = 0
    $beforeContext = 0
    $patterns = [System.Collections.Generic.List[string]]::new()
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

        # Handle -e pattern (multiple patterns)
        if ($arg -ceq '-e') {
            $i++
            if ($i -lt $Arguments.Count) {
                $patterns.Add($Arguments[$i])
            }
            $i++
            continue
        }

        # Handle -A NUM, -B NUM, -C NUM as separate args or joined (e.g. -A2)
        if ($arg -cmatch '^-([ABC])(\d+)$') {
            switch ($Matches[1]) {
                'A' { $afterContext = [int]$Matches[2] }
                'B' { $beforeContext = [int]$Matches[2] }
                'C' { $afterContext = [int]$Matches[2]; $beforeContext = [int]$Matches[2] }
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-([ABC])$') {
            $flag = $Matches[1]
            $i++
            if ($i -lt $Arguments.Count) {
                $val = [int]$Arguments[$i]
                switch ($flag) {
                    'A' { $afterContext = $val }
                    'B' { $beforeContext = $val }
                    'C' { $afterContext = $val; $beforeContext = $val }
                }
            }
            $i++
            continue
        }

        # Handle -m NUM (max matches)
        if ($arg -cmatch '^-m(\d+)$') {
            $maxMatches = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-m') {
            $i++
            if ($i -lt $Arguments.Count) {
                $maxMatches = [int]$Arguments[$i]
            }
            $i++
            continue
        }

        # Long-form flags
        if ($arg -eq '--fixed-strings') { $fixedString = $true; $i++; continue }
        if ($arg -eq '--with-filename') { $forceFileName = $true; $i++; continue }
        if ($arg -eq '--no-filename') { $suppressFileName = $true; $i++; continue }
        if ($arg -eq '--word-regexp') { $wholeWord = $true; $i++; continue }
        if ($arg -eq '--only-matching') { $outputMatchOnly = $true; $i++; continue }
        if ($arg -eq '--quiet' -or $arg -eq '--silent') { $quietMode = $true; $i++; continue }
        if ($arg -eq '--max-count') {
            $i++
            if ($i -lt $Arguments.Count) { $maxMatches = [int]$Arguments[$i] }
            $i++
            continue
        }
        if ($arg -cmatch '^--max-count=(\d+)$') {
            $maxMatches = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch -CaseSensitive ($ch) {
                    'i' { $ignoreCase = $true }
                    'v' { $invertMatch = $true }
                    'n' { $showLineNumbers = $true }
                    'c' { $countOnly = $true }
                    'q' { $quietMode = $true }
                    'r' { $recursive = $true }
                    'l' { $filesOnly = $true }
                    'E' { $extendedRegex = $true }
                    'F' { $fixedString = $true }
                    'w' { $wholeWord = $true }
                    'o' { $outputMatchOnly = $true }
                    'H' { $forceFileName = $true }
                    'h' { $suppressFileName = $true }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Handle pattern collection: -e patterns or first operand
    if ($patterns.Count -eq 0 -and $operands.Count -gt 0) {
        $patterns.Add($operands[0])
    }

    if ($patterns.Count -eq 0) {
        Write-BashError -Message 'grep: usage: grep [options] pattern [file ...]' -ExitCode 2
        return
    }

    $fileOperands = @(if ($patterns.Count -lt $operands.Count) {
        $operands.GetRange(1, $operands.Count - 1)
    } elseif ($operands.Count -gt 1) {
        $operands.GetRange(1, $operands.Count - 1)
    } else {
        @()
    })

    # Build regex list from patterns (OR logic for multiple -e patterns)
    $regexes = [System.Collections.Generic.List[regex]]::new()
    $regexOpts = [System.Text.RegularExpressions.RegexOptions]::None
    if ($ignoreCase) { $regexOpts = $regexOpts -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase }

    foreach ($pat in $patterns) {
        $regexPattern = if ($fixedString) {
            # Fixed string: escape all regex metacharacters
            [regex]::Escape($pat)
        } elseif (-not $extendedRegex) {
            # Basic grep: . * ^ $ [ ] are special; escape (){}|+?
            $pat -replace '(?<!\\)\(', '\(' -replace '(?<!\\)\)', '\)' -replace '(?<!\\)\{', '\{' -replace '(?<!\\)\}', '\}' -replace '(?<!\\)\|', '\|' -replace '(?<!\\)\+', '\+' -replace '(?<!\\)\?', '\?'
        } else {
            $pat
        }

        # Add word boundaries if -w is set
        if ($wholeWord) {
            $regexPattern = "\b$regexPattern\b"
        }

        $regexes.Add([regex]::new($regexPattern, $regexOpts))
    }

    # --- Pipeline mode ---
    if ($fileOperands.Count -eq 0 -and -not $recursive) {
        $matchCount = 0
        $lineNum = 0

        foreach ($item in $pipelineInput) {
            if ($matchCount -ge $maxMatches) { break }

            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    if ($matchCount -ge $maxMatches) { break }
                    $lineNum++

                    # Check if any regex matches (OR logic for multiple patterns)
                    $isMatch = $false
                    $matchObject = $null
                    foreach ($rx in $regexes) {
                        if ($rx.IsMatch($subLine)) {
                            $isMatch = $true
                            $matchObject = $rx.Match($subLine)
                            break
                        }
                    }

                    if ($invertMatch) { $isMatch = -not $isMatch }
                    if ($isMatch) {
                        $matchCount++
                        if ($quietMode) {
                            $global:LASTEXITCODE = 0
                            return
                        }
                        if (-not $countOnly) {
                            $outputText = if ($outputMatchOnly -and $matchObject) {
                                $matchObject.Value
                            } else {
                                $subLine
                            }

                            $prefix = ''
                            if ($forceFileName) { $prefix = "<stdin>:" }
                            if ($showLineNumbers) { $prefix = "${prefix}${lineNum}:" }
                            $bashText = "${prefix}${outputText}"

                            New-BashObject -BashText $bashText
                        }
                    }
                }
            } else {
                $lineNum++
                $lineText = $text.TrimEnd("`n".ToCharArray())

                # Check if any regex matches (OR logic for multiple patterns)
                $isMatch = $false
                $matchObject = $null
                foreach ($rx in $regexes) {
                    if ($rx.IsMatch($lineText)) {
                        $isMatch = $true
                        $matchObject = $rx.Match($lineText)
                        break
                    }
                }

                if ($invertMatch) { $isMatch = -not $isMatch }
                if ($isMatch) {
                    $matchCount++
                    if ($quietMode) {
                        $global:LASTEXITCODE = 0
                        return
                    }
                    if (-not $countOnly) {
                        $outputText = if ($outputMatchOnly -and $matchObject) {
                            $matchObject.Value
                        } else {
                            $lineText
                        }

                        $prefix = ''
                        if ($forceFileName) { $prefix = "<stdin>:" }
                        if ($showLineNumbers) { $prefix = "${prefix}${lineNum}:" }

                        if ($prefix -ne '') {
                            New-BashObject -BashText "${prefix}${outputText}"
                        } elseif ($outputMatchOnly) {
                            New-BashObject -BashText $outputText
                        } else {
                            $item
                        }
                    }
                }
            }
        }

        if ($quietMode) {
            # No match found: set LASTEXITCODE and signal failure via $? for && / || chains
            $global:LASTEXITCODE = 1
            Write-Error '' -ErrorAction SilentlyContinue
            return
        }

        # Non-quiet pipeline mode: set exit code based on whether any match was found.
        # grep exits 1 when no lines match, 0 when at least one matches.
        if ($matchCount -eq 0) {
            $global:LASTEXITCODE = 1
        } else {
            $global:LASTEXITCODE = 0
        }

        if ($countOnly) {
            New-BashObject -BashText "$matchCount"
        }
        return
    }

    # --- File mode ---
    $filePaths = [System.Collections.Generic.List[string]]::new()

    if ($recursive) {
        $searchDir = if ($fileOperands.Count -gt 0) { $fileOperands[0] } else { '.' }
        if (Test-Path -LiteralPath $searchDir -PathType Container) {
            Get-ChildItem -LiteralPath $searchDir -Recurse -File | ForEach-Object { $filePaths.Add($_.FullName) }
        } elseif (Test-Path -LiteralPath $searchDir) {
            $filePaths.Add((Resolve-Path -LiteralPath $searchDir).Path)
        }
    } else {
        foreach ($fp in $fileOperands) {
            if (-not (Test-Path -LiteralPath $fp)) {
                Write-BashError -Message "grep: ${fp}: No such file or directory" -ExitCode 2
                continue
            }
            $filePaths.Add((Resolve-Path -LiteralPath $fp).Path)
        }
    }

    $multipleFiles = $filePaths.Count -gt 1 -or $recursive -or $forceFileName
    $matchedFiles = [System.Collections.Generic.List[string]]::new()
    $perFileCounts = [System.Collections.Generic.Dictionary[string,int]]::new()
    $totalMatchCount = 0
    $filesProcessed = 0

    foreach ($filePath in (Resolve-BashGlob -Paths $filePaths)) {
        if ($totalMatchCount -ge $maxMatches) { break }
        $filesProcessed++

        $lines = Read-BashFileLines -Path $filePath -Command 'grep'
        if ($null -eq $lines) { continue }

        $matchIndices = [System.Collections.Generic.List[int]]::new()
        $matchObjects = [System.Collections.Generic.Dictionary[int, System.Text.RegularExpressions.Match]]::new()
        for ($li = 0; $li -lt $lines.Count; $li++) {
            # Check if any regex matches (OR logic for multiple patterns)
            $isMatch = $false
            $matchObj = $null
            foreach ($rx in $regexes) {
                if ($rx.IsMatch($lines[$li])) {
                    $isMatch = $true
                    $matchObj = $rx.Match($lines[$li])
                    break
                }
            }

            if ($invertMatch) { $isMatch = -not $isMatch }
            if ($isMatch) {
                $matchIndices.Add($li)
                if ($matchObj) { $matchObjects[$li] = $matchObj }
            }
        }

        $fileMatchCount = $matchIndices.Count
        $totalMatchCount += $fileMatchCount
        $perFileCounts[$filePath] = $fileMatchCount

        if ($quietMode -and $fileMatchCount -gt 0) {
            $global:LASTEXITCODE = 0
            return
        }

        if ($filesOnly) {
            if ($fileMatchCount -gt 0) { $matchedFiles.Add($filePath) }
            continue
        }

        if ($countOnly) { continue }

        # Determine which lines to emit (matches + context, respecting -m limit)
        $emitLines = [System.Collections.Generic.HashSet[int]]::new()
        $emitCount = 0
        foreach ($mi in $matchIndices) {
            if ($emitCount -ge $maxMatches) { break }

            $start = [System.Math]::Max(0, $mi - $beforeContext)
            $end = [System.Math]::Min($lines.Count - 1, $mi + $afterContext)
            for ($li = $start; $li -le $end; $li++) {
                [void]$emitLines.Add($li)
            }
            $emitCount++
        }

        $sortedEmit = $emitLines | Sort-Object
        foreach ($li in $sortedEmit) {
            if ($totalMatchCount -ge $maxMatches) { break }

            $line = $lines[$li]
            $lineNum = $li + 1
            $prefix = ''

            # Determine if filename should be shown
            $showFile = $multipleFiles -and -not $suppressFileName

            if ($outputMatchOnly -and $matchObjects.ContainsKey($li)) {
                $outputText = $matchObjects[$li].Value
            } else {
                $outputText = $line
            }

            if ($showFile) { $prefix = "${filePath}:" }

            $bashText = $outputText
            if ($showLineNumbers) {
                $bashText = "${prefix}${lineNum}:${outputText}"
            } elseif ($showFile) {
                $bashText = "${prefix}${outputText}"
            }

            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.GrepMatch'
                FileName   = $filePath
                LineNumber = $lineNum
                Line       = $line
                BashText   = $bashText
            }
            Set-BashDisplayProperty $obj

            if ($matchIndices -contains $li) {
                $totalMatchCount = $totalMatchCount   # Update count for context lines
            }
        }
    }

    if ($quietMode) {
        # No match found in any file: set LASTEXITCODE and signal failure via $? for && / || chains
        $global:LASTEXITCODE = 1
        Write-Error '' -ErrorAction SilentlyContinue
        return
    }

    # Non-quiet file mode: set exit code based on whether any match was found.
    # grep exits 1 when no lines match, 0 when at least one matches.
    if ($totalMatchCount -eq 0) {
        $global:LASTEXITCODE = 1
    } else {
        $global:LASTEXITCODE = 0
    }

    if ($filesOnly) {
        foreach ($fp in $matchedFiles) {
            New-BashObject -BashText $fp
        }
        return
    }

    if ($countOnly) {
        if ($multipleFiles) {
            foreach ($filePath in (Resolve-BashGlob -Paths $filePaths)) {
                if ($perFileCounts.ContainsKey($filePath)) {
                    New-BashObject -BashText "${filePath}:$($perFileCounts[$filePath])"
                }
            }
        } else {
            New-BashObject -BashText "$totalMatchCount"
        }
    }
}

