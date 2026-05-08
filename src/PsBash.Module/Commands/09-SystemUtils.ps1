# --- du Command ---

function Invoke-BashDu {
    [OutputType('PsBash.DuEntry')]
    param()
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
        $rootItem = Get-BashItem -Path $target -Command 'du'
        if ($null -eq $rootItem) {
            continue
        }

        $resolvedRoot = $rootItem.FullName

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
    [OutputType('PsBash.TreeEntry')]
    param()
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
    $rootItem = Get-BashItem -Path $target -Command 'tree'
    if ($null -eq $rootItem) {
        $global:LASTEXITCODE = 1
        return
    }

    $resolvedRoot = $rootItem.FullName
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
    [OutputType('PsBash.EnvEntry')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'env' }

    if ($Arguments.Count -gt 0) {
        $varName = $Arguments[0]
        $val = [System.Environment]::GetEnvironmentVariable($varName)
        if ($null -eq $val) {
            Write-BashError -Message "env: '$varName': not set"
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
    [OutputType('PsBash.TextOutput')]
    param()
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
    [OutputType('PsBash.TextOutput')]
    param()
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
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'pwd' }

    $physical = $false
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-P') { $physical = $true }
    }

    $location = if ($physical) {
        try {
            (Resolve-Path -Path (Get-Location).Path).ProviderPath
        } catch {
            Write-BashError -Message "pwd: error resolving path: $($_.Exception.Message)"
            return
        }
    } else {
        (Get-Location).Path
    }

    $location = $location -replace '\\', '/'
    New-BashObject -BashText $location -TypeName 'PsBash.TextOutput'
}

# --- hostname ---

function Invoke-BashHostname {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'hostname' }
    try {
        $name = [System.Net.Dns]::GetHostName()
    } catch {
        Write-BashError -Message "hostname: $($_.Exception.Message)"
        return
    }
    New-BashObject -BashText $name -TypeName 'PsBash.TextOutput'
}

# --- whoami ---

function Invoke-BashWhoami {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'whoami' }
    $name = [System.Environment]::UserName
    New-BashObject -BashText $name -TypeName 'PsBash.TextOutput'
}

# --- uname ---

function Invoke-BashUname {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'uname' }

    $flagS = $false
    $flagN = $false
    $flagR = $false
    $flagM = $false
    $flagA = $false

    foreach ($arg in $Arguments) {
        if ($arg -cmatch '^-([snrma]+)$') {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    's' { $flagS = $true }
                    'n' { $flagN = $true }
                    'r' { $flagR = $true }
                    'm' { $flagM = $true }
                    'a' { $flagA = $true }
                }
            }
        } elseif ($arg -ceq '-s') { $flagS = $true }
        elseif ($arg -ceq '-n') { $flagN = $true }
        elseif ($arg -ceq '-r') { $flagR = $true }
        elseif ($arg -ceq '-m') { $flagM = $true }
        elseif ($arg -ceq '-a') { $flagA = $true }
    }

    $osVer = [System.Environment]::OSVersion
    $ver = $osVer.Version
    $release = "$($ver.Major).$($ver.Minor).$($ver.Build)"
    $sysName = "MINGW64_NT-$release"
    $hostName = [System.Environment]::MachineName.ToLower()
    $arch = if ([System.Environment]::Is64BitProcess) { 'x86_64' } else { 'i686' }

    if ($flagA) {
        $text = "$sysName $hostName $release $arch MINGW64"
    } else {
        $anyFlag = $flagS -or $flagN -or $flagR -or $flagM
        if (-not $anyFlag) { $flagS = $true }
        $parts = @()
        if ($flagS) { $parts += $sysName }
        if ($flagN) { $parts += $hostName }
        if ($flagR) { $parts += $release }
        if ($flagM) { $parts += $arch }
        $text = $parts -join ' '
    }

    New-BashObject -BashText $text -TypeName 'PsBash.TextOutput'
}

# --- fold Command ---

function Invoke-BashFold {
    [OutputType('PsBash.TextOutput')]
    param()
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
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) { $lines.Add($subLine) }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'fold'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) { $lines.Add($l) }
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
    [OutputType('PsBash.TextOutput')]
    param()
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
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) { $lines.Add($subLine) }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'expand'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) { $lines.Add($l) }
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
    [OutputType('PsBash.TextOutput')]
    param()
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
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) { $lines.Add($subLine) }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'unexpand'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) { $lines.Add($l) }
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
    [OutputType('PsBash.TextOutput')]
    param()
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
            $fileText = Read-BashFileBytes -Path $filePath -Command 'strings'
            if ($null -eq $fileText) { continue }
            $content += $fileText
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
    [OutputType('PsBash.TextOutput')]
    param()
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
        if ($filePath -ne '-') {
            $filePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($filePath)
        }
        if ($filePath -eq '-') {
            foreach ($item in $pipelineInput) {
                $text = Get-BashText -InputObject $item
                if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                    foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) { $lines.Add($subLine) }
                } else {
                    $lines.Add(($text.TrimEnd("`n".ToCharArray())))
                }
            }
        } else {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'split'
            if ($null -eq $fileLines) { return }
            foreach ($l in $fileLines) { $lines.Add($l) }
        }
        if ($operands.Count -ge 2) { $prefix = $operands[1] }
    } elseif ($pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) { $lines.Add($subLine) }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        Write-BashError -Message 'split: missing operand'
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
        if (-not (Write-BashFileText -Path $outPath -Text $content -Command 'split')) { return }
        $chunkIndex++
    }
}

# --- tac Command ---

function Invoke-BashTac {
    [OutputType('PsBash.TextOutput')]
    param()
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
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) { $lines.Add($subLine) }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'tac'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) { $lines.Add($l) }
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
    [OutputType('PsBash.TextOutput')]
    param()
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
        $filePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[0])
        if ($decode) {
            $fileText = Read-BashFileBytes -Path $filePath -Command 'base64'
            if ($null -eq $fileText) { return }
            $rawText = $fileText.Trim()
        } else {
            try {
                $rawBytes = [System.IO.File]::ReadAllBytes($filePath)
            } catch {
                $normalized = $filePath -replace '\\', '/'
                Write-BashError -Message "base64: ${normalized}: $($_.Exception.Message)"
                return
            }
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
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'md5sum' }
    Invoke-BashChecksum -Algorithm 'MD5' -CommandName 'md5sum' -Arguments $Arguments -PipelineInput $pipelineInput
}

# --- sha1sum Command ---

function Invoke-BashSha1sum {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'sha1sum' }
    Invoke-BashChecksum -Algorithm 'SHA1' -CommandName 'sha1sum' -Arguments $Arguments -PipelineInput $pipelineInput
}

# --- sha256sum Command ---

function Invoke-BashSha256sum {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'sha256sum' }
    Invoke-BashChecksum -Algorithm 'SHA256' -CommandName 'sha256sum' -Arguments $Arguments -PipelineInput $pipelineInput
}

# --- file Command ---

function Invoke-BashFile {
    [OutputType('PsBash.TextOutput')]
    param()
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

    $hadError = $false
    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-BashError -Message "file: cannot open '${filePath}' (No such file or directory)"
            $hadError = $true
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

