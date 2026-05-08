# --- jq Command ---

function ConvertTo-JqJson {
    param([object]$Value, [bool]$Compact, [bool]$SortKeys, [bool]$RawOutput)

    if ($null -eq $Value) { return 'null' }

    if ($Value -is [bool]) {
        if ($Value) { return 'true' } else { return 'false' }
    }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) {
        return "$Value"
    }
    if ($Value -is [string]) {
        if ($RawOutput) { return $Value }
        $escaped = $Value -replace '\\', '\\' -replace '"', '\"' -replace "`n", '\n' -replace "`r", '\r' -replace "`t", '\t'
        return "`"$escaped`""
    }
    if ($Value -is [array] -or $Value -is [System.Collections.IList]) {
        $items = @(foreach ($item in $Value) {
            ConvertTo-JqJson -Value $item -Compact $Compact -SortKeys $SortKeys -RawOutput $false
        })
        if ($Compact) {
            return '[' + ($items -join ',') + ']'
        }
        if ($items.Count -eq 0) { return '[]' }
        $inner = ($items | ForEach-Object { "  $_" }) -join ",`n"
        return "[`n$inner`n]"
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $keys = @($Value.Keys)
        if ($SortKeys) { $keys = @($keys | Sort-Object) }
        $pairs = @(foreach ($k in $keys) {
            $kJson = "`"$k`""
            $vJson = ConvertTo-JqJson -Value $Value[$k] -Compact $Compact -SortKeys $SortKeys -RawOutput $false
            if ($Compact) { "${kJson}:${vJson}" } else { "  ${kJson}: ${vJson}" }
        })
        if ($Compact) {
            return '{' + ($pairs -join ',') + '}'
        }
        if ($pairs.Count -eq 0) { return '{}' }
        return "{`n" + ($pairs -join ",`n") + "`n}"
    }
    if ($Value -is [PSCustomObject]) {
        $dict = [ordered]@{}
        foreach ($prop in $Value.PSObject.Properties) {
            if ($prop.Name -eq 'PSTypeName') { continue }
            $dict[$prop.Name] = $prop.Value
        }
        return ConvertTo-JqJson -Value $dict -Compact $Compact -SortKeys $SortKeys -RawOutput $false
    }
    return "`"$Value`""
}

function Invoke-JqFilter {
    param([object]$Data, [string]$Filter, [hashtable]$Variables)

    if ($null -eq $Variables) { $Variables = @{} }

    $filter = $Filter.Trim()
    if ($filter -eq '') { return @(, $Data) }

    # Handle pipe: split on top-level | (not inside parens/brackets/strings)
    [string[]]$pipeSegments = @(Split-JqPipe -Filter $filter)
    if ($pipeSegments.Count -gt 1) {
        $current = @(, $Data)
        $scope = $Variables
        foreach ($seg in $pipeSegments) {
            # Handle: expr as $var | next_expr
            if ($seg -match '^(.+?)\s+as\s+(\$\w+)\s*$') {
                $bindingExpr = $Matches[1].Trim()
                $varName = $Matches[2]
                $bound = [System.Collections.Generic.List[object]]::new()
                foreach ($item in $current) {
                    $bound.AddRange(@(Invoke-JqFilter -Data $item -Filter $bindingExpr -Variables $scope))
                }
                $newScope = @{} + $scope
                $newScope[$varName] = $bound.ToArray()
                $current = $current
                $scope = $newScope
                continue
            }
            $next = [System.Collections.Generic.List[object]]::new()
            foreach ($item in $current) {
                $next.AddRange(@(Invoke-JqFilter -Data $item -Filter $seg -Variables $scope))
            }
            $current = $next.ToArray()
        }
        return $current
    }

    # Handle comma (multiple outputs) at top level
    [string[]]$commaSegments = @(Split-JqComma -Filter $filter)
    if ($commaSegments.Count -gt 1) {
        $results = [System.Collections.Generic.List[object]]::new()
        foreach ($seg in $commaSegments) {
            $results.AddRange(@(Invoke-JqFilter -Data $Data -Filter $seg.Trim() -Variables $Variables))
        }
        return $results.ToArray()
    }

    # Handle alternative operator: expr // fallback
    $altIdx = Find-JqTopLevelStr -S $filter -Sub '//'
    if ($altIdx -ge 0) {
        $leftExpr = $filter.Substring(0, $altIdx).Trim()
        $rightExpr = $filter.Substring($altIdx + 2).Trim()
        $leftResults = @(Invoke-JqFilter -Data $Data -Filter $leftExpr -Variables $Variables)
        foreach ($val in $leftResults) {
            if ($null -ne $val -and $val -ne $false) { return @(, $val) }
        }
        return @(Invoke-JqFilter -Data $Data -Filter $rightExpr -Variables $Variables)
    }

    # Handle if-then-elif-else-end
    if ($filter.StartsWith('if ')) {
        return @(Invoke-JqIf -Data $Data -Filter $filter -Variables $Variables)
    }

    # Recursive descent: ..
    if ($filter -eq '..') {
        return @(Invoke-JqRecurse -Data $Data)
    }

    # Variable reference: $varname
    if ($filter.StartsWith('$') -and $filter -match '^\$\w+$') {
        if ($Variables.ContainsKey($filter)) {
            $val = $Variables[$filter]
            if ($val -is [array] -or $val -is [System.Collections.IList]) {
                return @($val)
            }
            return @(, $val)
        }
        return @(, $null)
    }

    # Identity
    if ($filter -eq '.') { return @(, $Data) }

    # Array construction: [expr]
    if ($filter.StartsWith('[') -and (Get-JqMatchingBracket -S $filter -Open '[' -Close ']' -Start 0) -eq ($filter.Length - 1)) {
        $inner = $filter.Substring(1, $filter.Length - 2)
        $items = @(Invoke-JqFilter -Data $Data -Filter $inner -Variables $Variables)
        return @(, $items)
    }

    # Object construction: {key: expr, ...}
    if ($filter.StartsWith('{') -and (Get-JqMatchingBracket -S $filter -Open '{' -Close '}' -Start 0) -eq ($filter.Length - 1)) {
        $inner = $filter.Substring(1, $filter.Length - 2).Trim()
        $result = [ordered]@{}
        [string[]]$pairs = @(Split-JqComma -Filter $inner)
        foreach ($pair in $pairs) {
            $pair = $pair.Trim()
            $colonIdx = Find-JqTopLevelChar -S $pair -Ch ':'
            if ($colonIdx -ge 0) {
                $keyPart = $pair.Substring(0, $colonIdx).Trim()
                $valExpr = $pair.Substring($colonIdx + 1).Trim()
                # Strip quotes from key if present
                if ($keyPart.StartsWith('"') -and $keyPart.EndsWith('"')) {
                    $keyPart = $keyPart.Substring(1, $keyPart.Length - 2)
                }
                $vals = @(Invoke-JqFilter -Data $Data -Filter $valExpr -Variables $Variables)
                $result[$keyPart] = if ($vals.Count -eq 1) { $vals[0] } else { $vals }
            } else {
                # Shorthand: just a name means {name: .name}
                $keyPart = $pair.TrimStart('.')
                $vals = @(Invoke-JqFilter -Data $Data -Filter ".$keyPart" -Variables $Variables)
                $result[$keyPart] = if ($vals.Count -eq 1) { $vals[0] } else { $vals }
            }
        }
        return @(, $result)
    }

    # String literal with interpolation: "...\(expr)..."
    if ($filter.StartsWith('"') -and $filter.EndsWith('"')) {
        $strContent = $filter.Substring(1, $filter.Length - 2)
        $result = Resolve-JqStringInterpolation -S $strContent -Data $Data -Variables $Variables
        return @(, $result)
    }

    # Built-in functions
    if ($filter -eq 'keys') {
        if ($Data -is [System.Collections.IDictionary]) {
            return @(, @($Data.Keys | Sort-Object))
        }
        if ($Data -is [PSCustomObject]) {
            $names = @($Data.PSObject.Properties | Where-Object { $_.Name -ne 'PSTypeName' } | ForEach-Object { $_.Name } | Sort-Object)
            return @(, $names)
        }
        if ($Data -is [array] -or $Data -is [System.Collections.IList]) {
            return @(, @(0..($Data.Count - 1)))
        }
        return @(, @())
    }
    if ($filter -eq 'values') {
        if ($Data -is [System.Collections.IDictionary]) {
            return @(, @($Data.Values))
        }
        if ($Data -is [PSCustomObject]) {
            $vals = @($Data.PSObject.Properties | Where-Object { $_.Name -ne 'PSTypeName' } | ForEach-Object { $_.Value })
            return @(, $vals)
        }
        if ($Data -is [array] -or $Data -is [System.Collections.IList]) {
            return @(, @($Data))
        }
        return @(, @())
    }
    if ($filter -eq 'length') {
        if ($null -eq $Data) { return @(, 0) }
        if ($Data -is [string]) { return @(, $Data.Length) }
        if ($Data -is [array] -or $Data -is [System.Collections.IList]) { return @(, $Data.Count) }
        if ($Data -is [System.Collections.IDictionary]) { return @(, $Data.Count) }
        if ($Data -is [PSCustomObject]) {
            $count = @($Data.PSObject.Properties | Where-Object { $_.Name -ne 'PSTypeName' }).Count
            return @(, $count)
        }
        return @(, 0)
    }
    if ($filter -eq 'type') {
        if ($null -eq $Data) { return @(, 'null') }
        if ($Data -is [bool]) { return @(, 'boolean') }
        if ($Data -is [int] -or $Data -is [long] -or $Data -is [double] -or $Data -is [decimal]) { return @(, 'number') }
        if ($Data -is [string]) { return @(, 'string') }
        if ($Data -is [array] -or $Data -is [System.Collections.IList]) { return @(, 'array') }
        if ($Data -is [System.Collections.IDictionary] -or $Data -is [PSCustomObject]) { return @(, 'object') }
        return @(, 'unknown')
    }

    # not
    if ($filter -eq 'not') {
        $falsy = ($null -eq $Data) -or ($Data -is [bool] -and -not $Data) -or ($Data -eq $false)
        return @(, $falsy)
    }

    # map(expr)
    if ($filter -match '^map\((.+)\)$') {
        $innerExpr = $Matches[1]
        $items = @()
        if ($Data -is [array] -or $Data -is [System.Collections.IList]) {
            foreach ($elem in $Data) {
                $items += @(Invoke-JqFilter -Data $elem -Filter $innerExpr -Variables $Variables)
            }
        }
        return @(, $items)
    }

    # select(expr)
    if ($filter -match '^select\((.+)\)$') {
        $expr = $Matches[1]
        $result = Invoke-JqSelect -Data $Data -Expr $expr -Variables $Variables
        if ($result) { return @(, $Data) }
        return @()
    }

    # Field access chain: .foo, .foo.bar, .[0], .[], .[].foo etc.
    if ($filter.StartsWith('.')) {
        return @(Resolve-JqDotPath -Data $Data -Path $filter)
    }

    # Numeric literal
    if ($filter -match '^\-?\d+(\.\d+)?$') {
        return @(, [double]$filter)
    }

    # Boolean/null literals
    if ($filter -eq 'true') { return @(, $true) }
    if ($filter -eq 'false') { return @(, $false) }
    if ($filter -eq 'null') { return @(, $null) }

    Write-Error "jq: unknown filter: $filter" -ErrorAction Continue
    return @()
}

function Split-JqPipe {
    param([string]$Filter)
    $segments = [System.Collections.Generic.List[string]]::new()
    $depth = 0
    $inStr = $false
    $current = [System.Text.StringBuilder]::new()

    for ($i = 0; $i -lt $Filter.Length; $i++) {
        $c = $Filter[$i]
        if ($inStr) {
            $current.Append($c) | Out-Null
            if ($c -eq '\' -and ($i + 1) -lt $Filter.Length) {
                $i++
                $current.Append($Filter[$i]) | Out-Null
            } elseif ($c -eq '"') {
                $inStr = $false
            }
            continue
        }
        if ($c -eq '"') { $inStr = $true; $current.Append($c) | Out-Null; continue }
        if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++ }
        if ($c -eq ')' -or $c -eq ']' -or $c -eq '}') { $depth-- }
        if ($c -eq '|' -and $depth -eq 0) {
            $segments.Add($current.ToString().Trim())
            $current = [System.Text.StringBuilder]::new()
            continue
        }
        $current.Append($c) | Out-Null
    }
    $last = $current.ToString().Trim()
    if ($last -ne '') { $segments.Add($last) }
    return @($segments)
}

function Split-JqComma {
    param([string]$Filter)
    $segments = [System.Collections.Generic.List[string]]::new()
    $depth = 0
    $inStr = $false
    $current = [System.Text.StringBuilder]::new()

    for ($i = 0; $i -lt $Filter.Length; $i++) {
        $c = $Filter[$i]
        if ($inStr) {
            $current.Append($c) | Out-Null
            if ($c -eq '\' -and ($i + 1) -lt $Filter.Length) {
                $i++
                $current.Append($Filter[$i]) | Out-Null
            } elseif ($c -eq '"') {
                $inStr = $false
            }
            continue
        }
        if ($c -eq '"') { $inStr = $true; $current.Append($c) | Out-Null; continue }
        if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++ }
        if ($c -eq ')' -or $c -eq ']' -or $c -eq '}') { $depth-- }
        if ($c -eq ',' -and $depth -eq 0) {
            $segments.Add($current.ToString().Trim())
            $current = [System.Text.StringBuilder]::new()
            continue
        }
        $current.Append($c) | Out-Null
    }
    $last = $current.ToString().Trim()
    if ($last -ne '') { $segments.Add($last) }
    return @($segments)
}

function Get-JqMatchingBracket {
    param([string]$S, [char]$Open, [char]$Close, [int]$Start)
    $depth = 0
    $inStr = $false
    for ($i = $Start; $i -lt $S.Length; $i++) {
        $c = $S[$i]
        if ($inStr) {
            if ($c -eq '\' -and ($i + 1) -lt $S.Length) { $i++; continue }
            if ($c -eq '"') { $inStr = $false }
            continue
        }
        if ($c -eq '"') { $inStr = $true; continue }
        if ($c -eq $Open) { $depth++ }
        if ($c -eq $Close) { $depth--; if ($depth -eq 0) { return $i } }
    }
    return -1
}

function Find-JqTopLevelChar {
    param([string]$S, [char]$Ch)
    $depth = 0
    $inStr = $false
    for ($i = 0; $i -lt $S.Length; $i++) {
        $c = $S[$i]
        if ($inStr) {
            if ($c -eq '\' -and ($i + 1) -lt $S.Length) { $i++; continue }
            if ($c -eq '"') { $inStr = $false }
            continue
        }
        if ($c -eq '"') { $inStr = $true; continue }
        if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++ }
        if ($c -eq ')' -or $c -eq ']' -or $c -eq '}') { $depth-- }
        if ($c -eq $Ch -and $depth -eq 0) { return $i }
    }
    return -1
}

function Resolve-JqDotPath {
    param([object]$Data, [string]$Path)

    $pos = 1  # skip leading dot
    $current = @(, $Data)

    while ($pos -lt $Path.Length) {
        $ch = $Path[$pos]

        # Array iterate: .[]
        if ($ch -eq '[') {
            $closeIdx = Get-JqMatchingBracket -S $Path -Open '[' -Close ']' -Start $pos
            if ($closeIdx -lt 0) {
                Write-Error "jq: unmatched [ in path" -ErrorAction Continue
                return @()
            }
            $inner = $Path.Substring($pos + 1, $closeIdx - $pos - 1).Trim()
            $pos = $closeIdx + 1

            $next = [System.Collections.Generic.List[object]]::new()
            if ($inner -eq '') {
                # .[] iterate
                foreach ($item in $current) {
                    if ($item -is [array] -or $item -is [System.Collections.IList]) {
                        foreach ($elem in $item) { $next.Add($elem) }
                    } elseif ($item -is [System.Collections.IDictionary]) {
                        foreach ($val in $item.Values) { $next.Add($val) }
                    } elseif ($item -is [PSCustomObject]) {
                        foreach ($prop in $item.PSObject.Properties) {
                            if ($prop.Name -ne 'PSTypeName') { $next.Add($prop.Value) }
                        }
                    }
                }
            } else {
                # .[N] index
                $idx = [int]$inner
                foreach ($item in $current) {
                    if ($item -is [array] -or $item -is [System.Collections.IList]) {
                        if ($idx -lt 0) { $idx = $item.Count + $idx }
                        if ($idx -ge 0 -and $idx -lt $item.Count) {
                            $next.Add($item[$idx])
                        } else {
                            $next.Add($null)
                        }
                    }
                }
            }
            $current = $next.ToArray()
            continue
        }

        # Field access: .fieldname
        if ($ch -eq '.') {
            $pos++
            continue
        }

        # Read field name
        $nameStart = $pos
        while ($pos -lt $Path.Length -and $Path[$pos] -ne '.' -and $Path[$pos] -ne '[') {
            $pos++
        }
        $fieldName = $Path.Substring($nameStart, $pos - $nameStart)
        if ($fieldName -eq '') { continue }

        $next = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $current) {
            $val = $null
            if ($item -is [System.Collections.IDictionary]) {
                if ($item.Contains($fieldName)) { $val = $item[$fieldName] }
            } elseif ($item -is [PSCustomObject]) {
                $prop = $item.PSObject.Properties[$fieldName]
                if ($null -ne $prop) { $val = $prop.Value }
            }
            $next.Add($val)
        }
        $current = $next.ToArray()
    }

    return $current
}

function Invoke-JqSelect {
    param([object]$Data, [string]$Expr, [hashtable]$Variables)

    if ($null -eq $Variables) { $Variables = @{} }

    # Parse comparison: . op value, .field op value
    $ops = @('>=', '<=', '!=', '==', '>', '<')
    foreach ($op in $ops) {
        $opIdx = Find-JqTopLevelStr -S $Expr -Sub $op
        if ($opIdx -ge 0) {
            $leftExpr = $Expr.Substring(0, $opIdx).Trim()
            $rightExpr = $Expr.Substring($opIdx + $op.Length).Trim()

            $leftVals = @(Invoke-JqFilter -Data $Data -Filter $leftExpr -Variables $Variables)
            $rightVals = @(Invoke-JqFilter -Data $Data -Filter $rightExpr -Variables $Variables)
            $left = if ($leftVals.Count -gt 0) { $leftVals[0] } else { $null }
            $right = if ($rightVals.Count -gt 0) { $rightVals[0] } else { $null }

            switch ($op) {
                '==' { return $left -eq $right }
                '!=' { return $left -ne $right }
                '>'  { return $left -gt $right }
                '<'  { return $left -lt $right }
                '>=' { return $left -ge $right }
                '<=' { return $left -le $right }
            }
        }
    }

    # Boolean check: just evaluate the expression and check truthiness
    $vals = @(Invoke-JqFilter -Data $Data -Filter $Expr -Variables $Variables)
    if ($vals.Count -eq 0) { return $false }
    $val = $vals[0]
    return ($null -ne $val) -and ($val -ne $false)
}

function Invoke-JqIf {
    param([object]$Data, [string]$Filter, [hashtable]$Variables)

    # Parse: if COND then BODY [elif COND then BODY]* [else BODY] end
    $rest = $Filter
    $results = @()

    while ($rest.StartsWith('if ')) {
        # Find 'then' at depth 0
        $thenIdx = Find-JqKeyword -S $rest -Keyword 'then'
        if ($thenIdx -lt 0) {
            Write-Error "jq: expected 'then' in if expression" -ErrorAction Continue
            return @()
        }
        $condExpr = $rest.Substring(3, $thenIdx - 3).Trim()
        $rest = $rest.Substring($thenIdx + 4).Trim()

        # Find next keyword at depth 0: elif, else, end
        $nextKw = Find-JqBranchKeyword -S $rest
        $bodyExpr = $rest.Substring(0, $nextKw.Index).Trim()
        $rest = $rest.Substring($nextKw.Index).Trim()

        # Evaluate condition
        $condVals = @(Invoke-JqFilter -Data $Data -Filter $condExpr -Variables $Variables)
        $condTrue = ($condVals.Count -gt 0) -and ($null -ne $condVals[0]) -and ($condVals[0] -ne $false)

        if ($condTrue) {
            return @(Invoke-JqFilter -Data $Data -Filter $bodyExpr -Variables $Variables)
        }

        # Skip to next branch
        if ($nextKw.Keyword -eq 'elif') {
            $rest = "if $($rest.Substring(4).Trim())"
            continue
        } elseif ($nextKw.Keyword -eq 'else') {
            # Find 'end' at depth 0 after 'else'
            $endIdx = Find-JqKeyword -S $rest -Keyword 'end'
            if ($endIdx -lt 0) {
                Write-Error "jq: expected 'end' in if expression" -ErrorAction Continue
                return @()
            }
            $elseBody = $rest.Substring(4, $endIdx - 4).Trim()
            return @(Invoke-JqFilter -Data $Data -Filter $elseBody -Variables $Variables)
        } elseif ($nextKw.Keyword -eq 'end') {
            # No branch matched, no else -- return nothing
            return @()
        }
    }

    return @()
}

function Find-JqKeyword {
    param([string]$S, [string]$Keyword)
    $depth = 0
    $inStr = $false
    for ($i = 0; $i -le ($S.Length - $Keyword.Length); $i++) {
        $c = $S[$i]
        if ($inStr) {
            if ($c -eq '\' -and ($i + 1) -lt $S.Length) { $i++; continue }
            if ($c -eq '"') { $inStr = $false }
            continue
        }
        if ($c -eq '"') { $inStr = $true; continue }
        if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++ }
        if ($c -eq ')' -or $c -eq ']' -or $c -eq '}') { $depth-- }
        if ($depth -eq 0 -and $S.Substring($i, $Keyword.Length) -eq $Keyword) {
            # Ensure it's a word boundary (not part of a longer word)
            $beforeOk = ($i -eq 0) -or ($S[$i - 1] -match '[\s\(\[\{,;]')
            $afterIdx = $i + $Keyword.Length
            $afterOk = ($afterIdx -ge $S.Length) -or ($S[$afterIdx] -match '[\s\)\]\},;]')
            if ($beforeOk -and $afterOk) { return $i }
        }
    }
    return -1
}

function Find-JqBranchKeyword {
    param([string]$S)
    $depth = 0
    $inStr = $false
    $bestIdx = $S.Length
    $bestKw = 'end'
    foreach ($kw in @('elif', 'else', 'end')) {
        for ($i = 0; $i -le ($S.Length - $kw.Length); $i++) {
            $c = $S[$i]
            if ($inStr) {
                if ($c -eq '\' -and ($i + 1) -lt $S.Length) { $i++; continue }
                if ($c -eq '"') { $inStr = $false }
                continue
            }
            if ($c -eq '"') { $inStr = $true; continue }
            if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++ }
            if ($c -eq ')' -or $c -eq ']' -or $c -eq '}') { $depth-- }
            if ($depth -eq 0 -and $S.Substring($i, $kw.Length) -eq $kw) {
                $beforeOk = ($i -eq 0) -or ($S[$i - 1] -match '[\s\(\[\{,;]')
                $afterIdx = $i + $kw.Length
                $afterOk = ($afterIdx -ge $S.Length) -or ($S[$afterIdx] -match '[\s\)\]\},;]')
                if ($beforeOk -and $afterOk -and $i -lt $bestIdx) {
                    $bestIdx = $i
                    $bestKw = $kw
                    break
                }
            }
        }
    }
    return @{ Index = $bestIdx; Keyword = $bestKw }
}

function Invoke-JqRecurse {
    param([object]$Data)

    $results = [System.Collections.Generic.List[object]]::new()
    $results.Add($Data)
    if ($Data -is [array] -or $Data -is [System.Collections.IList]) {
        foreach ($elem in $Data) {
            $results.AddRange(@(Invoke-JqRecurse -Data $elem))
        }
    } elseif ($Data -is [System.Collections.IDictionary]) {
        foreach ($val in $Data.Values) {
            $results.AddRange(@(Invoke-JqRecurse -Data $val))
        }
    } elseif ($Data -is [PSCustomObject]) {
        foreach ($prop in $Data.PSObject.Properties) {
            if ($prop.Name -ne 'PSTypeName') {
                $results.AddRange(@(Invoke-JqRecurse -Data $prop.Value))
            }
        }
    }
    return $results.ToArray()
}

function Find-JqTopLevelStr {
    param([string]$S, [string]$Sub)
    $depth = 0
    $inStr = $false
    for ($i = 0; $i -le ($S.Length - $Sub.Length); $i++) {
        $c = $S[$i]
        if ($inStr) {
            if ($c -eq '\' -and ($i + 1) -lt $S.Length) { $i++; continue }
            if ($c -eq '"') { $inStr = $false }
            continue
        }
        if ($c -eq '"') { $inStr = $true; continue }
        if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++ }
        if ($c -eq ')' -or $c -eq ']' -or $c -eq '}') { $depth-- }
        if ($depth -eq 0 -and $S.Substring($i, $Sub.Length) -eq $Sub) {
            return $i
        }
    }
    return -1
}

function Resolve-JqStringInterpolation {
    param([string]$S, [object]$Data, [hashtable]$Variables)

    if ($null -eq $Variables) { $Variables = @{} }
    $result = [System.Text.StringBuilder]::new()
    $i = 0
    while ($i -lt $S.Length) {
        if ($S[$i] -eq '\' -and ($i + 1) -lt $S.Length) {
            $nc = $S[$i + 1]
            if ($nc -eq '(') {
                # Find matching )
                $depth = 1
                $start = $i + 2
                $j = $start
                while ($j -lt $S.Length -and $depth -gt 0) {
                    if ($S[$j] -eq '(') { $depth++ }
                    if ($S[$j] -eq ')') { $depth-- }
                    if ($depth -gt 0) { $j++ }
                }
                $expr = $S.Substring($start, $j - $start)
                $vals = @(Invoke-JqFilter -Data $Data -Filter $expr -Variables $Variables)
                $val = if ($vals.Count -gt 0) { $vals[0] } else { '' }
                $result.Append("$val") | Out-Null
                $i = $j + 1
                continue
            } elseif ($nc -eq 'n') {
                $result.Append("`n") | Out-Null
                $i += 2; continue
            } elseif ($nc -eq 't') {
                $result.Append("`t") | Out-Null
                $i += 2; continue
            } elseif ($nc -eq '\') {
                $result.Append('\') | Out-Null
                $i += 2; continue
            } elseif ($nc -eq '"') {
                $result.Append('"') | Out-Null
                $i += 2; continue
            }
        }
        $result.Append($S[$i]) | Out-Null
        $i++
    }
    return $result.ToString()
}

function Invoke-BashJq {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'jq' }

    $rawOutput = $false
    $compact = $false
    $sortKeys = $false
    $slurp = $false
    $filterExpr = '.'
    $filterSet = $false
    $files = [System.Collections.Generic.List[string]]::new()
    $pastDoubleDash = $false

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($pastDoubleDash) {
            $files.Add($arg)
            $i++
            continue
        }

        if ($arg -eq '--') {
            $pastDoubleDash = $true
            $i++
            continue
        }

        if ($arg -ceq '-r' -or $arg -ceq '--raw-output') {
            $rawOutput = $true
            $i++
            continue
        }
        if ($arg -ceq '-c' -or $arg -ceq '--compact-output') {
            $compact = $true
            $i++
            continue
        }
        if ($arg -ceq '-S' -or $arg -ceq '--sort-keys') {
            $sortKeys = $true
            $i++
            continue
        }
        if ($arg -ceq '-s' -or $arg -ceq '--slurp') {
            $slurp = $true
            $i++
            continue
        }

        # First non-flag argument is the filter, rest are files
        if (-not $filterSet) {
            $filterExpr = $arg
            $filterSet = $true
        } else {
            $files.Add($arg)
        }
        $i++
    }

    # Collect JSON input
    $jsonTexts = [System.Collections.Generic.List[string]]::new()

    if ($files.Count -gt 0) {
        foreach ($file in $files) {
            $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($file)
            if (-not (Test-Path -LiteralPath $resolved)) {
                Write-BashError -Message "jq: $file`: No such file or directory" -ExitCode 2
                return
            }
            $jsonTexts.Add([System.IO.File]::ReadAllText($resolved))
        }
    } else {
        # Pipeline input
        $textParts = [System.Text.StringBuilder]::new()
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $textParts.Append($text + "`n") | Out-Null
        }
        $combined = $textParts.ToString().Trim()
        if ($combined -ne '') {
            $jsonTexts.Add($combined)
        }
    }

    if ($jsonTexts.Count -eq 0) { return }

    # Parse and process
    $allData = [System.Collections.Generic.List[object]]::new()
    foreach ($jsonText in $jsonTexts) {
        try {
            $parsed = $jsonText | ConvertFrom-Json -AsHashtable -ErrorAction Stop
        } catch {
            Write-BashError -Message "jq: parse error: $($_.Exception.Message)" -ExitCode 5
            return
        }
        $allData.Add($parsed)
    }

    if ($slurp) {
        $dataToProcess = @(, [object[]]@($allData))
    } else {
        $dataToProcess = [System.Collections.Generic.List[object]]$allData
    }

    foreach ($data in $dataToProcess) {
        $results = @(Invoke-JqFilter -Data $data -Filter $filterExpr)
        foreach ($result in $results) {
            $text = ConvertTo-JqJson -Value $result -Compact $compact -SortKeys $sortKeys -RawOutput $rawOutput
            New-BashObject -BashText $text
        }
    }
}

# --- Date ---

function Invoke-BashDate {
    [OutputType('PsBash.DateOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'date' }

    $dateString = $null
    $format = $null
    $utc = $false
    $refFile = $null

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -ceq '-u' -or $arg -ceq '--utc' -or $arg -ceq '--universal') {
            $utc = $true
            $i++
            continue
        }

        if ($arg -ceq '-d' -or $arg -ceq '--date') {
            $i++
            if ($i -lt $Arguments.Count) { $dateString = $Arguments[$i] }
            $i++
            continue
        }

        if ($arg -cmatch '^--date=(.+)$') {
            $dateString = $Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-r' -or $arg -ceq '--reference') {
            $i++
            if ($i -lt $Arguments.Count) { $refFile = $Arguments[$i] }
            $i++
            continue
        }

        if ($arg -cmatch '^--reference=(.+)$') {
            $refFile = $Matches[1]
            $i++
            continue
        }

        if ($arg.StartsWith('+')) {
            $format = $arg.Substring(1)
            $i++
            continue
        }

        $i++
    }

    # Determine the source datetime
    [System.DateTimeOffset]$dto = if ($null -ne $refFile) {
        $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($refFile)
        if (-not (Test-Path -LiteralPath $resolved)) {
            Write-BashError -Message "date: '$refFile': No such file or directory"
            return
        }
        $mtime = (Get-Item -LiteralPath $resolved).LastWriteTime
        [System.DateTimeOffset]::new($mtime)
    } elseif ($null -ne $dateString) {
        try {
            [System.DateTimeOffset]::Parse($dateString, [System.Globalization.CultureInfo]::InvariantCulture)
        } catch {
            Write-BashError -Message "date: invalid date '$dateString'"
            return
        }
    } else {
        [System.DateTimeOffset]::Now
    }

    if ($utc) {
        $dto = $dto.ToUniversalTime()
    }

    # Build format output
    if ($null -ne $format) {
        $text = Convert-DateFormat -DTO $dto -Format $format
    } else {
        # Default: "Thu Jan  2 15:04:05 MST 2006" style
        $ci = [System.Globalization.CultureInfo]::InvariantCulture
        $dow = $dto.ToString('ddd', $ci)
        $mon = $dto.ToString('MMM', $ci)
        $day = $dto.Day.ToString().PadLeft(2)
        $time = $dto.ToString('HH:mm:ss')
        $tz = if ($utc) { 'UTC' } else { [System.TimeZoneInfo]::Local.Id }
        $yr = $dto.Year
        $text = "$dow $mon $day $time $tz $yr"
    }

    $epoch = [long]($dto.ToUnixTimeSeconds())
    $ci2 = [System.Globalization.CultureInfo]::InvariantCulture

    $obj = [PSCustomObject]@{
        PSTypeName = 'PsBash.DateOutput'
        Year       = [int]$dto.Year
        Month      = [int]$dto.Month
        Day        = [int]$dto.Day
        Hour       = [int]$dto.Hour
        Minute     = [int]$dto.Minute
        Second     = [int]$dto.Second
        Epoch      = $epoch
        DayOfWeek  = $dto.ToString('dddd', $ci2)
        TimeZone   = if ($utc) { 'UTC' } else { [System.TimeZoneInfo]::Local.Id }
        DateTime   = $dto
        BashText   = $text
    }
    Set-BashDisplayProperty $obj
}

function Convert-DateFormat {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [System.DateTimeOffset]$DTO,
        [Parameter(Mandatory)]
        [string]$Format
    )

    $ci = [System.Globalization.CultureInfo]::InvariantCulture
    $sb = [System.Text.StringBuilder]::new()
    $chars = $Format.ToCharArray()
    $i = 0
    while ($i -lt $chars.Length) {
        if ($chars[$i] -eq '%' -and ($i + 1) -lt $chars.Length) {
            $spec = $chars[$i + 1]
            switch -CaseSensitive ($spec) {
                'Y' { $sb.Append($DTO.ToString('yyyy', $ci)) | Out-Null }
                'y' { $sb.Append($DTO.ToString('yy', $ci))   | Out-Null }
                'm' { $sb.Append($DTO.ToString('MM', $ci))   | Out-Null }
                'd' { $sb.Append($DTO.ToString('dd', $ci))   | Out-Null }
                'H' { $sb.Append($DTO.ToString('HH', $ci))   | Out-Null }
                'M' { $sb.Append($DTO.ToString('mm', $ci))   | Out-Null }
                'S' { $sb.Append($DTO.ToString('ss', $ci))   | Out-Null }
                's' { $sb.Append([string]$DTO.ToUnixTimeSeconds()) | Out-Null }
                'F' { $sb.Append($DTO.ToString('yyyy-MM-dd', $ci)) | Out-Null }
                'T' { $sb.Append($DTO.ToString('HH:mm:ss', $ci)) | Out-Null }
                'w' { $sb.Append([int]$DTO.DayOfWeek) | Out-Null }
                'A' { $sb.Append($DTO.ToString('dddd', $ci)) | Out-Null }
                'B' { $sb.Append($DTO.ToString('MMMM', $ci)) | Out-Null }
                'Z' {
                    if ($DTO.Offset -eq [System.TimeSpan]::Zero) {
                        $sb.Append('UTC') | Out-Null
                    } else {
                        $sb.Append([System.TimeZoneInfo]::Local.Id) | Out-Null
                    }
                }
                'a' { $sb.Append($DTO.ToString('ddd', $ci))  | Out-Null }
                'b' { $sb.Append($DTO.ToString('MMM', $ci))  | Out-Null }
                'e' { $sb.Append($DTO.Day.ToString().PadLeft(2)) | Out-Null }
                'j' { $sb.Append($DTO.DayOfYear.ToString('000')) | Out-Null }
                'p' { $sb.Append($DTO.ToString('tt', $ci))   | Out-Null }
                'n' { $sb.Append("`n") | Out-Null }
                't' { $sb.Append("`t") | Out-Null }
                '%' { $sb.Append('%') | Out-Null }
                default { $sb.Append('%').Append($spec) | Out-Null }
            }
            $i += 2
        } else {
            $sb.Append($chars[$i]) | Out-Null
            $i++
        }
    }
    $sb.ToString()
}

# --- Seq ---

function Invoke-BashSeq {
    [OutputType('PsBash.SeqOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'seq' }

    $separator = $null
    $equalWidth = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -ceq '-s' -or $arg -ceq '--separator') {
            $i++
            if ($i -lt $Arguments.Count) { $separator = $Arguments[$i] }
            $i++
            continue
        }

        if ($arg -cmatch '^--separator=(.*)$') {
            $separator = $Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-w' -or $arg -ceq '--equal-width') {
            $equalWidth = $true
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Determine first, increment, last
    [double]$first = 1
    [double]$increment = 1
    [double]$last = 1

    if ($operands.Count -eq 1) {
        $last = [double]$operands[0]
    } elseif ($operands.Count -eq 2) {
        $first = [double]$operands[0]
        $last = [double]$operands[1]
    } elseif ($operands.Count -ge 3) {
        $first = [double]$operands[0]
        $increment = [double]$operands[1]
        $last = [double]$operands[2]
    }

    # Detect if inputs are integers
    $isInteger = ($first -eq [System.Math]::Floor($first)) -and
                 ($increment -eq [System.Math]::Floor($increment)) -and
                 ($last -eq [System.Math]::Floor($last))

    # Determine decimal places for formatting
    $decPlaces = 0
    if (-not $isInteger) {
        foreach ($op in $operands) {
            $dotPos = $op.IndexOf('.')
            if ($dotPos -ge 0) {
                $dp = $op.Length - $dotPos - 1
                if ($dp -gt $decPlaces) { $decPlaces = $dp }
            }
        }
    }

    # Determine width for -w padding
    $padWidth = 0
    if ($equalWidth -and $isInteger) {
        $maxVal = [System.Math]::Max([System.Math]::Abs($first), [System.Math]::Abs($last))
        $padWidth = [string][long]$maxVal
        $padWidth = $padWidth.Length
    }

    if ($increment -eq 0) {
        Write-BashError -Message 'seq: invalid zero increment' -ExitCode 1
        return
    }

    $index = 0
    $current = $first

    $ascending = $increment -gt 0

    # Output
    if ($null -ne $separator) {
        $sb = [System.Text.StringBuilder]::new()
        while (($ascending -and $current -le ($last + [double]1e-9)) -or
               (-not $ascending -and $current -ge ($last - [double]1e-9))) {
            if ($index -gt 0) { [void]$sb.Append($separator) }
            if ($isInteger) {
                $intVal = [long][System.Math]::Round($current)
                if ($equalWidth -and $padWidth -gt 0) {
                    [void]$sb.Append($intVal.ToString().PadLeft($padWidth, '0'))
                } else {
                    [void]$sb.Append([string]$intVal)
                }
            } else {
                [void]$sb.Append($current.ToString("F$decPlaces", [System.Globalization.CultureInfo]::InvariantCulture))
            }
            $index++
            $current = $first + ($increment * $index)
        }
        New-BashObject -BashText $sb.ToString()
    } else {
        while (($ascending -and $current -le ($last + [double]1e-9)) -or
               (-not $ascending -and $current -ge ($last - [double]1e-9))) {
            $numericValue = if ($isInteger) {
                $intVal = [long][System.Math]::Round($current)
                $formatted = if ($equalWidth -and $padWidth -gt 0) {
                    $intVal.ToString().PadLeft($padWidth, '0')
                } else {
                    [string]$intVal
                }
                $intVal
            } else {
                $formatted = $current.ToString("F$decPlaces", [System.Globalization.CultureInfo]::InvariantCulture)
                $current
            }
            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.SeqOutput'
                Value      = $numericValue
                Index      = $index
                BashText   = $formatted
            }
            Set-BashDisplayProperty $obj
            $index++
            $current = $first + ($increment * $index)
        }
    }
}

# --- Expr ---

function Invoke-BashExpr {
    [OutputType('PsBash.ExprOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'expr' }

    if ($Arguments.Count -eq 0) {
        Write-BashError -Message 'expr: missing operand' -ExitCode 2
        return
    }

    $result = $null

    # String operations (keyword first)
    $keyword = $Arguments[0]

    if ($keyword -ceq 'length' -and $Arguments.Count -ge 2) {
        $result = [string]($Arguments[1].Length)
    } elseif ($keyword -ceq 'substr' -and $Arguments.Count -ge 4) {
        $str = $Arguments[1]
        $pos = [int]$Arguments[2]
        $len = [int]$Arguments[3]
        $result = $str.Substring($pos - 1, [System.Math]::Min($len, $str.Length - $pos + 1))
    } elseif ($keyword -ceq 'index' -and $Arguments.Count -ge 3) {
        $str = $Arguments[1]
        $chars = $Arguments[2]
        $minPos = -1
        foreach ($ch in $chars.ToCharArray()) {
            $pos = $str.IndexOf($ch)
            if ($pos -ge 0 -and ($minPos -lt 0 -or $pos -lt $minPos)) {
                $minPos = $pos
            }
        }
        $val = if ($minPos -ge 0) { $minPos + 1 } else { 0 }
        $result = [string]$val
    } elseif ($keyword -ceq 'match' -and $Arguments.Count -ge 3) {
        $str = $Arguments[1]
        $pattern = $Arguments[2]
        # Convert POSIX BRE \(...\) to .NET (...)
        $netPattern = $pattern -replace '\\\(', '(' -replace '\\\)', ')'
        # Anchor at start like expr does
        if (-not $netPattern.StartsWith('^')) { $netPattern = "^$netPattern" }
        if ($str -match $netPattern) {
            if ($Matches.Count -gt 1) {
                $result = $Matches[1]
            } else {
                $result = [string]$Matches[0].Length
            }
        } else {
            $result = '0'
        }
    } elseif ($Arguments.Count -ge 3) {
        # Infix: operand1 operator operand2
        $left = $Arguments[0]
        $op = $Arguments[1]
        $right = $Arguments[2]

        $isNumericLeft = $left -match '^-?\d+$'
        $isNumericRight = $right -match '^-?\d+$'

        if ($isNumericLeft -and $isNumericRight) {
            $l = [long]$left
            $r = [long]$right

            $result = switch ($op) {
                '+'  { [string]($l + $r) }
                '-'  { [string]($l - $r) }
                '*'  { [string]($l * $r) }
                '/'  {
                    if ($r -eq 0) { Write-BashError -Message 'expr: division by zero' -ExitCode 2; return }
                    [string]([long][System.Math]::Truncate($l / $r))
                }
                '%'  {
                    if ($r -eq 0) { Write-BashError -Message 'expr: division by zero' -ExitCode 2; return }
                    [string]($l % $r)
                }
                '<'  { if ($l -lt $r) { '1' } else { '0' } }
                '<=' { if ($l -le $r) { '1' } else { '0' } }
                '='  { if ($l -eq $r) { '1' } else { '0' } }
                '!=' { if ($l -ne $r) { '1' } else { '0' } }
                '>=' { if ($l -ge $r) { '1' } else { '0' } }
                '>'  { if ($l -gt $r) { '1' } else { '0' } }
                default {
                    Write-BashError -Message "expr: unknown operator '$op'" -ExitCode 2
                    return
                }
            }
        } else {
            # String comparison
            $result = switch ($op) {
                '<'  { if ($left -lt $right) { '1' } else { '0' } }
                '<=' { if ($left -le $right) { '1' } else { '0' } }
                '='  { if ($left -ceq $right) { '1' } else { '0' } }
                '!=' { if ($left -cne $right) { '1' } else { '0' } }
                '>=' { if ($left -ge $right) { '1' } else { '0' } }
                '>'  { if ($left -gt $right) { '1' } else { '0' } }
                default {
                    Write-BashError -Message "expr: non-integer argument" -ExitCode 2
                    return
                }
            }
        }
    } else {
        # Single operand: echo it
        $result = $Arguments[0]
    }

    # Determine Value type
    $numericResult = $result -match '^-?\d+$'
    $value = if ($numericResult) { [long]$result } else { $result }

    $obj = [PSCustomObject]@{
        PSTypeName = 'PsBash.ExprOutput'
        Value      = $value
        BashText   = $result
    }
    Set-BashDisplayProperty $obj
}

