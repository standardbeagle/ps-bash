# --- Human-Numeric Comparator ---

function ConvertFrom-HumanNumeric {
    [CmdletBinding()]
    [OutputType([double])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    $trimmed = $Value.Trim()
    if ($trimmed -eq '') { return 0.0 }

    $multipliers = @{
        'K' = 1024.0
        'M' = 1048576.0
        'G' = 1073741824.0
        'T' = 1099511627776.0
        'P' = 1125899906842624.0
    }

    if ($trimmed -cmatch '^([0-9]*\.?[0-9]+)\s*([KMGTP])$') {
        $num = [double]$Matches[1]
        $suffix = $Matches[2]
        return $num * $multipliers[$suffix]
    }

    $parsed = 0.0
    if ([double]::TryParse($trimmed, [ref]$parsed)) {
        return $parsed
    }
    return 0.0
}

# --- Version Comparator ---

function Compare-Version {
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Left,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Right
    )

    $leftParts = $Left -split '[.\-]'
    $rightParts = $Right -split '[.\-]'
    $max = [System.Math]::Max($leftParts.Count, $rightParts.Count)

    for ($i = 0; $i -lt $max; $i++) {
        $lp = if ($i -lt $leftParts.Count) { $leftParts[$i] } else { '0' }
        $rp = if ($i -lt $rightParts.Count) { $rightParts[$i] } else { '0' }

        $ln = 0; $rn = 0
        $lIsNum = [int]::TryParse($lp, [ref]$ln)
        $rIsNum = [int]::TryParse($rp, [ref]$rn)

        if ($lIsNum -and $rIsNum) {
            if ($ln -ne $rn) { return ($ln - $rn) }
        } else {
            $cmp = [string]::Compare($lp, $rp, [System.StringComparison]::Ordinal)
            if ($cmp -ne 0) { return $cmp }
        }
    }
    return 0
}

# --- Month Comparator ---

function ConvertFrom-MonthName {
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    $monthMap = @{
        'jan' = 1; 'feb' = 2; 'mar' = 3; 'apr' = 4
        'may' = 5; 'jun' = 6; 'jul' = 7; 'aug' = 8
        'sep' = 9; 'oct' = 10; 'nov' = 11; 'dec' = 12
    }

    $trimmed = $Value.Trim().ToLower()
    if ($trimmed.Length -ge 3) {
        $key = $trimmed.Substring(0, 3)
        if ($monthMap.ContainsKey($key)) { return $monthMap[$key] }
    }
    return 0
}

# --- sort Command ---

function Invoke-BashSort {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'sort' }

    # Manual arg parsing for value-bearing flags (-k, -t)
    $reverse = $false
    $numeric = $false
    $unique = $false
    $foldCase = $false
    $humanNumeric = $false
    $versionSort = $false
    $monthSort = $false
    $checkOnly = $false
    $blankIgnore = $false
    $dictOrder = $false
    $stableSort = $false
    $delimiter = $null
    # Each key spec: @{ StartField; StartChar; EndField; EndChar; Numeric; Reverse; BlankIgnore }
    $keySpecs = [System.Collections.Generic.List[hashtable]]::new()
    $operands = [System.Collections.Generic.List[string]]::new()
    $pastDoubleDash = $false

    # Parse a single key position like "2.3rn" into field, char offset, and flags
    $parseKeySpecPos = {
        param([string]$s)
        $field = 0
        $charOffset = 0
        $keyNumeric = $false
        $keyReverse = $false
        $keyBlankIgnore = $false
        if ($s -match '^(\d+)(?:\.(\d+))?([nrRbB]*)?$') {
            $field = [int]$Matches[1]
            if ($null -ne $Matches[2] -and $Matches[2] -ne '') {
                $charOffset = [int]$Matches[2]
            }
            if ($null -ne $Matches[3]) {
                foreach ($c in $Matches[3].ToCharArray()) {
                    switch ($c) {
                        'n' { $keyNumeric = $true }
                        'r' { $keyReverse = $true }
                        'R' { $keyReverse = $true }
                        'b' { $keyBlankIgnore = $true }
                        'B' { $keyBlankIgnore = $true }
                    }
                }
            }
        }
        return @{ Field = $field; CharOffset = $charOffset; Numeric = $keyNumeric; Reverse = $keyReverse; BlankIgnore = $keyBlankIgnore }
    }

    # Parse a full -k spec like "2.3,4.1nr" into start and end positions
    $parseKeySpec = {
        param([string]$spec)
        $parts = $spec -split ',', 2
        $start = & $parseKeySpecPos $parts[0]
        $endField = 0; $endChar = 0
        $endNumeric = $start.Numeric; $endReverse = $start.Reverse; $endBlankIgnore = $start.BlankIgnore
        if ($parts.Count -ge 2) {
            $endPos = & $parseKeySpecPos $parts[1]
            $endField = $endPos.Field
            $endChar = $endPos.CharOffset
            if ($endPos.Numeric) { $endNumeric = $true }
            if ($endPos.Reverse) { $endReverse = $true }
            if ($endPos.BlankIgnore) { $endBlankIgnore = $true }
        }
        return @{
            StartField    = $start.Field
            StartChar     = $start.CharOffset
            EndField      = $endField
            EndChar       = $endChar
            Numeric       = $endNumeric
            Reverse       = $endReverse
            BlankIgnore   = $endBlankIgnore
        }
    }

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

        # -t with joined value (e.g. -t:)
        if ($arg -cmatch '^-t(.+)$') {
            $delimiter = $Matches[1]
            $i++
            continue
        }

        # -k with joined value (e.g. -k2 or -k2,2 or -k2.3,4.1n)
        if ($arg -cmatch '^-k(\d[^,\s]*(?:,\d[^,\s]*)?)$') {
            $keySpecs.Add((& $parseKeySpec $Matches[1]))
            $i++
            continue
        }

        # -t as separate arg
        if ($arg -ceq '-t') {
            $i++
            if ($i -lt $Arguments.Count) {
                $delimiter = $Arguments[$i]
            }
            $i++
            continue
        }

        # -k as separate arg
        if ($arg -ceq '-k') {
            $i++
            if ($i -lt $Arguments.Count) {
                $keySpecs.Add((& $parseKeySpec $Arguments[$i]))
            }
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'r' { $reverse = $true }
                    'n' { $numeric = $true }
                    'u' { $unique = $true }
                    'f' { $foldCase = $true }
                    'h' { $humanNumeric = $true }
                    'V' { $versionSort = $true }
                    'M' { $monthSort = $true }
                    'c' { $checkOnly = $true }
                    'b' { $blankIgnore = $true }
                    'd' { $dictOrder = $true }
                    's' { $stableSort = $true }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Collect items from pipeline or file operands
    $items = [System.Collections.Generic.List[object]]::new()

    if ($pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $items.Add(($subLine))
                }
            } else {
                $items.Add($item)
            }
        }
    }

    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
        $fileLines = Read-BashFileLines -Path $filePath -Command 'sort'
        if ($null -eq $fileLines) { continue }
        foreach ($line in $fileLines) {
            $items.Add((New-BashObject -BashText $line))
        }
    }

    # Extract text for a key spec from an item
    $extractKeyText = {
        param($item, $spec)
        $text = Get-BashText -InputObject $item
        $text = $text.TrimEnd("`n".ToCharArray())
        if ($null -eq $spec) { return $text }
        $sep = if ($null -ne $delimiter) { [regex]::Escape($delimiter) } else { '\s+' }
        $parts = $text -split $sep
        $startIdx = $spec.StartField - 1
        if ($startIdx -lt 0) { $startIdx = 0 }
        if ($startIdx -ge $parts.Count) { return '' }
        # Build the key from start field to end field
        $endIdx = if ($spec.EndField -gt 0) { $spec.EndField - 1 } else { $parts.Count - 1 }
        if ($endIdx -ge $parts.Count) { $endIdx = $parts.Count - 1 }
        $fields = [System.Collections.Generic.List[string]]::new()
        for ($fi = $startIdx; $fi -le $endIdx; $fi++) {
            $fieldText = $parts[$fi]
            # Trim leading chars before StartChar on first field
            if ($fi -eq $startIdx -and $spec.StartChar -gt 0) {
                $skip = $spec.StartChar - 1
                if ($skip -lt $fieldText.Length) {
                    $fieldText = $fieldText.Substring($skip)
                } else {
                    $fieldText = ''
                }
            }
            # Trim after EndChar on last field
            if ($fi -eq $endIdx -and $spec.EndChar -gt 0) {
                if ($spec.EndChar -lt $fieldText.Length) {
                    $fieldText = $fieldText.Substring(0, $spec.EndChar)
                }
            }
            $fields.Add($fieldText)
        }
        $key = $fields -join ' '
        if ($spec.BlankIgnore -or $blankIgnore) {
            $key = $key -replace '^\s+', ''
        }
        return $key
    }

    # Full-line sort key (no -k specs)
    $getFullText = {
        param($item)
        $text = Get-BashText -InputObject $item
        $text = $text.TrimEnd("`n".ToCharArray())
        if ($blankIgnore) { $text = $text -replace '^\s+', '' }
        return $text
    }

    # Compare two items returning -1, 0, or 1
    $compareItems = {
        param($a, $b)
        if ($keySpecs.Count -gt 0) {
            foreach ($spec in $keySpecs) {
                $aKey = & $extractKeyText $a $spec
                $bKey = & $extractKeyText $b $spec
                $aKey = if ($spec.BlankIgnore -or $blankIgnore) { $aKey -replace '^\s+', '' } else { $aKey }
                $bKey = if ($spec.BlankIgnore -or $blankIgnore) { $bKey -replace '^\s+', '' } else { $bKey }
                if ($dictOrder) {
                    $aKey = $aKey -replace '[^a-zA-Z0-9\s]', ''
                    $bKey = $bKey -replace '[^a-zA-Z0-9\s]', ''
                }
                $cmp = 0
                if ($humanNumeric) {
                    $aH = ConvertFrom-HumanNumeric -Value $aKey
                    $bH = ConvertFrom-HumanNumeric -Value $bKey
                    if ($aH -lt $bH) { $cmp = -1 }
                    elseif ($aH -gt $bH) { $cmp = 1 }
                } elseif ($spec.Numeric -or $numeric) {
                    $aN = 0.0; $bN = 0.0
                    $aNstr = if ($aKey -match '^[+-]?\d+(?:\.\d+)?') { $Matches[0] } else { '0' }
                    $bNstr = if ($bKey -match '^[+-]?\d+(?:\.\d+)?') { $Matches[0] } else { '0' }
                    [void][double]::TryParse($aNstr, [ref]$aN)
                    [void][double]::TryParse($bNstr, [ref]$bN)
                    if ($aN -lt $bN) { $cmp = -1 }
                    elseif ($aN -gt $bN) { $cmp = 1 }
                } elseif ($monthSort) {
                    $aM = ConvertFrom-MonthName -Value $aKey
                    $bM = ConvertFrom-MonthName -Value $bKey
                    if ($aM -lt $bM) { $cmp = -1 }
                    elseif ($aM -gt $bM) { $cmp = 1 }
                } elseif ($foldCase) {
                    $cmp = [string]::Compare($aKey, $bKey, [System.StringComparison]::OrdinalIgnoreCase)
                } else {
                    $cmp = [string]::Compare($aKey, $bKey, [System.StringComparison]::Ordinal)
                }
                if ($spec.Reverse -or $reverse) { $cmp = -$cmp }
                if ($cmp -ne 0) { return $cmp }
            }
            return 0
        }
        # No -k specs: use global flags on full line
        $aText = & $getFullText $a
        $bText = & $getFullText $b
        if ($dictOrder) {
            $aText = $aText -replace '[^a-zA-Z0-9\s]', ''
            $bText = $bText -replace '[^a-zA-Z0-9\s]', ''
        }
        $cmp = 0
        if ($humanNumeric) {
            $aH = ConvertFrom-HumanNumeric -Value $aText
            $bH = ConvertFrom-HumanNumeric -Value $bText
            if ($aH -lt $bH) { $cmp = -1 }
            elseif ($aH -gt $bH) { $cmp = 1 }
        } elseif ($numeric) {
            $aN = 0.0; $bN = 0.0
            [void][double]::TryParse($aText, [ref]$aN)
            [void][double]::TryParse($bText, [ref]$bN)
            if ($aN -lt $bN) { $cmp = -1 }
            elseif ($aN -gt $bN) { $cmp = 1 }
        } elseif ($monthSort) {
            $aM = ConvertFrom-MonthName -Value $aText
            $bM = ConvertFrom-MonthName -Value $bText
            if ($aM -lt $bM) { $cmp = -1 }
            elseif ($aM -gt $bM) { $cmp = 1 }
        } elseif ($foldCase) {
            $cmp = [string]::Compare($aText, $bText, [System.StringComparison]::OrdinalIgnoreCase)
        } else {
            $cmp = [string]::Compare($aText, $bText, [System.StringComparison]::Ordinal)
        }
        if ($reverse) { $cmp = -$cmp }
        return $cmp
    }

    # Smart path: -h with LsEntry objects uses SizeBytes directly
    $useSizeBytesPath = $humanNumeric -and $items.Count -gt 0 -and
        $null -ne $items[0].PSObject -and
        $null -ne $items[0].PSObject.Properties['SizeBytes']

    # Check-only mode
    if ($checkOnly) {
        for ($idx = 1; $idx -lt $items.Count; $idx++) {
            $cmp = & $compareItems $items[$idx - 1] $items[$idx]
            if ($cmp -gt 0) {
                $global:LASTEXITCODE = 1
                return
            }
        }
        $global:LASTEXITCODE = 0
        return
    }

    # Build indexed list for stable sort tracking
    $indexed = [System.Collections.Generic.List[object]]::new()
    for ($idx = 0; $idx -lt $items.Count; $idx++) {
        $indexed.Add(@{
            Index = $idx
            Item  = $items[$idx]
        })
    }

    # Sort path selection
    $useCustomSort = $keySpecs.Count -gt 0 -or $dictOrder -or $blankIgnore
    $sorted = $null

    if ($versionSort) {
        # Version sort: insertion sort with Compare-Version
        $list = [System.Collections.Generic.List[object]]::new(@($indexed))
        for ($i2 = 1; $i2 -lt $list.Count; $i2++) {
            $current = $list[$i2]
            $currentText = (& $getFullText $current.Item) -replace "`n$", ''
            $j = $i2 - 1
            while ($j -ge 0) {
                $otherText = (& $getFullText $list[$j].Item) -replace "`n$", ''
                $vcmp = Compare-Version -Left $otherText -Right $currentText
                if ($reverse) { $vcmp = -$vcmp }
                if ($vcmp -le 0) { break }
                $list[$j + 1] = $list[$j]
                $j--
            }
            $list[$j + 1] = $current
        }
        $sorted = $list
    } elseif ($useCustomSort) {
        # Custom comparison: insertion sort for multi-key / dict / blank support
        $list = [System.Collections.Generic.List[object]]::new(@($indexed))
        for ($i2 = 1; $i2 -lt $list.Count; $i2++) {
            $current = $list[$i2]
            $j = $i2 - 1
            while ($j -ge 0) {
                $cmp = & $compareItems $list[$j].Item $current.Item
                if ($cmp -le 0) { break }
                $list[$j + 1] = $list[$j]
                $j--
            }
            $list[$j + 1] = $current
        }
        $sorted = $list
    } else {
        # Standard path: List.Sort with Comparison delegate — avoids Sort-Object pipeline overhead
        $list = [System.Collections.Generic.List[object]]::new(@($indexed))
        $sortComparison = [Comparison[object]]{
            param($a, $b)
            $aItem = $a.Item; $bItem = $b.Item
            if ($useSizeBytesPath) {
                $cmp = [double]$aItem.SizeBytes - [double]$bItem.SizeBytes
            } else {
                $cmp = & $compareItems $aItem $bItem
            }
            if ($cmp -eq 0) {
                # Stable: preserve original order for equal items
                return $a.Index - $b.Index
            }
            return $cmp
        }
        $list.Sort($sortComparison)
        $sorted = $list
    }

    # Unique: deduplicate by sort text
    if ($unique) {
        $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $deduped = [System.Collections.Generic.List[object]]::new()
        foreach ($entry in $sorted) {
            $text = & $getFullText $entry.Item
            $key = if ($foldCase) { $text.ToLower() } else { $text }
            if ($seen.Add($key)) {
                $deduped.Add($entry)
            }
        }
        $sorted = $deduped
    }

    # Emit original objects (pipeline bridge: preserve types)
    foreach ($entry in $sorted) {
        $entry.Item
    }
}

