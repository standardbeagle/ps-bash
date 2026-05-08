# --- cut Command ---

function Invoke-BashCut {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'cut' }

    # Parse flags: -d (delimiter), -f (fields), -c (characters)
    $delimiter = "`t"
    $fieldSpec = ''
    $charSpec = ''
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

        if ($arg -ceq '-d') {
            $i++
            if ($i -lt $Arguments.Count) {
                $delimiter = $Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-d(.)$') {
            $delimiter = $Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-f') {
            $i++
            if ($i -lt $Arguments.Count) {
                $fieldSpec = $Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-f(.+)$') {
            $fieldSpec = $Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-c') {
            $i++
            if ($i -lt $Arguments.Count) {
                $charSpec = $Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-c(.+)$') {
            $charSpec = $Matches[1]
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    $parseSpec = {
        param([string]$Spec)
        $indices = [System.Collections.Generic.List[int]]::new()
        foreach ($part in $Spec.Split(',')) {
            if ($part -match '^(\d+)-(\d+)$') {
                $start = [int]$Matches[1]
                $end = [int]$Matches[2]
                for ($n = $start; $n -le $end; $n++) { $indices.Add($n) }
            } else {
                $indices.Add([int]$part)
            }
        }
        $indices
    }

    $cutLine = {
        param([string]$Line)

        if ($charSpec -ne '') {
            $positions = & $parseSpec $charSpec
            $chars = [System.Text.StringBuilder]::new()
            foreach ($pos in $positions) {
                $idx = $pos - 1
                if ($idx -ge 0 -and $idx -lt $Line.Length) {
                    [void]$chars.Append($Line[$idx])
                }
            }
            return $chars.ToString()
        }

        if ($fieldSpec -ne '') {
            $fields = $Line.Split($delimiter)
            $indices = & $parseSpec $fieldSpec
            $selected = [System.Collections.Generic.List[string]]::new()
            foreach ($idx in $indices) {
                $fi = $idx - 1
                if ($fi -ge 0 -and $fi -lt $fields.Count) {
                    $selected.Add($fields[$fi])
                }
            }
            return ($selected -join $delimiter)
        }

        return $Line
    }

    # Collect lines from pipeline or files
    $lines = [System.Collections.Generic.List[string]]::new()

    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $lines.Add($subLine)
                }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'cut'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) {
                $lines.Add($l)
            }
        }
    }

    foreach ($line in $lines) {
        $result = & $cutLine $line
        New-BashObject -BashText $result
    }
}

# --- tr Command ---

function Invoke-BashTr {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'tr' }

    # Parse flags: -d (delete), -s (squeeze), -c/-C/--complement, -t/--truncate-set1
    $deleteMode = $false
    $squeezeMode = $false
    $complementMode = $false
    $truncateMode = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -ceq '--complement') {
            $complementMode = $true
            $i++
            continue
        }

        if ($arg -ceq '--truncate-set1') {
            $truncateMode = $true
            $i++
            continue
        }

        if ($arg -ceq '-d') {
            $deleteMode = $true
            $i++
            continue
        }

        if ($arg -ceq '-s') {
            $squeezeMode = $true
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'd' { $deleteMode = $true }
                    's' { $squeezeMode = $true }
                    'c' { $complementMode = $true }
                    'C' { $complementMode = $true }
                    't' { $truncateMode = $true }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Expand escape sequences in operands before character class expansion
    for ($ei = 0; $ei -lt $operands.Count; $ei++) {
        $operands[$ei] = Expand-EscapeSequences -Text $operands[$ei]
    }

    # Expand POSIX character classes: [:alpha:], [:digit:], etc.
    $expandPosixClass = {
        param([string]$Spec)
        $posixClasses = @{
            'alpha' = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ'
            'digit' = '0123456789'
            'alnum' = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'
            'upper' = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
            'lower' = 'abcdefghijklmnopqrstuvwxyz'
            'space' = " `t`n`r`f`v"
            'punct' = '!"#$%&''()*+,-./:;<=>?@[\]^_`{|}~'
        }
        $result = $Spec
        foreach ($kv in $posixClasses.GetEnumerator()) {
            $pattern = "[:$($kv.Key):]"
            $result = $result.Replace($pattern, $kv.Value)
        }
        $result
    }

    $expandClass = {
        param([string]$Spec)
        # First expand POSIX classes
        $Spec = & $expandPosixClass $Spec
        $result = [System.Text.StringBuilder]::new()
        $ci = 0
        while ($ci -lt $Spec.Length) {
            if ($ci + 2 -lt $Spec.Length -and $Spec[$ci + 1] -eq '-') {
                $start = [int][char]$Spec[$ci]
                $end = [int][char]$Spec[$ci + 2]
                for ($c = $start; $c -le $end; $c++) {
                    [void]$result.Append([char]$c)
                }
                $ci += 3
            } else {
                [void]$result.Append($Spec[$ci])
                $ci++
            }
        }
        $result.ToString()
    }

    $transformLine = {
        param([string]$Text)

        if ($deleteMode) {
            $set = & $expandClass $operands[0]
            $sb = [System.Text.StringBuilder]::new()
            foreach ($ch in $Text.ToCharArray()) {
                $inSet = $set.IndexOf($ch) -ge 0
                if ($complementMode) {
                    # Complement + delete: keep chars that ARE in set
                    if ($inSet) { [void]$sb.Append($ch) }
                } else {
                    # Normal delete: keep chars NOT in set
                    if (-not $inSet) { [void]$sb.Append($ch) }
                }
            }
            return $sb.ToString()
        }

        if ($squeezeMode -and $operands.Count -eq 1) {
            $set = & $expandClass $operands[0]
            $sb = [System.Text.StringBuilder]::new()
            $prevChar = [char]0
            $prevInSet = $false
            foreach ($ch in $Text.ToCharArray()) {
                $inSet = $set.IndexOf($ch) -ge 0
                if ($complementMode) { $inSet = -not $inSet }
                if ($inSet -and $prevInSet -and $ch -eq $prevChar) {
                    continue
                }
                [void]$sb.Append($ch)
                $prevChar = $ch
                $prevInSet = $inSet
            }
            return $sb.ToString()
        }

        if ($operands.Count -ge 2) {
            $set1 = & $expandClass $operands[0]
            $set2 = & $expandClass $operands[1]

            # Truncate SET2 to length of SET1
            if ($truncateMode -and $set2.Length -gt $set1.Length) {
                $set2 = $set2.Substring(0, $set1.Length)
            }

            if ($complementMode) {
                # Complement: SET1 becomes all 256 chars minus the original SET1
                $compSb = [System.Text.StringBuilder]::new()
                $set1Hash = [System.Collections.Generic.HashSet[char]]::new($set1.ToCharArray())
                for ($c = 0; $c -le 255; $c++) {
                    $ch = [char]$c
                    if (-not $set1Hash.Contains($ch)) {
                        [void]$compSb.Append($ch)
                    }
                }
                $set1 = $compSb.ToString()
                # Extend SET2 by repeating last char to match complement SET1 length
                if ($set2.Length -gt 0) {
                    while ($set2.Length -lt $set1.Length) {
                        $set2 += $set2[$set2.Length - 1]
                    }
                }
            }

            $sb = [System.Text.StringBuilder]::new()
            foreach ($ch in $Text.ToCharArray()) {
                $idx = $set1.IndexOf($ch)
                if ($idx -ge 0 -and $idx -lt $set2.Length) {
                    [void]$sb.Append($set2[$idx])
                } elseif ($idx -ge 0) {
                    [void]$sb.Append($set2[$set2.Length - 1])
                } else {
                    [void]$sb.Append($ch)
                }
            }
            $result = $sb.ToString()

            if ($squeezeMode) {
                $sb2 = [System.Text.StringBuilder]::new()
                $prevCh = [char]0
                $prevInSet2 = $false
                foreach ($ch in $result.ToCharArray()) {
                    $inSet2 = $set2.IndexOf($ch) -ge 0
                    if ($inSet2 -and $prevInSet2 -and $ch -eq $prevCh) {
                        continue
                    }
                    [void]$sb2.Append($ch)
                    $prevCh = $ch
                    $prevInSet2 = $inSet2
                }
                return $sb2.ToString()
            }

            return $result
        }

        return $Text
    }

    # Collect all input text
    $allText = [System.Text.StringBuilder]::new()

    if ($pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            [void]$allText.Append($text + "`n")
        }
    }

    $inputText = $allText.ToString()
    if ($inputText.EndsWith("`n")) {
        $inputText = $inputText.Substring(0, $inputText.Length - 1)
    }

    $lines = $inputText.Split("`n")
    foreach ($line in $lines) {
        $result = & $transformLine $line
        New-BashObject -BashText $result
    }
}

# --- uniq Command ---

function Invoke-BashUniq {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'uniq' }

    $countMode = $false
    $duplicatesOnly = $false
    $uniqueOnly = $false
    $ignoreCase = $false
    $skipFields = 0
    $skipChars = 0
    $checkChars = 0
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -ceq '--') {
            $i++
            while ($i -lt $Arguments.Count) {
                $operands.Add($Arguments[$i])
                $i++
            }
            break
        }

        if ($arg -ceq '--ignore-case') {
            $ignoreCase = $true
            $i++
            continue
        }

        if ($arg -cmatch '^--skip-fields=(\d+)$') {
            $skipFields = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -cmatch '^--skip-chars=(\d+)$') {
            $skipChars = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -cmatch '^--check-chars=(\d+)$') {
            $checkChars = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and $arg -notmatch '^-\d') {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'c' { $countMode = $true }
                    'd' { $duplicatesOnly = $true }
                    'u' { $uniqueOnly = $true }
                    'i' { $ignoreCase = $true }
                    'f' {
                        $rest = $arg.Substring($arg.IndexOf('f') + 1)
                        if ($rest -match '^\d+') {
                            $skipFields = [int]$rest
                        } else {
                            $i++
                            if ($i -lt $Arguments.Count) {
                                $skipFields = [int]$Arguments[$i]
                            }
                        }
                    }
                    's' {
                        $rest = $arg.Substring($arg.IndexOf('s') + 1)
                        if ($rest -match '^\d+') {
                            $skipChars = [int]$rest
                        } else {
                            $i++
                            if ($i -lt $Arguments.Count) {
                                $skipChars = [int]$Arguments[$i]
                            }
                        }
                    }
                    'w' {
                        $rest = $arg.Substring($arg.IndexOf('w') + 1)
                        if ($rest -match '^\d+') {
                            $checkChars = [int]$rest
                        } else {
                            $i++
                            if ($i -lt $Arguments.Count) {
                                $checkChars = [int]$Arguments[$i]
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

    # Build the comparison key from a line
    function Get-UniqKey([string]$Line) {
        $key = $Line
        # Step 1: skip fields (whitespace-delimited)
        if ($skipFields -gt 0) {
            $parts = $key -split '\s+', ($skipFields + 1)
            if ($parts.Count -gt $skipFields) {
                $key = $parts[$skipFields]
            } else {
                $key = ''
            }
        }
        # Step 2: skip characters
        if ($skipChars -gt 0 -and $key.Length -gt $skipChars) {
            $key = $key.Substring($skipChars)
        } elseif ($skipChars -gt 0) {
            $key = ''
        }
        # Step 3: limit characters
        if ($checkChars -gt 0 -and $key.Length -gt $checkChars) {
            $key = $key.Substring(0, $checkChars)
        }
        return $key
    }

    # Collect lines
    $lines = [System.Collections.Generic.List[string]]::new()

    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $lines.Add($subLine)
                }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'uniq'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) {
                $lines.Add($l)
            }
        }
    }

    # Group consecutive identical lines (using key comparison)
    $groups = [System.Collections.Generic.List[object]]::new()
    $prevLine = $null
    $prevKey = $null
    $runCount = 0

    foreach ($line in $lines) {
        $key = Get-UniqKey $line
        $same = if ($ignoreCase) { $key -ieq $prevKey } else { $key -ceq $prevKey }
        if ($same) {
            $runCount++
        } else {
            if ($null -ne $prevLine) {
                $groups.Add(@{ Line = $prevLine; Count = $runCount })
            }
            $prevLine = $line
            $prevKey = $key
            $runCount = 1
        }
    }
    if ($null -ne $prevLine) {
        $groups.Add(@{ Line = $prevLine; Count = $runCount })
    }

    foreach ($group in $groups) {
        if ($duplicatesOnly -and $group.Count -lt 2) { continue }
        if ($uniqueOnly -and $group.Count -gt 1) { continue }

        if ($countMode) {
            $bashText = '{0,7} {1}' -f $group.Count, $group.Line
            New-BashObject -BashText $bashText
        } else {
            New-BashObject -BashText $group.Line
        }
    }
}

# --- rev Command ---

function Invoke-BashRev {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'rev' }

    # Collect lines
    $lines = [System.Collections.Generic.List[string]]::new()

    if ($Arguments.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $lines.Add($subLine)
                }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $Arguments)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'rev'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) {
                $lines.Add($l)
            }
        }
    }

    foreach ($line in $lines) {
        $chars = $line.ToCharArray()
        [System.Array]::Reverse($chars)
        New-BashObject -BashText ([string]::new($chars))
    }
}

# --- nl Command ---

function Invoke-BashNl {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'nl' }

    # Parse flags: -ba (number all lines including blank)
    $numberAll = $false
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

        if ($arg -ceq '-ba') {
            $numberAll = $true
            $i++
            continue
        }

        if ($arg -ceq '-b') {
            $i++
            if ($i -lt $Arguments.Count -and $Arguments[$i] -ceq 'a') {
                $numberAll = $true
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Collect lines
    $lines = [System.Collections.Generic.List[string]]::new()

    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $lines.Add($subLine)
                }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'nl'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) {
                $lines.Add($l)
            }
        }
    }

    $lineNum = 0
    foreach ($line in $lines) {
        if (-not $numberAll -and $line -eq '') {
            New-BashObject -BashText ''
        } else {
            $lineNum++
            $bashText = '{0,6}	{1}' -f $lineNum, $line
            New-BashObject -BashText $bashText
        }
    }
}

# --- diff Command ---

function Invoke-BashDiff {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'diff' }

    $unified = $false
    $context = $false
    $brief = $false
    $ignoreAllSpace = $false
    $ignoreSpaceChange = $false
    $ignoreBlankLines = $false
    $ignoreCase = $false
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

        if ($arg -ceq '-u') {
            $unified = $true
            $i++
            continue
        }

        if ($arg -ceq '-c') {
            $context = $true
            $i++
            continue
        }

        if ($arg -ceq '-q' -or $arg -ceq '--brief') {
            $brief = $true
            $i++
            continue
        }

        if ($arg -ceq '-w' -or $arg -ceq '--ignore-all-space') {
            $ignoreAllSpace = $true
            $i++
            continue
        }

        if ($arg -ceq '-b' -or $arg -ceq '--ignore-space-change') {
            $ignoreSpaceChange = $true
            $i++
            continue
        }

        if ($arg -ceq '-B' -or $arg -ceq '--ignore-blank-lines') {
            $ignoreBlankLines = $true
            $i++
            continue
        }

        if ($arg -ceq '-i' -or $arg -ceq '--ignore-case') {
            $ignoreCase = $true
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -lt 2) {
        Write-BashError -Message 'diff: missing operand'
        return
    }

    $path1 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[0])
    $path2 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[1])

    $fileLines1 = Read-BashFileLines -Path $path1 -Command 'diff'
    if ($null -eq $fileLines1) { return }
    [string[]]$lines1 = @($fileLines1)
    $fileLines2 = Read-BashFileLines -Path $path2 -Command 'diff'
    if ($null -eq $fileLines2) { return }
    [string[]]$lines2 = @($fileLines2)

    # Build comparison keys applying whitespace/case/blank-line flags
    $cmp1 = [string[]]::new($lines1.Count)
    for ($xi = 0; $xi -lt $lines1.Count; $xi++) {
        $key = $lines1[$xi]
        if ($ignoreAllSpace) {
            $key = $key -replace '\s', ''
        } elseif ($ignoreSpaceChange) {
            $key = $key -replace '^\s+', '' -replace '\s+$', '' -replace '\s+', ' '
        }
        if ($ignoreCase) { $key = $key.ToLowerInvariant() }
        $cmp1[$xi] = $key
    }
    $cmp2 = [string[]]::new($lines2.Count)
    for ($yi = 0; $yi -lt $lines2.Count; $yi++) {
        $key = $lines2[$yi]
        if ($ignoreAllSpace) {
            $key = $key -replace '\s', ''
        } elseif ($ignoreSpaceChange) {
            $key = $key -replace '^\s+', '' -replace '\s+$', '' -replace '\s+', ' '
        }
        if ($ignoreCase) { $key = $key.ToLowerInvariant() }
        $cmp2[$yi] = $key
    }

    # When -B is set, build indices skipping blank lines for comparison
    $idx1 = if ($ignoreBlankLines) {
        ,@(
            for ($xi = 0; $xi -lt $cmp1.Count; $xi++) {
                if ($cmp1[$xi] -ne '') { $xi }
            }
        )
    } else {
        ,@(
            for ($xi = 0; $xi -lt $cmp1.Count; $xi++) { $xi }
        )
    }
    $idx2 = if ($ignoreBlankLines) {
        ,@(
            for ($yi = 0; $yi -lt $cmp2.Count; $yi++) {
                if ($cmp2[$yi] -ne '') { $yi }
            }
        )
    } else {
        ,@(
            for ($yi = 0; $yi -lt $cmp2.Count; $yi++) { $yi }
        )
    }

    $n = $idx1.Count
    $m = $idx2.Count

    # Compute LCS table on filtered comparison keys
    $dp = [int[,]]::new($n + 1, $m + 1)
    for ($xi = $n - 1; $xi -ge 0; $xi--) {
        for ($yi = $m - 1; $yi -ge 0; $yi--) {
            if ($cmp1[$idx1[$xi]] -ceq $cmp2[$idx2[$yi]]) {
                $dp[$xi, $yi] = $dp[($xi + 1), ($yi + 1)] + 1
            } else {
                $a = $dp[($xi + 1), $yi]
                $b = $dp[$xi, ($yi + 1)]
                $dp[$xi, $yi] = if ($a -ge $b) { $a } else { $b }
            }
        }
    }

    # Build edit script using original line indices
    $edits = [System.Collections.Generic.List[object]]::new()
    $xi = 0; $yi = 0
    while ($xi -lt $n -and $yi -lt $m) {
        if ($cmp1[$idx1[$xi]] -ceq $cmp2[$idx2[$yi]]) {
            $edits.Add(@{ Op = '='; Line1 = $idx1[$xi]; Line2 = $idx2[$yi] })
            $xi++; $yi++
        } elseif ($dp[($xi + 1), $yi] -ge $dp[$xi, ($yi + 1)]) {
            $edits.Add(@{ Op = '-'; Line1 = $idx1[$xi] })
            $xi++
        } else {
            $edits.Add(@{ Op = '+'; Line2 = $idx2[$yi] })
            $yi++
        }
    }
    while ($xi -lt $n) {
        $edits.Add(@{ Op = '-'; Line1 = $idx1[$xi] })
        $xi++
    }
    while ($yi -lt $m) {
        $edits.Add(@{ Op = '+'; Line2 = $idx2[$yi] })
        $yi++
    }

    # Check if files are identical
    $hasDiff = $false
    foreach ($e in $edits) {
        if ($e.Op -ne '=') { $hasDiff = $true; break }
    }
    if (-not $hasDiff) { return }

    # Brief mode: just report whether files differ
    if ($brief) {
        New-BashObject -BashText "Files $($operands[0]) and $($operands[1]) differ"
        return
    }

    # Collect hunks (shared by unified and context formats)
    # For normal format, hunks are emitted inline without context lines
    if ($unified -or $context) {
        $contextLines = 3
        $hunkGroups = [System.Collections.Generic.List[object]]::new()
        $ei = 0
        while ($ei -lt $edits.Count) {
            if ($edits[$ei].Op -ne '=') {
                $start = [Math]::Max(0, $ei - $contextLines)
                $end = $ei
                while ($end -lt $edits.Count) {
                    if ($edits[$end].Op -ne '=') {
                        $end++
                        continue
                    }
                    $lookAhead = 0
                    $j = $end
                    while ($j -lt $edits.Count -and $edits[$j].Op -eq '=') {
                        $lookAhead++
                        $j++
                    }
                    if ($lookAhead -le $contextLines * 2 -and $j -lt $edits.Count) {
                        $end = $j
                    } else {
                        $end = [Math]::Min($end + $contextLines, $edits.Count)
                        break
                    }
                }
                $hunkGroup = [System.Collections.Generic.List[object]]::new()
                for ($k = $start; $k -lt $end; $k++) {
                    $hunkGroup.Add($edits[$k])
                }
                $hunkGroups.Add($hunkGroup)
                $ei = $end
            } else {
                $ei++
            }
        }

        if ($unified) {
            # Unified format
            New-BashObject -BashText "--- $($operands[0])"
            New-BashObject -BashText "+++ $($operands[1])"
            foreach ($group in $hunkGroups) {
                $l1Start = -1; $l1Count = 0; $l2Start = -1; $l2Count = 0
                $hunkLines = [System.Collections.Generic.List[string]]::new()
                foreach ($e in $group) {
                    switch ($e.Op) {
                        '=' {
                            if ($l1Start -eq -1) { $l1Start = $e.Line1 + 1 }
                            if ($l2Start -eq -1) { $l2Start = $e.Line2 + 1 }
                            $l1Count++; $l2Count++
                            $hunkLines.Add(" $($lines1[$e.Line1])")
                        }
                        '-' {
                            if ($l1Start -eq -1) { $l1Start = $e.Line1 + 1 }
                            if ($l2Start -eq -1) { $l2Start = $e.Line1 + 1 }
                            $l1Count++
                            $hunkLines.Add("-$($lines1[$e.Line1])")
                        }
                        '+' {
                            if ($l1Start -eq -1) { $l1Start = $e.Line2 + 1 }
                            if ($l2Start -eq -1) { $l2Start = $e.Line2 + 1 }
                            $l2Count++
                            $hunkLines.Add("+$($lines2[$e.Line2])")
                        }
                    }
                }
                New-BashObject -BashText "@@ -${l1Start},${l1Count} +${l2Start},${l2Count} @@"
                foreach ($hl in $hunkLines) {
                    New-BashObject -BashText $hl
                }
            }
        } else {
            # Context format
            New-BashObject -BashText "*** $($operands[0])"
            New-BashObject -BashText "--- $($operands[1])"
            foreach ($group in $hunkGroups) {
                $l1Start = -1; $l1End = -1; $l2Start = -1; $l2End = -1
                foreach ($e in $group) {
                    switch ($e.Op) {
                        '=' {
                            if ($l1Start -eq -1) { $l1Start = $e.Line1 + 1 }
                            $l1End = $e.Line1 + 1
                            if ($l2Start -eq -1) { $l2Start = $e.Line2 + 1 }
                            $l2End = $e.Line2 + 1
                        }
                        '-' {
                            if ($l1Start -eq -1) { $l1Start = $e.Line1 + 1 }
                            $l1End = $e.Line1 + 1
                        }
                        '+' {
                            if ($l2Start -eq -1) { $l2Start = $e.Line2 + 1 }
                            $l2End = $e.Line2 + 1
                        }
                    }
                }
                New-BashObject -BashText "***************"
                New-BashObject -BashText "*** ${l1Start},${l1End}"
                # Mark deletes that are paired with inserts as changes (!)
                $changeLine1 = @{}
                $gi = 0
                while ($gi -lt $group.Count) {
                    if ($group[$gi].Op -eq '-' -and ($gi + 1) -lt $group.Count -and $group[$gi + 1].Op -eq '+') {
                        $changeLine1[$group[$gi].Line1] = $true
                    }
                    $gi++
                }
                foreach ($e in $group) {
                    switch ($e.Op) {
                        '=' { New-BashObject -BashText "  $($lines1[$e.Line1])" }
                        '-' {
                            if ($changeLine1.ContainsKey($e.Line1)) {
                                New-BashObject -BashText "! $($lines1[$e.Line1])"
                            } else {
                                New-BashObject -BashText "- $($lines1[$e.Line1])"
                            }
                        }
                        '+' { <# shown in --- section #> }
                    }
                }
                New-BashObject -BashText "--- ${l2Start},${l2End}"
                $changeLine2 = @{}
                $gi = 0
                while ($gi -lt $group.Count) {
                    if ($group[$gi].Op -eq '+' -and $gi -gt 0 -and $group[$gi - 1].Op -eq '-') {
                        $changeLine2[$group[$gi].Line2] = $true
                    }
                    $gi++
                }
                foreach ($e in $group) {
                    switch ($e.Op) {
                        '=' { New-BashObject -BashText "  $($lines2[$e.Line2])" }
                        '-' { <# shown in *** section #> }
                        '+' {
                            if ($changeLine2.ContainsKey($e.Line2)) {
                                New-BashObject -BashText "! $($lines2[$e.Line2])"
                            } else {
                                New-BashObject -BashText "+ $($lines2[$e.Line2])"
                            }
                        }
                    }
                }
            }
        }
    } else {
        # Normal diff format
        $ei = 0
        while ($ei -lt $edits.Count) {
            if ($edits[$ei].Op -eq '=') { $ei++; continue }

            $delStart = -1; $delEnd = -1
            $addStart = -1; $addEnd = -1
            $delLines = [System.Collections.Generic.List[string]]::new()
            $addLines = [System.Collections.Generic.List[string]]::new()

            while ($ei -lt $edits.Count -and $edits[$ei].Op -ne '=') {
                $e = $edits[$ei]
                if ($e.Op -eq '-') {
                    if ($delStart -eq -1) { $delStart = $e.Line1 + 1 }
                    $delEnd = $e.Line1 + 1
                    $delLines.Add($lines1[$e.Line1])
                } elseif ($e.Op -eq '+') {
                    if ($addStart -eq -1) { $addStart = $e.Line2 + 1 }
                    $addEnd = $e.Line2 + 1
                    $addLines.Add($lines2[$e.Line2])
                }
                $ei++
            }

            $delRange = if ($delStart -eq $delEnd -or $delStart -eq -1) { "$delStart" } else { "${delStart},${delEnd}" }
            $addRange = if ($addStart -eq $addEnd -or $addStart -eq -1) { "$addStart" } else { "${addStart},${addEnd}" }

            if ($delLines.Count -gt 0 -and $addLines.Count -gt 0) {
                New-BashObject -BashText "${delRange}c${addRange}"
                foreach ($dl in $delLines) { New-BashObject -BashText "< $dl" }
                New-BashObject -BashText '---'
                foreach ($al in $addLines) { New-BashObject -BashText "> $al" }
            } elseif ($delLines.Count -gt 0) {
                $addPos = if ($addStart -eq -1) {
                    if ($delStart -gt 1) { $delStart - 1 } else { 0 }
                } else { $addStart }
                New-BashObject -BashText "${delRange}d${addPos}"
                foreach ($dl in $delLines) { New-BashObject -BashText "< $dl" }
            } elseif ($addLines.Count -gt 0) {
                $delPos = if ($delStart -eq -1) {
                    if ($addStart -gt 1) { $addStart - 1 } else { 0 }
                } else { $delStart }
                New-BashObject -BashText "${delPos}a${addRange}"
                foreach ($al in $addLines) { New-BashObject -BashText "> $al" }
            }
        }
    }
}

# --- comm Command ---

function Invoke-BashComm {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'comm' }

    $suppress1 = $false
    $suppress2 = $false
    $suppress3 = $false
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

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and $arg -cmatch '^-[123]+$') {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    '1' { $suppress1 = $true }
                    '2' { $suppress2 = $true }
                    '3' { $suppress3 = $true }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -lt 2) {
        Write-BashError -Message 'comm: missing operand'
        return
    }

    $path1 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[0])
    $path2 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[1])

    $fileLines1 = Read-BashFileLines -Path $path1 -Command 'comm'
    if ($null -eq $fileLines1) { return }
    [string[]]$lines1 = @($fileLines1)
    $fileLines2 = Read-BashFileLines -Path $path2 -Command 'comm'
    if ($null -eq $fileLines2) { return }
    [string[]]$lines2 = @($fileLines2)

    $i1 = 0; $i2 = 0
    while ($i1 -lt $lines1.Count -and $i2 -lt $lines2.Count) {
        $cmp = [string]::Compare($lines1[$i1], $lines2[$i2], [System.StringComparison]::Ordinal)
        if ($cmp -eq 0) {
            if (-not $suppress3) {
                $prefix = ''
                if (-not $suppress1) { $prefix += "`t" }
                if (-not $suppress2) { $prefix += "`t" }
                New-BashObject -BashText "${prefix}$($lines1[$i1])"
            }
            $i1++; $i2++
        } elseif ($cmp -lt 0) {
            if (-not $suppress1) {
                New-BashObject -BashText $lines1[$i1]
            }
            $i1++
        } else {
            if (-not $suppress2) {
                $prefix = ''
                if (-not $suppress1) { $prefix += "`t" }
                New-BashObject -BashText "${prefix}$($lines2[$i2])"
            }
            $i2++
        }
    }

    while ($i1 -lt $lines1.Count) {
        if (-not $suppress1) {
            New-BashObject -BashText $lines1[$i1]
        }
        $i1++
    }

    while ($i2 -lt $lines2.Count) {
        if (-not $suppress2) {
            $prefix = ''
            if (-not $suppress1) { $prefix += "`t" }
            New-BashObject -BashText "${prefix}$($lines2[$i2])"
        }
        $i2++
    }
}

# --- column Command ---

function Invoke-BashColumn {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'column' }

    $tableMode = $false
    $separator = $null
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

        if ($arg -ceq '-t') {
            $tableMode = $true
            $i++
            continue
        }

        if ($arg -ceq '-s') {
            $i++
            if ($i -lt $Arguments.Count) {
                $separator = $Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-s(.)$') {
            $separator = $Matches[1]
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    $lines = [System.Collections.Generic.List[string]]::new()

    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $lines.Add($subLine)
                }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'column'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) {
                $lines.Add($l)
            }
        }
    }

    if (-not $tableMode) {
        foreach ($line in $lines) {
            New-BashObject -BashText $line
        }
        return
    }

    # Table mode: split each line into fields and align columns
    $splitPattern = if ($null -ne $separator) { [regex]::Escape($separator) } else { '\s+' }
    $rows = [System.Collections.Generic.List[string[]]]::new()
    $maxCols = 0

    foreach ($line in $lines) {
        if ($line -eq '') {
            $rows.Add(@(''))
            continue
        }
        $fields = [regex]::Split($line.Trim(), $splitPattern)
        $rows.Add($fields)
        if ($fields.Count -gt $maxCols) { $maxCols = $fields.Count }
    }

    # Calculate column widths
    $widths = [int[]]::new($maxCols)
    foreach ($row in $rows) {
        for ($c = 0; $c -lt $row.Count; $c++) {
            if ($row[$c].Length -gt $widths[$c]) {
                $widths[$c] = $row[$c].Length
            }
        }
    }

    foreach ($row in $rows) {
        $sb = [System.Text.StringBuilder]::new()
        for ($c = 0; $c -lt $row.Count; $c++) {
            if ($c -gt 0) { [void]$sb.Append('  ') }
            if ($c -lt $row.Count - 1) {
                [void]$sb.Append($row[$c].PadRight($widths[$c]))
            } else {
                [void]$sb.Append($row[$c])
            }
        }
        New-BashObject -BashText $sb.ToString()
    }
}

# --- join Command ---

function Invoke-BashJoin {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'join' }

    $delimiter = ' '
    $field1 = 1
    $field2 = 1
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

        if ($arg -ceq '-t') {
            $i++
            if ($i -lt $Arguments.Count) {
                $delimiter = $Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-t(.)$') {
            $delimiter = $Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-1') {
            $i++
            if ($i -lt $Arguments.Count) {
                $field1 = [int]$Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -ceq '-2') {
            $i++
            if ($i -lt $Arguments.Count) {
                $field2 = [int]$Arguments[$i]
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -lt 2) {
        Write-BashError -Message 'join: missing operand'
        return
    }

    $path1 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[0])
    $path2 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[1])

    $fileLines1 = Read-BashFileLines -Path $path1 -Command 'join'
    if ($null -eq $fileLines1) { return }
    [string[]]$lines1 = @($fileLines1)
    $fileLines2 = Read-BashFileLines -Path $path2 -Command 'join'
    if ($null -eq $fileLines2) { return }
    [string[]]$lines2 = @($fileLines2)

    # Build lookup from file2 keyed by join field
    $file2Map = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string[]]]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($line in $lines2) {
        $fields = $line.Split($delimiter)
        $keyIdx = $field2 - 1
        if ($keyIdx -ge $fields.Count) { continue }
        $key = $fields[$keyIdx]
        if (-not $file2Map.ContainsKey($key)) {
            $file2Map[$key] = [System.Collections.Generic.List[string[]]]::new()
        }
        $file2Map[$key].Add($fields)
    }

    foreach ($line in $lines1) {
        $fields1 = $line.Split($delimiter)
        $keyIdx1 = $field1 - 1
        if ($keyIdx1 -ge $fields1.Count) { continue }
        $key = $fields1[$keyIdx1]

        if ($file2Map.ContainsKey($key)) {
            foreach ($fields2 in $file2Map[$key]) {
                $parts = [System.Collections.Generic.List[string]]::new()
                $parts.Add($key)
                for ($c = 0; $c -lt $fields1.Count; $c++) {
                    if ($c -ne $keyIdx1) { $parts.Add($fields1[$c]) }
                }
                for ($c = 0; $c -lt $fields2.Count; $c++) {
                    if ($c -ne ($field2 - 1)) { $parts.Add($fields2[$c]) }
                }
                New-BashObject -BashText ($parts -join $delimiter)
            }
        }
    }
}

# --- paste Command ---

function Invoke-BashPaste {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'paste' }

    $delimiter = "`t"
    $serial = $false
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

        if ($arg -ceq '-s') {
            $serial = $true
            $i++
            continue
        }

        if ($arg -ceq '-d') {
            $i++
            if ($i -lt $Arguments.Count) {
                $delimiter = $Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-d(.+)$') {
            $delimiter = $Matches[1]
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Read all files
    $allFiles = [System.Collections.Generic.List[string[]]]::new()
    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
        $fileLines = Read-BashFileLines -Path $filePath -Command 'paste'
        if ($null -eq $fileLines) { return }
        $allFiles.Add([string[]]@($fileLines))
    }

    if ($allFiles.Count -eq 0) { return }

    if ($serial) {
        # Serial mode: each file becomes one line with fields joined
        foreach ($fileLines in $allFiles) {
            New-BashObject -BashText ($fileLines -join $delimiter)
        }
    } else {
        # Normal mode: merge files line by line
        $maxLines = 0
        foreach ($fileLines in $allFiles) {
            if ($fileLines.Count -gt $maxLines) { $maxLines = $fileLines.Count }
        }

        for ($lineIdx = 0; $lineIdx -lt $maxLines; $lineIdx++) {
            $parts = [System.Collections.Generic.List[string]]::new()
            foreach ($fileLines in $allFiles) {
                if ($lineIdx -lt $fileLines.Count) {
                    $parts.Add($fileLines[$lineIdx])
                } else {
                    $parts.Add('')
                }
            }
            New-BashObject -BashText ($parts -join $delimiter)
        }
    }
}

# --- tee Command ---

function Invoke-BashTee {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'tee' }

    $append = $false
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

        if ($arg -ceq '-a') {
            $append = $true
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Collect BashText for file output
    $textParts = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $pipelineInput) {
        $textParts.Add((Get-BashText -InputObject $item))
    }

    # Join parts: if BashText already has trailing newlines (echo), concatenate directly
    # If not (ls, grep), join with newlines and add trailing newline
    $textContent = ''
    if ($textParts.Count -gt 0) {
        $hasTrailingNewlines = $textParts[0].EndsWith("`n")
        if ($hasTrailingNewlines) {
            $textContent = $textParts -join ''
        } else {
            $textContent = ($textParts -join "`n") + "`n"
        }
    }

    # Write to each file (skip $null which represents /dev/null)
    $resolvedPaths = $operands | Where-Object { $null -ne $_ -and $_ -ne '' }
    foreach ($filePath in (Resolve-BashGlob -Paths $resolvedPaths)) {
        $parentDir = Split-Path -Parent $filePath
        if ($parentDir -and -not (Test-Path -LiteralPath $parentDir)) {
            Write-BashError -Message "tee: ${filePath}: No such file or directory"
            continue
        }
        if ($append) {
            if (-not (Write-BashFileText -Path $filePath -Text $textContent -Command 'tee' -Append)) { continue }
        } else {
            if (-not (Write-BashFileText -Path $filePath -Text $textContent -Command 'tee')) { continue }
        }
    }

    # Pass through original objects
    foreach ($item in $pipelineInput) {
        $item
    }
}

# --- xargs Command ---

function Invoke-BashXargs {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'xargs' }

    $replaceStr = $null
    $maxArgs = 0
    $nullDelim = $false       # -0: use null-delimited input
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

        if ($arg -ceq '-0' -or $arg -ceq '--null') {
            $nullDelim = $true
            $i++
            continue
        }

        if ($arg -ceq '-I') {
            $i++
            if ($i -lt $Arguments.Count) {
                $replaceStr = [string]$Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg.Length -gt 2 -and $arg.StartsWith('-I')) {
            $replaceStr = $arg.Substring(2)
            $i++
            continue
        }

        if ($arg -ceq '-n') {
            $i++
            if ($i -lt $Arguments.Count) {
                $maxArgs = [int]$Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-n(\d+)$') {
            $maxArgs = [int]$Matches[1]
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -eq 0) {
        Write-BashError -Message 'xargs: no command specified'
        return
    }

    $cmd = $operands[0]
    # Resolve to Invoke-Bash* if the command has a runtime function
    $bashCmd = 'Invoke-Bash' + ($cmd.Substring(0,1).ToUpper() + $cmd.Substring(1))
    if (Get-Command $bashCmd -ErrorAction SilentlyContinue) { $cmd = $bashCmd }
    $cmdArgs = @()
    if ($operands.Count -gt 1) {
        $cmdArgs = @($operands[1..($operands.Count - 1)])
    }

    # Collect input lines (split by delimiter)
    $inputLines = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $pipelineInput) {
        $text = Get-BashText -InputObject $item

        if ($nullDelim) {
            # -0: split on null characters
            $delim = "`0"
        } else {
            # Default: split on newlines (bash-style)
            $delim = "`n"
        }

        # Remove trailing delimiter if present
        $text = $text -replace "$([regex]::Escape($delim))$", ''

        if ($text -match $([regex]::Escape($delim))) {
            foreach ($subLine in ($text -split $([regex]::Escape($delim)))) {
                if ($subLine -ne '') { $inputLines.Add($subLine) }
            }
        } else {
            if ($text -ne '') { $inputLines.Add($text) }
        }
    }

    if ($null -ne $replaceStr) {
        # Replacement mode: run command once per input line
        foreach ($line in $inputLines) {
            $replacedArgs = @($cmdArgs | ForEach-Object { $_.Replace($replaceStr, $line) })
            & $cmd @replacedArgs
        }
    } elseif ($maxArgs -gt 0) {
        # Batch mode: run command with N args at a time
        for ($bi = 0; $bi -lt $inputLines.Count; $bi += $maxArgs) {
            $end = [System.Math]::Min($bi + $maxArgs, $inputLines.Count) - 1
            $batch = @($inputLines[$bi..$end])
            $allArgs = @($cmdArgs) + $batch
            & $cmd @allArgs
        }
    } else {
        # Default: all args in one invocation
        $allArgs = @($cmdArgs) + @($inputLines)
        & $cmd @allArgs
    }
}

