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
        $statOutput = & stat @statArgs 2>$null
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

# --- Aliases ---

Set-Alias -Name 'echo'   -Value 'Invoke-BashEcho'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'printf'  -Value 'Invoke-BashPrintf'  -Force -Scope Global -Option AllScope
Set-Alias -Name 'ls'      -Value 'Invoke-BashLs'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'cat'     -Value 'Invoke-BashCat'     -Force -Scope Global -Option AllScope
