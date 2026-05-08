# --- echo Command ---

function Invoke-BashEcho {
    [OutputType('PsBash.TextOutput')]
    param()
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

    Emit-BashLine -Text $text -Command 'echo'
    $global:LASTEXITCODE = 0
    $global:BashLastArg = if ($parsed.Operands.Count -gt 0) { $parsed.Operands[-1] } else { '' }
}

# --- printf Command ---

function Invoke-BashPrintf {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'printf' }

    if (-not $Arguments -or $Arguments.Count -eq 0) {
        Write-BashError -Message 'printf: usage: printf format [arguments]' -ExitCode 2
        return
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
            $j = $i + 1
            $flags = ''
            while ($j -lt $format.Length -and $format[$j] -match '^[-+ 0#]$') {
                $flags += $format[$j]
                $j++
            }
            $width = ''
            while ($j -lt $format.Length -and $format[$j] -match '^\d$') {
                $width += $format[$j]
                $j++
            }
            $precision = ''
            if ($j -lt $format.Length -and $format[$j] -eq '.') {
                $j++
                while ($j -lt $format.Length -and $format[$j] -match '^\d$') {
                    $precision += $format[$j]
                    $j++
                }
            }
            $spec = if ($j -lt $format.Length) { $format[$j] } else { '' }
            $fullSpec = $format.Substring($i, $j - $i + 1)

            switch -casesensitive ($spec) {
                's' {
                    if ($argIdx -lt $converted.Count) {
                        $val = [string]$converted[$argIdx]
                        if ($width -and $precision) {
                            $val = $val.PadLeft([int]$width).Substring(0, [math]::Min([int]$precision, $val.Length))
                        } elseif ($width) {
                            if ($flags.Contains('-')) { $val = $val.PadRight([int]$width) }
                            else { $val = $val.PadLeft([int]$width) }
                        }
                        [void]$sb.Append($val)
                    }
                    $argIdx++
                    $i = $j + 1
                }
                'd' {
                    if ($argIdx -lt $converted.Count) {
                        $val = [int]$converted[$argIdx]
                        if ($width) {
                            $zeroPad = $flags.Contains('0')
                            $leftAlign = $flags.Contains('-')
                            $showPlus = $flags.Contains('+')
                            $prefix = if ($val -ge 0 -and $showPlus) { '+' } else { '' }
                            $str = "$prefix$($val.ToString())"
                            if ($zeroPad -and -not $leftAlign) { $str = $str.PadLeft([int]$width, '0') }
                            elseif ($leftAlign) { $str = $str.PadRight([int]$width) }
                            else { $str = $str.PadLeft([int]$width) }
                            [void]$sb.Append($str)
                        } else {
                            [void]$sb.Append($val)
                        }
                    }
                    $argIdx++
                    $i = $j + 1
                }
                'f' {
                    if ($argIdx -lt $converted.Count) {
                        $prec = if ($precision) { [int]$precision } else { 6 }
                        [void]$sb.Append([string]::Format("{0,${width}:F${prec}}", [double]$converted[$argIdx]).Trim())
                    }
                    $argIdx++
                    $i = $j + 1
                }
                'x' {
                    if ($argIdx -lt $converted.Count) {
                        $val = [int]$converted[$argIdx]
                        $str = $val.ToString('x')
                        if ($flags.Contains('#')) { $str = "0x$str" }
                        if ($width) { $str = $str.PadLeft([int]$width, '0') }
                        [void]$sb.Append($str)
                    }
                    $argIdx++
                    $i = $j + 1
                }
                'X' {
                    if ($argIdx -lt $converted.Count) {
                        $val = [int]$converted[$argIdx]
                        $str = $val.ToString('X')
                        if ($flags.Contains('#')) { $str = "0X$str" }
                        if ($width) { $str = $str.PadLeft([int]$width, '0') }
                        [void]$sb.Append($str)
                    }
                    $argIdx++
                    $i = $j + 1
                }
                'o' {
                    if ($argIdx -lt $converted.Count) {
                        $val = [int]$converted[$argIdx]
                        $str = [Convert]::ToString($val, 8)
                        if ($flags.Contains('#') -and -not $str.StartsWith('0')) { $str = "0$str" }
                        if ($width) { $str = $str.PadLeft([int]$width, '0') }
                        [void]$sb.Append($str)
                    }
                    $argIdx++
                    $i = $j + 1
                }
                'c' {
                    if ($argIdx -lt $converted.Count) {
                        $v = $converted[$argIdx]
                        if ($v -is [int] -or $v -is [long]) {
                            [void]$sb.Append([char][int]$v)
                        } else {
                            $s = [string]$v
                            if ($s.Length -gt 0) { [void]$sb.Append($s[0]) }
                        }
                    }
                    $argIdx++
                    $i = $j + 1
                }
                'b' {
                    if ($argIdx -lt $converted.Count) {
                        $expanded = Expand-EscapeSequences -Text ([string]$converted[$argIdx])
                        [void]$sb.Append($expanded)
                    }
                    $argIdx++
                    $i = $j + 1
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

    New-BashObject -BashText $result -NoTrailingNewline -Command 'printf'
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

# --- ls Provider Architecture ---
# Three-tier strategy for Invoke-BashLs:
#   1. Fast path  — real filesystem paths use System.IO streaming APIs (no Get-ChildItem, no Get-Acl)
#   2. Custom providers — user-registered handlers for synthetic/virtual paths
#   3. PS provider fallback — Get-ChildItem for Registry:, Cert:, Variable:, custom PSDrives, etc.
#
# Register a custom provider:
#   Register-BashLsProvider -Name 'MyProvider' -Detect { param($path) $path.StartsWith('myfs:') } -List { param($path,$flags) <yield LsEntry objects> }

$script:BashLsProviders = [System.Collections.Generic.List[hashtable]]::new()

function Register-BashLsProvider {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Detect,   # ($path) -> $true if this provider handles it
        [Parameter(Mandatory)][scriptblock]$List       # ($path, $flags) -> LsEntry objects
    )
    $script:BashLsProviders.Add(@{ Name = $Name; Detect = $Detect; List = $List })
}

# Build an LsEntry from a real System.IO.FileSystemInfo — no Get-Acl, no Get-ChildItem.
function Get-LsEntryFromFsi {
    [OutputType([PSCustomObject])]
    param([Parameter(Mandatory)][System.IO.FileSystemInfo]$Item)

    $attrs    = $Item.Attributes
    $isDir    = $Item -is [System.IO.DirectoryInfo]
    $isLink   = [bool]($attrs -band [System.IO.FileAttributes]::ReparsePoint)
    $typeChar = if ($isDir) { 'd' } elseif ($isLink) { 'l' } else { '-' }

    if ($IsWindows) {
        # Derive permissions from attributes — no ACL call (avoids Get-Acl latency and reserved-name failures)
        $ro    = [bool]($attrs -band [System.IO.FileAttributes]::ReadOnly)
        $execExts = '.exe','.bat','.cmd','.ps1','.sh','.com'
        $isExec = $isDir -or ($execExts -contains $Item.Extension.ToLowerInvariant())
        $r = 'r'; $w = if ($ro) { '-' } else { 'w' }; $x = if ($isExec) { 'x' } else { '-' }
        $perm = "$typeChar$r$w$x$r-$x$r-$x"
        $owner = $env:USERNAME; $group = $env:USERNAME
    } else {
        $mode = [int]$Item.UnixFileMode
        $perm = "$typeChar$(ConvertTo-PermissionString -Mode $mode)"
        $owner = ''; $group = ''
        $statArgs = if ($IsMacOS) { @('-f','%Su %Sg',$Item.FullName) } else { @('-c','%U %G',$Item.FullName) }
        $statOut = & /usr/bin/stat @statArgs 2>$null
        if ($statOut) { $parts = $statOut -split ' ',2; $owner = $parts[0]; $group = $parts[1] }
    }

    [PSCustomObject]@{
        PSTypeName   = 'PsBash.LsEntry'
        Name         = $Item.Name
        FullPath     = $Item.FullName
        IsDirectory  = $isDir
        IsSymlink    = $isLink
        SizeBytes    = if ($isDir) { 4096L } else { ([System.IO.FileInfo]$Item).Length }
        Permissions  = $perm
        LinkCount    = 1
        Owner        = $owner
        Group        = $group
        LastModified = $Item.LastWriteTime
        BashText     = ''
    }
}

# Build a best-effort LsEntry from any PSItem (Registry key, Cert, custom PSDrive item, etc.)
function Get-LsEntryFromPsItem {
    [OutputType([PSCustomObject])]
    param([Parameter(Mandatory)]$Item)

    $name  = if ($Item.PSChildName) { $Item.PSChildName } elseif ($Item.Name) { $Item.Name } else { "$Item" }
    $isDir = [bool]$Item.PSIsContainer
    $size  = if ($Item.PSObject.Properties['Length']) { [long]$Item.Length } else { 0L }
    $mtime = if ($Item.PSObject.Properties['LastWriteTime']) { $Item.LastWriteTime } else { [datetime]::MinValue }
    $perm  = if ($isDir) { 'dr-xr-xr-x' } else { '-r--r--r--' }

    [PSCustomObject]@{
        PSTypeName   = 'PsBash.LsEntry'
        Name         = $name
        FullPath     = if ($Item.PSPath) { $Item.PSPath } else { $name }
        IsDirectory  = $isDir
        IsSymlink    = $false
        SizeBytes    = $size
        Permissions  = $perm
        LinkCount    = 1
        Owner        = ''
        Group        = ''
        LastModified = $mtime
        BashText     = ''
    }
}

# Kept for backward-compat callers outside of ls (find, stat).
function Get-BashFileInfo {
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([Parameter(Mandatory)][System.IO.FileSystemInfo]$Item)
    Get-LsEntryFromFsi -Item $Item
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
        [AllowEmptyString()]
        [AllowEmptyCollection()]
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

function Test-IsExecutable {
    param([Parameter(Mandatory)][PSCustomObject]$Entry)
    if ($Entry.IsDirectory) { return $false }
    if ($Entry.IsSymlink) { return $false }
    if ($IsWindows) {
        $execExts = '.exe','.bat','.cmd','.ps1','.sh','.com'
        $ext = if ($Entry.Name.Contains('.')) {
            $Entry.Name.Substring($Entry.Name.LastIndexOf('.')).ToLowerInvariant()
        } else { '' }
        return $execExts -contains $ext
    }
    $perm = $Entry.Permissions
    if ($perm.Length -ge 4 -and $perm[3] -eq 'x') { return $true }
    if ($perm.Length -ge 7 -and $perm[6] -eq 'x') { return $true }
    if ($perm.Length -ge 10 -and $perm[9] -eq 'x') { return $true }
    $false
}

function Invoke-BashLs {
    [OutputType('PsBash.LsEntry')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'ls' }

    $defs = New-FlagDefs -Entries @(
        '-l', 'long listing'
        '-a', 'show hidden (dotfiles + Windows hidden attr)'
        '-A', 'almost-all (like -a but excludes . and ..)'
        '-h', 'human readable sizes'
        '-R', 'recursive'
        '-S', 'sort by size'
        '-t', 'sort by time'
        '-r', 'reverse sort'
        '-1', 'one per line'
        '-p', 'append / to directories'
        '-d', 'list directories themselves'
        '-F', 'classify (append */=>@| type indicators)'
        '--color', 'colorize output'
        '-i', 'show inode number'
        '-s', 'show allocated size in blocks'
    )

    $parsed    = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs
    $longMode     = $parsed.Flags['-l']
    $showHidden   = $parsed.Flags['-a']
    $almostAll    = $parsed.Flags['-A']
    $humanSizes   = $parsed.Flags['-h']
    $recursive    = $parsed.Flags['-R']
    $sortBySize   = $parsed.Flags['-S']
    $sortByTime   = $parsed.Flags['-t']
    $reverseSort  = $parsed.Flags['-r']
    $dirOnly      = $parsed.Flags['-d']
    $classify     = $parsed.Flags['-F'] -or $parsed.Flags['-p'] -or $longMode
    $colorize     = $parsed.Flags['--color']
    $showInode    = $parsed.Flags['-i']
    $showBlocks   = $parsed.Flags['-s']

    if ($almostAll) { $showHidden = $true }

    $targets   = Resolve-BashGlob -Paths $(if ($parsed.Operands.Count -gt 0) { $parsed.Operands } else { @('.') })

    $allEntries= [System.Collections.Generic.List[PSCustomObject]]::new()
    $hadError  = $false

    foreach ($target in $targets) {

        # ── Tier 1: custom provider ──────────────────────────────────────────
        $customProvider = $null
        foreach ($cp in $script:BashLsProviders) {
            if (& $cp.Detect $target) { $customProvider = $cp; break }
        }
        if ($null -ne $customProvider) {
            $flags = @{ Long=$longMode; Hidden=$showHidden; Recursive=$recursive }
            foreach ($e in (& $customProvider.List $target $flags)) { $allEntries.Add($e) }
            continue
        }

        # ── Tier 2: real filesystem — System.IO streaming ───────────────────
        $resolvedPath = $null
        try { $resolvedPath = [System.IO.Path]::GetFullPath($target) } catch { }

        if ($null -ne $resolvedPath -and [System.IO.Directory]::Exists($resolvedPath)) {
            if ($dirOnly) {
                # -d: list the directory itself, not its contents
                $allEntries.Add((Get-LsEntryFromFsi -Item ([System.IO.DirectoryInfo]::new($resolvedPath))))
            } else {
                try {
                    $dirInfo = [System.IO.DirectoryInfo]::new($resolvedPath)
                    $searchOpt = if ($recursive) {
                        [System.IO.SearchOption]::AllDirectories
                    } else {
                        [System.IO.SearchOption]::TopDirectoryOnly
                    }
                    foreach ($fsi in $dirInfo.EnumerateFileSystemInfos('*', $searchOpt)) {
                        $attrs = $fsi.Attributes
                        if (-not $showHidden) {
                            if ($fsi.Name[0] -eq '.') { continue }
                            if ($IsWindows -and ($attrs -band [System.IO.FileAttributes]::Hidden)) { continue }
                        }
                        $allEntries.Add((Get-LsEntryFromFsi -Item $fsi))
                    }
                } catch {
                    Write-BashError "ls: cannot open directory '$target': $($_.Exception.Message)" -ExitCode 2
                    $hadError = $true
                }
            }
            continue
        }

        if ($null -ne $resolvedPath -and [System.IO.File]::Exists($resolvedPath)) {
            $allEntries.Add((Get-LsEntryFromFsi -Item ([System.IO.FileInfo]::new($resolvedPath))))
            continue
        }

        # ── Tier 3: PS provider fallback (Registry:, Cert:, custom PSDrives) ─
        $psItem = $null
        try { $psItem = Get-Item -LiteralPath $target -Force -ErrorAction Stop } catch { }

        if ($null -ne $psItem) {
            if ($psItem.PSIsContainer -and -not $dirOnly) {
                $children = Get-ChildItem -LiteralPath $target -Force -ErrorAction SilentlyContinue
                foreach ($child in $children) {
                    if (-not $showHidden -and $child.Name[0] -eq '.') { continue }
                    $allEntries.Add((Get-LsEntryFromPsItem -Item $child))
                }
            } else {
                $allEntries.Add((Get-LsEntryFromPsItem -Item $psItem))
            }
            continue
        }

        Write-BashError "ls: cannot access '$target': No such file or directory" -ExitCode 2
        $hadError = $true
    }

    # ── Sort ─────────────────────────────────────────────────────────────────
    # Bash default: case-insensitive alphabetical, dirs and files interleaved
    $sorted = if ($sortBySize) {
        $allEntries | Sort-Object -Property SizeBytes -Descending:(-not $reverseSort)
    } elseif ($sortByTime) {
        $allEntries | Sort-Object -Property LastModified -Descending:(-not $reverseSort)
    } else {
        $cmp = [System.StringComparer]::OrdinalIgnoreCase
        $ordered = [System.Linq.Enumerable]::OrderBy(
            [System.Collections.Generic.IEnumerable[PSCustomObject]]$allEntries,
            [System.Func[PSCustomObject,string]]{ param($e) $e.Name },
            $cmp)
        if ($reverseSort) { [System.Linq.Enumerable]::Reverse($ordered) } else { $ordered }
    }

    # ── Format and emit ───────────────────────────────────────────────────────
    $Reset  = "`e[0m"
    $Bold   = "`e[1m"
    $Blue   = "`e[34m"
    $Cyan   = "`e[36m"
    $Green  = "`e[32m"
    $Red    = "`e[31m"

    foreach ($entry in $sorted) {
        $indicator = ''
        if ($parsed.Flags['-F']) {
            if ($entry.IsDirectory) { $indicator = '/' }
            elseif ($entry.IsSymlink) { $indicator = '@' }
            elseif (Test-IsExecutable -Entry $entry) { $indicator = '*' }
        } elseif ($parsed.Flags['-p']) {
            if ($entry.IsDirectory) { $indicator = '/' }
        } elseif ($longMode) {
            # -l implicitly shows type via perms column
        }

        if ($longMode) {
            $line = Format-LsLine -Entry $entry -HumanReadable:$humanSizes
            if ($classify) { $line += $indicator }
            $entry.BashText = "$line`n"
        } else {
            $name = $entry.Name
            if ($colorize) {
                if ($entry.IsDirectory) { $name = "${Blue}${Bold}${name}${Reset}" }
                elseif ($entry.IsSymlink) { $name = "${Cyan}${name}${Reset}" }
                elseif (Test-IsExecutable -Entry $entry) { $name = "${Green}${name}${Reset}" }
            }
            $entry.BashText = "${name}${indicator}`n"
        }
        Set-BashDisplayProperty $entry
    }

    if ($hadError) { $global:LASTEXITCODE = 2 }
}

# --- cat Command ---

function Invoke-BashCat {
    [OutputType('PsBash.CatLine')]
    [OutputType('PsBash.TextOutput')]
    param()
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
    $hasFlags = $numberAll -or $numberNonBlank -or $squeezeBlanks -or $showEnds -or $showTabs

    $operands = $parsed.Operands
    $readStdin = $operands.Count -eq 0 -or $operands -contains '-'

    # Fast path: bare cat with no flags emits lightweight TextOutput objects
    if (-not $hasFlags) {
        $hadError = $false

        if ($readStdin -and $pipelineInput.Count -gt 0) {
            foreach ($item in $pipelineInput) {
                $text = Get-BashText -InputObject $item
                if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                    foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                        $subLine
                    }
                } else {
                    $item
                }
            }
        }

        $fileOperands = @($operands | Where-Object { $_ -ne '-' })
        $resolvedFiles = @(Resolve-BashGlob -Paths $fileOperands)
        foreach ($filePath in $resolvedFiles) {
            $content = Read-BashFileBytes -Path $filePath -Command 'cat'
            if ($null -eq $content) { $hadError = $true; continue }
            Emit-BashLine -Text $content
        }

        if ($hadError) {
            $global:LASTEXITCODE = 1
        }
        return
    }

    # Flagged path: full CatLine objects with line numbering, squeezing, etc.
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
            BashText   = "$text`n"
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
        $reader = Open-BashFileReader -Path $filePath -Command 'cat'
        if ($null -eq $reader) { $hadError = $true; continue }

        try {
            while ($null -ne ($line = $reader.ReadLine())) {
                & $emitLine $line $filePath
            }
        } finally {
            $reader.Dispose()
        }
    }

    if ($hadError) {
        $global:LASTEXITCODE = 1
    }
}

# --- File Redirect Helper ---

function Invoke-BashRedirect {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $filePath = $null
    $append = $false
    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-Append') { $append = $true; $i++; continue }
        if ($arg -ceq '-Path' -and ($i + 1) -lt $Arguments.Count) { $i++; $filePath = $Arguments[$i]; $i++; continue }
        if ($null -eq $filePath) { $filePath = $arg }
        $i++
    }

    if ($null -eq $filePath) { return }

    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $pipelineInput) {
        $text = Get-BashText -InputObject $item
        $text = $text.TrimEnd("`n".ToCharArray())
        $lines.Add($text)
    }
    $content = ($lines -join "`n")
    if ($lines.Count -gt 0) { $content += "`n" }

    if ($append) {
        [System.IO.File]::AppendAllText($filePath, $content)
    } else {
        [System.IO.File]::WriteAllText($filePath, $content)
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
    if ($InputObject -is [string]) { return $InputObject }
    if ($null -ne $InputObject.PSObject -and $null -ne $InputObject.PSObject.Properties['BashText']) {
        return [string]$InputObject.BashText
    }
    return "$InputObject"
}

