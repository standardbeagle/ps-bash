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

# --- ps Command ---

function Get-LinuxProcEntry {
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$ProcDir
    )

    $pidStr = Split-Path $ProcDir -Leaf
    $pid = [int]$pidStr

    # Read /proc/[pid]/stat
    $statPath = Join-Path $ProcDir 'stat'
    if (-not (Test-Path -LiteralPath $statPath)) { return $null }
    $statRaw = $null
    try { $statRaw = [System.IO.File]::ReadAllText($statPath) } catch { return $null }

    # Parse stat: PID (comm) state PPID ... — comm can contain spaces and parens
    if ($statRaw -notmatch '^\d+\s+\((.+)\)\s+(\S+)\s+(\d+)\s+(.*)$') { return $null }
    $comm = $Matches[1]
    $state = $Matches[2]
    $ppid = [int]$Matches[3]
    $restFields = $Matches[4] -split '\s+'

    # Fields after PPID in /proc/[pid]/stat (0-indexed from field 5 onward):
    # 0=pgrp 1=session 2=tty_nr 3=tpgid 4=flags 5=minflt 6=cminflt 7=majflt
    # 8=cmajflt 9=utime 10=stime 11=cutime 12=cstime 13=priority 14=nice
    # 15=num_threads 16=itrealvalue 17=starttime 18=vsize 19=rss
    $ttyNr = if ($restFields.Count -gt 2) { [int]$restFields[2] } else { 0 }
    $utime = if ($restFields.Count -gt 9) { [long]$restFields[9] } else { 0 }
    $stime = if ($restFields.Count -gt 10) { [long]$restFields[10] } else { 0 }
    $starttime = if ($restFields.Count -gt 17) { [long]$restFields[17] } else { 0 }
    $vsize = if ($restFields.Count -gt 18) { [long]$restFields[18] } else { 0 }
    $rssPages = if ($restFields.Count -gt 19) { [long]$restFields[19] } else { 0 }

    # Read /proc/[pid]/status for Uid (user)
    $uid = 0
    $statusPath = Join-Path $ProcDir 'status'
    try {
        $statusLines = [System.IO.File]::ReadAllLines($statusPath)
        foreach ($line in $statusLines) {
            if ($line.StartsWith('Uid:')) {
                $uidParts = $line.Substring(4).Trim() -split '\s+'
                $uid = [int]$uidParts[0]
                break
            }
        }
    } catch {}

    # Resolve username from UID
    $userName = $uid.ToString()
    try {
        $passwdLine = & /usr/bin/getent passwd $uid 2>$null
        if ($passwdLine) { $userName = ($passwdLine -split ':')[0] }
    } catch {}

    # Read /proc/[pid]/cmdline
    $cmdline = ''
    $cmdlinePath = Join-Path $ProcDir 'cmdline'
    try {
        $cmdlineBytes = [System.IO.File]::ReadAllBytes($cmdlinePath)
        if ($cmdlineBytes.Length -gt 0) {
            $cmdline = [System.Text.Encoding]::UTF8.GetString($cmdlineBytes).TrimEnd([char]0) -replace [char]0, ' '
        }
    } catch {}
    if ([string]::IsNullOrWhiteSpace($cmdline)) { $cmdline = "[$comm]" }

    # TTY resolution
    $tty = '?'
    if ($ttyNr -ne 0) {
        $major = ($ttyNr -shr 8) -band 0xFF
        $minor = $ttyNr -band 0xFF
        if ($major -eq 136) { $tty = "pts/$minor" }
        elseif ($major -eq 4) { $tty = "tty$minor" }
        else { $tty = "$major/$minor" }
    }

    # CPU time in seconds (clock ticks -> seconds, typically 100 ticks/sec)
    $clkTck = 100
    $totalCpuSec = ($utime + $stime) / $clkTck
    $cpuMin = [System.Math]::Floor($totalCpuSec / 60)
    $cpuSec = [int]($totalCpuSec % 60)
    $cpuTime = '{0}:{1:D2}' -f $cpuMin, $cpuSec

    # Start time: boot time + starttime ticks
    $bootTime = [System.DateTimeOffset]::UtcNow
    try {
        $uptimeStr = [System.IO.File]::ReadAllText('/proc/uptime').Trim().Split(' ')[0]
        $uptimeSec = [double]$uptimeStr
        $bootTime = [System.DateTimeOffset]::UtcNow.AddSeconds(-$uptimeSec)
    } catch {}
    $startDate = $bootTime.AddSeconds($starttime / $clkTck).LocalDateTime

    # RSS in KB, VSZ in KB
    $pageSize = 4096
    $rssKB = [long]($rssPages * $pageSize / 1024)
    $vszKB = [long]($vsize / 1024)

    # CPU% approximate (snapshot, not accumulated — use 0.0 for snapshot mode)
    $cpuPct = [double]0.0

    # Memory %
    $totalMemKB = [long]1
    try {
        $memLines = [System.IO.File]::ReadAllLines('/proc/meminfo')
        foreach ($ml in $memLines) {
            if ($ml.StartsWith('MemTotal:')) {
                $totalMemKB = [long](($ml -replace '[^\d]', '').Trim())
                break
            }
        }
    } catch {}
    $memPct = if ($totalMemKB -gt 0) { [System.Math]::Round(($rssKB / $totalMemKB) * 100.0, 1) } else { [double]0.0 }

    # Process state to STAT string
    $statStr = $state

    [PSCustomObject]@{
        PID         = $pid
        PPID        = $ppid
        User        = $userName
        CPU         = [double]$cpuPct
        Memory      = [double]$memPct
        MemoryMB    = [double][System.Math]::Round($rssKB / 1024.0, 1)
        VSZ         = [long]$vszKB
        RSS         = [long]$rssKB
        TTY         = $tty
        Stat        = $statStr
        Start       = $startDate
        Time        = $cpuTime
        Command     = $cmdline
        CommandLine = $cmdline
        ProcessName = $comm
        WorkingSet  = [long]($rssKB * 1024)
    }
}

function Get-DotNetProcEntry {
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    $p = $Process
    $procName = $p.ProcessName
    $pid = $p.Id
    $ppid = 0
    $userName = ''
    $cpu = [double]0.0
    $memPct = [double]0.0
    $vszKB = [long]0
    $rssKB = [long]0
    $ws = [long]0
    $tty = '?'
    $statStr = 'S'
    $startDate = [System.DateTime]::Now
    $cpuTime = '0:00'
    $cmdline = ''

    try { $ws = [long]$p.WorkingSet64 } catch {}
    $rssKB = [long]($ws / 1024)
    try { $vszKB = [long]($p.VirtualMemorySize64 / 1024) } catch {}

    $totalMemBytes = [long]1
    try {
        if ($IsWindows) {
            $totalMemBytes = [long](Get-CimInstance Win32_OperatingSystem).TotalVisibleMemorySize * 1024
        } elseif ($IsMacOS) {
            $sysctl = & /usr/sbin/sysctl -n hw.memsize 2>$null
            if ($sysctl) { $totalMemBytes = [long]$sysctl }
        }
    } catch {}
    if ($totalMemBytes -gt 0) {
        $memPct = [double][System.Math]::Round(($ws / $totalMemBytes) * 100.0, 1)
    }

    try { $startDate = $p.StartTime } catch {}
    try {
        $totalSec = $p.TotalProcessorTime.TotalSeconds
        $cpuMin = [System.Math]::Floor($totalSec / 60)
        $cpuSec = [int]($totalSec % 60)
        $cpuTime = '{0}:{1:D2}' -f $cpuMin, $cpuSec
    } catch {}

    if ($IsWindows) {
        try {
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $pid" -ErrorAction SilentlyContinue
            if ($cim) {
                $cmdline = $cim.CommandLine
                $userName = $cim.GetOwner().User
                if ($cim.ParentProcessId) { $ppid = [int]$cim.ParentProcessId }
            }
        } catch {}
        if ([string]::IsNullOrEmpty($userName)) {
            try { $userName = $env:USERNAME } catch {}
        }
        if ($p.SessionId -gt 0) { $tty = "con$($p.SessionId)" }
    } elseif ($IsMacOS) {
        try {
            $psLine = & /bin/ps -o user=,ppid=,tty= -p $pid 2>$null
            if ($psLine) {
                $parts = $psLine.Trim() -split '\s+', 3
                $userName = $parts[0]
                $ppid = [int]$parts[1]
                $tty = if ($parts[2] -eq '??') { '?' } else { $parts[2] }
            }
        } catch {}
    }

    if ([string]::IsNullOrEmpty($cmdline)) { $cmdline = $procName }
    if ([string]::IsNullOrEmpty($userName)) { $userName = '?' }

    if (-not $p.Responding -and -not $IsWindows) { $statStr = 'D' }
    elseif ($p.Threads.Count -gt 1) { $statStr = 'Sl' }

    [PSCustomObject]@{
        PID         = $pid
        PPID        = $ppid
        User        = $userName
        CPU         = [double]$cpu
        Memory      = [double]$memPct
        MemoryMB    = [double][System.Math]::Round($rssKB / 1024.0, 1)
        VSZ         = [long]$vszKB
        RSS         = [long]$rssKB
        TTY         = $tty
        Stat        = $statStr
        Start       = $startDate
        Time        = $cpuTime
        Command     = $cmdline
        CommandLine = $cmdline
        ProcessName = $procName
        WorkingSet  = [long]$ws
    }
}

function Format-PsAuxLine {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Entry
    )

    $startStr = $Entry.Start.ToString('HH:mm')
    '{0,-8} {1,7} {2,4:F1} {3,4:F1} {4,7} {5,6} {6,-7} {7,-4} {8,5} {9,8} {10}' -f `
        $Entry.User,
        $Entry.PID,
        $Entry.CPU,
        $Entry.Memory,
        $Entry.VSZ,
        $Entry.RSS,
        $Entry.TTY,
        $Entry.Stat,
        $startStr,
        $Entry.Time,
        $Entry.Command
}

function Format-PsCustomLine {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Entry,

        [Parameter(Mandatory)]
        [string[]]$Columns
    )

    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($col in $Columns) {
        switch ($col.ToLower().Trim()) {
            'pid'     { $parts.Add('{0,7}' -f $Entry.PID) }
            'ppid'    { $parts.Add('{0,7}' -f $Entry.PPID) }
            'user'    { $parts.Add('{0,-8}' -f $Entry.User) }
            '%cpu'    { $parts.Add('{0,4:F1}' -f $Entry.CPU) }
            'cpu'     { $parts.Add('{0,4:F1}' -f $Entry.CPU) }
            '%mem'    { $parts.Add('{0,4:F1}' -f $Entry.Memory) }
            'mem'     { $parts.Add('{0,4:F1}' -f $Entry.Memory) }
            'vsz'     { $parts.Add('{0,7}' -f $Entry.VSZ) }
            'rss'     { $parts.Add('{0,6}' -f $Entry.RSS) }
            'tty'     { $parts.Add('{0,-7}' -f $Entry.TTY) }
            'stat'    { $parts.Add('{0,-4}' -f $Entry.Stat) }
            'start'   { $parts.Add('{0,5}' -f $Entry.Start.ToString('HH:mm')) }
            'time'    { $parts.Add('{0,8}' -f $Entry.Time) }
            'command' { $parts.Add($Entry.Command) }
            'cmd'     { $parts.Add($Entry.Command) }
            'comm'    { $parts.Add($Entry.ProcessName) }
            'args'    { $parts.Add($Entry.CommandLine) }
            default   { $parts.Add('?') }
        }
    }
    $parts -join ' '
}

function Invoke-BashPs {
    $Arguments = [string[]]$args

    $showAll = $false
    $bsdAux = $false
    $fullFormat = $false
    $filterUser = $null
    $filterPid = $null
    $sortKey = $null
    $sortDescending = $false
    $customFormat = $null

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -eq 'aux' -or $arg -eq '-aux') {
            $bsdAux = $true
            $showAll = $true
            $i++; continue
        }
        if ($arg -eq '-e' -or $arg -eq '-A') {
            $showAll = $true
            $i++; continue
        }
        if ($arg -eq '-f') {
            $fullFormat = $true
            $i++; continue
        }
        if ($arg -eq '-u' -and ($i + 1) -lt $Arguments.Count) {
            $filterUser = $Arguments[$i + 1]
            $i += 2; continue
        }
        if ($arg -eq '-p' -and ($i + 1) -lt $Arguments.Count) {
            $filterPid = [int]$Arguments[$i + 1]
            $i += 2; continue
        }
        if ($arg -match '^--sort=(.+)$') {
            $sk = $Matches[1]
            if ($sk.StartsWith('-')) {
                $sortDescending = $true
                $sk = $sk.Substring(1)
            }
            $sortKey = $sk
            $i++; continue
        }
        if ($arg -eq '-o' -and ($i + 1) -lt $Arguments.Count) {
            $customFormat = $Arguments[$i + 1]
            $i += 2; continue
        }
        $i++
    }

    # Gather process entries
    $entries = [System.Collections.Generic.List[PSCustomObject]]::new()

    if ($IsLinux) {
        $currentUser = & /usr/bin/id -un 2>$null
        $procDirs = [System.IO.Directory]::GetDirectories('/proc')
        foreach ($dir in $procDirs) {
            $dirName = [System.IO.Path]::GetFileName($dir)
            if ($dirName -notmatch '^\d+$') { continue }

            if ($null -ne $filterPid -and [int]$dirName -ne $filterPid) { continue }

            $entry = Get-LinuxProcEntry -ProcDir $dir
            if ($null -eq $entry) { continue }

            if (-not $showAll -and -not $bsdAux -and $null -eq $filterPid) {
                if ($fullFormat) {
                    # ps -f: show current user's processes (no TTY restriction)
                    if ($entry.User -ne $currentUser) { continue }
                } else {
                    # Default ps: show current user's processes with a TTY
                    if ($entry.User -ne $currentUser -or $entry.TTY -eq '?') { continue }
                }
            }

            if ($null -ne $filterUser -and $entry.User -ne $filterUser) { continue }

            $entries.Add($entry)
        }
    } else {
        # Windows / macOS: use Get-Process
        $procs = if ($null -ne $filterPid) {
            Get-Process -Id $filterPid -ErrorAction SilentlyContinue
        } else {
            Get-Process -ErrorAction SilentlyContinue
        }
        if ($procs) {
            foreach ($p in $procs) {
                $entry = Get-DotNetProcEntry -Process $p
                if ($null -eq $entry) { continue }

                if (-not $showAll -and -not $bsdAux -and $null -eq $filterPid) {
                    if ($IsWindows) {
                        $currentUser = $env:USERNAME
                    } else {
                        $currentUser = & /usr/bin/id -un 2>$null
                    }
                    if ($null -ne $currentUser -and $entry.User -ne $currentUser) { continue }
                }

                if ($null -ne $filterUser -and $entry.User -ne $filterUser) { continue }

                $entries.Add($entry)
            }
        }
    }

    # Sort
    if ($null -ne $sortKey) {
        $propName = switch ($sortKey.ToLower()) {
            'pid'  { 'PID' }
            'ppid' { 'PPID' }
            'cpu'  { 'CPU' }
            '%cpu' { 'CPU' }
            'mem'  { 'Memory' }
            '%mem' { 'Memory' }
            'rss'  { 'RSS' }
            'vsz'  { 'VSZ' }
            'user' { 'User' }
            'comm' { 'ProcessName' }
            'time' { 'Time' }
            default { 'PID' }
        }
        if ($sortDescending) {
            $entries = [System.Collections.Generic.List[PSCustomObject]]@(
                $entries | Sort-Object -Property $propName -Descending
            )
        } else {
            $entries = [System.Collections.Generic.List[PSCustomObject]]@(
                $entries | Sort-Object -Property $propName
            )
        }
    }

    # Format columns
    $columns = $null
    if ($null -ne $customFormat) {
        $columns = $customFormat -split ','
    }

    # Emit objects
    foreach ($entry in $entries) {
        $bashText = if ($null -ne $columns) {
            Format-PsCustomLine -Entry $entry -Columns $columns
        } elseif ($bsdAux -or $fullFormat) {
            Format-PsAuxLine -Entry $entry
        } else {
            '{0,7} {1,-7} {2,8} {3}' -f $entry.PID, $entry.TTY, $entry.Time, $entry.Command
        }

        $psEntry = [PSCustomObject]@{
            PSTypeName  = 'PsBash.PsEntry'
            PID         = [int]$entry.PID
            PPID        = [int]$entry.PPID
            User        = [string]$entry.User
            CPU         = [double]$entry.CPU
            Memory      = [double]$entry.Memory
            MemoryMB    = [double]$entry.MemoryMB
            VSZ         = [long]$entry.VSZ
            RSS         = [long]$entry.RSS
            TTY         = [string]$entry.TTY
            Stat        = [string]$entry.Stat
            Start       = $entry.Start
            Time        = [string]$entry.Time
            Command     = [string]$entry.Command
            CommandLine = [string]$entry.CommandLine
            ProcessName = [string]$entry.ProcessName
            WorkingSet  = [long]$entry.WorkingSet
            BashText    = "$bashText`n"
        }
        $psEntry | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
            $this.BashText
        } -Force
        $psEntry
    }
}

# --- sed Command ---

function Invoke-BashSed {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    # Parse flags and expressions
    $suppressDefault = $false
    $inPlace = $false
    $extendedRegex = $false
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

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'n' { $suppressDefault = $true }
                    'i' { $inPlace = $true }
                    'E' { $extendedRegex = $true }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # First operand is the expression if no -e was used
    if ($expressions.Count -eq 0 -and $operands.Count -gt 0) {
        $expressions.Add($operands[0])
        $operands.RemoveAt(0)
    }

    if ($expressions.Count -eq 0) {
        throw 'sed: usage: sed [options] expression [file ...]'
    }

    # Parse sed commands from expressions
    $commands = [System.Collections.Generic.List[hashtable]]::new()
    foreach ($expr in $expressions) {
        $commands.Add((ConvertFrom-SedExpression -Expression $expr -ExtendedRegex $extendedRegex))
    }

    # Apply commands to a single line, return $null to delete
    $applyCommands = {
        param([string]$line, [int]$lineNum, [int]$totalLines, [string[]]$allLines)

        $currentLine = $line
        $printed = $false
        $deleted = $false

        foreach ($cmd in $commands) {
            if ($deleted) { break }

            # Check address match
            if (-not (Test-SedAddress -Cmd $cmd -Line $currentLine -LineNum $lineNum -TotalLines $totalLines -AllLines $allLines)) {
                continue
            }

            switch ($cmd.Type) {
                's' {
                    $regex = $cmd.Regex
                    if ($cmd.Global) {
                        $currentLine = $regex.Replace($currentLine, $cmd.Replacement)
                    } else {
                        $currentLine = $regex.Replace($currentLine, $cmd.Replacement, 1)
                    }
                }
                'd' {
                    $deleted = $true
                }
                'p' {
                    $printed = $true
                }
                'y' {
                    $sb = [System.Text.StringBuilder]::new($currentLine.Length)
                    foreach ($ch in $currentLine.ToCharArray()) {
                        $idx = $cmd.Source.IndexOf($ch)
                        if ($idx -ge 0) {
                            [void]$sb.Append($cmd.Dest[$idx])
                        } else {
                            [void]$sb.Append($ch)
                        }
                    }
                    $currentLine = $sb.ToString()
                }
            }
        }

        @{
            Line    = $currentLine
            Deleted = $deleted
            Printed = $printed
        }
    }

    # --- File mode (including in-place) ---
    if ($operands.Count -gt 0) {
        foreach ($filePath in $operands) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "sed: ${filePath}: No such file or directory" -ErrorAction Continue
                continue
            }

            $bytes = [System.IO.File]::ReadAllBytes($filePath)
            $byteOffset = 0
            if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
                $byteOffset = 3
            }
            $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
            $rawText = $rawText -replace "`r`n", "`n"
            $hadTrailingNewline = $rawText.EndsWith("`n")
            if ($hadTrailingNewline) {
                $rawText = $rawText.Substring(0, $rawText.Length - 1)
            }
            $lines = $rawText.Split("`n")

            $outputLines = [System.Collections.Generic.List[string]]::new()
            for ($li = 0; $li -lt $lines.Count; $li++) {
                $result = & $applyCommands $lines[$li] ($li + 1) $lines.Count $lines
                if (-not $result.Deleted) {
                    if (-not $suppressDefault) {
                        $outputLines.Add($result.Line)
                    }
                    if ($result.Printed) {
                        $outputLines.Add($result.Line)
                    }
                }
            }

            if ($inPlace) {
                $outText = ($outputLines -join "`n")
                if ($hadTrailingNewline) { $outText += "`n" }
                [System.IO.File]::WriteAllText($filePath, $outText, [System.Text.Encoding]::UTF8)
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

    # Collect all lines for address matching that needs total count
    $items = @($pipelineInput)
    $allTexts = [string[]]::new($items.Count)
    for ($idx = 0; $idx -lt $items.Count; $idx++) {
        $text = Get-BashText -InputObject $items[$idx]
        $allTexts[$idx] = $text -replace "`n$", ''
    }

    for ($idx = 0; $idx -lt $items.Count; $idx++) {
        $lineText = $allTexts[$idx]
        $result = & $applyCommands $lineText ($idx + 1) $items.Count $allTexts

        if ($result.Deleted) { continue }

        # Pipeline bridge: modify BashText on the original object
        $item = $items[$idx]
        if ($null -ne $item.PSObject -and $null -ne $item.PSObject.Properties['BashText']) {
            $item.BashText = "$($result.Line)`n"
        }

        if (-not $suppressDefault) {
            $item
        }
        if ($result.Printed) {
            $item
        }
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
        if ($endSlash -lt 0) { throw "sed: unterminated address regex" }
        $addr = @{ Type = 'regex'; Pattern = $Expression.Substring($pos, $endSlash - $pos) }
        $pos = $endSlash + 1

        # Check for range: /start/,/end/
        if ($pos -lt $Expression.Length -and $Expression[$pos] -eq ',') {
            $pos++
            if ($pos -lt $Expression.Length -and $Expression[$pos] -eq '/') {
                $pos++
                $endSlash2 = $Expression.IndexOf('/', $pos)
                if ($endSlash2 -lt 0) { throw "sed: unterminated address regex" }
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
        throw "sed: missing command"
    }

    $cmdChar = $remaining[0]

    switch ($cmdChar) {
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

            if ($parts.Count -lt 2) { throw "sed: bad substitution" }

            $searchPattern = $parts[0]
            $replacement = $parts[1]
            $flags = if ($parts.Count -gt 2) { $parts[2] } else { '' }
            $global = $flags.Contains('g')

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
        'p' {
            @{
                Type    = 'p'
                Address = $addr
            }
        }
        'y' {
            $delim = $remaining[1]
            $parts = $remaining.Substring(2).Split($delim)
            if ($parts.Count -lt 2) { throw "sed: bad transliteration" }
            $source = $parts[0]
            $dest = $parts[1]
            if ($source.Length -ne $dest.Length) {
                throw "sed: y: source and dest must be the same length"
            }
            @{
                Type    = 'y'
                Address = $addr
                Source  = $source
                Dest    = $dest
            }
        }
        default {
            throw "sed: unsupported command '$cmdChar'"
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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    # Parse flags: -F FS, -v VAR=VAL
    $fieldSep = ' '
    $fieldSepIsDefault = $true
    $variables = @{}
    $programText = $null
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

        if ($null -eq $programText) {
            $programText = $arg
        }
        $i++
    }

    if ($null -eq $programText) {
        throw 'awk: usage: awk [options] program [file ...]'
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
    $items = @($pipelineInput)
    for ($idx = 0; $idx -lt $items.Count; $idx++) {
        $text = Get-BashText -InputObject $items[$idx]
        $text = $text -replace "`n$", ''
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
    $funcMatch = [regex]::Match($e, '^(length|substr|tolower|toupper)\s*\((.*)$')
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
        default { return '' }
    }
}

# --- cut Command ---

function Invoke-BashCut {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

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
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) {
                $lines.Add($l)
            }
        }
    } else {
        foreach ($filePath in $operands) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "cut: ${filePath}: No such file or directory" -ErrorAction Continue
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
            foreach ($l in $rawText.Split("`n")) {
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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    # Parse flags: -d (delete), -s (squeeze)
    $deleteMode = $false
    $squeezeMode = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

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
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    $expandClass = {
        param([string]$Spec)
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
                if ($set.IndexOf($ch) -lt 0) {
                    [void]$sb.Append($ch)
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
            [void]$allText.Append($text)
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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $countMode = $false
    $duplicatesOnly = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg.StartsWith('-') -and $arg.Length -gt 1) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'c' { $countMode = $true }
                    'd' { $duplicatesOnly = $true }
                }
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
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) {
                $lines.Add($l)
            }
        }
    } else {
        foreach ($filePath in $operands) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "uniq: ${filePath}: No such file or directory" -ErrorAction Continue
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
            foreach ($l in $rawText.Split("`n")) {
                $lines.Add($l)
            }
        }
    }

    # Group consecutive identical lines
    $groups = [System.Collections.Generic.List[object]]::new()
    $prevLine = $null
    $runCount = 0

    foreach ($line in $lines) {
        if ($line -ceq $prevLine) {
            $runCount++
        } else {
            if ($null -ne $prevLine) {
                $groups.Add(@{ Line = $prevLine; Count = $runCount })
            }
            $prevLine = $line
            $runCount = 1
        }
    }
    if ($null -ne $prevLine) {
        $groups.Add(@{ Line = $prevLine; Count = $runCount })
    }

    foreach ($group in $groups) {
        if ($duplicatesOnly -and $group.Count -lt 2) { continue }

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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    # Collect lines
    $lines = [System.Collections.Generic.List[string]]::new()

    if ($Arguments.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) {
                $lines.Add($l)
            }
        }
    } else {
        foreach ($filePath in $Arguments) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "rev: ${filePath}: No such file or directory" -ErrorAction Continue
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
            foreach ($l in $rawText.Split("`n")) {
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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

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
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) {
                $lines.Add($l)
            }
        }
    } else {
        foreach ($filePath in $operands) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "nl: ${filePath}: No such file or directory" -ErrorAction Continue
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
            foreach ($l in $rawText.Split("`n")) {
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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $unified = $false
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

        $operands.Add($arg)
        $i++
    }

    $readFileLines = {
        param([string]$FilePath)
        if (-not (Test-Path -LiteralPath $FilePath)) {
            Write-Error -Message "diff: ${FilePath}: No such file or directory" -ErrorAction Continue
            return $null
        }
        $bytes = [System.IO.File]::ReadAllBytes($FilePath)
        $byteOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $byteOffset = 3
        }
        $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
        $rawText = $rawText -replace "`r`n", "`n"
        if ($rawText.EndsWith("`n")) {
            $rawText = $rawText.Substring(0, $rawText.Length - 1)
        }
        if ($rawText -eq '') { return @() }
        return @($rawText.Split("`n"))
    }

    if ($operands.Count -lt 2) {
        Write-Error -Message 'diff: missing operand' -ErrorAction Continue
        return
    }

    $result1 = & $readFileLines $operands[0]
    if ($null -eq $result1) { return }
    [string[]]$lines1 = @($result1)
    $result2 = & $readFileLines $operands[1]
    if ($null -eq $result2) { return }
    [string[]]$lines2 = @($result2)

    # Compute LCS table for diff
    $n = $lines1.Count
    $m = $lines2.Count
    $dp = [int[,]]::new($n + 1, $m + 1)
    for ($xi = $n - 1; $xi -ge 0; $xi--) {
        for ($yi = $m - 1; $yi -ge 0; $yi--) {
            if ($lines1[$xi] -ceq $lines2[$yi]) {
                $dp[$xi, $yi] = $dp[($xi + 1), ($yi + 1)] + 1
            } else {
                $a = $dp[($xi + 1), $yi]
                $b = $dp[$xi, ($yi + 1)]
                $dp[$xi, $yi] = if ($a -ge $b) { $a } else { $b }
            }
        }
    }

    # Build edit script
    $edits = [System.Collections.Generic.List[object]]::new()
    $xi = 0; $yi = 0
    while ($xi -lt $n -and $yi -lt $m) {
        if ($lines1[$xi] -ceq $lines2[$yi]) {
            $edits.Add(@{ Op = '='; Line1 = $xi; Line2 = $yi })
            $xi++; $yi++
        } elseif ($dp[($xi + 1), $yi] -ge $dp[$xi, ($yi + 1)]) {
            $edits.Add(@{ Op = '-'; Line1 = $xi })
            $xi++
        } else {
            $edits.Add(@{ Op = '+'; Line2 = $yi })
            $yi++
        }
    }
    while ($xi -lt $n) {
        $edits.Add(@{ Op = '-'; Line1 = $xi })
        $xi++
    }
    while ($yi -lt $m) {
        $edits.Add(@{ Op = '+'; Line2 = $yi })
        $yi++
    }

    # Check if files are identical
    $hasDiff = $false
    foreach ($e in $edits) {
        if ($e.Op -ne '=') { $hasDiff = $true; break }
    }
    if (-not $hasDiff) { return }

    if ($unified) {
        # Unified format
        New-BashObject -BashText "--- $($operands[0])"
        New-BashObject -BashText "+++ $($operands[1])"

        # Group edits into hunks
        $hunkEdits = [System.Collections.Generic.List[object]]::new()
        $contextLines = 3
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
                for ($k = $start; $k -lt $end; $k++) {
                    $hunkEdits.Add($edits[$k])
                }
                $ei = $end
            } else {
                $ei++
            }
        }

        if ($hunkEdits.Count -gt 0) {
            $l1Start = -1; $l1Count = 0; $l2Start = -1; $l2Count = 0
            $hunkLines = [System.Collections.Generic.List[string]]::new()
            foreach ($e in $hunkEdits) {
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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

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

    $readFileLines = {
        param([string]$FilePath)
        if (-not (Test-Path -LiteralPath $FilePath)) {
            Write-Error -Message "comm: ${FilePath}: No such file or directory" -ErrorAction Continue
            return $null
        }
        $bytes = [System.IO.File]::ReadAllBytes($FilePath)
        $byteOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $byteOffset = 3
        }
        $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
        $rawText = $rawText -replace "`r`n", "`n"
        if ($rawText.EndsWith("`n")) {
            $rawText = $rawText.Substring(0, $rawText.Length - 1)
        }
        if ($rawText -eq '') { return @() }
        return @($rawText.Split("`n"))
    }

    if ($operands.Count -lt 2) {
        Write-Error -Message 'comm: missing operand' -ErrorAction Continue
        return
    }

    $result1 = & $readFileLines $operands[0]
    if ($null -eq $result1) { return }
    [string[]]$lines1 = @($result1)
    $result2 = & $readFileLines $operands[1]
    if ($null -eq $result2) { return }
    [string[]]$lines2 = @($result2)

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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

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
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) {
                $lines.Add($l)
            }
        }
    } else {
        foreach ($filePath in $operands) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "column: ${filePath}: No such file or directory" -ErrorAction Continue
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
            foreach ($l in $rawText.Split("`n")) {
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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

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

    $readFileLines = {
        param([string]$FilePath)
        if (-not (Test-Path -LiteralPath $FilePath)) {
            Write-Error -Message "join: ${FilePath}: No such file or directory" -ErrorAction Continue
            return $null
        }
        $bytes = [System.IO.File]::ReadAllBytes($FilePath)
        $byteOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $byteOffset = 3
        }
        $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
        $rawText = $rawText -replace "`r`n", "`n"
        if ($rawText.EndsWith("`n")) {
            $rawText = $rawText.Substring(0, $rawText.Length - 1)
        }
        if ($rawText -eq '') { return @() }
        return @($rawText.Split("`n"))
    }

    if ($operands.Count -lt 2) {
        Write-Error -Message 'join: missing operand' -ErrorAction Continue
        return
    }

    $result1 = & $readFileLines $operands[0]
    if ($null -eq $result1) { return }
    [string[]]$lines1 = @($result1)
    $result2 = & $readFileLines $operands[1]
    if ($null -eq $result2) { return }
    [string[]]$lines2 = @($result2)

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
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

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

    $readFileLines = {
        param([string]$FilePath)
        if (-not (Test-Path -LiteralPath $FilePath)) {
            Write-Error -Message "paste: ${FilePath}: No such file or directory" -ErrorAction Continue
            return $null
        }
        $bytes = [System.IO.File]::ReadAllBytes($FilePath)
        $byteOffset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $byteOffset = 3
        }
        $rawText = [System.Text.Encoding]::UTF8.GetString($bytes, $byteOffset, $bytes.Length - $byteOffset)
        $rawText = $rawText -replace "`r`n", "`n"
        if ($rawText.EndsWith("`n")) {
            $rawText = $rawText.Substring(0, $rawText.Length - 1)
        }
        if ($rawText -eq '') { return @() }
        return @($rawText.Split("`n"))
    }

    # Read all files
    $allFiles = [System.Collections.Generic.List[string[]]]::new()
    foreach ($filePath in $operands) {
        $fileResult = & $readFileLines $filePath
        if ($null -eq $fileResult) { return }
        [string[]]$fileLines = @($fileResult)
        $allFiles.Add($fileLines)
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
Set-Alias -Name 'ps'      -Value 'Invoke-BashPs'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'sed'     -Value 'Invoke-BashSed'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'awk'     -Value 'Invoke-BashAwk'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'cut'     -Value 'Invoke-BashCut'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'tr'      -Value 'Invoke-BashTr'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'uniq'    -Value 'Invoke-BashUniq'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'rev'     -Value 'Invoke-BashRev'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'nl'      -Value 'Invoke-BashNl'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'diff'    -Value 'Invoke-BashDiff'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'comm'    -Value 'Invoke-BashComm'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'column'  -Value 'Invoke-BashColumn'  -Force -Scope Global -Option AllScope
Set-Alias -Name 'join'    -Value 'Invoke-BashJoin'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'paste'   -Value 'Invoke-BashPaste'   -Force -Scope Global -Option AllScope
