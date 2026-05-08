# --- find Command ---

function Invoke-BashFind {
    [OutputType('PsBash.FindEntry')]
    param()
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
    $printNull = $false      # -print0: use null delimiters
    $execCmd = $null          # -exec command template
    $execTerminator = $null   # ';' or '+'
    $operands = [System.Collections.Generic.List[string]]::new()

    # Known unsupported predicates (value-bearing and standalone) for strict mode warnings
    $unsupportedValuePredicates = @('-iname','-path','-ipath','-regex','-iregex','-newer',
        '-perm','-user','-group','-printf','-mindepth','-amin','-atime','-cmin','-ctime',
        '-gid','-uid','-links','-samefile','-wholename','-iwholename','-lname','-ilname')
    $unsupportedStandalonePredicates = @('-delete','-print','-prune','-depth',
        '-follow','-ls','-mount','-xdev','-noleaf','-daystart','-warn','-nowarn',
        '-not','-or','-o','-and','-a','-true','-false')

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
            '-print0' {
                $printNull = $true
                $i++
                continue
            }
            '--print0' {
                $printNull = $true
                $i++
                continue
            }
            '-exec' {
                # Collect -exec args until ';' or '+'
                $i++
                $execCmd = [System.Collections.Generic.List[string]]::new()
                while ($i -lt $Arguments.Count) {
                    $ea = $Arguments[$i]
                    if ($ea -eq ';' -or $ea -eq '+') {
                        $execTerminator = $ea
                        $i++
                        break
                    }
                    $execCmd.Add($ea)
                    $i++
                }
                continue
            }
            default {
                # Check for unsupported predicates
                if ($unsupportedValuePredicates -contains $arg) {
                    Write-BashError -Message "find: unsupported predicate '$arg'" -ExitCode 1
                    $i += 2  # skip the predicate and its value
                    continue
                }
                if ($unsupportedStandalonePredicates -contains $arg) {
                    Write-BashError -Message "find: unsupported predicate '$arg'" -ExitCode 1
                    $i++
                    continue
                }
                $operands.Add($arg)
                $i++
            }
        }
    }

    if ($operands.Count -gt 0) {
        $searchPath = $operands[0]
    }

    $rootItem = Get-BashItem -Path $searchPath -Command 'find'
    if ($null -eq $rootItem) {
        $global:LASTEXITCODE = 1
        return
    }

    $resolvedRoot = $rootItem.FullName
    $rootDepth = ($resolvedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) -split '[\\/]').Count

    # Collect filesystem items, respecting maxdepth during enumeration to avoid OOM on large trees
    $allItems = [System.Collections.Generic.List[System.IO.FileSystemInfo]]::new()

    # Include the search path itself (find includes the root)
    $allItems.Add($rootItem)

    if ($rootItem -is [System.IO.DirectoryInfo]) {
        try {
            # Use -Depth to cap enumeration at the OS level; avoids loading all of node_modules into memory
            $gcArgs = @{ LiteralPath = $resolvedRoot; Force = $true; ErrorAction = 'SilentlyContinue' }
            if ($maxDepth -lt [int]::MaxValue) {
                $gcArgs['Depth'] = $maxDepth - 1
            } else {
                $gcArgs['Recurse'] = $true
            }
            $children = Get-ChildItem @gcArgs
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
    $execCollectedPaths = [System.Collections.Generic.List[string]]::new()
    $nullDelimitedPaths = [System.Text.StringBuilder]::new()  # For -print0 mode

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

        if ($null -ne $execCmd) {
            if ($execTerminator -eq ';') {
                # -exec cmd {} \; — run once per file, replacing {} with path
                $cmdArgs = @($execCmd | ForEach-Object { if ($_ -eq '{}') { $displayPath } else { $_ } })
                $cmdName = $cmdArgs[0]
                $cmdRest = if ($cmdArgs.Count -gt 1) { $cmdArgs[1..($cmdArgs.Count - 1)] } else { @() }
                & $cmdName @cmdRest
            } else {
                # -exec cmd {} + — collect paths, run once at end
                $execCollectedPaths.Add($displayPath)
            }
        } elseif ($printNull) {
            # -print0: accumulate paths with null delimiters
            [void]$nullDelimitedPaths.Append($displayPath)
            [void]$nullDelimitedPaths.Append("`0")
        } else {
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
                BashText     = "$displayPath`n"
            }
            Set-BashDisplayProperty $obj
        }
    }

    # -exec cmd {} + — execute once with all collected paths
    if ($null -ne $execCmd -and $execTerminator -eq '+' -and $execCollectedPaths.Count -gt 0) {
        $cmdArgs = [System.Collections.Generic.List[string]]::new()
        foreach ($ea in $execCmd) {
            if ($ea -eq '{}') {
                $cmdArgs.AddRange($execCollectedPaths)
            } else {
                $cmdArgs.Add($ea)
            }
        }
        $cmdName = $cmdArgs[0]
        $cmdRest = if ($cmdArgs.Count -gt 1) { $cmdArgs[1..($cmdArgs.Count - 1)] } else { @() }
        & $cmdName @cmdRest
    }

    # Output null-delimited paths if -print0 was used
    if ($printNull -and $nullDelimitedPaths.Length -gt 0) {
        $outputText = $nullDelimitedPaths.ToString()
        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.TextOutput'
            BashText = $outputText
            NoTrailingNewline = $true
        }
        Set-BashDisplayProperty $obj
    }
}

# --- stat Command ---

function Invoke-BashStat {
    [OutputType('PsBash.StatEntry')]
    param()
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
        Write-BashError -Message "stat: missing operand"
        return
    }

    $hadError = $false

    foreach ($target in $operands) {
        $item = Get-BashItem -Path $target -Command 'stat' -Verb 'cannot stat'
        if ($null -eq $item) {
            $hadError = $true
            continue
        }
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
    [OutputType('PsBash.TextOutput')]
    param()
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
        Write-BashError -Message "cp: missing file operand"
        return
    }

    $dest = $parsed.Operands[$parsed.Operands.Count - 1]
    $sources = Resolve-BashGlob -Paths $parsed.Operands[0..($parsed.Operands.Count - 2)]

    $hadError = $false

    foreach ($src in $sources) {
        $srcItem = Get-BashItem -Path $src -Command 'cp'
        if ($null -eq $srcItem) {
            $hadError = $true
            continue
        }

        $isDir = $srcItem -is [System.IO.DirectoryInfo]

        if ($isDir -and -not $recursive) {
            Write-BashError -Message "cp: -r not specified; omitting directory '$src'"
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
    [OutputType('PsBash.TextOutput')]
    param()
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
        Write-BashError -Message "mv: missing file operand"
        return
    }

    $dest = $parsed.Operands[$parsed.Operands.Count - 1]
    $sources = Resolve-BashGlob -Paths $parsed.Operands[0..($parsed.Operands.Count - 2)]

    $hadError = $false

    foreach ($src in $sources) {
        $srcItem = Get-BashItem -Path $src -Command 'mv'
        if ($null -eq $srcItem) {
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
    [OutputType('PsBash.TextOutput')]
    param()
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
            Write-BashError -Message "rm: missing operand"
        }
        return
    }

    $resolvedOperands = Resolve-BashGlob -Paths $parsed.Operands
    $hadError = $false

    $winReservedNames = @('CON','PRN','AUX','NUL','COM1','COM2','COM3','COM4','COM5','COM6','COM7','COM8','COM9','LPT1','LPT2','LPT3','LPT4','LPT5','LPT6','LPT7','LPT8','LPT9')

    foreach ($target in $resolvedOperands) {
        if ($IsWindows) {
            $leaf = Split-Path -Leaf $target
            $baseName = if ($leaf.Contains('.')) { $leaf.Substring(0, $leaf.IndexOf('.')) } else { $leaf }
            if ($winReservedNames -contains $baseName.ToUpperInvariant()) {
                Write-BashError -Message "rm: cannot remove '$target': Windows reserved device name"
                $hadError = $true
                continue
            }
        }
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
                    Write-BashError -Message "rm: refusing to remove '$target': protected path"
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
                Write-BashError -Message "rm: cannot remove '$target': No such file or directory"
                $hadError = $true
            }
            continue
        }

        $item = Get-BashItem -Path $target -Command 'rm'
        if ($null -eq $item) {
            $hadError = $true
            continue
        }
        $isDir = $item -is [System.IO.DirectoryInfo]

        if ($isDir -and -not $recursive) {
            Write-BashError -Message "rm: cannot remove '$target': Is a directory"
            $hadError = $true
            continue
        }

        if ($verbose) {
            if ($isDir -and $recursive) {
                $children = Get-ChildItem -LiteralPath $target -Force -Recurse -ErrorAction SilentlyContinue
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
    [OutputType('PsBash.TextOutput')]
    param()
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
        Write-BashError -Message "mkdir: missing operand"
        return
    }

    $hadError = $false

    foreach ($dir in $parsed.Operands) {
        if (Test-Path -LiteralPath $dir) {
            if (-not $parents) {
                Write-BashError -Message "mkdir: cannot create directory '$dir': File exists"
                $hadError = $true
            }
            continue
        }

        $parentDir = Split-Path $dir -Parent
        if ($parentDir -and -not (Test-Path -LiteralPath $parentDir) -and -not $parents) {
            Write-BashError -Message "mkdir: cannot create directory '$dir': No such file or directory"
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
    [OutputType('PsBash.TextOutput')]
    param()
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
        Write-BashError -Message "rmdir: missing operand"
        return
    }

    $hadError = $false

    foreach ($dir in $parsed.Operands) {
        $item = Get-BashItem -Path $dir -Command 'rmdir'
        if ($null -eq $item) {
            $hadError = $true
            continue
        }

        if ($item -isnot [System.IO.DirectoryInfo]) {
            Write-BashError -Message "rmdir: failed to remove '$dir': Not a directory"
            $hadError = $true
            continue
        }

        $children = Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue
        if ($children) {
            Write-BashError -Message "rmdir: failed to remove '$dir': Directory not empty"
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
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'touch' }

    # Manual arg parsing for flags
    $verbose = $false
    $dateStr = $null
    $accessOnly = $false
    $modOnly = $false
    $noCreate = $false
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
            '-a' {
                $accessOnly = $true
                $i++
                continue
            }
            '-m' {
                $modOnly = $true
                $i++
                continue
            }
            '-c' {
                $noCreate = $true
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
        Write-BashError -Message "touch: missing file operand"
        return
    }

    $timestamp = [System.DateTime]::Now
    if ($null -ne $dateStr) {
        try {
            $timestamp = [System.DateTime]::Parse($dateStr)
        } catch {
            Write-BashError -Message "touch: invalid date format '$dateStr'"
            return
        }
    }

    foreach ($file in $operands) {
        if (Test-Path -LiteralPath $file) {
            $item = Get-BashItem -Path $file -Command 'touch'
            if ($null -eq $item) { continue }
            if (-not $accessOnly) { $item.LastWriteTime = $timestamp }
            if (-not $modOnly)    { $item.LastAccessTime = $timestamp }
        } else {
            if ($noCreate) { continue }
            $parentDir = Split-Path $file -Parent
            if ($parentDir -and -not (Test-Path -LiteralPath $parentDir)) {
                Write-BashError -Message "touch: cannot touch '$file': No such file or directory"
                continue
            }
            New-Item -Path $file -ItemType File -Force | Out-Null
            $item = Get-BashItem -Path $file -Command 'touch'
            if ($null -eq $item) { continue }
            if (-not $accessOnly) { $item.LastWriteTime = $timestamp }
            if (-not $modOnly)    { $item.LastAccessTime = $timestamp }
        }
    }
}

# --- ln Command ---

function Invoke-BashLn {
    [OutputType('PsBash.TextOutput')]
    param()
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
        Write-BashError -Message "ln: missing file operand"
        return
    }

    $target = $parsed.Operands[0]
    $linkName = $parsed.Operands[1]

    if ($force -and (Test-Path -LiteralPath $linkName)) {
        Remove-Item -LiteralPath $linkName -Force
    }

    if (Test-Path -LiteralPath $linkName) {
        Write-BashError -Message "ln: failed to create $( if ($symbolic) { 'symbolic ' } else { '' })link '$linkName': File exists"
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
            try { if ($p.SessionId -eq [System.Diagnostics.Process]::GetCurrentProcess().SessionId) { $userName = $env:USERNAME } } catch {}
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
    [OutputType('PsBash.PsEntry')]
    param()
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

