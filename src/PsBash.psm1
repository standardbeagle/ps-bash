#Requires -Version 7.0

Set-StrictMode -Version Latest

# --- Platform Detection ---

function Get-BashPlatform {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    if ($IsWindows) { return 'Windows' }
    if ($IsLinux)   { return 'Linux' }
    if ($IsMacOS)   { return 'macOS' }
    return 'Unknown'
}

# --- BashObject Factory ---

function New-BashObject {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$BashText,

        [Parameter()]
        [string]$TypeName = 'PsBash.TextOutput'
    )

    $obj = [PSCustomObject]@{
        PSTypeName = $TypeName
        BashText   = $BashText
    }
    $obj | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
        $this.BashText
    } -Force
    $obj
}

# --- Arg Parser ---

function ConvertFrom-BashArgs {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$FlagDefs
    )

    $flags = [System.Collections.Generic.Dictionary[string,bool]]::new(
        [System.StringComparer]::Ordinal
    )
    $operands = [System.Collections.Generic.List[string]]::new()

    foreach ($key in $FlagDefs.Keys) {
        $flags[$key] = $false
    }

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -eq '--') {
            $i++
            while ($i -lt $Arguments.Count) {
                $operands.Add($Arguments[$i])
                $i++
            }
            break
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                $flag = "-$ch"
                if ($flags.ContainsKey($flag)) {
                    $flags[$flag] = $true
                } else {
                    $operands.Add($arg)
                    break
                }
            }
        } else {
            $operands.Add($arg)
        }
        $i++
    }

    @{
        Flags    = $flags
        Operands = $operands
    }
}

# --- Escape Sequence Processing ---

function Expand-EscapeSequences {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Text
    )

    $Text = $Text -replace '\\\\', "`0ESCAPED_BACKSLASH`0"
    $Text = $Text -replace '\\n', "`n"
    $Text = $Text -replace '\\t', "`t"
    $Text = $Text -replace '\\r', "`r"
    $Text = $Text -replace '\\a', "`a"
    $Text = $Text -replace '\\b', "`b"
    $Text = $Text -replace '\\f', "`f"
    $Text = $Text -replace '\\v', "`v"
    $Text = $Text -replace "`0ESCAPED_BACKSLASH`0", '\'
    $Text
}

# --- Case-sensitive flag dictionary helper ---

function New-FlagDefs {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.Dictionary[string,string]])]
    param(
        [Parameter(Mandatory)]
        [string[]]$Entries
    )

    $dict = [System.Collections.Generic.Dictionary[string,string]]::new(
        [System.StringComparer]::Ordinal
    )
    for ($i = 0; $i -lt $Entries.Count; $i += 2) {
        $dict[$Entries[$i]] = $Entries[$i + 1]
    }
    $dict
}

# --- echo Command ---

function Invoke-BashEcho {
    $Arguments = [string[]]$args

    $defs = New-FlagDefs -Entries @(
        '-n', 'no trailing newline'
        '-e', 'enable escape sequences'
        '-E', 'disable escape sequences'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $text = $parsed.Operands -join ' '

    if ($parsed.Flags['-e']) {
        $text = Expand-EscapeSequences -Text $text
    }

    if (-not $parsed.Flags['-n']) {
        $text = $text + "`n"
    }

    New-BashObject -BashText $text
}

# --- printf Command ---

function Invoke-BashPrintf {
    $Arguments = [string[]]$args

    if (-not $Arguments -or $Arguments.Count -eq 0) {
        throw 'printf: usage: printf format [arguments]'
    }

    $format = $Arguments[0]
    $args_list = if ($Arguments.Count -gt 1) { $Arguments[1..($Arguments.Count - 1)] } else { @() }

    $converted = [System.Collections.Generic.List[object]]::new()
    foreach ($a in $args_list) {
        $intVal = 0
        $doubleVal = 0.0
        if ([int]::TryParse($a, [ref]$intVal)) {
            $converted.Add($intVal)
        } elseif ([double]::TryParse($a, [ref]$doubleVal)) {
            $converted.Add($doubleVal)
        } else {
            $converted.Add($a)
        }
    }

    $format = $format -replace '%%', "`0ESCAPED_PERCENT`0"
    $format = Expand-EscapeSequences -Text $format

    $sb = [System.Text.StringBuilder]::new()
    $argIdx = 0
    $i = 0
    while ($i -lt $format.Length) {
        if ($format[$i] -eq '%' -and ($i + 1) -lt $format.Length) {
            $spec = $format[$i + 1]
            switch ($spec) {
                's' {
                    if ($argIdx -lt $converted.Count) { [void]$sb.Append($converted[$argIdx]) }
                    $argIdx++
                    $i += 2
                }
                'd' {
                    if ($argIdx -lt $converted.Count) { [void]$sb.Append([int]$converted[$argIdx]) }
                    $argIdx++
                    $i += 2
                }
                'f' {
                    if ($argIdx -lt $converted.Count) { [void]$sb.Append([string]::Format('{0:F6}', [double]$converted[$argIdx])) }
                    $argIdx++
                    $i += 2
                }
                default {
                    [void]$sb.Append($format[$i])
                    $i++
                }
            }
        } else {
            [void]$sb.Append($format[$i])
            $i++
        }
    }

    $result = $sb.ToString() -replace "`0ESCAPED_PERCENT`0", '%'

    New-BashObject -BashText $result
}

# --- Human-readable Size Formatter ---

function Format-BashSize {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [long]$Bytes
    )

    if ($Bytes -lt 1024) {
        return "$Bytes"
    }

    $units = @('K', 'M', 'G', 'T', 'P')
    $value = [double]$Bytes
    $unitIdx = -1

    while ($value -ge 1024 -and $unitIdx -lt ($units.Count - 1)) {
        $value /= 1024
        $unitIdx++
    }

    if ($value -ge 10) {
        $rounded = [System.Math]::Ceiling($value)
        return "{0}{1}" -f $rounded, $units[$unitIdx]
    }
    $rounded = [System.Math]::Ceiling($value * 10) / 10
    return "{0:F1}{1}" -f $rounded, $units[$unitIdx]
}

# --- Bash Date Formatter ---

function Format-BashDate {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [datetime]$Date
    )

    $now = [datetime]::Now
    $sixMonthsAgo = $now.AddMonths(-6)

    $month = $Date.ToString('MMM', [System.Globalization.CultureInfo]::InvariantCulture)
    $day = $Date.Day.ToString().PadLeft(2)

    if ($Date -lt $sixMonthsAgo -or $Date -gt $now) {
        return "$month $day  $($Date.Year)"
    }
    $time = $Date.ToString('HH:mm')
    return "$month $day $time"
}

# --- Unix File Mode to Permission String ---

function ConvertTo-PermissionString {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [int]$Mode
    )

    $sb = [System.Text.StringBuilder]::new(9)
    $bits = @(
        @(256, 'r'), @(128, 'w'), @(64, 'x'),
        @(32, 'r'),  @(16, 'w'),  @(8, 'x'),
        @(4, 'r'),   @(2, 'w'),   @(1, 'x')
    )
    foreach ($pair in $bits) {
        if ($Mode -band $pair[0]) {
            [void]$sb.Append($pair[1])
        } else {
            [void]$sb.Append('-')
        }
    }
    $sb.ToString()
}

# --- Platform File Info Adapter ---

function Get-BashFileInfo {
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [System.IO.FileSystemInfo]$Item
    )

    $isDir = $Item -is [System.IO.DirectoryInfo]
    $size = if ($isDir) { 4096 } else { $Item.Length }
    $linkCount = 1
    $owner = ''
    $group = ''
    $permString = 'rwxr-xr-x'

    if (-not $IsWindows) {
        $mode = [int]$Item.UnixFileMode
        $permString = ConvertTo-PermissionString -Mode $mode
        $statArgs = if ($IsMacOS) { @('-f', '%l %Su %Sg', $Item.FullName) } else { @('-c', '%h %U %G', $Item.FullName) }
        $statOutput = & /usr/bin/stat @statArgs 2>$null
        if ($statOutput) {
            $parts = $statOutput -split ' ', 3
            $linkCount = [int]$parts[0]
            $owner = $parts[1]
            $group = $parts[2]
        }
        if ($isDir) {
            $size = 4096
        }
    } else {
        try {
            $acl = Get-Acl -Path $Item.FullName
            $owner = ($acl.Owner -split '\\')[-1]
            $group = ($acl.Group -split '\\')[-1]
            $userRead = $false; $userWrite = $false; $userExec = $false
            foreach ($rule in $acl.Access) {
                $rights = $rule.FileSystemRights
                if ($rights -band [System.Security.AccessControl.FileSystemRights]::Read) { $userRead = $true }
                if ($rights -band [System.Security.AccessControl.FileSystemRights]::Write) { $userWrite = $true }
                if ($rights -band [System.Security.AccessControl.FileSystemRights]::ExecuteFile) { $userExec = $true }
            }
            $u = "$(if ($userRead) {'r'} else {'-'})$(if ($userWrite) {'w'} else {'-'})$(if ($userExec) {'x'} else {'-'})"
            $permString = "$u$u$u"
        } catch {
            $permString = if ($isDir) { 'rwxr-xr-x' } else { 'rw-r--r--' }
            $owner = $env:USERNAME
            $group = $env:USERNAME
        }
    }

    $typeChar = if ($isDir) { 'd' } elseif ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { 'l' } else { '-' }

    [PSCustomObject]@{
        PSTypeName   = 'PsBash.LsEntry'
        Name         = $Item.Name
        FullPath     = $Item.FullName
        IsDirectory  = $isDir
        SizeBytes    = $size
        Permissions  = "$typeChar$permString"
        LinkCount    = $linkCount
        Owner        = $owner
        Group        = $group
        LastModified = $Item.LastWriteTime
        BashText     = ''
    }
}

# --- Format ls -l line ---

function Format-LsLine {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Entry,

        [Parameter()]
        [switch]$HumanReadable
    )

    $size = if ($HumanReadable) {
        (Format-BashSize -Bytes $Entry.SizeBytes).PadLeft(4)
    } else {
        $Entry.SizeBytes.ToString().PadLeft(8)
    }

    $date = Format-BashDate -Date $Entry.LastModified

    "{0} {1} {2} {3} {4} {5} {6}" -f `
        $Entry.Permissions,
        $Entry.LinkCount,
        $Entry.Owner,
        $Entry.Group,
        $size,
        $date,
        $Entry.Name
}

# --- ls Command ---

function Invoke-BashLs {
    $Arguments = [string[]]$args

    $defs = New-FlagDefs -Entries @(
        '-l', 'long listing'
        '-a', 'show hidden'
        '-h', 'human readable sizes'
        '-R', 'recursive'
        '-S', 'sort by size'
        '-t', 'sort by time'
        '-r', 'reverse sort'
        '-1', 'one per line'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $longMode = $parsed.Flags['-l']
    $showHidden = $parsed.Flags['-a']
    $humanSizes = $parsed.Flags['-h']
    $recursive = $parsed.Flags['-R']
    $sortBySize = $parsed.Flags['-S']
    $sortByTime = $parsed.Flags['-t']
    $reverseSort = $parsed.Flags['-r']

    $targets = if ($parsed.Operands.Count -gt 0) { $parsed.Operands } else { @('.') }

    $allEntries = [System.Collections.Generic.List[PSCustomObject]]::new()
    $hadError = $false

    foreach ($target in $targets) {
        if (-not (Test-Path -LiteralPath $target)) {
            $msg = "ls: cannot access '$target': No such file or directory"
            Write-Error -Message $msg -ErrorAction Continue
            $hadError = $true
            continue
        }

        $item = Get-Item -LiteralPath $target -Force
        if ($item -is [System.IO.DirectoryInfo]) {
            $children = if ($recursive) {
                Get-ChildItem -LiteralPath $target -Force -Recurse
            } else {
                Get-ChildItem -LiteralPath $target -Force
            }
            foreach ($child in $children) {
                if (-not $showHidden -and $child.Name.StartsWith('.')) { continue }
                $entry = Get-BashFileInfo -Item $child
                $allEntries.Add($entry)
            }
        } else {
            $entry = Get-BashFileInfo -Item $item
            $allEntries.Add($entry)
        }
    }

    if ($sortBySize) {
        $sorted = $allEntries | Sort-Object -Property SizeBytes -Descending
        $allEntries = [System.Collections.Generic.List[PSCustomObject]]::new()
        foreach ($e in $sorted) { $allEntries.Add($e) }
    } elseif ($sortByTime) {
        $sorted = $allEntries | Sort-Object -Property LastModified -Descending
        $allEntries = [System.Collections.Generic.List[PSCustomObject]]::new()
        foreach ($e in $sorted) { $allEntries.Add($e) }
    }

    if ($reverseSort) {
        $reversed = [System.Collections.Generic.List[PSCustomObject]]::new()
        for ($i = $allEntries.Count - 1; $i -ge 0; $i--) {
            $reversed.Add($allEntries[$i])
        }
        $allEntries = $reversed
    }

    foreach ($entry in $allEntries) {
        if ($longMode) {
            $line = Format-LsLine -Entry $entry -HumanReadable:$humanSizes
            $entry.BashText = $line
        } else {
            $entry.BashText = $entry.Name
        }
        $entry | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
            $this.BashText
        } -Force
        $entry
    }

    if ($hadError -and $allEntries.Count -eq 0) {
        $global:LASTEXITCODE = 2
    }
}

# --- cat Command ---

function Invoke-BashCat {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $defs = New-FlagDefs -Entries @(
        '-n', 'number all lines'
        '-b', 'number non-blank lines'
        '-s', 'squeeze blank lines'
        '-E', 'show $ at line end'
        '-T', 'show ^I for tabs'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $numberAll     = $parsed.Flags['-n']
    $numberNonBlank = $parsed.Flags['-b']
    $squeezeBlanks = $parsed.Flags['-s']
    $showEnds      = $parsed.Flags['-E']
    $showTabs      = $parsed.Flags['-T']

    $operands = $parsed.Operands
    $readStdin = $operands.Count -eq 0 -or $operands -contains '-'

    $lineNum = 0
    $nonBlankNum = 0
    $lastWasBlank = $false
    $hadError = $false

    $emitLine = {
        param([string]$Content, [string]$FileName)

        $isBlank = $Content -eq ''

        if ($squeezeBlanks -and $isBlank -and $lastWasBlank) {
            return
        }
        Set-Variable -Name lastWasBlank -Value $isBlank -Scope 1

        $ln = (Get-Variable -Name lineNum -Scope 1).Value + 1
        Set-Variable -Name lineNum -Value $ln -Scope 1

        if (-not $isBlank) {
            $nb = (Get-Variable -Name nonBlankNum -Scope 1).Value + 1
            Set-Variable -Name nonBlankNum -Value $nb -Scope 1
        }

        $text = $Content

        if ($showTabs) {
            $text = $text -replace "`t", '^I'
        }

        if ($showEnds) {
            $text = $text + '$'
        }

        if ($numberNonBlank) {
            if (-not $isBlank) {
                $nbCurrent = (Get-Variable -Name nonBlankNum -Scope 1).Value
                $text = "{0}`t{1}" -f $nbCurrent.ToString().PadLeft(6), $text
            }
        } elseif ($numberAll) {
            $text = "{0}`t{1}" -f $ln.ToString().PadLeft(6), $text
        }

        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.CatLine'
            LineNumber = $ln
            Content    = $Content
            FileName   = $FileName
            BashText   = $text
        }
        $obj | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
            $this.BashText
        } -Force
        $obj
    }

    if ($readStdin -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $content = if ($null -ne $item.BashText) { $item.BashText } else { "$item" }
            & $emitLine $content ''
        }
    }

    $fileOperands = @($operands | Where-Object { $_ -ne '-' })
    foreach ($filePath in $fileOperands) {
        if (-not (Test-Path -LiteralPath $filePath)) {
            $normalized = $filePath -replace '\\', '/'
            $msg = "cat: ${normalized}: No such file or directory"
            Write-Error -Message $msg -ErrorAction Continue
            $hadError = $true
            continue
        }

        $bytes = [System.IO.File]::ReadAllBytes($filePath)

        $byteOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $byteOffset = 3
        }

        $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
        $rawText = $rawText -replace "`r`n", "`n"

        if ($rawText.EndsWith("`n")) {
            $rawText = $rawText.Substring(0, $rawText.Length - 1)
        }

        $lines = $rawText.Split("`n")
        foreach ($line in $lines) {
            & $emitLine $line $filePath
        }
    }

    if ($hadError) {
        $global:LASTEXITCODE = 1
    }
}

# --- BashText Extraction Helper ---

function Get-BashText {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        $InputObject
    )

    if ($null -eq $InputObject) { return '' }
    if ($null -ne $InputObject.PSObject -and $null -ne $InputObject.PSObject.Properties['BashText']) {
        return [string]$InputObject.BashText
    }
    return "$InputObject"
}

# --- grep Command ---

function Invoke-BashGrep {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    # Parse arguments manually because grep has value-bearing flags (-A, -B, -C, -m)
    $ignoreCase = $false
    $invertMatch = $false
    $showLineNumbers = $false
    $countOnly = $false
    $recursive = $false
    $filesOnly = $false
    $extendedRegex = $false
    $afterContext = 0
    $beforeContext = 0
    $pattern = $null
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

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'i' { $ignoreCase = $true }
                    'v' { $invertMatch = $true }
                    'n' { $showLineNumbers = $true }
                    'c' { $countOnly = $true }
                    'r' { $recursive = $true }
                    'l' { $filesOnly = $true }
                    'E' { $extendedRegex = $true }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -eq 0) {
        throw 'grep: usage: grep [options] pattern [file ...]'
    }

    $pattern = $operands[0]
    $fileOperands = @(if ($operands.Count -gt 1) { $operands.GetRange(1, $operands.Count - 1) } else { @() })

    # Build regex options
    $regexOpts = [System.Text.RegularExpressions.RegexOptions]::None
    if ($ignoreCase) { $regexOpts = $regexOpts -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase }

    # For basic regex (non -E), escape special chars except basic ones that grep always supports
    $regexPattern = if (-not $extendedRegex) {
        # Basic grep: . * ^ $ [ ] are special; escape (){}|+?
        $pattern -replace '(?<!\\)\(', '\(' -replace '(?<!\\)\)', '\)' -replace '(?<!\\)\{', '\{' -replace '(?<!\\)\}', '\}' -replace '(?<!\\)\|', '\|' -replace '(?<!\\)\+', '\+' -replace '(?<!\\)\?', '\?'
    } else {
        $pattern
    }

    $regex = [regex]::new($regexPattern, $regexOpts)

    # --- Pipeline mode ---
    if ($fileOperands.Count -eq 0 -and -not $recursive) {
        $matchCount = 0

        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            # Strip trailing newline for matching (echo adds \n)
            $matchText = $text -replace "`n$", ''
            $isMatch = $regex.IsMatch($matchText)
            if ($invertMatch) { $isMatch = -not $isMatch }

            if ($isMatch) {
                $matchCount++
                if (-not $countOnly) {
                    # Pipeline bridge: pass through the original object
                    $item
                }
            }
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
                Write-Error -Message "grep: ${fp}: No such file or directory" -ErrorAction Continue
                continue
            }
            $filePaths.Add((Resolve-Path -LiteralPath $fp).Path)
        }
    }

    $multipleFiles = $filePaths.Count -gt 1 -or $recursive
    $matchedFiles = [System.Collections.Generic.List[string]]::new()
    $perFileCounts = [System.Collections.Generic.Dictionary[string,int]]::new()
    $totalMatchCount = 0

    foreach ($filePath in $filePaths) {
        $bytes = [System.IO.File]::ReadAllBytes($filePath)
        $byteOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $byteOffset = 3
        }
        $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
        $rawText = $rawText -replace "`r`n", "`n"
        if ($rawText.EndsWith("`n")) {
            $rawText = $rawText.Substring(0, $rawText.Length - 1)
        }
        $lines = $rawText.Split("`n")

        $matchIndices = [System.Collections.Generic.List[int]]::new()
        for ($li = 0; $li -lt $lines.Count; $li++) {
            $isMatch = $regex.IsMatch($lines[$li])
            if ($invertMatch) { $isMatch = -not $isMatch }
            if ($isMatch) { $matchIndices.Add($li) }
        }

        $fileMatchCount = $matchIndices.Count
        $totalMatchCount += $fileMatchCount
        $perFileCounts[$filePath] = $fileMatchCount

        if ($filesOnly) {
            if ($fileMatchCount -gt 0) { $matchedFiles.Add($filePath) }
            continue
        }

        if ($countOnly) { continue }

        # Determine which lines to emit (matches + context)
        $emitLines = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($mi in $matchIndices) {
            $start = [System.Math]::Max(0, $mi - $beforeContext)
            $end = [System.Math]::Min($lines.Count - 1, $mi + $afterContext)
            for ($li = $start; $li -le $end; $li++) {
                [void]$emitLines.Add($li)
            }
        }

        $sortedEmit = $emitLines | Sort-Object
        foreach ($li in $sortedEmit) {
            $line = $lines[$li]
            $lineNum = $li + 1
            $prefix = ''
            if ($multipleFiles) { $prefix = "${filePath}:" }

            $bashText = $line
            if ($showLineNumbers) {
                $bashText = "${prefix}${lineNum}:${line}"
            } elseif ($multipleFiles) {
                $bashText = "${prefix}${line}"
            }

            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.GrepMatch'
                FileName   = $filePath
                LineNumber = $lineNum
                Line       = $line
                BashText   = $bashText
            }
            $obj | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
                $this.BashText
            } -Force
            $obj
        }
    }

    if ($filesOnly) {
        foreach ($fp in $matchedFiles) {
            New-BashObject -BashText $fp
        }
        return
    }

    if ($countOnly) {
        if ($multipleFiles) {
            foreach ($filePath in $filePaths) {
                New-BashObject -BashText "${filePath}:$($perFileCounts[$filePath])"
            }
        } else {
            New-BashObject -BashText "$totalMatchCount"
        }
    }
}

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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    # Manual arg parsing for value-bearing flags (-k, -t)
    $reverse = $false
    $numeric = $false
    $unique = $false
    $foldCase = $false
    $humanNumeric = $false
    $versionSort = $false
    $monthSort = $false
    $checkOnly = $false
    $keyField = $null
    $delimiter = $null
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

        # -t with joined value (e.g. -t:)
        if ($arg -cmatch '^-t(.+)$') {
            $delimiter = $Matches[1]
            $i++
            continue
        }

        # -k with joined value (e.g. -k2)
        if ($arg -cmatch '^-k(\d+.*)$') {
            $keyField = [int]($Matches[1] -replace '[^0-9].*', '')
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
                $keyField = [int]($Arguments[$i] -replace '[^0-9].*', '')
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
            $text = $text -replace "`n$", ''
            if ($text.Contains("`n")) {
                foreach ($line in $text.Split("`n")) {
                    $items.Add((New-BashObject -BashText $line))
                }
            } else {
                $items.Add($item)
            }
        }
    }

    foreach ($filePath in $operands) {
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-Error -Message "sort: cannot read: ${filePath}: No such file or directory" -ErrorAction Continue
            continue
        }
        $bytes = [System.IO.File]::ReadAllBytes($filePath)
        $byteOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $byteOffset = 3
        }
        $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
        $rawText = $rawText -replace "`r`n", "`n"
        if ($rawText.EndsWith("`n")) {
            $rawText = $rawText.Substring(0, $rawText.Length - 1)
        }
        foreach ($line in $rawText.Split("`n")) {
            $items.Add((New-BashObject -BashText $line))
        }
    }

    # Extract sort key from an item
    $getSortText = {
        param($item)
        $text = Get-BashText -InputObject $item
        $text = $text -replace "`n$", ''
        if ($null -ne $keyField) {
            $sep = if ($null -ne $delimiter) { [regex]::Escape($delimiter) } else { '\s+' }
            $parts = $text -split $sep
            $idx = $keyField - 1
            if ($idx -ge 0 -and $idx -lt $parts.Count) {
                return $parts[$idx]
            }
            return ''
        }
        return $text
    }

    # Smart path: -h with LsEntry objects uses SizeBytes directly
    $useSizeBytesPath = $humanNumeric -and $items.Count -gt 0 -and
        $null -ne $items[0].PSObject -and
        $null -ne $items[0].PSObject.Properties['SizeBytes']

    # Check-only mode
    if ($checkOnly) {
        for ($idx = 1; $idx -lt $items.Count; $idx++) {
            $prevText = & $getSortText $items[$idx - 1]
            $currText = & $getSortText $items[$idx]
            $cmp = [string]::Compare($prevText, $currText, [System.StringComparison]::Ordinal)
            if ($cmp -gt 0) {
                $global:LASTEXITCODE = 1
                return
            }
        }
        $global:LASTEXITCODE = 0
        return
    }

    # Build sort key for each item
    $indexed = [System.Collections.Generic.List[object]]::new()
    for ($idx = 0; $idx -lt $items.Count; $idx++) {
        $indexed.Add(@{
            Index = $idx
            Item  = $items[$idx]
        })
    }

    # Sort using a comparison
    $sorted = $indexed | Sort-Object -Property @{
        Expression = {
            $item = $_.Item

            if ($useSizeBytesPath) {
                return [double]$item.SizeBytes
            }

            $text = & $getSortText $item

            if ($humanNumeric) {
                return ConvertFrom-HumanNumeric -Value $text
            }
            if ($numeric) {
                $n = 0.0
                if ([double]::TryParse($text, [ref]$n)) { return $n }
                return 0.0
            }
            if ($monthSort) {
                return ConvertFrom-MonthName -Value $text
            }
            if ($foldCase) {
                return $text.ToLower()
            }
            return $text
        }
    } -Stable

    if ($versionSort) {
        $list = [System.Collections.Generic.List[object]]::new(@($sorted))
        for ($i2 = 1; $i2 -lt $list.Count; $i2++) {
            $current = $list[$i2]
            $currentText = (& $getSortText $current.Item) -replace "`n$", ''
            $j = $i2 - 1
            while ($j -ge 0) {
                $otherText = (& $getSortText $list[$j].Item) -replace "`n$", ''
                if ((Compare-Version -Left $otherText -Right $currentText) -le 0) { break }
                $list[$j + 1] = $list[$j]
                $j--
            }
            $list[$j + 1] = $current
        }
        $sorted = $list
    }

    if ($reverse) {
        $reversed = [System.Collections.Generic.List[object]]::new()
        $asList = @($sorted)
        for ($idx = $asList.Count - 1; $idx -ge 0; $idx--) {
            $reversed.Add($asList[$idx])
        }
        $sorted = $reversed
    }

    # Unique: deduplicate by sort text
    if ($unique) {
        $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $deduped = [System.Collections.Generic.List[object]]::new()
        foreach ($entry in $sorted) {
            $text = & $getSortText $entry.Item
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

# --- head Command ---

function Invoke-BashHead {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    # Manual arg parsing for value-bearing -n flag
    $count = 10
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

        $operands.Add($arg)
        $i++
    }

    # Pipeline mode
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        $emitted = 0
        foreach ($item in $pipelineInput) {
            if ($emitted -ge $count) { break }
            $item
            $emitted++
        }
        return
    }

    # File mode
    foreach ($filePath in $operands) {
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-Error -Message "head: cannot open '$filePath' for reading: No such file or directory" -ErrorAction Continue
            continue
        }

        $bytes = [System.IO.File]::ReadAllBytes($filePath)
        $byteOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $byteOffset = 3
        }
        $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
        $rawText = $rawText -replace "`r`n", "`n"
        if ($rawText.EndsWith("`n")) {
            $rawText = $rawText.Substring(0, $rawText.Length - 1)
        }

        $lines = $rawText.Split("`n")
        $lineCount = [System.Math]::Min($count, $lines.Count)
        for ($li = 0; $li -lt $lineCount; $li++) {
            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.CatLine'
                LineNumber = $li + 1
                Content    = $lines[$li]
                FileName   = $filePath
                BashText   = $lines[$li]
            }
            $obj | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
                $this.BashText
            } -Force
            $obj
        }
    }
}

# --- tail Command ---

function Invoke-BashTail {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    # Manual arg parsing for value-bearing -n flag
    $count = 10
    $fromLine = $false
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

        $operands.Add($arg)
        $i++
    }

    # Pipeline mode
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        if ($fromLine) {
            $startIdx = $count - 1
            for ($idx = $startIdx; $idx -lt $pipelineInput.Count; $idx++) {
                $pipelineInput[$idx]
            }
        } else {
            $startIdx = [System.Math]::Max(0, $pipelineInput.Count - $count)
            for ($idx = $startIdx; $idx -lt $pipelineInput.Count; $idx++) {
                $pipelineInput[$idx]
            }
        }
        return
    }

    # File mode
    foreach ($filePath in $operands) {
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-Error -Message "tail: cannot open '$filePath' for reading: No such file or directory" -ErrorAction Continue
            continue
        }

        $bytes = [System.IO.File]::ReadAllBytes($filePath)
        $byteOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $byteOffset = 3
        }
        $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
        $rawText = $rawText -replace "`r`n", "`n"
        if ($rawText.EndsWith("`n")) {
            $rawText = $rawText.Substring(0, $rawText.Length - 1)
        }

        $lines = $rawText.Split("`n")

        if ($fromLine) {
            $startIdx = $count - 1
        } else {
            $startIdx = [System.Math]::Max(0, $lines.Count - $count)
        }

        for ($li = $startIdx; $li -lt $lines.Count; $li++) {
            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.CatLine'
                LineNumber = $li + 1
                Content    = $lines[$li]
                FileName   = $filePath
                BashText   = $lines[$li]
            }
            $obj | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
                $this.BashText
            } -Force
            $obj
        }
    }
}

# --- wc Command ---

function Invoke-BashWc {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

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
        param([int]$Lines, [int]$Words, [int]$Bytes, [string]$FileName)

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
        $obj | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
            $this.BashText
        } -Force
        $obj
    }

    # Pipeline mode
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        $totalLines = 0
        $totalWords = 0
        $totalBytes = 0

        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $text = $text -replace "`n$", ''
            $totalLines++
            $lineWords = @($text -split '\s+' | Where-Object { $_ -ne '' }).Count
            $totalWords += $lineWords
            $totalBytes += [System.Text.Encoding]::UTF8.GetByteCount($text) + 1
        }

        & $emitResult $totalLines $totalWords $totalBytes ''
        return
    }

    # File mode
    $totalLines = 0
    $totalWords = 0
    $totalBytes = 0
    $multipleFiles = $operands.Count -gt 1

    foreach ($filePath in $operands) {
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-Error -Message "wc: ${filePath}: No such file or directory" -ErrorAction Continue
            continue
        }

        $bytes = [System.IO.File]::ReadAllBytes($filePath)
        $byteOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $byteOffset = 3
        }
        $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
        $rawText = $rawText -replace "`r`n", "`n"

        $fileBytes = $bytes.Length - $byteOffset
        $lineCount = @($rawText.ToCharArray() | Where-Object { $_ -eq "`n" }).Count
        $wordCount = @($rawText -split '\s+' | Where-Object { $_ -ne '' }).Count

        $totalLines += $lineCount
        $totalWords += $wordCount
        $totalBytes += $fileBytes

        & $emitResult $lineCount $wordCount $fileBytes $filePath
    }

    if ($multipleFiles) {
        & $emitResult $totalLines $totalWords $totalBytes 'total'
    }
}

# --- find Command ---

function Invoke-BashFind {
    $Arguments = [string[]]$args

    # Manual arg parsing for find's predicate-style flags
    $searchPath = '.'
    $namePattern = $null
    $typeFilter = $null
    $maxDepth = [int]::MaxValue
    $sizeExpr = $null
    $mtimeExpr = $null
    $findEmpty = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        switch ($arg) {
            '-name' {
                $i++
                if ($i -lt $Arguments.Count) { $namePattern = $Arguments[$i] }
                $i++
                continue
            }
            '-type' {
                $i++
                if ($i -lt $Arguments.Count) { $typeFilter = $Arguments[$i] }
                $i++
                continue
            }
            '-maxdepth' {
                $i++
                if ($i -lt $Arguments.Count) { $maxDepth = [int]$Arguments[$i] }
                $i++
                continue
            }
            '-size' {
                $i++
                if ($i -lt $Arguments.Count) { $sizeExpr = $Arguments[$i] }
                $i++
                continue
            }
            '-mtime' {
                $i++
                if ($i -lt $Arguments.Count) { $mtimeExpr = $Arguments[$i] }
                $i++
                continue
            }
            '-empty' {
                $findEmpty = $true
                $i++
                continue
            }
            default {
                $operands.Add($arg)
                $i++
            }
        }
    }

    if ($operands.Count -gt 0) {
        $searchPath = $operands[0]
    }

    if (-not (Test-Path -LiteralPath $searchPath)) {
        Write-Error -Message "find: '$searchPath': No such file or directory" -ErrorAction Continue
        return
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $searchPath).Path
    $rootDepth = ($resolvedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) -split '[\\/]').Count

    # Collect all filesystem items recursively
    $allItems = [System.Collections.Generic.List[System.IO.FileSystemInfo]]::new()

    # Include the search path itself (find includes the root)
    $rootItem = Get-Item -LiteralPath $resolvedRoot -Force
    $allItems.Add($rootItem)

    if ($rootItem -is [System.IO.DirectoryInfo]) {
        try {
            $children = Get-ChildItem -LiteralPath $resolvedRoot -Force -Recurse -ErrorAction SilentlyContinue
            foreach ($child in $children) { $allItems.Add($child) }
        } catch { }
    }

    # Parse size expression: +1k, -500c, +1M etc.
    $sizeOp = $null
    $sizeBytes = 0
    if ($null -ne $sizeExpr) {
        if ($sizeExpr -match '^([+-])(\d+)([ckMG]?)$') {
            $sizeOp = $Matches[1]
            $sizeNum = [long]$Matches[2]
            $sizeSuffix = $Matches[3]
            $sizeBytes = switch ($sizeSuffix) {
                'c' { $sizeNum }
                'k' { $sizeNum * 1024 }
                'M' { $sizeNum * 1048576 }
                'G' { $sizeNum * 1073741824 }
                default { $sizeNum * 512 }
            }
        }
    }

    # Parse mtime expression: -7 (less than 7 days ago), +30 (more than 30 days ago)
    $mtimeOp = $null
    $mtimeDays = 0
    if ($null -ne $mtimeExpr) {
        if ($mtimeExpr -match '^([+-])(\d+)$') {
            $mtimeOp = $Matches[1]
            $mtimeDays = [int]$Matches[2]
        }
    }

    $now = [datetime]::Now

    foreach ($item in $allItems) {
        $itemPath = $item.FullName
        $itemDepth = ($itemPath -split '[\\/]').Count
        $relativeDepth = $itemDepth - $rootDepth

        # maxdepth filter
        if ($relativeDepth -gt $maxDepth) { continue }

        $isDir = $item -is [System.IO.DirectoryInfo]

        # type filter
        if ($null -ne $typeFilter) {
            if ($typeFilter -eq 'f' -and $isDir) { continue }
            if ($typeFilter -eq 'd' -and -not $isDir) { continue }
        }

        # name filter (glob pattern)
        if ($null -ne $namePattern) {
            if ($item.Name -notlike $namePattern) { continue }
        }

        # size filter
        if ($null -ne $sizeOp) {
            $fileSize = if ($isDir) { 0 } else { $item.Length }
            if ($sizeOp -eq '+' -and $fileSize -le $sizeBytes) { continue }
            if ($sizeOp -eq '-' -and $fileSize -ge $sizeBytes) { continue }
        }

        # mtime filter
        if ($null -ne $mtimeOp) {
            $daysAgo = ($now - $item.LastWriteTime).TotalDays
            if ($mtimeOp -eq '-' -and $daysAgo -ge $mtimeDays) { continue }
            if ($mtimeOp -eq '+' -and $daysAgo -le $mtimeDays) { continue }
        }

        # empty filter
        if ($findEmpty) {
            if ($isDir) {
                $dirChildren = @(Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue)
                if ($dirChildren.Count -gt 0) { continue }
            } else {
                if ($item.Length -gt 0) { continue }
            }
        }

        # Build relative path with forward slashes
        $relativePath = $itemPath.Substring($resolvedRoot.Length)
        $relativePath = $relativePath -replace '\\', '/'
        if ($relativePath.StartsWith('/')) {
            $relativePath = $relativePath.Substring(1)
        }

        $displayPath = if ($searchPath -eq '.') {
            if ($relativePath -eq '') { '.' } else { "./$relativePath" }
        } else {
            $normalized = $searchPath -replace '\\', '/'
            if ($relativePath -eq '') { $normalized } else { "$normalized/$relativePath" }
        }

        # Reuse Get-BashFileInfo for metadata
        $fileInfo = Get-BashFileInfo -Item $item

        $obj = [PSCustomObject]@{
            PSTypeName   = 'PsBash.FindEntry'
            Path         = $displayPath
            Name         = $item.Name
            FullPath     = $itemPath
            IsDirectory  = $isDir
            SizeBytes    = $fileInfo.SizeBytes
            Permissions  = $fileInfo.Permissions
            LinkCount    = $fileInfo.LinkCount
            Owner        = $fileInfo.Owner
            Group        = $fileInfo.Group
            LastModified = $item.LastWriteTime
            BashText     = $displayPath
        }
        $obj | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
            $this.BashText
        } -Force
        $obj
    }
}

# --- stat Command ---

function Invoke-BashStat {
    $Arguments = [string[]]$args

    $formatString = $null
    $printfString = $null
    $terseMode = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -eq '-c' -and ($i + 1) -lt $Arguments.Count) {
            $formatString = $Arguments[$i + 1]
            $i += 2
            continue
        }
        if ($arg -match '^--printf=(.+)$') {
            $printfString = $Matches[1]
            $i++
            continue
        }
        if ($arg -eq '-t') {
            $terseMode = $true
            $i++
            continue
        }
        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -eq 0) {
        Write-Error -Message "stat: missing operand" -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }

    $hadError = $false

    foreach ($target in $operands) {
        if (-not (Test-Path -LiteralPath $target)) {
            $msg = "stat: cannot stat '$target': No such file or directory"
            Write-Error -Message $msg -ErrorAction Continue
            $hadError = $true
            continue
        }

        $item = Get-Item -LiteralPath $target -Force
        $fileInfo = Get-BashFileInfo -Item $item
        $isDir = $item -is [System.IO.DirectoryInfo]
        $size = if ($isDir) { 4096 } else { $item.Length }

        # Cross-platform: inode, blocks, device
        $inode = [long]0
        $blocks = [long]([System.Math]::Ceiling($size / 512.0))
        $device = [long]0

        if (-not $IsWindows) {
            $nativeArgs = if ($IsMacOS) {
                @('-f', '%i %b %d', $item.FullName)
            } else {
                @('-c', '%i %b %d', $item.FullName)
            }
            $nativeOutput = & /usr/bin/stat @nativeArgs 2>$null
            if ($nativeOutput) {
                $parts = $nativeOutput -split '\s+', 3
                $inode = [long]$parts[0]
                $blocks = [long]$parts[1]
                $device = [long]$parts[2]
            }
        } else {
            # Windows: synthesize inode=0, blocks from size, device from drive letter
            $driveLetter = $item.FullName.Substring(0, 1).ToUpper()
            $device = [long]([byte][char]$driveLetter) - [long]([byte][char]'A')
        }

        $mode = 0
        if (-not $IsWindows) {
            $mode = [int]$item.UnixFileMode
        } else {
            # Approximate from permission string
            $perm = $fileInfo.Permissions.Substring(1)
            $bitMap = @{ 'r' = @(256,32,4); 'w' = @(128,16,2); 'x' = @(64,8,1) }
            for ($ci = 0; $ci -lt 9; $ci++) {
                $ch = $perm[$ci]
                if ($ch -ne '-') {
                    $groupIdx = [System.Math]::Floor($ci / 3)
                    $typeIdx = $ci % 3
                    $typeChar = @('r','w','x')[$typeIdx]
                    $mode = $mode -bor $bitMap[$typeChar][$groupIdx]
                }
            }
        }

        $octalPerms = [System.Convert]::ToString(($mode -band 0x1FF), 8).PadLeft(4, '0')
        $mtime = $item.LastWriteTime
        $mtimeEpoch = [long]([System.DateTimeOffset]::new($mtime).ToUnixTimeSeconds())
        $accessTime = $item.LastAccessTime
        $atimeEpoch = [long]([System.DateTimeOffset]::new($accessTime).ToUnixTimeSeconds())

        $statEntry = [PSCustomObject]@{
            PSTypeName   = 'PsBash.StatEntry'
            Name         = $item.Name
            FullPath     = $item.FullName
            IsDirectory  = $isDir
            SizeBytes    = $size
            Permissions  = $fileInfo.Permissions
            OctalPerms   = $octalPerms
            LinkCount    = $fileInfo.LinkCount
            Owner        = $fileInfo.Owner
            Group        = $fileInfo.Group
            Inode        = $inode
            Blocks       = $blocks
            Device       = $device
            LastModified = $mtime
            MtimeEpoch   = $mtimeEpoch
            AccessTime   = $accessTime
            AtimeEpoch   = $atimeEpoch
            BashText     = ''
        }

        # Format output
        if ($null -ne $printfString) {
            $text = Format-StatString -Entry $statEntry -FormatStr $printfString
            $text = Expand-EscapeSequences -Text $text
            $statEntry.BashText = $text
        } elseif ($null -ne $formatString) {
            $text = Format-StatString -Entry $statEntry -FormatStr $formatString
            $statEntry.BashText = $text + "`n"
        } elseif ($terseMode) {
            $statEntry.BashText = "{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11} {12} {13}`n" -f `
                $statEntry.Name,
                $statEntry.SizeBytes,
                $statEntry.Blocks,
                $octalPerms,
                $statEntry.Owner,
                $statEntry.Group,
                $statEntry.Device,
                $statEntry.Inode,
                $statEntry.LinkCount,
                '0',
                '0',
                $statEntry.AtimeEpoch,
                $statEntry.MtimeEpoch,
                '0'
        } else {
            $typeDesc = if ($isDir) { 'directory' } else { 'regular file' }
            $sb = [System.Text.StringBuilder]::new()
            [void]$sb.AppendLine("  File: $($statEntry.Name)")
            [void]$sb.AppendLine("  Size: $($statEntry.SizeBytes)`tBlocks: $($statEntry.Blocks)`tIO Block: 4096`t$typeDesc")
            [void]$sb.AppendLine("Device: $($statEntry.Device)`tInode: $($statEntry.Inode)`tLinks: $($statEntry.LinkCount)")
            [void]$sb.AppendLine("Access: ($octalPerms/$($statEntry.Permissions))`tUid: ($($statEntry.Owner))`tGid: ($($statEntry.Group))")
            [void]$sb.Append("Modify: $($mtime.ToString('yyyy-MM-dd HH:mm:ss.fffffff zzz'))")
            $statEntry.BashText = $sb.ToString() + "`n"
        }

        $statEntry | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
            $this.BashText
        } -Force
        $statEntry
    }

    if ($hadError) {
        $global:LASTEXITCODE = 1
    }
}

function Format-StatString {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Entry,

        [Parameter(Mandatory)]
        [string]$FormatStr
    )

    $sb = [System.Text.StringBuilder]::new()
    $i = 0
    while ($i -lt $FormatStr.Length) {
        if ($FormatStr[$i] -eq '%' -and ($i + 1) -lt $FormatStr.Length) {
            $spec = $FormatStr[$i + 1]
            switch -CaseSensitive ($spec) {
                's' { [void]$sb.Append($Entry.SizeBytes);   $i += 2; break }
                'a' { [void]$sb.Append($Entry.OctalPerms);  $i += 2; break }
                'A' { [void]$sb.Append($Entry.Permissions); $i += 2; break }
                'n' { [void]$sb.Append($Entry.Name);        $i += 2; break }
                'N' { [void]$sb.Append($Entry.FullPath);    $i += 2; break }
                'U' { [void]$sb.Append($Entry.Owner);       $i += 2; break }
                'G' { [void]$sb.Append($Entry.Group);       $i += 2; break }
                'i' { [void]$sb.Append($Entry.Inode);       $i += 2; break }
                'b' { [void]$sb.Append($Entry.Blocks);      $i += 2; break }
                'd' { [void]$sb.Append($Entry.Device);      $i += 2; break }
                'Y' { [void]$sb.Append($Entry.MtimeEpoch);  $i += 2; break }
                'h' { [void]$sb.Append($Entry.LinkCount);   $i += 2; break }
                '%' { [void]$sb.Append('%');                 $i += 2; break }
                default {
                    [void]$sb.Append($FormatStr[$i])
                    $i++
                }
            }
        } else {
            [void]$sb.Append($FormatStr[$i])
            $i++
        }
    }
    $sb.ToString()
}

# --- cp Command ---

function Invoke-BashCp {
    $Arguments = [string[]]$args

    $defs = New-FlagDefs -Entries @(
        '-r', 'recursive'
        '-R', 'recursive'
        '-v', 'verbose'
        '-n', 'no-clobber'
        '-f', 'force'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $recursive = $parsed.Flags['-r'] -or $parsed.Flags['-R']
    $verbose = $parsed.Flags['-v']
    $noClobber = $parsed.Flags['-n']
    $force = $parsed.Flags['-f']

    if ($parsed.Operands.Count -lt 2) {
        Write-Error -Message "cp: missing file operand" -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }

    $dest = $parsed.Operands[$parsed.Operands.Count - 1]
    $sources = $parsed.Operands[0..($parsed.Operands.Count - 2)]

    $hadError = $false

    foreach ($src in $sources) {
        if (-not (Test-Path -LiteralPath $src)) {
            Write-Error -Message "cp: cannot stat '$src': No such file or directory" -ErrorAction Continue
            $hadError = $true
            continue
        }

        $srcItem = Get-Item -LiteralPath $src -Force
        $isDir = $srcItem -is [System.IO.DirectoryInfo]

        if ($isDir -and -not $recursive) {
            Write-Error -Message "cp: -r not specified; omitting directory '$src'" -ErrorAction Continue
            $hadError = $true
            continue
        }

        $targetPath = $dest
        if ((Test-Path -LiteralPath $dest) -and (Get-Item -LiteralPath $dest -Force) -is [System.IO.DirectoryInfo]) {
            $targetPath = Join-Path $dest $srcItem.Name
        }

        if ($noClobber -and (Test-Path -LiteralPath $targetPath)) {
            continue
        }

        if ($isDir) {
            if (Test-Path -LiteralPath $targetPath) {
                if ($force) {
                    Remove-Item -LiteralPath $targetPath -Recurse -Force
                }
            }
            Copy-Item -LiteralPath $src -Destination $targetPath -Recurse -Force
        } else {
            $destDir = Split-Path $targetPath -Parent
            if ($destDir -and -not (Test-Path -LiteralPath $destDir)) {
                New-Item -Path $destDir -ItemType Directory -Force | Out-Null
            }
            Copy-Item -LiteralPath $src -Destination $targetPath -Force
        }

        if ($verbose) {
            $bashSrc = $src -replace '\\', '/'
            $bashDest = $targetPath -replace '\\', '/'
            New-BashObject -BashText "'$bashSrc' -> '$bashDest'`n"
        }
    }

    if ($hadError) {
        $global:LASTEXITCODE = 1
    }
}

# --- mv Command ---

function Invoke-BashMv {
    $Arguments = [string[]]$args

    $defs = New-FlagDefs -Entries @(
        '-v', 'verbose'
        '-n', 'no-clobber'
        '-f', 'force'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $verbose = $parsed.Flags['-v']
    $noClobber = $parsed.Flags['-n']

    if ($parsed.Operands.Count -lt 2) {
        Write-Error -Message "mv: missing file operand" -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }

    $dest = $parsed.Operands[$parsed.Operands.Count - 1]
    $sources = $parsed.Operands[0..($parsed.Operands.Count - 2)]

    $hadError = $false

    foreach ($src in $sources) {
        if (-not (Test-Path -LiteralPath $src)) {
            Write-Error -Message "mv: cannot stat '$src': No such file or directory" -ErrorAction Continue
            $hadError = $true
            continue
        }

        $srcItem = Get-Item -LiteralPath $src -Force
        $targetPath = $dest
        if ((Test-Path -LiteralPath $dest) -and (Get-Item -LiteralPath $dest -Force) -is [System.IO.DirectoryInfo]) {
            $targetPath = Join-Path $dest $srcItem.Name
        }

        if ($noClobber -and (Test-Path -LiteralPath $targetPath)) {
            continue
        }

        Move-Item -LiteralPath $src -Destination $targetPath -Force

        if ($verbose) {
            $bashSrc = $src -replace '\\', '/'
            $bashDest = $targetPath -replace '\\', '/'
            New-BashObject -BashText "'$bashSrc' -> '$bashDest'`n"
        }
    }

    if ($hadError) {
        $global:LASTEXITCODE = 1
    }
}

# --- rm Command ---

function Invoke-BashRm {
    $Arguments = [string[]]$args

    $defs = New-FlagDefs -Entries @(
        '-r', 'recursive'
        '-R', 'recursive'
        '-f', 'force'
        '-v', 'verbose'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $recursive = $parsed.Flags['-r'] -or $parsed.Flags['-R']
    $force = $parsed.Flags['-f']
    $verbose = $parsed.Flags['-v']

    if ($parsed.Operands.Count -eq 0) {
        if (-not $force) {
            Write-Error -Message "rm: missing operand" -ErrorAction Continue
            $global:LASTEXITCODE = 1
        }
        return
    }

    $hadError = $false

    foreach ($target in $parsed.Operands) {
        # Safety: refuse to delete root or home directory
        $resolved = $null
        try {
            if (Test-Path -LiteralPath $target) {
                $resolved = (Resolve-Path -LiteralPath $target).Path
            }
        } catch { }

        if ($null -ne $resolved) {
            $normalized = $resolved.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
            $roots = @(
                [System.IO.Path]::GetPathRoot($resolved).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
            )
            $homeDir = [System.Environment]::GetFolderPath('UserProfile')
            if ($homeDir) {
                $roots += $homeDir.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
            }
            foreach ($root in $roots) {
                if ($normalized -eq $root) {
                    Write-Error -Message "rm: refusing to remove '$target': protected path" -ErrorAction Continue
                    $hadError = $true
                    continue
                }
            }
            # Skip this target if it matched a protected path
            $isProtected = $false
            foreach ($root in $roots) {
                if ($normalized -eq $root) { $isProtected = $true; break }
            }
            if ($isProtected) { continue }
        }

        if (-not (Test-Path -LiteralPath $target)) {
            if (-not $force) {
                Write-Error -Message "rm: cannot remove '$target': No such file or directory" -ErrorAction Continue
                $hadError = $true
            }
            continue
        }

        $item = Get-Item -LiteralPath $target -Force
        $isDir = $item -is [System.IO.DirectoryInfo]

        if ($isDir -and -not $recursive) {
            Write-Error -Message "rm: cannot remove '$target': Is a directory" -ErrorAction Continue
            $hadError = $true
            continue
        }

        if ($verbose) {
            if ($isDir -and $recursive) {
                $children = Get-ChildItem -LiteralPath $target -Force -Recurse
                foreach ($child in $children) {
                    $relPath = $child.FullName -replace '\\', '/'
                    New-BashObject -BashText "removed '$relPath'`n"
                }
            }
            $bashTarget = $target -replace '\\', '/'
            New-BashObject -BashText "removed '$bashTarget'`n"
        }

        Remove-Item -LiteralPath $target -Recurse:$recursive -Force
    }

    if ($hadError) {
        $global:LASTEXITCODE = 1
    }
}

# --- mkdir Command ---

function Invoke-BashMkdir {
    $Arguments = [string[]]$args

    $defs = New-FlagDefs -Entries @(
        '-p', 'parents'
        '-v', 'verbose'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $parents = $parsed.Flags['-p']
    $verbose = $parsed.Flags['-v']

    if ($parsed.Operands.Count -eq 0) {
        Write-Error -Message "mkdir: missing operand" -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }

    $hadError = $false

    foreach ($dir in $parsed.Operands) {
        if (Test-Path -LiteralPath $dir) {
            if (-not $parents) {
                Write-Error -Message "mkdir: cannot create directory '$dir': File exists" -ErrorAction Continue
                $hadError = $true
            }
            continue
        }

        $parentDir = Split-Path $dir -Parent
        if ($parentDir -and -not (Test-Path -LiteralPath $parentDir) -and -not $parents) {
            Write-Error -Message "mkdir: cannot create directory '$dir': No such file or directory" -ErrorAction Continue
            $hadError = $true
            continue
        }

        if ($parents) {
            New-Item -Path $dir -ItemType Directory -Force | Out-Null
        } else {
            New-Item -Path $dir -ItemType Directory | Out-Null
        }

        if ($verbose) {
            $bashDir = $dir -replace '\\', '/'
            New-BashObject -BashText "mkdir: created directory '$bashDir'`n"
        }
    }

    if ($hadError) {
        $global:LASTEXITCODE = 1
    }
}

# --- rmdir Command ---

function Invoke-BashRmdir {
    $Arguments = [string[]]$args

    $defs = New-FlagDefs -Entries @(
        '-p', 'parents'
        '-v', 'verbose'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $removeParents = $parsed.Flags['-p']
    $verbose = $parsed.Flags['-v']

    if ($parsed.Operands.Count -eq 0) {
        Write-Error -Message "rmdir: missing operand" -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }

    $hadError = $false

    foreach ($dir in $parsed.Operands) {
        if (-not (Test-Path -LiteralPath $dir)) {
            Write-Error -Message "rmdir: failed to remove '$dir': No such file or directory" -ErrorAction Continue
            $hadError = $true
            continue
        }

        $item = Get-Item -LiteralPath $dir -Force
        if ($item -isnot [System.IO.DirectoryInfo]) {
            Write-Error -Message "rmdir: failed to remove '$dir': Not a directory" -ErrorAction Continue
            $hadError = $true
            continue
        }

        $children = Get-ChildItem -LiteralPath $dir -Force
        if ($children) {
            Write-Error -Message "rmdir: failed to remove '$dir': Directory not empty" -ErrorAction Continue
            $hadError = $true
            continue
        }

        Remove-Item -LiteralPath $dir -Force

        if ($verbose) {
            $bashDir = $dir -replace '\\', '/'
            New-BashObject -BashText "rmdir: removing directory, '$bashDir'`n"
        }

        if ($removeParents) {
            $parent = Split-Path $dir -Parent
            while ($parent -and (Test-Path -LiteralPath $parent)) {
                $parentChildren = Get-ChildItem -LiteralPath $parent -Force
                if ($parentChildren) { break }
                if ($verbose) {
                    $bashParent = $parent -replace '\\', '/'
                    New-BashObject -BashText "rmdir: removing directory, '$bashParent'`n"
                }
                Remove-Item -LiteralPath $parent -Force
                $parent = Split-Path $parent -Parent
            }
        }
    }

    if ($hadError) {
        $global:LASTEXITCODE = 1
    }
}

# --- touch Command ---

function Invoke-BashTouch {
    $Arguments = [string[]]$args

    # Manual arg parsing for -d which takes a value
    $verbose = $false
    $dateStr = $null
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        switch ($arg) {
            '-d' {
                $i++
                if ($i -lt $Arguments.Count) { $dateStr = $Arguments[$i] }
                $i++
                continue
            }
            '-v' {
                $verbose = $true
                $i++
                continue
            }
            default {
                $operands.Add($arg)
                $i++
            }
        }
    }

    if ($operands.Count -eq 0) {
        Write-Error -Message "touch: missing file operand" -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }

    $timestamp = [System.DateTime]::Now
    if ($null -ne $dateStr) {
        try {
            $timestamp = [System.DateTime]::Parse($dateStr)
        } catch {
            Write-Error -Message "touch: invalid date format '$dateStr'" -ErrorAction Continue
            $global:LASTEXITCODE = 1
            return
        }
    }

    foreach ($file in $operands) {
        if (Test-Path -LiteralPath $file) {
            $item = Get-Item -LiteralPath $file -Force
            $item.LastWriteTime = $timestamp
            $item.LastAccessTime = $timestamp
        } else {
            $parentDir = Split-Path $file -Parent
            if ($parentDir -and -not (Test-Path -LiteralPath $parentDir)) {
                Write-Error -Message "touch: cannot touch '$file': No such file or directory" -ErrorAction Continue
                $global:LASTEXITCODE = 1
                continue
            }
            New-Item -Path $file -ItemType File -Force | Out-Null
            $item = Get-Item -LiteralPath $file -Force
            $item.LastWriteTime = $timestamp
            $item.LastAccessTime = $timestamp
        }
    }
}

# --- ln Command ---

function Invoke-BashLn {
    $Arguments = [string[]]$args

    $defs = New-FlagDefs -Entries @(
        '-s', 'symbolic'
        '-f', 'force'
        '-v', 'verbose'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $symbolic = $parsed.Flags['-s']
    $force = $parsed.Flags['-f']
    $verbose = $parsed.Flags['-v']

    if ($parsed.Operands.Count -lt 2) {
        Write-Error -Message "ln: missing file operand" -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }

    $target = $parsed.Operands[0]
    $linkName = $parsed.Operands[1]

    if ($force -and (Test-Path -LiteralPath $linkName)) {
        Remove-Item -LiteralPath $linkName -Force
    }

    if (Test-Path -LiteralPath $linkName) {
        Write-Error -Message "ln: failed to create $( if ($symbolic) { 'symbolic ' } else { '' })link '$linkName': File exists" -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }

    if ($symbolic) {
        New-Item -ItemType SymbolicLink -Path $linkName -Target $target -Force | Out-Null
    } else {
        New-Item -ItemType HardLink -Path $linkName -Target $target -Force | Out-Null
    }

    if ($verbose) {
        $bashLink = $linkName -replace '\\', '/'
        $bashTarget = $target -replace '\\', '/'
        if ($symbolic) {
            New-BashObject -BashText "'$bashLink' -> '$bashTarget'`n"
        } else {
            New-BashObject -BashText "'$bashLink' => '$bashTarget'`n"
        }
    }
}

# --- Aliases ---

Set-Alias -Name 'echo'   -Value 'Invoke-BashEcho'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'printf'  -Value 'Invoke-BashPrintf'  -Force -Scope Global -Option AllScope
Set-Alias -Name 'ls'      -Value 'Invoke-BashLs'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'cat'     -Value 'Invoke-BashCat'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'grep'    -Value 'Invoke-BashGrep'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'sort'    -Value 'Invoke-BashSort'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'head'    -Value 'Invoke-BashHead'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'tail'    -Value 'Invoke-BashTail'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'wc'      -Value 'Invoke-BashWc'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'find'    -Value 'Invoke-BashFind'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'stat'    -Value 'Invoke-BashStat'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'cp'      -Value 'Invoke-BashCp'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'mv'      -Value 'Invoke-BashMv'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'rm'      -Value 'Invoke-BashRm'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'mkdir'   -Value 'Invoke-BashMkdir'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'rmdir'   -Value 'Invoke-BashRmdir'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'touch'   -Value 'Invoke-BashTouch'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'ln'      -Value 'Invoke-BashLn'      -Force -Scope Global -Option AllScope
