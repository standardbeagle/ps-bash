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

# --- Process Substitution Helper ---

function Invoke-ProcessSub {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        & $Command | Out-File -FilePath $tmp -Encoding utf8NoBOM
        return $tmp
    }
    catch {
        Remove-Item -Path $tmp -Force -ErrorAction SilentlyContinue
        throw
    }
}

# --- BashObject Factory ---

function Set-BashDisplayProperty {
    # Configures a PSCustomObject for bash-style display:
    # - Adds ToString() returning BashText
    # - Sets DefaultDisplayProperty to BashText (single-property shortcut)
    # The ps1xml TableControl view handles multi-object display.
    param([PSCustomObject]$Object)
    $Object | Add-Member -MemberType ScriptMethod -Name 'ToString' -Value {
        $this.BashText
    } -Force
    $Object
}

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
    Set-BashDisplayProperty $obj
}

# --- Glob Expansion ---

function Resolve-BashGlob {
    # Expands glob patterns in file operands, matching bash behavior.
    # Literal paths pass through unchanged. Patterns with * or ? are resolved.
    # Returns expanded list of file paths.
    param([string[]]$Paths)
    $resolved = [System.Collections.Generic.List[string]]::new()
    foreach ($p in $Paths) {
        if ($p -match '[*?]') {
            $expanded = @(Resolve-Path -Path $p -ErrorAction SilentlyContinue | ForEach-Object { $_.Path })
            if ($expanded.Count -eq 0) {
                # No matches — pass through literally so the caller can emit its own error
                $resolved.Add($p)
            } else {
                foreach ($e in $expanded) { $resolved.Add($e) }
            }
        } else {
            $resolved.Add($p)
        }
    }
    $resolved
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
    if ($Arguments -contains '--help') { return Show-BashHelp 'echo' }

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
    if ($Arguments -contains '--help') { return Show-BashHelp 'printf' }

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

# --- ls Grid Formatting ---

function Get-LsDisplayName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Entry
    )

    $name = $Entry.Name
    if ($Entry.IsDirectory) {
        $name += '/'
    }
    $name
}

function Format-LsGrid {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [string[]]$Names,

        [Parameter()]
        [int]$TerminalWidth = 80
    )

    if ($Names.Count -eq 0) { return @() }
    if ($Names.Count -eq 1) { return @($Names[0]) }

    $columnGap = 2
    $maxNameLen = 0
    foreach ($n in $Names) {
        if ($n.Length -gt $maxNameLen) { $maxNameLen = $n.Length }
    }

    $bestCols = 1
    $bestColWidths = @($maxNameLen)

    $maxPossibleCols = [Math]::Max(1, [Math]::Floor($TerminalWidth / ($columnGap + 1)))
    if ($maxPossibleCols -gt $Names.Count) { $maxPossibleCols = $Names.Count }

    for ($tryCol = $maxPossibleCols; $tryCol -ge 2; $tryCol--) {
        $rows = [Math]::Ceiling($Names.Count / $tryCol)
        $colWidths = [int[]]::new($tryCol)

        for ($c = 0; $c -lt $tryCol; $c++) {
            $widest = 0
            for ($r = 0; $r -lt $rows; $r++) {
                $idx = $r + $c * $rows
                if ($idx -lt $Names.Count -and $Names[$idx].Length -gt $widest) {
                    $widest = $Names[$idx].Length
                }
            }
            $colWidths[$c] = $widest
        }

        $totalWidth = 0
        for ($c = 0; $c -lt $tryCol; $c++) {
            $totalWidth += $colWidths[$c]
            if ($c -lt $tryCol - 1) { $totalWidth += $columnGap }
        }

        if ($totalWidth -le $TerminalWidth) {
            $bestCols = $tryCol
            $bestColWidths = $colWidths
            break
        }
    }

    $rows = [Math]::Ceiling($Names.Count / $bestCols)
    $lines = [System.Collections.Generic.List[string]]::new()

    for ($r = 0; $r -lt $rows; $r++) {
        $parts = [System.Text.StringBuilder]::new()
        for ($c = 0; $c -lt $bestCols; $c++) {
            $idx = $r + $c * $rows
            if ($idx -ge $Names.Count) { break }
            $name = $Names[$idx]
            if ($c -lt $bestCols - 1) {
                $padded = $name.PadRight($bestColWidths[$c] + $columnGap)
                [void]$parts.Append($padded)
            } else {
                [void]$parts.Append($name)
            }
        }
        $lines.Add($parts.ToString())
    }

    $lines.ToArray()
}

# --- ls Command ---

function Invoke-BashLs {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'ls' }

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
    $onePerLine = $parsed.Flags['-1']

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

    $gridMode = -not $longMode -and -not $onePerLine

    if ($longMode) {
        foreach ($entry in $allEntries) {
            $line = Format-LsLine -Entry $entry -HumanReadable:$humanSizes
            $entry.BashText = $line
            Set-BashDisplayProperty $entry
        }
    } elseif ($gridMode -and $allEntries.Count -gt 0) {
        $displayNames = [string[]]::new($allEntries.Count)
        for ($i = 0; $i -lt $allEntries.Count; $i++) {
            $displayNames[$i] = Get-LsDisplayName -Entry $allEntries[$i]
        }

        $termWidth = 80
        try {
            $w = $Host.UI.RawUI.WindowSize.Width
            if ($w -gt 0) { $termWidth = $w }
        } catch { }

        $gridLines = Format-LsGrid -Names $displayNames -TerminalWidth $termWidth
        foreach ($line in $gridLines) {
            New-BashObject -BashText $line -TypeName 'PsBash.TextOutput'
        }
    } else {
        foreach ($entry in $allEntries) {
            $entry.BashText = Get-LsDisplayName -Entry $entry
            Set-BashDisplayProperty $entry
        }
    }

    if ($hadError -and $allEntries.Count -eq 0) {
        $global:LASTEXITCODE = 2
    }
}

# --- cat Command ---

function Invoke-BashCat {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'cat' }

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
        Set-BashDisplayProperty $obj
    }

    if ($readStdin -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $content = if ($null -ne $item.PSObject -and $null -ne $item.PSObject.Properties['BashText']) { $item.BashText } else { "$item" }
            & $emitLine $content ''
        }
    }

    $fileOperands = @($operands | Where-Object { $_ -ne '-' })
    $resolvedFiles = Resolve-BashGlob -Paths $fileOperands
    foreach ($filePath in $resolvedFiles) {
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
    if ($Arguments -contains '--help') { return Show-BashHelp 'grep' }

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

    foreach ($filePath in (Resolve-BashGlob -Paths $filePaths)) {
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
            Set-BashDisplayProperty $obj
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
            foreach ($filePath in (Resolve-BashGlob -Paths $filePaths)) {
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
                $typeName = if ($null -ne $item.PSObject -and $item.PSObject.TypeNames.Count -gt 0) { $item.PSObject.TypeNames[0] } else { 'PsBash.TextOutput' }
                foreach ($line in $text.Split("`n")) {
                    $items.Add((New-BashObject -BashText $line -TypeName $typeName))
                }
            } else {
                $items.Add($item)
            }
        }
    }

    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
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
    if ($Arguments -contains '--help') { return Show-BashHelp 'head' }

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
        $emitted = 0
        foreach ($item in $pipelineInput) {
            if ($emitted -ge $count) { break }
            $item
            $emitted++
        }
        return
    }

    # File mode — resolve globs
    $resolvedFiles = Resolve-BashGlob -Paths $operands
    foreach ($filePath in $resolvedFiles) {
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
            Set-BashDisplayProperty $obj
        }
    }
}

# --- tail Command ---

function Invoke-BashTail {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'tail' }

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

    # File mode — resolve globs
    $resolvedFiles = Resolve-BashGlob -Paths $operands
    foreach ($filePath in $resolvedFiles) {
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
            Set-BashDisplayProperty $obj
        }
    }
}

# --- wc Command ---

function Invoke-BashWc {
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
        Set-BashDisplayProperty $obj
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

    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
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
    if ($Arguments -contains '--help') { return Show-BashHelp 'find' }

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
        Set-BashDisplayProperty $obj
    }
}

# --- stat Command ---

function Invoke-BashStat {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'stat' }

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

        Set-BashDisplayProperty $statEntry
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
    if ($Arguments -contains '--help') { return Show-BashHelp 'cp' }

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
    if ($Arguments -contains '--help') { return Show-BashHelp 'mv' }

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
    if ($Arguments -contains '--help') { return Show-BashHelp 'rm' }

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
    if ($Arguments -contains '--help') { return Show-BashHelp 'mkdir' }

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
    if ($Arguments -contains '--help') { return Show-BashHelp 'rmdir' }

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
    if ($Arguments -contains '--help') { return Show-BashHelp 'touch' }

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
    if ($Arguments -contains '--help') { return Show-BashHelp 'ln' }

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

    if (-not (Get-Variable -Name TotalMemBytes -Scope Script -ErrorAction SilentlyContinue)) {
        $script:TotalMemBytes = [long]1
        try {
            if ($IsWindows) {
                $script:TotalMemBytes = [long](Get-CimInstance Win32_OperatingSystem).TotalVisibleMemorySize * 1024
            } elseif ($IsMacOS) {
                $sysctl = & /usr/sbin/sysctl -n hw.memsize 2>$null
                if ($sysctl) { $script:TotalMemBytes = [long]$sysctl }
            }
        } catch {}
    }
    $totalMemBytes = $script:TotalMemBytes
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
        if ($null -ne $script:WinCimLookup -and $script:WinCimLookup.ContainsKey($pid)) {
            $info = $script:WinCimLookup[$pid]
            $cmdline = $info.CommandLine
            $userName = $info.User
            $ppid = $info.PPID
        }
        if ([string]::IsNullOrEmpty($userName)) {
            try { $userName = $env:USERNAME } catch {}
        }
        if ($p.SessionId -gt 0) { $tty = "con$($p.SessionId)" }
    } elseif ($IsMacOS) {
        if ($null -ne $script:MacPsLookup -and $script:MacPsLookup.ContainsKey($pid)) {
            $info = $script:MacPsLookup[$pid]
            $userName = $info.User
            $ppid = $info.PPID
            $tty = $info.TTY
        }
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

    $startStr = if ($null -ne $Entry.Start) { $Entry.Start.ToString('HH:mm') } else { '?' }
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
    if ($Arguments -contains '--help') { return Show-BashHelp 'ps' }

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

            if (-not $showAll -and -not $bsdAux -and $null -eq $filterPid -and $null -eq $filterUser) {
                if ($fullFormat -or $null -ne $customFormat) {
                    # ps -f or ps -o: show current user's processes (no TTY restriction)
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

        # Windows: batch-fetch cmdline/user/ppid for all processes in one CIM call
        if ($IsWindows -and $procs) {
            $script:WinCimLookup = [System.Collections.Generic.Dictionary[int,PSCustomObject]]::new()
            try {
                $cimProcs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
                foreach ($cim in $cimProcs) {
                    $cimUser = ''
                    try { $cimUser = $cim.GetOwner().User } catch {}
                    $script:WinCimLookup[[int]$cim.ProcessId] = [PSCustomObject]@{
                        CommandLine = $cim.CommandLine
                        User        = $cimUser
                        PPID        = if ($cim.ParentProcessId) { [int]$cim.ParentProcessId } else { 0 }
                    }
                }
            } catch {}
        }

        # macOS: batch-fetch user/ppid/tty for all PIDs in one /bin/ps call
        if ($IsMacOS -and $procs) {
            $script:MacPsLookup = [System.Collections.Generic.Dictionary[int,PSCustomObject]]::new()
            try {
                $psOutput = & /bin/ps -axo pid=,user=,ppid=,tty= 2>$null
                foreach ($line in $psOutput) {
                    $parts = $line.Trim() -split '\s+', 4
                    if ($parts.Count -ge 4 -and $parts[0] -match '^\d+$') {
                        $script:MacPsLookup[[int]$parts[0]] = [PSCustomObject]@{
                            User = $parts[1]
                            PPID = [int]$parts[2]
                            TTY  = if ($parts[3] -eq '??') { '?' } else { $parts[3] }
                        }
                    }
                }
            } catch {}
        }

        if ($procs) {
            foreach ($p in $procs) {
                $entry = Get-DotNetProcEntry -Process $p
                if ($null -eq $entry) { continue }

                if (-not $showAll -and -not $bsdAux -and $null -eq $filterPid -and $null -eq $filterUser) {
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
        Set-BashDisplayProperty $psEntry
    }
}

# --- sed Command ---

function Invoke-BashSed {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'sed' }

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
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
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
    if ($Arguments -contains '--help') { return Show-BashHelp 'awk' }

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
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) {
                $lines.Add($l)
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
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
    if ($Arguments -contains '--help') { return Show-BashHelp 'tr' }

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
    if ($Arguments -contains '--help') { return Show-BashHelp 'uniq' }

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
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
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
    if ($Arguments -contains '--help') { return Show-BashHelp 'rev' }

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
        foreach ($filePath in (Resolve-BashGlob -Paths $Arguments)) {
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
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) {
                $lines.Add($l)
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
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
    if ($Arguments -contains '--help') { return Show-BashHelp 'diff' }

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
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) {
                $lines.Add($l)
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
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
    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
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

# --- tee Command ---

function Invoke-BashTee {
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

    # Write to each file
    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
        $parentDir = Split-Path -Parent $filePath
        if ($parentDir -and -not (Test-Path -LiteralPath $parentDir)) {
            Write-Error -Message "tee: ${filePath}: No such file or directory" -ErrorAction Continue
            continue
        }
        if ($append -and (Test-Path -LiteralPath $filePath)) {
            $existing = [System.IO.File]::ReadAllText($filePath)
            [System.IO.File]::WriteAllText($filePath, $existing + $textContent)
        } else {
            [System.IO.File]::WriteAllText($filePath, $textContent)
        }
    }

    # Pass through original objects
    foreach ($item in $pipelineInput) {
        $item
    }
}

# --- xargs Command ---

function Invoke-BashXargs {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'xargs' }

    $replaceStr = $null
    $maxArgs = 0
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

        if ($arg -ceq '-I') {
            $i++
            if ($i -lt $Arguments.Count) {
                $replaceStr = $Arguments[$i]
            }
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
        Write-Error -Message "xargs: no command specified" -ErrorAction Continue
        return
    }

    $cmd = $operands[0]
    $cmdArgs = @()
    if ($operands.Count -gt 1) {
        $cmdArgs = @($operands[1..($operands.Count - 1)])
    }

    # Collect input lines: split each BashText by newlines for individual args
    $inputLines = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $pipelineInput) {
        $text = Get-BashText -InputObject $item
        $splitLines = $text -split "`n"
        foreach ($line in $splitLines) {
            if ($line -ne '') {
                $inputLines.Add($line)
            }
        }
    }

    if ($null -ne $replaceStr) {
        # Replacement mode: run command once per input line
        foreach ($line in $inputLines) {
            $replacedArgs = @($cmdArgs | ForEach-Object { $_ -replace [regex]::Escape($replaceStr), $line })
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
    param([object]$Data, [string]$Filter)

    $filter = $Filter.Trim()
    if ($filter -eq '') { return @(, $Data) }

    # Handle pipe: split on top-level | (not inside parens/brackets/strings)
    [string[]]$pipeSegments = @(Split-JqPipe -Filter $filter)
    if ($pipeSegments.Count -gt 1) {
        $current = @(, $Data)
        foreach ($seg in $pipeSegments) {
            $next = @()
            foreach ($item in $current) {
                $next += @(Invoke-JqFilter -Data $item -Filter $seg)
            }
            $current = $next
        }
        return $current
    }

    # Handle comma (multiple outputs) at top level
    [string[]]$commaSegments = @(Split-JqComma -Filter $filter)
    if ($commaSegments.Count -gt 1) {
        $results = @()
        foreach ($seg in $commaSegments) {
            $results += @(Invoke-JqFilter -Data $Data -Filter $seg.Trim())
        }
        return $results
    }

    # Identity
    if ($filter -eq '.') { return @(, $Data) }

    # Array construction: [expr]
    if ($filter.StartsWith('[') -and (Get-JqMatchingBracket -S $filter -Open '[' -Close ']' -Start 0) -eq ($filter.Length - 1)) {
        $inner = $filter.Substring(1, $filter.Length - 2)
        $items = @(Invoke-JqFilter -Data $Data -Filter $inner)
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
                $vals = @(Invoke-JqFilter -Data $Data -Filter $valExpr)
                $result[$keyPart] = if ($vals.Count -eq 1) { $vals[0] } else { $vals }
            } else {
                # Shorthand: just a name means {name: .name}
                $keyPart = $pair.TrimStart('.')
                $vals = @(Invoke-JqFilter -Data $Data -Filter ".$keyPart")
                $result[$keyPart] = if ($vals.Count -eq 1) { $vals[0] } else { $vals }
            }
        }
        return @(, $result)
    }

    # String literal with interpolation: "...\(expr)..."
    if ($filter.StartsWith('"') -and $filter.EndsWith('"')) {
        $strContent = $filter.Substring(1, $filter.Length - 2)
        $result = Resolve-JqStringInterpolation -S $strContent -Data $Data
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
                $items += @(Invoke-JqFilter -Data $elem -Filter $innerExpr)
            }
        }
        return @(, $items)
    }

    # select(expr)
    if ($filter -match '^select\((.+)\)$') {
        $expr = $Matches[1]
        $result = Invoke-JqSelect -Data $Data -Expr $expr
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

            $next = @()
            if ($inner -eq '') {
                # .[] iterate
                foreach ($item in $current) {
                    if ($item -is [array] -or $item -is [System.Collections.IList]) {
                        foreach ($elem in $item) { $next += @(, $elem) }
                    } elseif ($item -is [System.Collections.IDictionary]) {
                        foreach ($val in $item.Values) { $next += @(, $val) }
                    } elseif ($item -is [PSCustomObject]) {
                        foreach ($prop in $item.PSObject.Properties) {
                            if ($prop.Name -ne 'PSTypeName') { $next += @(, $prop.Value) }
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
                            $next += @(, $item[$idx])
                        } else {
                            $next += @(, $null)
                        }
                    }
                }
            }
            $current = $next
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

        $next = @()
        foreach ($item in $current) {
            $val = $null
            if ($item -is [System.Collections.IDictionary]) {
                if ($item.Contains($fieldName)) { $val = $item[$fieldName] }
            } elseif ($item -is [PSCustomObject]) {
                $prop = $item.PSObject.Properties[$fieldName]
                if ($null -ne $prop) { $val = $prop.Value }
            }
            $next += @(, $val)
        }
        $current = $next
    }

    return $current
}

function Invoke-JqSelect {
    param([object]$Data, [string]$Expr)

    # Parse comparison: . op value, .field op value
    $ops = @('>=', '<=', '!=', '==', '>', '<')
    foreach ($op in $ops) {
        $opIdx = Find-JqTopLevelStr -S $Expr -Sub $op
        if ($opIdx -ge 0) {
            $leftExpr = $Expr.Substring(0, $opIdx).Trim()
            $rightExpr = $Expr.Substring($opIdx + $op.Length).Trim()

            $leftVals = @(Invoke-JqFilter -Data $Data -Filter $leftExpr)
            $rightVals = @(Invoke-JqFilter -Data $Data -Filter $rightExpr)
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
    $vals = @(Invoke-JqFilter -Data $Data -Filter $Expr)
    if ($vals.Count -eq 0) { return $false }
    $val = $vals[0]
    return ($null -ne $val) -and ($val -ne $false)
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
    param([string]$S, [object]$Data)
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
                $vals = @(Invoke-JqFilter -Data $Data -Filter $expr)
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
                Write-Error "jq: $file`: No such file or directory" -ErrorAction Continue
                return
            }
            $jsonTexts.Add((Get-Content -LiteralPath $resolved -Raw))
        }
    } else {
        # Pipeline input
        $textParts = [System.Text.StringBuilder]::new()
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $textParts.Append($text) | Out-Null
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
        $parsed = $jsonText | ConvertFrom-Json -AsHashtable -ErrorAction Stop
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
            Write-Error "date: '$refFile': No such file or directory" -ErrorAction Continue
            return
        }
        $mtime = (Get-Item -LiteralPath $resolved).LastWriteTime
        [System.DateTimeOffset]::new($mtime)
    } elseif ($null -ne $dateString) {
        [System.DateTimeOffset]::Parse($dateString, [System.Globalization.CultureInfo]::InvariantCulture)
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
                'm' { $sb.Append($DTO.ToString('MM', $ci))   | Out-Null }
                'd' { $sb.Append($DTO.ToString('dd', $ci))   | Out-Null }
                'H' { $sb.Append($DTO.ToString('HH', $ci))   | Out-Null }
                'M' { $sb.Append($DTO.ToString('mm', $ci))   | Out-Null }
                'S' { $sb.Append($DTO.ToString('ss', $ci))   | Out-Null }
                's' { $sb.Append([string]$DTO.ToUnixTimeSeconds()) | Out-Null }
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

    # Generate values
    $values = [System.Collections.Generic.List[string]]::new()
    $index = 0
    $current = $first

    $ascending = $increment -gt 0
    while (($ascending -and $current -le ($last + [double]1e-9)) -or
           (-not $ascending -and $current -ge ($last - [double]1e-9))) {
        $formatted = if ($isInteger) {
            $intVal = [long][System.Math]::Round($current)
            if ($equalWidth -and $padWidth -gt 0) {
                $intVal.ToString().PadLeft($padWidth, '0')
            } else {
                [string]$intVal
            }
        } else {
            $current.ToString("F$decPlaces", [System.Globalization.CultureInfo]::InvariantCulture)
        }
        $values.Add($formatted)
        $index++
        $current = $first + ($increment * $index)
    }

    # Output
    if ($null -ne $separator) {
        $text = $values -join $separator
        New-BashObject -BashText $text
    } else {
        for ($j = 0; $j -lt $values.Count; $j++) {
            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.SeqOutput'
                Value      = if ($isInteger) { [long][System.Math]::Round([double]$values[$j]) } else { [double]$values[$j] }
                Index      = $j
                BashText   = $values[$j]
            }
            Set-BashDisplayProperty $obj
        }
    }
}

# --- Expr ---

function Invoke-BashExpr {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'expr' }

    if ($Arguments.Count -eq 0) {
        Write-Error 'expr: missing operand' -ErrorAction Continue
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
                    if ($r -eq 0) { Write-Error 'expr: division by zero' -ErrorAction Continue; return }
                    [string]([long][System.Math]::Truncate($l / $r))
                }
                '%'  {
                    if ($r -eq 0) { Write-Error 'expr: division by zero' -ErrorAction Continue; return }
                    [string]($l % $r)
                }
                '<'  { if ($l -lt $r) { '1' } else { '0' } }
                '<=' { if ($l -le $r) { '1' } else { '0' } }
                '='  { if ($l -eq $r) { '1' } else { '0' } }
                '!=' { if ($l -ne $r) { '1' } else { '0' } }
                '>=' { if ($l -ge $r) { '1' } else { '0' } }
                '>'  { if ($l -gt $r) { '1' } else { '0' } }
                default {
                    Write-Error "expr: unknown operator '$op'" -ErrorAction Continue
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
                    Write-Error "expr: non-integer argument" -ErrorAction Continue
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

# --- du Command ---

function Invoke-BashDu {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'du' }

    $humanReadable = $false
    $summarize = $false
    $allFiles = $false
    $showTotal = $false
    $maxDepth = [int]::MaxValue
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -cmatch '^-d(\d+)$') {
            $maxDepth = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -eq '-d' -and ($i + 1) -lt $Arguments.Count) {
            $maxDepth = [int]$Arguments[$i + 1]
            $i += 2
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'h' { $humanReadable = $true }
                    's' { $summarize = $true }
                    'a' { $allFiles = $true }
                    'c' { $showTotal = $true }
                    default { }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -eq 0) {
        $operands.Add('.')
    }

    $grandTotal = [long]0

    foreach ($target in $operands) {
        if (-not (Test-Path -LiteralPath $target)) {
            Write-Error -Message "du: cannot access '$target': No such file or directory" -ErrorAction Continue
            continue
        }

        $resolvedRoot = (Resolve-Path -LiteralPath $target).Path
        $rootItem = Get-Item -LiteralPath $resolvedRoot -Force

        if ($rootItem -isnot [System.IO.DirectoryInfo]) {
            $sizeBytes = $rootItem.Length
            $grandTotal += $sizeBytes
            $sizeKb = [long][System.Math]::Ceiling($sizeBytes / 1024)
            $sizeHuman = Format-BashSize -Bytes $sizeBytes
            $displaySize = if ($humanReadable) { $sizeHuman } else { $sizeKb.ToString() }
            $displayPath = $target -replace '\\', '/'

            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.DuEntry'
                Size       = $sizeKb
                SizeBytes  = $sizeBytes
                SizeHuman  = $sizeHuman
                Path       = $displayPath
                Depth      = 0
                IsTotal    = $false
                BashText   = "$displaySize`t$displayPath"
            }
            Set-BashDisplayProperty $obj
            continue
        }

        $rootDepth = ($resolvedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) -split '[\\/]').Count

        # Collect all directories and compute sizes bottom-up
        $allDirs = [System.Collections.Generic.List[System.IO.DirectoryInfo]]::new()
        $allDirs.Add($rootItem)
        try {
            $children = Get-ChildItem -LiteralPath $resolvedRoot -Force -Recurse -Directory -ErrorAction SilentlyContinue
            foreach ($child in $children) { $allDirs.Add($child) }
        } catch { }

        # Calculate size for each directory (files directly inside it)
        $dirSizes = [System.Collections.Generic.Dictionary[string,long]]::new([System.StringComparer]::Ordinal)
        foreach ($dir in $allDirs) {
            $dirFiles = @(Get-ChildItem -LiteralPath $dir.FullName -Force -File -ErrorAction SilentlyContinue)
            $dirSize = [long]0
            foreach ($f in $dirFiles) { $dirSize += $f.Length }
            $dirSizes[$dir.FullName] = $dirSize
        }

        # Accumulate sizes: each directory includes all descendants
        $accumSizes = [System.Collections.Generic.Dictionary[string,long]]::new([System.StringComparer]::Ordinal)
        # Sort directories deepest-first for bottom-up accumulation
        $sortedDirs = $allDirs | Sort-Object { $_.FullName.Length } -Descending
        foreach ($dir in $sortedDirs) {
            $total = $dirSizes[$dir.FullName]
            $subDirs = @(Get-ChildItem -LiteralPath $dir.FullName -Force -Directory -ErrorAction SilentlyContinue)
            foreach ($sd in $subDirs) {
                if ($accumSizes.ContainsKey($sd.FullName)) {
                    $total += $accumSizes[$sd.FullName]
                }
            }
            $accumSizes[$dir.FullName] = $total
        }

        # Build output entries
        $entries = [System.Collections.Generic.List[PSObject]]::new()

        foreach ($dir in $allDirs) {
            $itemDepth = ($dir.FullName -split '[\\/]').Count - $rootDepth
            if ($itemDepth -gt $maxDepth) { continue }
            if ($summarize -and $dir.FullName -ne $resolvedRoot) { continue }

            $sizeBytes = $accumSizes[$dir.FullName]
            $sizeKb = [long][System.Math]::Ceiling($sizeBytes / 1024)
            if ($sizeKb -eq 0 -and $sizeBytes -gt 0) { $sizeKb = 1 }
            $sizeHuman = Format-BashSize -Bytes $sizeBytes
            $displaySize = if ($humanReadable) { $sizeHuman } else { $sizeKb.ToString() }

            $relativePath = $dir.FullName.Substring($resolvedRoot.Length) -replace '\\', '/'
            if ($relativePath.StartsWith('/')) { $relativePath = $relativePath.Substring(1) }
            $normalized = $target -replace '\\', '/'
            $displayPath = if ($relativePath -eq '') { $normalized } else { "$normalized/$relativePath" }

            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.DuEntry'
                Size       = $sizeKb
                SizeBytes  = $sizeBytes
                SizeHuman  = $sizeHuman
                Path       = $displayPath
                Depth      = $itemDepth
                IsTotal    = $false
                BashText   = "$displaySize`t$displayPath"
            }
            Set-BashDisplayProperty $obj | Out-Null
            $entries.Add($obj)
        }

        # Also add individual file entries when -a
        if ($allFiles) {
            $allFileItems = @(Get-ChildItem -LiteralPath $resolvedRoot -Force -Recurse -File -ErrorAction SilentlyContinue)
            foreach ($file in $allFileItems) {
                $fileDepth = ($file.FullName -split '[\\/]').Count - $rootDepth
                if ($fileDepth -gt $maxDepth) { continue }
                if ($summarize) { continue }

                $sizeBytes = $file.Length
                $sizeKb = [long][System.Math]::Ceiling($sizeBytes / 1024)
                if ($sizeKb -eq 0 -and $sizeBytes -gt 0) { $sizeKb = 1 }
                $sizeHuman = Format-BashSize -Bytes $sizeBytes
                $displaySize = if ($humanReadable) { $sizeHuman } else { $sizeKb.ToString() }

                $relativePath = $file.FullName.Substring($resolvedRoot.Length) -replace '\\', '/'
                if ($relativePath.StartsWith('/')) { $relativePath = $relativePath.Substring(1) }
                $normalized = $target -replace '\\', '/'
                $displayPath = if ($relativePath -eq '') { $normalized } else { "$normalized/$relativePath" }

                $obj = [PSCustomObject]@{
                    PSTypeName = 'PsBash.DuEntry'
                    Size       = $sizeKb
                    SizeBytes  = $sizeBytes
                    SizeHuman  = $sizeHuman
                    Path       = $displayPath
                    Depth      = $fileDepth
                    IsTotal    = $false
                    BashText   = "$displaySize`t$displayPath"
                }
                Set-BashDisplayProperty $obj | Out-Null
                $entries.Add($obj)
            }
        }

        # Sort: subdirectories first (deepest first), then root
        $sorted = $entries | Sort-Object { $_.Path }
        foreach ($e in $sorted) { $e }

        $grandTotal += $accumSizes[$resolvedRoot]
    }

    if ($showTotal) {
        $sizeKb = [long][System.Math]::Ceiling($grandTotal / 1024)
        if ($sizeKb -eq 0 -and $grandTotal -gt 0) { $sizeKb = 1 }
        $sizeHuman = Format-BashSize -Bytes $grandTotal
        $displaySize = if ($humanReadable) { $sizeHuman } else { $sizeKb.ToString() }

        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.DuEntry'
            Size       = $sizeKb
            SizeBytes  = $grandTotal
            SizeHuman  = $sizeHuman
            Path       = 'total'
            Depth      = 0
            IsTotal    = $true
            BashText   = "$displaySize`ttotal"
        }
        Set-BashDisplayProperty $obj
    }
}

# --- tree Command ---

function Invoke-BashTree {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'tree' }

    $showAll = $false
    $dirsOnly = $false
    $maxDepth = [int]::MaxValue
    $excludePattern = $null
    $dirsFirst = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -cmatch '^-L(\d+)$') {
            $maxDepth = [int]$Matches[1]
            $i++
            continue
        }
        if ($arg -eq '-L' -and ($i + 1) -lt $Arguments.Count) {
            $maxDepth = [int]$Arguments[$i + 1]
            $i += 2
            continue
        }
        if ($arg -eq '-I' -and ($i + 1) -lt $Arguments.Count) {
            $excludePattern = $Arguments[$i + 1]
            $i += 2
            continue
        }
        if ($arg -eq '--dirsfirst') {
            $dirsFirst = $true
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'a' { $showAll = $true }
                    'd' { $dirsOnly = $true }
                    default { }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -eq 0) {
        $operands.Add('.')
    }

    $target = $operands[0]
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Error -Message "tree: '$target': No such file or directory" -ErrorAction Continue
        return
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $target).Path
    $rootItem = Get-Item -LiteralPath $resolvedRoot -Force
    $rootName = $rootItem.Name

    # Root entry
    $rootObj = [PSCustomObject]@{
        PSTypeName = 'PsBash.TreeEntry'
        Name       = $rootName
        Path       = ($target -replace '\\', '/')
        Depth      = 0
        IsDirectory = $true
        TreePrefix = ''
        BashText   = $rootName
    }
    Set-BashDisplayProperty $rootObj

    $dirCount = 0
    $fileCount = 0

    # Recursive tree walker
    function Write-TreeLevel {
        param(
            [string]$DirPath,
            [int]$CurrentDepth,
            [string]$Prefix
        )

        if ($CurrentDepth -gt $maxDepth) { return }

        $items = @(Get-ChildItem -LiteralPath $DirPath -Force -ErrorAction SilentlyContinue)

        # Filter dotfiles unless -a
        if (-not $showAll) {
            $items = @($items | Where-Object { -not $_.Name.StartsWith('.') })
        }

        # Filter excluded pattern
        if ($null -ne $excludePattern) {
            $items = @($items | Where-Object { $_.Name -notlike $excludePattern })
        }

        # Filter files if -d
        if ($dirsOnly) {
            $items = @($items | Where-Object { $_ -is [System.IO.DirectoryInfo] })
        }

        # Sort: dirsfirst if requested, then alphabetical
        if ($dirsFirst) {
            $items = @($items | Sort-Object @{Expression={if ($_ -is [System.IO.DirectoryInfo]) { 0 } else { 1 }}}, Name)
        } else {
            $items = @($items | Sort-Object Name)
        }

        for ($idx = 0; $idx -lt $items.Count; $idx++) {
            $item = $items[$idx]
            $isLast = ($idx -eq ($items.Count - 1))
            $connector = if ($isLast) { [char]0x2514 + [string]([char]0x2500) + [string]([char]0x2500) + ' ' } else { [char]0x251C + [string]([char]0x2500) + [string]([char]0x2500) + ' ' }
            $childPrefix = if ($isLast) { $Prefix + '    ' } else { $Prefix + [char]0x2502 + '   ' }

            $isDir = $item -is [System.IO.DirectoryInfo]
            if ($isDir) {
                Set-Variable -Name dirCount -Value ($dirCount + 1) -Scope 2
            } else {
                Set-Variable -Name fileCount -Value ($fileCount + 1) -Scope 2
            }

            $relativePath = $item.FullName.Substring($resolvedRoot.Length) -replace '\\', '/'
            if ($relativePath.StartsWith('/')) { $relativePath = $relativePath.Substring(1) }

            $treePrefix = "$Prefix$connector"
            $bashText = "$Prefix$connector$($item.Name)"

            $entryObj = [PSCustomObject]@{
                PSTypeName  = 'PsBash.TreeEntry'
                Name        = $item.Name
                Path        = $relativePath
                Depth       = $CurrentDepth
                IsDirectory = $isDir
                TreePrefix  = $treePrefix
                BashText    = $bashText
            }
            Set-BashDisplayProperty $entryObj

            if ($isDir) {
                Write-TreeLevel -DirPath $item.FullName -CurrentDepth ($CurrentDepth + 1) -Prefix $childPrefix
            }
        }
    }

    Write-TreeLevel -DirPath $resolvedRoot -CurrentDepth 1 -Prefix ''

    # Summary line
    $dirLabel = if ($dirCount -eq 1) { 'directory' } else { 'directories' }
    $fileLabel = if ($fileCount -eq 1) { 'file' } else { 'files' }
    $summaryText = if ($dirsOnly) {
        "$dirCount $dirLabel"
    } else {
        "$dirCount $dirLabel, $fileCount $fileLabel"
    }

    $summaryObj = [PSCustomObject]@{
        PSTypeName  = 'PsBash.TreeEntry'
        Name        = ''
        Path        = ''
        Depth       = 0
        IsDirectory = $false
        TreePrefix  = ''
        BashText    = $summaryText
    }
    Set-BashDisplayProperty $summaryObj
}

# --- env / printenv ---

function Invoke-BashEnv {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'env' }

    if ($Arguments.Count -gt 0) {
        $varName = $Arguments[0]
        $val = [System.Environment]::GetEnvironmentVariable($varName)
        if ($null -eq $val) {
            Write-Error "env: '$varName': not set" -ErrorAction Continue
            return
        }
        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.EnvEntry'
            Name       = $varName
            Value      = $val
            BashText   = "$varName=$val"
        }
        return (Set-BashDisplayProperty $obj)
    }

    $entries = [System.Environment]::GetEnvironmentVariables()
    foreach ($key in ($entries.Keys | Sort-Object)) {
        $val = $entries[$key]
        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.EnvEntry'
            Name       = [string]$key
            Value      = [string]$val
            BashText   = "$key=$val"
        }
        Set-BashDisplayProperty $obj
    }
}

# --- basename ---

function Invoke-BashBasename {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'basename' }

    $suffix = $null
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -ceq '-s' -or $arg -ceq '--suffix') {
            $i++
            if ($i -lt $Arguments.Count) { $suffix = $Arguments[$i] }
            $i++
            continue
        }

        if ($arg -cmatch '^--suffix=(.+)$') {
            $suffix = $Matches[1]
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    foreach ($path in $operands) {
        $normalized = $path -replace '\\', '/'
        $normalized = $normalized.TrimEnd('/')
        if ($normalized -eq '') { $normalized = '/' }

        $slashIdx = $normalized.LastIndexOf('/')
        $name = if ($slashIdx -ge 0) { $normalized.Substring($slashIdx + 1) } else { $normalized }
        if ($name -eq '') { $name = '/' }

        if ($null -ne $suffix -and $name.Length -gt $suffix.Length -and $name.EndsWith($suffix)) {
            $name = $name.Substring(0, $name.Length - $suffix.Length)
        }

        $obj = New-BashObject -BashText $name -TypeName 'PsBash.TextOutput'
        $obj
    }
}

# --- dirname ---

function Invoke-BashDirname {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'dirname' }

    foreach ($path in $Arguments) {
        $normalized = $path -replace '\\', '/'
        $normalized = $normalized.TrimEnd('/')
        if ($normalized -eq '') {
            $dir = '/'
        } else {
            $slashIdx = $normalized.LastIndexOf('/')
            if ($slashIdx -lt 0) {
                $dir = '.'
            } elseif ($slashIdx -eq 0) {
                $dir = '/'
            } else {
                $dir = $normalized.Substring(0, $slashIdx)
            }
        }

        $obj = New-BashObject -BashText $dir -TypeName 'PsBash.TextOutput'
        $obj
    }
}

# --- pwd ---

function Invoke-BashPwd {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'pwd' }

    $physical = $false
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-P') { $physical = $true }
    }

    $location = if ($physical) {
        [System.IO.Directory]::GetCurrentDirectory()
    } else {
        (Get-Location).Path
    }

    $location = $location -replace '\\', '/'
    New-BashObject -BashText $location -TypeName 'PsBash.TextOutput'
}

# --- hostname ---

function Invoke-BashHostname {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'hostname' }
    $name = [System.Net.Dns]::GetHostName()
    New-BashObject -BashText $name -TypeName 'PsBash.TextOutput'
}

# --- whoami ---

function Invoke-BashWhoami {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'whoami' }
    $name = [System.Environment]::UserName
    New-BashObject -BashText $name -TypeName 'PsBash.TextOutput'
}

# --- fold Command ---

function Invoke-BashFold {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'fold' }

    $width = 80
    $breakSpaces = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -cmatch '^-w(\d+)$') {
            $width = [int]$Matches[1]; $i++; continue
        }
        if ($arg -eq '-w' -and ($i + 1) -lt $Arguments.Count) {
            $width = [int]$Arguments[$i + 1]; $i += 2; continue
        }
        if ($arg -match '^--width=(.+)$') {
            $width = [int]$Matches[1]; $i++; continue
        }
        if ($arg -ceq '-s' -or $arg -eq '--spaces') {
            $breakSpaces = $true; $i++; continue
        }
        if ($arg -ceq '-b' -or $arg -eq '--bytes') {
            $i++; continue  # bytes mode is default for ASCII
        }
        $operands.Add($arg); $i++
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) { $lines.Add($l) }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "fold: ${filePath}: No such file or directory" -ErrorAction Continue
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
            foreach ($l in $rawText.Split("`n")) { $lines.Add($l) }
        }
    }

    foreach ($line in $lines) {
        if ($line.Length -le $width) {
            New-BashObject -BashText $line
            continue
        }
        $pos = 0
        while ($pos -lt $line.Length) {
            $remaining = $line.Length - $pos
            if ($remaining -le $width) {
                New-BashObject -BashText $line.Substring($pos)
                break
            }
            $chunkEnd = $pos + $width
            if ($breakSpaces) {
                $spaceIdx = $line.LastIndexOf(' ', $chunkEnd - 1, $width)
                if ($spaceIdx -gt $pos) {
                    $chunkEnd = $spaceIdx + 1
                }
            }
            New-BashObject -BashText $line.Substring($pos, $chunkEnd - $pos)
            $pos = $chunkEnd
        }
    }
}

# --- expand Command ---

function Invoke-BashExpand {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'expand' }

    $tabWidth = 8
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -cmatch '^-t(\d+)$') {
            $tabWidth = [int]$Matches[1]; $i++; continue
        }
        if ($arg -eq '-t' -and ($i + 1) -lt $Arguments.Count) {
            $tabWidth = [int]$Arguments[$i + 1]; $i += 2; continue
        }
        if ($arg -match '^--tabs=(.+)$') {
            $tabWidth = [int]$Matches[1]; $i++; continue
        }
        $operands.Add($arg); $i++
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) { $lines.Add($l) }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "expand: ${filePath}: No such file or directory" -ErrorAction Continue
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
            foreach ($l in $rawText.Split("`n")) { $lines.Add($l) }
        }
    }

    foreach ($line in $lines) {
        $sb = [System.Text.StringBuilder]::new()
        $col = 0
        foreach ($ch in $line.ToCharArray()) {
            if ($ch -eq "`t") {
                $spaces = $tabWidth - ($col % $tabWidth)
                [void]$sb.Append(' ', $spaces)
                $col += $spaces
            } else {
                [void]$sb.Append($ch)
                $col++
            }
        }
        New-BashObject -BashText $sb.ToString()
    }
}

# --- unexpand Command ---

function Invoke-BashUnexpand {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'unexpand' }

    $tabWidth = 8
    $allSpaces = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -cmatch '^-t(\d+)$') {
            $tabWidth = [int]$Matches[1]; $i++; continue
        }
        if ($arg -eq '-t' -and ($i + 1) -lt $Arguments.Count) {
            $tabWidth = [int]$Arguments[$i + 1]; $i += 2; continue
        }
        if ($arg -match '^--tabs=(.+)$') {
            $tabWidth = [int]$Matches[1]; $i++; continue
        }
        if ($arg -ceq '-a' -or $arg -eq '--all') {
            $allSpaces = $true; $i++; continue
        }
        if ($arg -eq '--first-only') {
            $allSpaces = $false; $i++; continue
        }
        $operands.Add($arg); $i++
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) { $lines.Add($l) }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "unexpand: ${filePath}: No such file or directory" -ErrorAction Continue
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
            foreach ($l in $rawText.Split("`n")) { $lines.Add($l) }
        }
    }

    foreach ($line in $lines) {
        if ($allSpaces) {
            $sb = [System.Text.StringBuilder]::new()
            $col = 0
            $spaceRun = 0
            foreach ($ch in $line.ToCharArray()) {
                if ($ch -eq ' ') {
                    $spaceRun++
                    $col++
                    if (($col % $tabWidth) -eq 0 -and $spaceRun -ge 2) {
                        [void]$sb.Append("`t")
                        $spaceRun = 0
                    }
                } else {
                    if ($spaceRun -gt 0) {
                        [void]$sb.Append(' ', $spaceRun)
                        $spaceRun = 0
                    }
                    [void]$sb.Append($ch)
                    $col++
                }
            }
            if ($spaceRun -gt 0) { [void]$sb.Append(' ', $spaceRun) }
            New-BashObject -BashText $sb.ToString()
        } else {
            # Leading spaces only
            $leadingSpaces = 0
            while ($leadingSpaces -lt $line.Length -and $line[$leadingSpaces] -eq ' ') {
                $leadingSpaces++
            }
            $tabs = [System.Math]::Floor($leadingSpaces / $tabWidth)
            $remainSpaces = $leadingSpaces % $tabWidth
            $prefix = ("`t" * $tabs) + (' ' * $remainSpaces)
            New-BashObject -BashText ($prefix + $line.Substring($leadingSpaces))
        }
    }
}

# --- strings Command ---

function Invoke-BashStrings {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'strings' }

    $minLength = 4
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -eq '-n' -and ($i + 1) -lt $Arguments.Count) {
            $minLength = [int]$Arguments[$i + 1]; $i += 2; continue
        }
        if ($arg -match '^--bytes=(.+)$') {
            $minLength = [int]$Matches[1]; $i++; continue
        }
        $operands.Add($arg); $i++
    }

    $content = ''
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        $parts = [System.Collections.Generic.List[string]]::new()
        foreach ($item in $pipelineInput) {
            $parts.Add((Get-BashText -InputObject $item))
        }
        $content = $parts -join "`n"
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "strings: ${filePath}: No such file or directory" -ErrorAction Continue
                continue
            }
            $content += [System.IO.File]::ReadAllText($filePath)
        }
    }

    $pattern = "[\x20-\x7E]{$minLength,}"
    $matches = [regex]::Matches($content, $pattern)
    foreach ($m in $matches) {
        New-BashObject -BashText $m.Value
    }
}

# --- split Command ---

function Invoke-BashSplit {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'split' }

    $lineCount = $null
    $numericSuffix = $false
    $suffixLength = 2
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -eq '-l' -and ($i + 1) -lt $Arguments.Count) {
            $lineCount = [int]$Arguments[$i + 1]; $i += 2; continue
        }
        if ($arg -match '^--lines=(.+)$') {
            $lineCount = [int]$Matches[1]; $i++; continue
        }
        if ($arg -ceq '-d' -or $arg -eq '--numeric-suffixes') {
            $numericSuffix = $true; $i++; continue
        }
        if ($arg -eq '-a' -and ($i + 1) -lt $Arguments.Count) {
            $suffixLength = [int]$Arguments[$i + 1]; $i += 2; continue
        }
        if ($arg -match '^--suffix-length=(.+)$') {
            $suffixLength = [int]$Matches[1]; $i++; continue
        }
        $operands.Add($arg); $i++
    }

    if (-not $lineCount) { $lineCount = 1000 }

    $lines = [System.Collections.Generic.List[string]]::new()
    $prefix = 'x'

    if ($operands.Count -ge 1) {
        $filePath = $operands[0]
        if ($filePath -eq '-') {
            foreach ($item in $pipelineInput) {
                $text = Get-BashText -InputObject $item
                $text = $text -replace "`n$", ''
                foreach ($l in $text.Split("`n")) { $lines.Add($l) }
            }
        } else {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "split: cannot open '${filePath}' for reading: No such file or directory" -ErrorAction Continue
                $global:LASTEXITCODE = 1
                return
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
            foreach ($l in $rawText.Split("`n")) { $lines.Add($l) }
        }
        if ($operands.Count -ge 2) { $prefix = $operands[1] }
    } elseif ($pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) { $lines.Add($l) }
        }
    } else {
        Write-Error -Message "split: missing operand" -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }

    $chunkIndex = 0
    for ($start = 0; $start -lt $lines.Count; $start += $lineCount) {
        $end = [System.Math]::Min($start + $lineCount, $lines.Count)
        $chunk = $lines.GetRange($start, $end - $start)
        if ($numericSuffix) {
            $suffix = $chunkIndex.ToString().PadLeft($suffixLength, '0')
        } else {
            $suffix = ''
            $idx = [int]$chunkIndex
            for ($si = 0; $si -lt $suffixLength; $si++) {
                $charCode = [int]([int][char]'a' + ($idx % 26))
                $suffix = [char]$charCode + $suffix
                $idx = [int][System.Math]::Floor($idx / 26)
            }
        }
        $outName = "${prefix}${suffix}"
        $outPath = if ([System.IO.Path]::IsPathRooted($outName)) { $outName } else { Join-Path $PWD $outName }
        $content = ($chunk -join "`n") + "`n"
        [System.IO.File]::WriteAllText($outPath, $content)
        $chunkIndex++
    }
}

# --- tac Command ---

function Invoke-BashTac {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'tac' }

    $separator = $null
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -eq '-s' -and ($i + 1) -lt $Arguments.Count) {
            $separator = $Arguments[$i + 1]; $i += 2; continue
        }
        if ($arg -match '^--separator=(.+)$') {
            $separator = $Matches[1]; $i++; continue
        }
        $operands.Add($arg); $i++
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $text = $text -replace "`n$", ''
            foreach ($l in $text.Split("`n")) { $lines.Add($l) }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Error -Message "tac: ${filePath}: No such file or directory" -ErrorAction Continue
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
            foreach ($l in $rawText.Split("`n")) { $lines.Add($l) }
        }
    }

    if ($separator) {
        $all = $lines -join "`n"
        $chunks = $all.Split($separator)
        [System.Array]::Reverse($chunks)
        foreach ($chunk in $chunks) {
            New-BashObject -BashText $chunk
        }
    } else {
        $lines.Reverse()
        foreach ($line in $lines) {
            New-BashObject -BashText $line
        }
    }
}

# --- base64 Command ---

function Invoke-BashBase64 {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'base64' }

    $decode = $false
    $wrapCol = 76
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-d' -or $arg -eq '--decode') {
            $decode = $true; $i++; continue
        }
        if ($arg -ceq '-w' -and ($i + 1) -lt $Arguments.Count) {
            $wrapCol = [int]$Arguments[$i + 1]; $i += 2; continue
        }
        if ($arg -match '^--wrap=(.+)$') {
            $wrapCol = [int]$Matches[1]; $i++; continue
        }
        $operands.Add($arg); $i++
    }

    $rawBytes = $null
    $rawText = $null

    if ($operands.Count -gt 0) {
        $filePath = $operands[0]
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-Error -Message "base64: ${filePath}: No such file or directory" -ErrorAction Continue
            $global:LASTEXITCODE = 1
            return
        }
        if ($decode) {
            $rawText = [System.IO.File]::ReadAllText($filePath).Trim()
        } else {
            $rawBytes = [System.IO.File]::ReadAllBytes($filePath)
        }
    } elseif ($pipelineInput.Count -gt 0) {
        $parts = [System.Collections.Generic.List[string]]::new()
        foreach ($item in $pipelineInput) {
            $parts.Add((Get-BashText -InputObject $item))
        }
        $text = $parts -join "`n"
        if (-not $text.EndsWith("`n")) { $text += "`n" }
        if ($decode) {
            $rawText = $text.Trim()
        } else {
            $rawBytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        }
    } else {
        return
    }

    if ($decode) {
        $decoded = [System.Convert]::FromBase64String($rawText)
        $output = [System.Text.Encoding]::UTF8.GetString($decoded)
        $output = $output -replace "`n$", ''
        New-BashObject -BashText $output
    } else {
        $encoded = [System.Convert]::ToBase64String($rawBytes)
        if ($wrapCol -gt 0) {
            $wrapped = [System.Text.StringBuilder]::new()
            for ($c = 0; $c -lt $encoded.Length; $c += $wrapCol) {
                $len = [System.Math]::Min($wrapCol, $encoded.Length - $c)
                [void]$wrapped.AppendLine($encoded.Substring($c, $len))
            }
            $output = $wrapped.ToString().TrimEnd("`r", "`n")
        } else {
            $output = $encoded
        }
        New-BashObject -BashText $output
    }
}

# --- Checksum Helper ---

function Invoke-BashChecksum {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Algorithm,
        [Parameter(Mandatory)][string]$CommandName,
        [string[]]$Arguments,
        [object[]]$PipelineInput
    )

    $operands = [System.Collections.Generic.List[string]]::new()
    $i = 0
    while ($i -lt $Arguments.Count) {
        $operands.Add($Arguments[$i]); $i++
    }

    $hasher = switch ($Algorithm) {
        'MD5'    { [System.Security.Cryptography.MD5]::Create() }
        'SHA1'   { [System.Security.Cryptography.SHA1]::Create() }
        'SHA256' { [System.Security.Cryptography.SHA256]::Create() }
    }

    try {
        if ($operands.Count -gt 0) {
            foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
                if (-not (Test-Path -LiteralPath $filePath)) {
                    Write-Error -Message "${CommandName}: ${filePath}: No such file or directory" -ErrorAction Continue
                    continue
                }
                $bytes = [System.IO.File]::ReadAllBytes($filePath)
                $hashBytes = $hasher.ComputeHash($bytes)
                $hex = [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLower()
                $obj = [PSCustomObject]@{
                    PSTypeName = 'PsBash.TextOutput'
                    BashText   = "$hex  $filePath"
                    Hash       = $hex
                    FileName   = $filePath
                    Algorithm  = $Algorithm
                }
                Set-BashDisplayProperty $obj
            }
        } elseif ($PipelineInput.Count -gt 0) {
            $parts = [System.Collections.Generic.List[string]]::new()
            foreach ($item in $PipelineInput) {
                $parts.Add((Get-BashText -InputObject $item))
            }
            $text = ($parts -join "`n") + "`n"
            $textBytes = [System.Text.Encoding]::UTF8.GetBytes($text)
            $hashBytes = $hasher.ComputeHash($textBytes)
            $hex = [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLower()
            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.TextOutput'
                BashText   = "$hex  -"
                Hash       = $hex
                FileName   = '-'
                Algorithm  = $Algorithm
            }
            Set-BashDisplayProperty $obj
        }
    } finally {
        $hasher.Dispose()
    }
}

# --- md5sum Command ---

function Invoke-BashMd5sum {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'md5sum' }
    Invoke-BashChecksum -Algorithm 'MD5' -CommandName 'md5sum' -Arguments $Arguments -PipelineInput $pipelineInput
}

# --- sha1sum Command ---

function Invoke-BashSha1sum {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'sha1sum' }
    Invoke-BashChecksum -Algorithm 'SHA1' -CommandName 'sha1sum' -Arguments $Arguments -PipelineInput $pipelineInput
}

# --- sha256sum Command ---

function Invoke-BashSha256sum {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'sha256sum' }
    Invoke-BashChecksum -Algorithm 'SHA256' -CommandName 'sha256sum' -Arguments $Arguments -PipelineInput $pipelineInput
}

# --- file Command ---

function Invoke-BashFile {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'file' }

    $brief = $false
    $mime = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-b' -or $arg -eq '--brief') {
            $brief = $true; $i++; continue
        }
        if ($arg -ceq '-i' -or $arg -eq '--mime') {
            $mime = $true; $i++; continue
        }
        if ($arg -ceq '-L' -or $arg -eq '--dereference') {
            $i++; continue
        }
        $operands.Add($arg); $i++
    }

    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-Error -Message "file: cannot open '${filePath}' (No such file or directory)" -ErrorAction Continue
            continue
        }

        $resolvedPath = (Resolve-Path -LiteralPath $filePath).Path
        $bytes = [byte[]]@()
        try {
            $stream = [System.IO.File]::OpenRead($resolvedPath)
            $buf = [byte[]]::new(16)
            $read = $stream.Read($buf, 0, 16)
            $stream.Close()
            if ($read -gt 0) { $bytes = $buf[0..($read - 1)] }
        } catch {
            $bytes = [byte[]]@()
        }

        $fileType = $null
        $mimeType = 'application/octet-stream'

        if ($bytes.Count -ge 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47) {
            $fileType = 'PNG image data'; $mimeType = 'image/png'
        } elseif ($bytes.Count -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xD8) {
            $fileType = 'JPEG image data'; $mimeType = 'image/jpeg'
        } elseif ($bytes.Count -ge 4 -and $bytes[0] -eq 0x25 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x44 -and $bytes[3] -eq 0x46) {
            $fileType = 'PDF document'; $mimeType = 'application/pdf'
        } elseif ($bytes.Count -ge 4 -and $bytes[0] -eq 0x50 -and $bytes[1] -eq 0x4B -and $bytes[2] -eq 0x03 -and $bytes[3] -eq 0x04) {
            $fileType = 'Zip archive data'; $mimeType = 'application/zip'
        } elseif ($bytes.Count -ge 4 -and $bytes[0] -eq 0x7F -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x4C -and $bytes[3] -eq 0x46) {
            $fileType = 'ELF executable'; $mimeType = 'application/x-executable'
        } elseif ($bytes.Count -ge 4 -and $bytes[0] -eq 0x47 -and $bytes[1] -eq 0x49 -and $bytes[2] -eq 0x46 -and $bytes[3] -eq 0x38) {
            $fileType = 'GIF image data'; $mimeType = 'image/gif'
        } elseif ($bytes.Count -ge 4 -and $bytes[0] -eq 0x52 -and $bytes[1] -eq 0x49 -and $bytes[2] -eq 0x46 -and $bytes[3] -eq 0x46) {
            $fileType = 'RIFF data'; $mimeType = 'application/octet-stream'
        }

        if (-not $fileType) {
            $allText = $true
            $fileBytes = [System.IO.File]::ReadAllBytes($resolvedPath)
            foreach ($b in $fileBytes) {
                if ($b -lt 0x07 -or ($b -gt 0x0D -and $b -lt 0x20 -and $b -ne 0x1B)) {
                    $allText = $false; break
                }
            }
            if ($allText) {
                $fileType = 'ASCII text'; $mimeType = 'text/plain'
            } else {
                $fileType = 'data'; $mimeType = 'application/octet-stream'
            }
        }

        if ($mime) {
            $bashText = if ($brief) { $mimeType } else { "${filePath}: $mimeType" }
        } else {
            $bashText = if ($brief) { $fileType } else { "${filePath}: $fileType" }
        }

        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.TextOutput'
            BashText   = $bashText
            FileName   = $filePath
            FileType   = $fileType
            MimeType   = $mimeType
        }
        Set-BashDisplayProperty $obj
    }
}

# --- rg (ripgrep-style search) ---

function Invoke-BashRg {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'rg' }

    $ignoreCase = $false
    $wordRegexp = $false
    $countOnly = $false
    $filesOnly = $false
    $showLineNumbers = $true
    $onlyMatching = $false
    $invertMatch = $false
    $fixedStrings = $false
    $includeHidden = $false
    $afterContext = 0
    $beforeContext = 0
    $globPattern = $null
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

        if ($arg -eq '-g' -or $arg -eq '--glob') {
            $i++
            if ($i -lt $Arguments.Count) { $globPattern = $Arguments[$i] }
            $i++
            continue
        }

        if ($arg -cmatch '^-g(.+)$') {
            $globPattern = $Matches[1]
            $i++
            continue
        }

        if ($arg -eq '--hidden') {
            $includeHidden = $true
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'i' { $ignoreCase = $true }
                    'w' { $wordRegexp = $true }
                    'c' { $countOnly = $true }
                    'l' { $filesOnly = $true }
                    'n' { $showLineNumbers = $true }
                    'N' { $showLineNumbers = $false }
                    'o' { $onlyMatching = $true }
                    'v' { $invertMatch = $true }
                    'F' { $fixedStrings = $true }
                }
            }
            $i++
            continue
        }

        if ($arg -eq '--ignore-case') { $ignoreCase = $true; $i++; continue }
        if ($arg -eq '--word-regexp') { $wordRegexp = $true; $i++; continue }
        if ($arg -eq '--count') { $countOnly = $true; $i++; continue }
        if ($arg -eq '--files-with-matches') { $filesOnly = $true; $i++; continue }
        if ($arg -eq '--line-number') { $showLineNumbers = $true; $i++; continue }
        if ($arg -eq '--no-line-number') { $showLineNumbers = $false; $i++; continue }
        if ($arg -eq '--only-matching') { $onlyMatching = $true; $i++; continue }
        if ($arg -eq '--invert-match') { $invertMatch = $true; $i++; continue }
        if ($arg -eq '--fixed-strings') { $fixedStrings = $true; $i++; continue }

        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -eq 0) {
        throw 'rg: usage: rg [options] pattern [path ...]'
    }

    $pattern = $operands[0]
    $fileOperands = @(if ($operands.Count -gt 1) { $operands.GetRange(1, $operands.Count - 1) } else { @() })

    if ($fixedStrings) { $pattern = [regex]::Escape($pattern) }
    if ($wordRegexp) { $pattern = "\b${pattern}\b" }

    $regexOpts = [System.Text.RegularExpressions.RegexOptions]::None
    if ($ignoreCase) { $regexOpts = $regexOpts -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase }
    $regex = [regex]::new($pattern, $regexOpts)

    # --- Pipeline mode ---
    if ($pipelineInput.Count -gt 0 -and $fileOperands.Count -eq 0) {
        $matchCount = 0

        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $matchText = $text -replace "`n$", ''
            $isMatch = $regex.IsMatch($matchText)
            if ($invertMatch) { $isMatch = -not $isMatch }

            if ($isMatch) {
                $matchCount++
                if (-not $countOnly) {
                    if ($onlyMatching) {
                        foreach ($m in $regex.Matches($matchText)) {
                            New-BashObject -BashText $m.Value
                        }
                    } else {
                        $item
                    }
                }
            }
        }

        if ($countOnly) {
            New-BashObject -BashText "$matchCount"
        }
        return
    }

    # --- File mode (recursive by default) ---
    $filePaths = [System.Collections.Generic.List[string]]::new()
    $searchTargets = if ($fileOperands.Count -gt 0) { $fileOperands } else { @('.') }

    foreach ($target in $searchTargets) {
        if (-not (Test-Path -LiteralPath $target)) {
            Write-Error -Message "rg: ${target}: No such file or directory" -ErrorAction Continue
            continue
        }

        if (Test-Path -LiteralPath $target -PathType Container) {
            Get-ChildItem -LiteralPath $target -Recurse -File -Force:$includeHidden | ForEach-Object {
                $rel = $_.FullName
                if ($rel -match '[\\/]\.git[\\/]') { return }
                if (-not $includeHidden) {
                    $relFromTarget = $rel.Substring((Resolve-Path -LiteralPath $target).Path.Length)
                    if ($relFromTarget -match '[\\/]\.[^\\/]') { return }
                }
                if ($globPattern) {
                    if (-not ($_.Name -like $globPattern)) { return }
                }
                $filePaths.Add($_.FullName)
            }
        } else {
            $filePaths.Add((Resolve-Path -LiteralPath $target).Path)
        }
    }

    $multipleFiles = $filePaths.Count -gt 1 -or @($searchTargets | Where-Object { Test-Path -LiteralPath $_ -PathType Container }).Count -gt 0
    $matchedFiles = [System.Collections.Generic.List[string]]::new()
    $perFileCounts = [System.Collections.Generic.Dictionary[string,int]]::new()
    $totalMatchCount = 0

    foreach ($filePath in (Resolve-BashGlob -Paths $filePaths)) {
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

            if ($onlyMatching -and $matchIndices.Contains($li)) {
                foreach ($m in $regex.Matches($line)) {
                    $matchText = $m.Value
                    $bashText = if ($multipleFiles -and $showLineNumbers) {
                        "${filePath}:${lineNum}:${matchText}"
                    } elseif ($multipleFiles) {
                        "${filePath}:${matchText}"
                    } elseif ($showLineNumbers) {
                        "${lineNum}:${matchText}"
                    } else {
                        $matchText
                    }
                    $obj = [PSCustomObject]@{
                        PSTypeName = 'PsBash.RgMatch'
                        FileName   = $filePath
                        LineNumber = $lineNum
                        Line       = $line
                        BashText   = $bashText
                    }
                    Set-BashDisplayProperty $obj
                }
                continue
            }

            $bashText = if ($multipleFiles -and $showLineNumbers) {
                "${filePath}:${lineNum}:${line}"
            } elseif ($multipleFiles) {
                "${filePath}:${line}"
            } elseif ($showLineNumbers) {
                "${lineNum}:${line}"
            } else {
                $line
            }

            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.RgMatch'
                FileName   = $filePath
                LineNumber = $lineNum
                Line       = $line
                BashText   = $bashText
            }
            Set-BashDisplayProperty $obj
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
            foreach ($filePath in (Resolve-BashGlob -Paths $filePaths)) {
                New-BashObject -BashText "${filePath}:$($perFileCounts[$filePath])"
            }
        } else {
            New-BashObject -BashText "$totalMatchCount"
        }
    }
}

# --- gzip / gunzip / zcat ---

function Invoke-BashGzip {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'gzip' }

    $decompress = $false
    $toStdout = $false
    $keep = $false
    $force = $false
    $verbose = $false
    $list = $false
    $level = 6
    $operands = [System.Collections.Generic.List[string]]::new()

    # Detect gunzip/zcat invocation via alias
    $invokedAs = $MyInvocation.InvocationName
    if ($invokedAs -eq 'gunzip') { $decompress = $true }
    if ($invokedAs -eq 'zcat') { $decompress = $true; $toStdout = $true }

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -eq '--') { $i++; while ($i -lt $Arguments.Count) { $operands.Add($Arguments[$i]); $i++ }; break }
        if ($arg -eq '--decompress' -or $arg -eq '--uncompress') { $decompress = $true; $i++; continue }
        if ($arg -eq '--stdout' -or $arg -eq '--to-stdout') { $toStdout = $true; $i++; continue }
        if ($arg -eq '--keep') { $keep = $true; $i++; continue }
        if ($arg -eq '--force') { $force = $true; $i++; continue }
        if ($arg -eq '--verbose') { $verbose = $true; $i++; continue }
        if ($arg -eq '--list') { $list = $true; $i++; continue }

        if ($arg -cmatch '^-(\d)$') {
            $level = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'd' { $decompress = $true }
                    'c' { $toStdout = $true }
                    'k' { $keep = $true }
                    'f' { $force = $true }
                    'v' { $verbose = $true }
                    'l' { $list = $true }
                    default {
                        if ($ch -match '\d') { $level = [int][string]$ch }
                    }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -eq 0) {
        Write-Error -Message 'gzip: missing file operand' -ErrorAction Continue
        return
    }

    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-Error -Message "gzip: ${filePath}: No such file or directory" -ErrorAction Continue
            continue
        }

        if ($list) {
            $compressedBytes = [System.IO.File]::ReadAllBytes($filePath)
            $compressedSize = $compressedBytes.Length
            $ms = [System.IO.MemoryStream]::new($compressedBytes)
            try {
                $gs = [System.IO.Compression.GZipStream]::new($ms, [System.IO.Compression.CompressionMode]::Decompress)
                $buf = [System.IO.MemoryStream]::new()
                try { $gs.CopyTo($buf) } finally { $gs.Dispose(); $buf.Dispose() }
                $uncompressedSize = $buf.ToArray().Length
            } finally {
                $ms.Dispose()
            }
            $ratio = if ($uncompressedSize -gt 0) {
                '{0:F1}%' -f ((1.0 - ($compressedSize / $uncompressedSize)) * 100)
            } else { '0.0%' }
            $line = '{0,10} {1,10} {2,6} {3}' -f $compressedSize, $uncompressedSize, $ratio, $filePath
            $obj = [PSCustomObject]@{
                PSTypeName       = 'PsBash.GzipListOutput'
                BashText         = $line
                CompressedSize   = $compressedSize
                UncompressedSize = $uncompressedSize
                Ratio            = $ratio
                FileName         = $filePath
            }
            Set-BashDisplayProperty $obj
            continue
        }

        if ($decompress) {
            $compressedBytes = [System.IO.File]::ReadAllBytes($filePath)
            $ms = [System.IO.MemoryStream]::new($compressedBytes)
            $outBytes = $null
            try {
                $gs = [System.IO.Compression.GZipStream]::new($ms, [System.IO.Compression.CompressionMode]::Decompress)
                $buf = [System.IO.MemoryStream]::new()
                try { $gs.CopyTo($buf); $outBytes = $buf.ToArray() } finally { $gs.Dispose(); $buf.Dispose() }
            } finally {
                $ms.Dispose()
            }

            if ($toStdout) {
                $text = [System.Text.Encoding]::UTF8.GetString($outBytes)
                New-BashObject -BashText $text
            } else {
                $outPath = $filePath -replace '\.gz$', ''
                [System.IO.File]::WriteAllBytes($outPath, $outBytes)
                if (-not $keep) { Remove-Item -LiteralPath $filePath -Force }
                if ($verbose) {
                    $ratio = if ($outBytes.Length -gt 0) {
                        '{0:F1}%' -f ((1.0 - ($compressedBytes.Length / $outBytes.Length)) * 100)
                    } else { '0.0%' }
                    New-BashObject -BashText "${filePath}: $ratio"
                }
            }
        } else {
            $rawBytes = [System.IO.File]::ReadAllBytes($filePath)
            $ms = [System.IO.MemoryStream]::new()
            try {
                $compLevel = switch ($level) {
                    { $_ -le 1 } { [System.IO.Compression.CompressionLevel]::Fastest }
                    { $_ -ge 9 } { [System.IO.Compression.CompressionLevel]::SmallestSize }
                    default       { [System.IO.Compression.CompressionLevel]::Optimal }
                }
                $gs = [System.IO.Compression.GZipStream]::new($ms, $compLevel, $true)
                try { $gs.Write($rawBytes, 0, $rawBytes.Length) } finally { $gs.Dispose() }
                $compressedBytes = $ms.ToArray()
            } finally {
                $ms.Dispose()
            }

            if ($toStdout) {
                $b64 = [System.Convert]::ToBase64String($compressedBytes)
                New-BashObject -BashText $b64
            } else {
                $outPath = "${filePath}.gz"
                [System.IO.File]::WriteAllBytes($outPath, $compressedBytes)
                if (-not $keep) { Remove-Item -LiteralPath $filePath -Force }
                if ($verbose) {
                    $ratio = if ($rawBytes.Length -gt 0) {
                        '{0:F1}%' -f ((1.0 - ($compressedBytes.Length / $rawBytes.Length)) * 100)
                    } else { '0.0%' }
                    New-BashObject -BashText "${filePath}: $ratio"
                }
            }
        }
    }
}

# --- tar ---

function Invoke-BashTar {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'tar' }

    $create = $false
    $extract = $false
    $listMode = $false
    $gzipFilter = $false
    $verbose = $false
    $archiveFile = $null
    $changeDir = $null
    $excludePatterns = [System.Collections.Generic.List[string]]::new()
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -eq '--') { $i++; while ($i -lt $Arguments.Count) { $operands.Add($Arguments[$i]); $i++ }; break }
        if ($arg -eq '--create') { $create = $true; $i++; continue }
        if ($arg -eq '--extract' -or $arg -eq '--get') { $extract = $true; $i++; continue }
        if ($arg -eq '--list') { $listMode = $true; $i++; continue }
        if ($arg -eq '--gzip' -or $arg -eq '--gunzip') { $gzipFilter = $true; $i++; continue }
        if ($arg -eq '--verbose') { $verbose = $true; $i++; continue }

        if ($arg -eq '--file' -or $arg -ceq '-f') {
            $i++
            if ($i -lt $Arguments.Count) { $archiveFile = $Arguments[$i] }
            $i++
            continue
        }
        if ($arg -cmatch '^--file=(.+)$') {
            $archiveFile = $Matches[1]; $i++; continue
        }

        if ($arg -eq '--directory' -or $arg -ceq '-C') {
            $i++
            if ($i -lt $Arguments.Count) { $changeDir = $Arguments[$i] }
            $i++
            continue
        }
        if ($arg -cmatch '^--directory=(.+)$') {
            $changeDir = $Matches[1]; $i++; continue
        }

        if ($arg -cmatch '^--exclude=(.+)$') {
            $excludePatterns.Add($Matches[1]); $i++; continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            $chars = $arg.Substring(1).ToCharArray()
            for ($j = 0; $j -lt $chars.Length; $j++) {
                $ch = $chars[$j]
                if ($ch -eq 'c') { $create = $true }
                elseif ($ch -eq 'x') { $extract = $true }
                elseif ($ch -eq 't') { $listMode = $true }
                elseif ($ch -eq 'z') { $gzipFilter = $true }
                elseif ($ch -eq 'v') { $verbose = $true }
                elseif ($ch -eq 'p') { }
                elseif ($ch -eq 'f') {
                    $rest = [string]::new($chars, $j + 1, $chars.Length - $j - 1)
                    if ($rest.Length -gt 0) {
                        $archiveFile = $rest
                    } else {
                        $i++
                        if ($i -lt $Arguments.Count) { $archiveFile = $Arguments[$i] }
                    }
                    break
                }
                elseif ($ch -eq 'C') {
                    $rest = [string]::new($chars, $j + 1, $chars.Length - $j - 1)
                    if ($rest.Length -gt 0) {
                        $changeDir = $rest
                    } else {
                        $i++
                        if ($i -lt $Arguments.Count) { $changeDir = $Arguments[$i] }
                    }
                    break
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    if (-not $archiveFile) {
        Write-Error -Message 'tar: you must specify -f archive' -ErrorAction Continue
        return
    }

    if ($create) {
        if ($operands.Count -eq 0) {
            Write-Error -Message 'tar: no files to archive' -ErrorAction Continue
            return
        }

        $ms = [System.IO.MemoryStream]::new()
        try {
            $zip = [System.IO.Compression.ZipArchive]::new($ms, [System.IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                foreach ($source in $operands) {
                    if (-not (Test-Path -LiteralPath $source)) {
                        Write-Error -Message "tar: ${source}: No such file or directory" -ErrorAction Continue
                        continue
                    }

                    $resolvedSource = (Resolve-Path -LiteralPath $source).Path
                    if (Test-Path -LiteralPath $resolvedSource -PathType Container) {
                        $baseName = Split-Path $resolvedSource -Leaf
                        $files = Get-ChildItem -LiteralPath $resolvedSource -Recurse -File
                        foreach ($file in $files) {
                            $relPath = $file.FullName.Substring($resolvedSource.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
                            $entryName = "${baseName}/$($relPath -replace '\\', '/')"
                            $skip = $false
                            foreach ($pat in $excludePatterns) {
                                if ($file.Name -like $pat) { $skip = $true; break }
                            }
                            if ($skip) { continue }
                            $entry = $zip.CreateEntry($entryName)
                            $entryStream = $entry.Open()
                            try {
                                $fileBytes = [System.IO.File]::ReadAllBytes($file.FullName)
                                $entryStream.Write($fileBytes, 0, $fileBytes.Length)
                            } finally {
                                $entryStream.Dispose()
                            }
                            if ($verbose) {
                                New-BashObject -BashText $entryName
                            }
                        }
                    } else {
                        $entryName = Split-Path $resolvedSource -Leaf
                        $skip = $false
                        foreach ($pat in $excludePatterns) {
                            if ($entryName -like $pat) { $skip = $true; break }
                        }
                        if ($skip) { continue }
                        $entry = $zip.CreateEntry($entryName)
                        $entryStream = $entry.Open()
                        try {
                            $fileBytes = [System.IO.File]::ReadAllBytes($resolvedSource)
                            $entryStream.Write($fileBytes, 0, $fileBytes.Length)
                        } finally {
                            $entryStream.Dispose()
                        }
                        if ($verbose) {
                            New-BashObject -BashText $entryName
                        }
                    }
                }
            } finally {
                $zip.Dispose()
            }

            $zipBytes = $ms.ToArray()
        } finally {
            $ms.Dispose()
        }

        if ($gzipFilter) {
            $gms = [System.IO.MemoryStream]::new()
            try {
                $gs = [System.IO.Compression.GZipStream]::new($gms, [System.IO.Compression.CompressionLevel]::Optimal, $true)
                try { $gs.Write($zipBytes, 0, $zipBytes.Length) } finally { $gs.Dispose() }
                [System.IO.File]::WriteAllBytes($archiveFile, $gms.ToArray())
            } finally {
                $gms.Dispose()
            }
        } else {
            [System.IO.File]::WriteAllBytes($archiveFile, $zipBytes)
        }
    } elseif ($listMode) {
        if (-not (Test-Path -LiteralPath $archiveFile)) {
            Write-Error -Message "tar: ${archiveFile}: No such file or directory" -ErrorAction Continue
            return
        }

        $archiveBytes = [System.IO.File]::ReadAllBytes($archiveFile)
        $zipBytes = $archiveBytes
        if ($gzipFilter) {
            $ims = [System.IO.MemoryStream]::new($archiveBytes)
            try {
                $gs = [System.IO.Compression.GZipStream]::new($ims, [System.IO.Compression.CompressionMode]::Decompress)
                $oms = [System.IO.MemoryStream]::new()
                try { $gs.CopyTo($oms); $zipBytes = $oms.ToArray() } finally { $gs.Dispose(); $oms.Dispose() }
            } finally {
                $ims.Dispose()
            }
        }

        $zms = [System.IO.MemoryStream]::new($zipBytes)
        try {
            $zip = [System.IO.Compression.ZipArchive]::new($zms, [System.IO.Compression.ZipArchiveMode]::Read)
            try {
                foreach ($entry in $zip.Entries) {
                    if ($entry.FullName.EndsWith('/')) { continue }
                    $obj = [PSCustomObject]@{
                        PSTypeName   = 'PsBash.TarListOutput'
                        BashText     = $entry.FullName
                        Name         = $entry.Name
                        Size         = $entry.Length
                        ModifiedDate = $entry.LastWriteTime.DateTime
                    }
                    Set-BashDisplayProperty $obj
                }
            } finally {
                $zip.Dispose()
            }
        } finally {
            $zms.Dispose()
        }
    } elseif ($extract) {
        if (-not (Test-Path -LiteralPath $archiveFile)) {
            Write-Error -Message "tar: ${archiveFile}: No such file or directory" -ErrorAction Continue
            return
        }

        $targetDir = if ($changeDir) { $changeDir } else { Get-Location | Select-Object -ExpandProperty Path }
        if (-not (Test-Path -LiteralPath $targetDir)) {
            New-Item -Path $targetDir -ItemType Directory -Force | Out-Null
        }

        $archiveBytes = [System.IO.File]::ReadAllBytes($archiveFile)
        $zipBytes = $archiveBytes
        if ($gzipFilter) {
            $ims = [System.IO.MemoryStream]::new($archiveBytes)
            try {
                $gs = [System.IO.Compression.GZipStream]::new($ims, [System.IO.Compression.CompressionMode]::Decompress)
                $oms = [System.IO.MemoryStream]::new()
                try { $gs.CopyTo($oms); $zipBytes = $oms.ToArray() } finally { $gs.Dispose(); $oms.Dispose() }
            } finally {
                $ims.Dispose()
            }
        }

        $zms = [System.IO.MemoryStream]::new($zipBytes)
        try {
            $zip = [System.IO.Compression.ZipArchive]::new($zms, [System.IO.Compression.ZipArchiveMode]::Read)
            try {
                foreach ($entry in $zip.Entries) {
                    if ($entry.FullName.EndsWith('/')) { continue }
                    $outPath = Join-Path $targetDir ($entry.FullName -replace '/', [System.IO.Path]::DirectorySeparatorChar)
                    $outDir = Split-Path $outPath -Parent
                    if (-not (Test-Path -LiteralPath $outDir)) {
                        New-Item -Path $outDir -ItemType Directory -Force | Out-Null
                    }
                    $entryStream = $entry.Open()
                    try {
                        $buf = [System.IO.MemoryStream]::new()
                        try {
                            $entryStream.CopyTo($buf)
                            [System.IO.File]::WriteAllBytes($outPath, $buf.ToArray())
                        } finally {
                            $buf.Dispose()
                        }
                    } finally {
                        $entryStream.Dispose()
                    }
                    if ($verbose) {
                        New-BashObject -BashText $entry.FullName
                    }
                }
            } finally {
                $zip.Dispose()
            }
        } finally {
            $zms.Dispose()
        }
    } else {
        Write-Error -Message 'tar: you must specify one of -c, -x, -t' -ErrorAction Continue
    }
}

# --- yq Command ---

function ConvertFrom-SimpleYaml {
    param([string]$Text)

    $lines = $Text -split "`n"
    $root = [ordered]@{}
    # Stack: list of {indent, container, lastKey}
    # container is always the dict that owns keys at this level
    $stack = [System.Collections.Generic.List[object]]::new()
    $stack.Add(@{ indent = -2; container = $root; lastKey = $null })

    foreach ($rawLine in $lines) {
        $line = $rawLine -replace "`r$", ''
        if ($line.Trim() -eq '' -or $line.Trim().StartsWith('#')) { continue }

        $stripped = $line.TrimStart()
        $indent = $line.Length - $stripped.Length

        # Pop deeper or same-level entries to find the correct parent
        while ($stack.Count -gt 1 -and $stack[$stack.Count - 1].indent -ge $indent) {
            $stack.RemoveAt($stack.Count - 1)
        }

        $top = $stack[$stack.Count - 1]

        # List item
        if ($stripped.StartsWith('- ')) {
            $itemText = $stripped.Substring(2).Trim()
            # The list lives under the parent's lastKey
            $parentKey = $top.lastKey
            $parentContainer = $top.container
            if ($null -ne $parentKey -and $parentContainer -is [System.Collections.IDictionary]) {
                if (-not ($parentContainer[$parentKey] -is [System.Collections.IList])) {
                    $parentContainer[$parentKey] = [System.Collections.Generic.List[object]]::new()
                }
                $parentContainer[$parentKey].Add((ConvertFrom-YamlValue $itemText))
            }
            continue
        }

        # Key: value pair
        $colonIdx = $stripped.IndexOf(':')
        if ($colonIdx -lt 0) { continue }
        $key = $stripped.Substring(0, $colonIdx).Trim()
        $valPart = ''
        if ($colonIdx + 1 -lt $stripped.Length) {
            $valPart = $stripped.Substring($colonIdx + 1).Trim()
        }

        $target = $top.container
        # If the top of stack points to a dict that was created for nesting,
        # and lastKey is set, resolve the child dict
        if ($null -ne $top.lastKey -and $target -is [System.Collections.IDictionary] -and $target.Contains($top.lastKey) -and $target[$top.lastKey] -is [System.Collections.IDictionary]) {
            $target = $target[$top.lastKey]
        }

        if ($valPart -eq '') {
            $child = [ordered]@{}
            $target[$key] = $child
            $stack.Add(@{ indent = $indent; container = $target; lastKey = $key })
        } else {
            $target[$key] = ConvertFrom-YamlValue $valPart
        }
    }

    $root
}

function ConvertFrom-YamlValue {
    param([string]$Raw)

    $s = $Raw.Trim()
    if ($s -eq 'null' -or $s -eq '~') { return $null }
    if ($s -eq 'true') { return $true }
    if ($s -eq 'false') { return $false }
    if ($s -match '^\-?\d+$') { return [long]$s }
    if ($s -match '^\-?\d+\.\d+$') { return [double]$s }
    # Quoted strings
    if (($s.StartsWith('"') -and $s.EndsWith('"')) -or ($s.StartsWith("'") -and $s.EndsWith("'"))) {
        return $s.Substring(1, $s.Length - 2)
    }
    $s
}

function ConvertTo-SimpleYaml {
    param([object]$Data, [int]$Indent = 0)

    $prefix = ' ' * $Indent
    $sb = [System.Text.StringBuilder]::new()

    if ($null -eq $Data) {
        $sb.Append('null') | Out-Null
    } elseif ($Data -is [bool]) {
        $sb.Append($(if ($Data) { 'true' } else { 'false' })) | Out-Null
    } elseif ($Data -is [int] -or $Data -is [long] -or $Data -is [double] -or $Data -is [decimal]) {
        $sb.Append("$Data") | Out-Null
    } elseif ($Data -is [string]) {
        if ($Data -match '[:,{}\[\]#&*!|>''"%@`]' -or $Data -eq '') {
            $escaped = $Data -replace '"', '\"'
            $sb.Append("`"$escaped`"") | Out-Null
        } else {
            $sb.Append($Data) | Out-Null
        }
    } elseif ($Data -is [array] -or $Data -is [System.Collections.IList]) {
        $first = $true
        foreach ($item in $Data) {
            if (-not $first) { $sb.Append("`n") | Out-Null }
            $sb.Append("${prefix}- ") | Out-Null
            $valYaml = ConvertTo-SimpleYaml -Data $item -Indent ($Indent + 2)
            $sb.Append($valYaml) | Out-Null
            $first = $false
        }
    } elseif ($Data -is [System.Collections.IDictionary]) {
        $first = $true
        foreach ($key in $Data.Keys) {
            if (-not $first) { $sb.Append("`n") | Out-Null }
            $val = $Data[$key]
            if ($val -is [System.Collections.IDictionary] -or $val -is [array] -or $val -is [System.Collections.IList]) {
                $sb.Append("${prefix}${key}:") | Out-Null
                $sb.Append("`n") | Out-Null
                $sb.Append((ConvertTo-SimpleYaml -Data $val -Indent ($Indent + 2))) | Out-Null
            } else {
                $sb.Append("${prefix}${key}: ") | Out-Null
                $sb.Append((ConvertTo-SimpleYaml -Data $val -Indent 0)) | Out-Null
            }
            $first = $false
        }
    } else {
        $sb.Append("$Data") | Out-Null
    }

    $sb.ToString()
}

function Invoke-BashYq {
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'yq' }

    $rawOutput = $false
    $outputFormat = 'json'
    $filterExpr = '.'
    $filterSet = $false
    $files = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -ceq '-r' -or $arg -ceq '--raw-output') {
            $rawOutput = $true
            $i++
            continue
        }
        if ($arg -ceq '-o' -or $arg -ceq '--output-format') {
            $i++
            if ($i -lt $Arguments.Count) { $outputFormat = $Arguments[$i] }
            $i++
            continue
        }

        if (-not $filterSet) {
            $filterExpr = $arg
            $filterSet = $true
        } else {
            $files.Add($arg)
        }
        $i++
    }

    # Collect YAML input
    $yamlTexts = [System.Collections.Generic.List[string]]::new()

    if ($files.Count -gt 0) {
        foreach ($file in $files) {
            $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($file)
            if (-not (Test-Path -LiteralPath $resolved)) {
                Write-Error "yq: $file`: No such file or directory" -ErrorAction Continue
                return
            }
            $yamlTexts.Add((Get-Content -LiteralPath $resolved -Raw))
        }
    } else {
        $textParts = [System.Text.StringBuilder]::new()
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $textParts.Append($text) | Out-Null
        }
        $combined = $textParts.ToString().Trim()
        if ($combined -ne '') {
            $yamlTexts.Add($combined)
        }
    }

    if ($yamlTexts.Count -eq 0) { return }

    foreach ($yamlText in $yamlTexts) {
        $parsed = ConvertFrom-SimpleYaml -Text $yamlText
        $results = @(Invoke-JqFilter -Data $parsed -Filter $filterExpr)
        foreach ($result in $results) {
            if ($outputFormat -eq 'yaml') {
                $text = ConvertTo-SimpleYaml -Data $result
                New-BashObject -BashText $text
            } else {
                $text = ConvertTo-JqJson -Value $result -Compact $false -SortKeys $false -RawOutput $rawOutput
                New-BashObject -BashText $text
            }
        }
    }
}

# --- xan Command ---

function Invoke-BashXan {
    # Normalize args: PowerShell comma operator creates arrays, rejoin them
    $Arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($a in $args) {
        if ($a -is [array]) {
            $Arguments.Add(($a -join ','))
        } else {
            $Arguments.Add([string]$a)
        }
    }
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'xan' }

    $delimiter = ','
    $subcommand = $null
    $subArgs = [System.Collections.Generic.List[string]]::new()

    $i = 0
    # Parse global flags before subcommand
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-d') {
            $i++
            if ($i -lt $Arguments.Count) { $delimiter = $Arguments[$i] }
            $i++
            continue
        }
        if ($null -eq $subcommand -and -not $arg.StartsWith('-')) {
            $subcommand = $arg
            $i++
            while ($i -lt $Arguments.Count) {
                $subArgs.Add($Arguments[$i])
                $i++
            }
            break
        }
        $i++
    }

    if (-not $subcommand) {
        Write-Error 'xan: missing subcommand (headers, count, select, search, table)' -ErrorAction Continue
        return
    }

    # Resolve CSV text: last subArg may be a file, or pipeline
    $csvText = $null
    $fileArg = $null

    # For select/search: last arg is file if it exists on disk, rest are the operand.
    # PowerShell comma operator splits 'a,b' into separate args, so rejoin them.
    switch ($subcommand) {
        'headers' {
            if ($subArgs.Count -gt 0) { $fileArg = $subArgs[$subArgs.Count - 1] }
        }
        'count' {
            if ($subArgs.Count -gt 0) { $fileArg = $subArgs[$subArgs.Count - 1] }
        }
        'select' {
            if ($subArgs.Count -gt 1) { $fileArg = $subArgs[$subArgs.Count - 1] }
        }
        'search' {
            if ($subArgs.Count -gt 1) { $fileArg = $subArgs[$subArgs.Count - 1] }
        }
        'table' {
            if ($subArgs.Count -gt 0) { $fileArg = $subArgs[$subArgs.Count - 1] }
        }
        default {
            Write-Error "xan: unknown subcommand '$subcommand'" -ErrorAction Continue
            return
        }
    }

    if ($fileArg) {
        $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($fileArg)
        if (-not (Test-Path -LiteralPath $resolved)) {
            Write-Error "xan: $fileArg`: No such file or directory" -ErrorAction Continue
            return
        }
        $csvText = Get-Content -LiteralPath $resolved -Raw
    } else {
        $textParts = [System.Text.StringBuilder]::new()
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $textParts.Append($text) | Out-Null
        }
        $csvText = $textParts.ToString().Trim()
    }

    if (-not $csvText -or $csvText -eq '') { return }

    $records = @($csvText | ConvertFrom-Csv -Delimiter $delimiter)
    if ($records.Count -eq 0 -and $csvText.Trim() -ne '') {
        # Header-only: ConvertFrom-Csv returns empty for header-only
        $headerLine = ($csvText -split "`n")[0].Trim()
        $headers = $headerLine -split [regex]::Escape($delimiter)
    } else {
        $headers = @($records[0].PSObject.Properties | ForEach-Object { $_.Name })
    }

    switch ($subcommand) {
        'headers' {
            foreach ($h in $headers) {
                New-BashObject -BashText $h
            }
        }
        'count' {
            New-BashObject -BashText "$($records.Count)"
        }
        'select' {
            $cols = @($subArgs[0] -split ',')
            $outLines = [System.Collections.Generic.List[string]]::new()
            $outLines.Add(($cols -join $delimiter))
            foreach ($rec in $records) {
                $vals = @(foreach ($c in $cols) { $rec.$c })
                $outLines.Add(($vals -join $delimiter))
            }
            foreach ($line in $outLines) {
                New-BashObject -BashText $line
            }
        }
        'search' {
            $pattern = $subArgs[0]
            $outLines = [System.Collections.Generic.List[string]]::new()
            $outLines.Add(($headers -join $delimiter))
            foreach ($rec in $records) {
                $rowText = ($headers | ForEach-Object { $rec.$_ }) -join $delimiter
                if ($rowText -match $pattern) {
                    $outLines.Add($rowText)
                }
            }
            foreach ($line in $outLines) {
                New-BashObject -BashText $line
            }
        }
        'table' {
            $colWidths = @{}
            foreach ($h in $headers) { $colWidths[$h] = $h.Length }
            foreach ($rec in $records) {
                foreach ($h in $headers) {
                    $val = "$($rec.$h)"
                    if ($val.Length -gt $colWidths[$h]) { $colWidths[$h] = $val.Length }
                }
            }
            $sb = [System.Text.StringBuilder]::new()
            $headerParts = @(foreach ($h in $headers) { $h.PadRight($colWidths[$h]) })
            $sb.AppendLine(($headerParts -join '  ')) | Out-Null
            foreach ($rec in $records) {
                $parts = @(foreach ($h in $headers) { "$($rec.$h)".PadRight($colWidths[$h]) })
                $sb.AppendLine(($parts -join '  ')) | Out-Null
            }
            New-BashObject -BashText $sb.ToString().TrimEnd()
        }
    }
}

# --- sleep ---

function Invoke-BashSleep {
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'sleep' }

    if ($Arguments.Count -eq 0) {
        Write-Error 'sleep: missing operand' -ErrorAction Continue
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
            Write-Error "sleep: invalid time interval '$arg'" -ErrorAction Continue
            return
        }
        if ($val -lt 0) {
            Write-Error "sleep: invalid time interval '$arg'" -ErrorAction Continue
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
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'time' }

    if ($Arguments.Count -eq 0) {
        Write-Error 'time: missing command' -ErrorAction Continue
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
        foreach ($e in $errors) { Write-Error $e -ErrorAction Continue }
        if ($errors.Count -gt 0) { $exitCode = 1 }
    } catch {
        $sw.Stop()
        Write-Error $_.Exception.Message -ErrorAction Continue
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
        Write-Error $_
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
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'which' }

    $showAll = $false
    $operands = [System.Collections.Generic.List[string]]::new()
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-a') { $showAll = $true }
        else { $operands.Add($arg) }
    }

    if ($operands.Count -eq 0) {
        Write-Error 'which: missing operand' -ErrorAction Continue
        return
    }

    foreach ($name in $operands) {
        $cmds = @(Get-Command $name -ErrorAction SilentlyContinue)
        if ($cmds.Count -eq 0) {
            Write-Error "which: no $name in PATH" -ErrorAction Continue
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

# --- alias / unalias ---

$script:BashUserAliases = [System.Collections.Generic.Dictionary[string,string]]::new(
    [System.StringComparer]::Ordinal
)

function Invoke-BashAlias {
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
            # list mode, same as bare alias
        } else {
            $operands.Add($arg)
        }
        $i++
    }

    if ($unaliasMode) {
        if ($removeAll) {
            $script:BashUserAliases.Clear()
            return
        }
        foreach ($name in $operands) {
            if (-not $script:BashUserAliases.ContainsKey($name)) {
                Write-Error "unalias: ${name}: not found" -ErrorAction Continue
                continue
            }
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
            $script:BashUserAliases[$Matches[1]] = $Matches[2]
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
                Write-Error "alias: ${arg}: not found"
            }
        }
    }
}

# --- Help Support ---

$script:BashHelpSpecs = @{
    'echo'     = 'Display a line of text.'
    'printf'   = 'Format and print data.'
    'ls'       = 'List directory contents.'
    'cat'      = 'Concatenate files and print on the standard output.'
    'grep'     = 'Print lines that match patterns.'
    'rg'       = 'Recursively search the current directory for a regex pattern.'
    'sort'     = 'Sort lines of text files.'
    'head'     = 'Output the first part of files.'
    'tail'     = 'Output the last part of files.'
    'wc'       = 'Print newline, word, and byte counts for each file.'
    'find'     = 'Search for files in a directory hierarchy.'
    'stat'     = 'Display file or file system status.'
    'cp'       = 'Copy files and directories.'
    'mv'       = 'Move (rename) files.'
    'rm'       = 'Remove files or directories.'
    'mkdir'    = 'Make directories.'
    'rmdir'    = 'Remove empty directories.'
    'touch'    = 'Change file timestamps.'
    'ln'       = 'Make links between files.'
    'ps'       = 'Report a snapshot of the current processes.'
    'sed'      = 'Stream editor for filtering and transforming text.'
    'awk'      = 'Pattern scanning and processing language.'
    'cut'      = 'Remove sections from each line of files.'
    'tr'       = 'Translate or delete characters.'
    'uniq'     = 'Report or omit repeated lines.'
    'rev'      = 'Reverse lines characterwise.'
    'nl'       = 'Number lines of files.'
    'diff'     = 'Compare files line by line.'
    'comm'     = 'Compare two sorted files line by line.'
    'column'   = 'Columnate lists.'
    'join'     = 'Join lines of two files on a common field.'
    'paste'    = 'Merge lines of files.'
    'tee'      = 'Read from standard input and write to standard output and files.'
    'xargs'    = 'Build and execute command lines from standard input.'
    'jq'       = 'Command-line JSON processor.'
    'date'     = 'Print or set the system date and time.'
    'seq'      = 'Print a sequence of numbers.'
    'expr'     = 'Evaluate expressions.'
    'du'       = 'Estimate file space usage.'
    'tree'     = 'List contents of directories in a tree-like format.'
    'env'      = 'Print the environment or run a program in a modified environment.'
    'basename' = 'Strip directory and suffix from filenames.'
    'dirname'  = 'Strip last component from file name.'
    'pwd'      = 'Print name of current/working directory.'
    'hostname' = 'Show the system host name.'
    'whoami'   = 'Print effective userid.'
    'fold'     = 'Wrap each input line to fit in specified width.'
    'expand'   = 'Convert tabs to spaces.'
    'unexpand' = 'Convert spaces to tabs.'
    'strings'  = 'Print the sequences of printable characters in files.'
    'split'    = 'Split a file into pieces.'
    'tac'      = 'Concatenate and print files in reverse.'
    'base64'   = 'Base64 encode/decode data and print to standard output.'
    'md5sum'   = 'Compute and check MD5 message digest.'
    'sha1sum'  = 'Compute and check SHA1 message digest.'
    'sha256sum' = 'Compute and check SHA256 message digest.'
    'file'     = 'Determine file type.'
    'gzip'     = 'Compress or expand files.'
    'tar'      = 'Store and extract files from an archive.'
    'yq'       = 'Command-line YAML/JSON processor.'
    'xan'      = 'CSV toolkit for column selection, search, and display.'
    'sleep'    = 'Delay for a specified amount of time.'
    'time'     = 'Run programs and summarize system resource usage.'
    'which'    = 'Locate a command.'
    'alias'    = 'Define or display aliases.'
}

function Test-BashHelpFlag {
    [CmdletBinding()]
    param([string[]]$Arguments)
    return ($Arguments -contains '--help')
}

function Show-BashHelp {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CommandName)

    $synopsis = $script:BashHelpSpecs[$CommandName]
    if (-not $synopsis) { $synopsis = '' }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("Usage: $CommandName [OPTION]... [ARG]...")
    $lines.Add($synopsis)
    $lines.Add('')

    $flagEntries = $script:BashFlagSpecs[$CommandName]
    if ($flagEntries -and $flagEntries.Count -gt 0) {
        # Single-flag commands flatten to a string array; wrap back into nested array
        if ($flagEntries[0] -is [string]) {
            $flagEntries = @(,$flagEntries)
        }
        $lines.Add('Options:')
        foreach ($entry in $flagEntries) {
            $flag = $entry[0]
            $desc = $entry[1]
            $pad = ' ' * [Math]::Max(1, 14 - $flag.Length)
            $lines.Add("  $flag$pad$desc")
        }
    }

    $text = ($lines -join "`n") + "`n"
    New-BashObject -BashText $text
}

# --- Tab Completion ---

$script:BashFlagSpecs = @{
    'echo'     = @(
        @('-n', 'no trailing newline'), @('-e', 'enable escape sequences'), @('-E', 'disable escape sequences')
    )
    'ls'       = @(
        @('-l', 'long listing'),    @('-a', 'show hidden'),      @('-h', 'human readable sizes'),
        @('-R', 'recursive'),       @('-S', 'sort by size'),     @('-t', 'sort by time'),
        @('-r', 'reverse sort'),    @('-1', 'one per line')
    )
    'cat'      = @(
        @('-n', 'number all lines'),   @('-b', 'number non-blank lines'), @('-s', 'squeeze blank lines'),
        @('-E', 'show $ at line end'), @('-T', 'show ^I for tabs')
    )
    'grep'     = @(
        @('-i', 'ignore case'),       @('-v', 'invert match'),     @('-n', 'line numbers'),
        @('-c', 'count only'),        @('-r', 'recursive'),        @('-l', 'files with matches'),
        @('-E', 'extended regex'),    @('-A', 'after context'),    @('-B', 'before context'),
        @('-C', 'context')
    )
    'rg'       = @(
        @('-i', 'ignore case'),       @('-w', 'word regexp'),      @('-c', 'count matches'),
        @('-l', 'files with matches'),@('-n', 'line numbers'),     @('-N', 'no line numbers'),
        @('-o', 'only matching'),     @('-v', 'invert match'),     @('-F', 'fixed strings'),
        @('-g', 'glob filter'),       @('-A', 'after context'),    @('-B', 'before context'),
        @('-C', 'context'),           @('--hidden', 'include dotfiles')
    )
    'sort'     = @(
        @('-r', 'reverse'),           @('-n', 'numeric sort'),     @('-u', 'unique'),
        @('-f', 'fold case'),         @('-k', 'key field'),        @('-t', 'field separator'),
        @('-h', 'human numeric'),     @('-V', 'version sort'),     @('-M', 'month sort'),
        @('-c', 'check sorted')
    )
    'head'     = @( @('-n', 'number of lines') )
    'tail'     = @( @('-n', 'number of lines') )
    'wc'       = @( @('-l', 'line count'), @('-w', 'word count'), @('-c', 'byte count') )
    'find'     = @(
        @('-name', 'name pattern'),   @('-type', 'file type'),     @('-size', 'file size'),
        @('-maxdepth', 'max depth'),  @('-mtime', 'modify time'),  @('-empty', 'empty files')
    )
    'stat'     = @( @('-c', 'format string'), @('-t', 'terse'), @('--printf', 'printf format') )
    'cp'       = @( @('-r', 'recursive'), @('-v', 'verbose'), @('-n', 'no-clobber'), @('-f', 'force') )
    'mv'       = @( @('-v', 'verbose'), @('-n', 'no-clobber'), @('-f', 'force') )
    'rm'       = @( @('-r', 'recursive'), @('-f', 'force'), @('-v', 'verbose') )
    'mkdir'    = @( @('-p', 'parents'), @('-v', 'verbose') )
    'rmdir'    = @( @('-p', 'parents'), @('-v', 'verbose') )
    'touch'    = @( @('-d', 'date string') )
    'ln'       = @( @('-s', 'symbolic'), @('-f', 'force'), @('-v', 'verbose') )
    'ps'       = @(
        @('-e', 'all processes'),     @('-A', 'all processes'),    @('-f', 'full format'),
        @('-u', 'filter user'),       @('-p', 'filter pid'),       @('--sort', 'sort key'),
        @('-o', 'output format')
    )
    'sed'      = @( @('-n', 'suppress default'), @('-i', 'in-place'), @('-E', 'extended regex'), @('-e', 'expression') )
    'awk'      = @( @('-F', 'field separator'), @('-v', 'variable') )
    'cut'      = @( @('-d', 'delimiter'), @('-f', 'fields'), @('-c', 'characters') )
    'tr'       = @( @('-d', 'delete'), @('-s', 'squeeze') )
    'uniq'     = @( @('-c', 'count'), @('-d', 'duplicates only') )
    'nl'       = @( @('-ba', 'number all lines') )
    'diff'     = @( @('-u', 'unified format') )
    'comm'     = @( @('-1', 'suppress col 1'), @('-2', 'suppress col 2'), @('-3', 'suppress col 3') )
    'column'   = @( @('-t', 'table mode'), @('-s', 'separator') )
    'join'     = @( @('-t', 'delimiter'), @('-1', 'field from file 1'), @('-2', 'field from file 2') )
    'paste'    = @( @('-d', 'delimiter'), @('-s', 'serial') )
    'tee'      = @( @('-a', 'append') )
    'xargs'    = @( @('-I', 'replace string'), @('-n', 'max args') )
    'jq'       = @(
        @('-r', 'raw output'),        @('-c', 'compact output'),   @('-S', 'sort keys'),
        @('-s', 'slurp')
    )
    'date'     = @( @('-d', 'date string'), @('-u', 'UTC'), @('-r', 'reference file'), @('+FORMAT', 'output format') )
    'seq'      = @( @('-s', 'separator'), @('-w', 'equal width') )
    'du'       = @(
        @('-h', 'human readable'),    @('-s', 'summarize'),        @('-a', 'all files'),
        @('-c', 'show total'),        @('-d', 'max depth')
    )
    'tree'     = @(
        @('-a', 'all files'),         @('-d', 'directories only'), @('-L', 'max depth'),
        @('-I', 'exclude pattern'),   @('--dirsfirst', 'directories first')
    )
    'basename' = @( @('-s', 'suffix') )
    'pwd'      = @( @('-P', 'physical path') )
    'fold'     = @( @('-w', 'wrap width'), @('-s', 'break at spaces'), @('-b', 'count bytes') )
    'expand'   = @( @('-t', 'tab width') )
    'unexpand' = @( @('-t', 'tab width'), @('-a', 'convert all spaces') )
    'strings'  = @( @('-n', 'minimum string length') )
    'split'    = @( @('-l', 'lines per file'), @('-d', 'numeric suffixes'), @('-a', 'suffix length') )
    'tac'      = @( @('-s', 'separator') )
    'base64'   = @( @('-d', 'decode'), @('-w', 'wrap at column') )
    'md5sum'   = @( @('-c', 'check'), @('-b', 'binary mode') )
    'sha1sum'  = @( @('-c', 'check'), @('-b', 'binary mode') )
    'sha256sum' = @( @('-c', 'check'), @('-b', 'binary mode') )
    'file'     = @( @('-b', 'brief'), @('-i', 'MIME type'), @('-L', 'follow symlinks') )
    'gzip'     = @(
        @('-d', 'decompress'),           @('-c', 'write to stdout'),   @('-k', 'keep original'),
        @('-f', 'force'),                @('-v', 'verbose'),           @('-l', 'list'),
        @('-1', 'fastest compression'),  @('-9', 'best compression')
    )
    'tar'      = @(
        @('-c', 'create archive'),       @('-x', 'extract archive'),   @('-t', 'list contents'),
        @('-f', 'archive file'),         @('-z', 'gzip filter'),       @('-v', 'verbose'),
        @('-C', 'change directory'),     @('--exclude', 'exclude pattern')
    )
    'yq'       = @(
        @('-r', 'raw output'),           @('-o', 'output format (json, yaml)')
    )
    'xan'      = @(
        @('-d', 'delimiter'),            @('headers', 'show column headers'),
        @('count', 'count rows'),        @('select', 'select columns'),
        @('search', 'search rows'),      @('table', 'pretty table display')
    )
    'sleep'    = @( @('NUMBER', 'seconds to sleep (suffix: s/m/h/d)') )
    'time'     = @( @('COMMAND', 'command to time') )
    'which'    = @( @('-a', 'show all matches') )
    'alias'    = @( @('-p', 'list all aliases'), @('-u', 'unalias mode'), @('-a', 'remove all (with -u)') )
}

$script:BashCompleters = @{}

function Register-BashCompletions {
    [CmdletBinding()]
    param()

    foreach ($commandName in $script:BashFlagSpecs.Keys) {
        $flagEntries = $script:BashFlagSpecs[$commandName]

        $completerBlock = {
            param($wordToComplete, $commandAst, $cursorPosition)

            $word = if ($wordToComplete) { $wordToComplete } else { '' }
            if (-not $word.StartsWith('-')) { return }

            foreach ($entry in $flagEntries) {
                $flag = $entry[0]
                $desc = $entry[1]
                if ($flag.StartsWith($word)) {
                    [System.Management.Automation.CompletionResult]::new(
                        $flag,
                        $flag,
                        [System.Management.Automation.CompletionResultType]::ParameterValue,
                        $desc
                    )
                }
            }
        }.GetNewClosure()

        $script:BashCompleters[$commandName] = $completerBlock
        Register-ArgumentCompleter -Native -CommandName $commandName -ScriptBlock $completerBlock
    }
}

Register-BashCompletions

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
Set-Alias -Name 'tee'     -Value 'Invoke-BashTee'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'xargs'   -Value 'Invoke-BashXargs'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'jq'      -Value 'Invoke-BashJq'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'date'    -Value 'Invoke-BashDate'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'seq'     -Value 'Invoke-BashSeq'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'expr'    -Value 'Invoke-BashExpr'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'du'      -Value 'Invoke-BashDu'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'tree'    -Value 'Invoke-BashTree'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'env'      -Value 'Invoke-BashEnv'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'printenv' -Value 'Invoke-BashEnv'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'basename' -Value 'Invoke-BashBasename' -Force -Scope Global -Option AllScope
Set-Alias -Name 'dirname'  -Value 'Invoke-BashDirname'  -Force -Scope Global -Option AllScope
Set-Alias -Name 'pwd'      -Value 'Invoke-BashPwd'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'hostname' -Value 'Invoke-BashHostname' -Force -Scope Global -Option AllScope
Set-Alias -Name 'whoami'   -Value 'Invoke-BashWhoami'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'fold'     -Value 'Invoke-BashFold'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'expand'   -Value 'Invoke-BashExpand'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'unexpand' -Value 'Invoke-BashUnexpand' -Force -Scope Global -Option AllScope
Set-Alias -Name 'strings'  -Value 'Invoke-BashStrings'  -Force -Scope Global -Option AllScope
Set-Alias -Name 'split'    -Value 'Invoke-BashSplit'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'tac'      -Value 'Invoke-BashTac'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'base64'   -Value 'Invoke-BashBase64'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'md5sum'   -Value 'Invoke-BashMd5sum'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'sha1sum'  -Value 'Invoke-BashSha1sum'  -Force -Scope Global -Option AllScope
Set-Alias -Name 'sha256sum' -Value 'Invoke-BashSha256sum' -Force -Scope Global -Option AllScope
Set-Alias -Name 'file'     -Value 'Invoke-BashFile'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'rg'       -Value 'Invoke-BashRg'       -Force -Scope Global -Option AllScope
Set-Alias -Name 'gzip'     -Value 'Invoke-BashGzip'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'gunzip'   -Value 'Invoke-BashGzip'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'zcat'     -Value 'Invoke-BashGzip'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'tar'      -Value 'Invoke-BashTar'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'yq'       -Value 'Invoke-BashYq'       -Force -Scope Global -Option AllScope
Set-Alias -Name 'xan'      -Value 'Invoke-BashXan'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'sleep'    -Value 'Invoke-BashSleep'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'time'     -Value 'Invoke-BashTime'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'which'    -Value 'Invoke-BashWhich'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'balias'   -Value 'Invoke-BashAlias'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'unalias'  -Value 'Invoke-BashAlias'    -Force -Scope Global -Option AllScope
