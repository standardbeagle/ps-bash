# --- rg (ripgrep-style search) ---

function Invoke-BashRg {
    [OutputType('PsBash.RgMatch')]
    param()
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
        Write-BashError -Message 'rg: usage: rg [options] pattern [path ...]' -ExitCode 2
        return
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
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $isMatch = $regex.IsMatch($subLine)
                    if ($invertMatch) { $isMatch = -not $isMatch }
                    if ($isMatch) {
                        $matchCount++
                        if (-not $countOnly) {
                            if ($onlyMatching) {
                                foreach ($m in $regex.Matches($subLine)) {
                                    New-BashObject -BashText $m.Value
                                }
                            } else {
                                New-BashObject -BashText $subLine
                            }
                        }
                    }
                }
            } else {
                $lineText = $text.TrimEnd("`n".ToCharArray())
                $isMatch = $regex.IsMatch($lineText)
                if ($invertMatch) { $isMatch = -not $isMatch }
                if ($isMatch) {
                    $matchCount++
                    if (-not $countOnly) {
                        if ($onlyMatching) {
                            foreach ($m in $regex.Matches($lineText)) {
                                New-BashObject -BashText $m.Value
                            }
                        } else {
                            $item
                        }
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
            Write-BashError -Message "rg: ${target}: No such file or directory"
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
        $lines = Read-BashFileLines -Path $filePath -Command 'rg'
        if ($null -eq $lines) { continue }

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
    [OutputType('PsBash.TextOutput')]
    param()
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
        Write-BashError -Message 'gzip: missing file operand'
        return
    }

    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-BashError -Message "gzip: ${filePath}: No such file or directory"
            continue
        }

        if ($list) {
            $compressedBytes = Read-BashFileRaw -Path $filePath -Command 'gzip'
            if ($null -eq $compressedBytes) { continue }
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
            $compressedBytes = Read-BashFileRaw -Path $filePath -Command 'gzip'
            if ($null -eq $compressedBytes) { continue }
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
                if (-not (Write-BashFileRaw -Path $outPath -Data $outBytes -Command 'gzip')) { continue }
                if (-not $keep) { Remove-Item -LiteralPath $filePath -Force }
                if ($verbose) {
                    $ratio = if ($outBytes.Length -gt 0) {
                        '{0:F1}%' -f ((1.0 - ($compressedBytes.Length / $outBytes.Length)) * 100)
                    } else { '0.0%' }
                    New-BashObject -BashText "${filePath}: $ratio"
                }
            }
        } else {
            $rawBytes = Read-BashFileRaw -Path $filePath -Command 'gzip'
            if ($null -eq $rawBytes) { continue }
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
                if (-not (Write-BashFileRaw -Path $outPath -Data $compressedBytes -Command 'gzip')) { continue }
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
    [OutputType('PsBash.TextOutput')]
    param()
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

    if ($archiveFile) {
        $archiveFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($archiveFile)
    }
    if ($changeDir) {
        $changeDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($changeDir)
    }

    if (-not $archiveFile) {
        Write-BashError -Message 'tar: you must specify -f archive'
        return
    }

    Add-Type -AssemblyName System.Formats.Tar -ErrorAction SilentlyContinue

    # Determine mode
    if ($create) {
        $sources = @($operands)
        if ($sources.Count -eq 0) {
            Write-BashError -Message 'tar: no files or directories specified'
            return
        }
        $outStream = $null; $tarStream = $null; $writer = $null
        try {
            $outStream = [System.IO.File]::Open($archiveFile, 'Create', 'Write', 'None')
            $tarStream = if ($gzipFilter) {
                [System.IO.Compression.GZipStream]::new($outStream, [System.IO.Compression.CompressionMode]::Compress)
            } else { $outStream }

            $writer = [System.Formats.Tar.TarWriter]::new($tarStream)
            foreach ($src in $sources) {
                $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($src)
                if (-not (Test-Path $resolved)) {
                    Write-BashError -Message "tar: ${src}: Cannot stat: No such file or directory"
                    continue
                }
                $item = Get-BashItem -Path $resolved -Command 'tar'
                if ($item.PSIsContainer) {
                    $root = [System.IO.Path]::GetFileName($resolved)
                    $baseDir = [System.IO.Path]::GetDirectoryName($resolved)
                    $enumOpts = [System.IO.EnumerationOptions]::new()
                    $enumOpts.RecurseSubdirectories = $true
                    $children = [System.IO.Directory]::GetFileSystemEntries($resolved, '*', $enumOpts)
                    $writer.WriteEntry($resolved, $root)
                    if ($verbose) { Write-Output $root }
                    foreach ($child in $children) {
                        $skip = $false
                        foreach ($pat in $excludePatterns) {
                            if ($child -like "*$pat*") { $skip = $true; break }
                        }
                        if ($skip) { continue }
                        $relPath = $child.Substring($baseDir.Length + 1).Replace('\', '/')
                        if ($verbose) { Write-Output $relPath }
                        $writer.WriteEntry($child, $relPath)
                    }
                } else {
                    $skip = $false
                    foreach ($pat in $excludePatterns) {
                        if ($resolved -like "*$pat*") { $skip = $true; break }
                    }
                    if ($skip) { continue }
                    $relPath = [System.IO.Path]::GetFileName($resolved)
                    if ($verbose) { Write-Output $relPath }
                    $writer.WriteEntry($resolved, $relPath)
                }
            }
        } catch {
            Write-BashError -Message "tar: $_" -ExitCode 1
        } finally {
            if ($null -ne $writer) { $writer.Dispose() }
            if ($null -ne $tarStream -and $gzipFilter) { $tarStream.Dispose() }
            if ($null -ne $outStream) { $outStream.Dispose() }
        }
    }
    elseif ($extract) {
        if (-not (Test-Path $archiveFile)) {
            Write-BashError -Message "tar: ${archiveFile}: Cannot open: No such file or directory"
            return
        }
        $isGz = $gzipFilter -or $archiveFile -match '\.(tar\.gz|tgz)$'
        $destDir = if ($changeDir) { $changeDir } else { $PWD.Path }
        $inStream = $null; $tarStream = $null; $reader = $null
        try {
            $inStream = [System.IO.File]::OpenRead($archiveFile)
            $tarStream = if ($isGz) {
                [System.IO.Compression.GZipStream]::new($inStream, [System.IO.Compression.CompressionMode]::Decompress)
            } else { $inStream }

            $reader = [System.Formats.Tar.TarReader]::new($tarStream)
            while ($null -ne ($entry = $reader.GetNextEntry($true))) {
                if ($null -eq $entry.DataStream) { continue }
                $targetPath = [System.IO.Path]::Join($destDir, $entry.Name.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
                $dir = [System.IO.Path]::GetDirectoryName($targetPath)
                if ($dir -and -not [System.IO.Directory]::Exists($dir)) {
                    [System.IO.Directory]::CreateDirectory($dir) | Out-Null
                }
                if ($verbose) { Write-Output $entry.Name }
                $fs = [System.IO.File]::Create($targetPath)
                try { $entry.DataStream.CopyTo($fs) } finally { $fs.Dispose() }
            }
        } catch {
            Write-BashError -Message "tar: $_" -ExitCode 1
        } finally {
            if ($null -ne $reader) { $reader.Dispose() }
            if ($null -ne $tarStream -and $isGz) { $tarStream.Dispose() }
            if ($null -ne $inStream) { $inStream.Dispose() }
        }
    }
    elseif ($listMode) {
        if (-not (Test-Path $archiveFile)) {
            Write-BashError -Message "tar: ${archiveFile}: Cannot open: No such file or directory"
            return
        }
        $isGz = $gzipFilter -or $archiveFile -match '\.(tar\.gz|tgz)$'
        $inStream = $null; $tarStream = $null; $reader = $null
        try {
            $inStream = [System.IO.File]::OpenRead($archiveFile)
            $tarStream = if ($isGz) {
                [System.IO.Compression.GZipStream]::new($inStream, [System.IO.Compression.CompressionMode]::Decompress)
            } else { $inStream }

            $reader = [System.Formats.Tar.TarReader]::new($tarStream)
            while ($null -ne ($entry = $reader.GetNextEntry($false))) {
                $name = $entry.Name
                if ($entry.EntryType -eq 'Directory') {
                    $name = $name.TrimEnd('/') + '/'
                }
                $leaf = [System.IO.Path]::GetFileName($name.TrimEnd('/'))
                $obj = [PSCustomObject]@{
                    PSTypeName = 'PsBash.TarListOutput'
                    BashText   = $name
                    Name       = $leaf
                }
                $obj
            }
        } catch {
            Write-BashError -Message "tar: $_" -ExitCode 1
        } finally {
            if ($null -ne $reader) { $reader.Dispose() }
            if ($null -ne $tarStream -and $isGz) { $tarStream.Dispose() }
            if ($null -ne $inStream) { $inStream.Dispose() }
        }
    }
    else {
        Write-BashError -Message 'tar: you must specify -c, -x, or -t'
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
    [OutputType('PsBash.TextOutput')]
    param()
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
                Write-BashError -Message "yq: $file`: No such file or directory"
                return
            }
            $yamlTexts.Add([System.IO.File]::ReadAllText($resolved))
        }
    } else {
        $textParts = [System.Text.StringBuilder]::new()
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $textParts.Append($text + "`n") | Out-Null
        }
        $combined = $textParts.ToString().Trim()
        if ($combined -ne '') {
            $yamlTexts.Add($combined)
        }
    }

    if ($yamlTexts.Count -eq 0) { return }

    foreach ($yamlText in $yamlTexts) {
        try {
            $parsed = ConvertFrom-SimpleYaml -Text $yamlText
        } catch {
            Write-BashError -Message "yq: parse error: $($_.Exception.Message)"
            return
        }
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
    [OutputType('PsBash.TextOutput')]
    param()
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
        Write-BashError -Message 'xan: missing subcommand (headers, count, select, search, table)'
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
            Write-BashError -Message "xan: unknown subcommand '$subcommand'"
            return
        }
    }

    if ($fileArg) {
        $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($fileArg)
        if (-not (Test-Path -LiteralPath $resolved)) {
            Write-BashError -Message "xan: $fileArg`: No such file or directory"
            return
        }
        $csvText = [System.IO.File]::ReadAllText($resolved)
    } else {
        $textParts = [System.Text.StringBuilder]::new()
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            $textParts.Append($text + "`n") | Out-Null
        }
        $csvText = $textParts.ToString().Trim()
    }

    if (-not $csvText -or $csvText -eq '') { return }

    try {
        $records = @($csvText | ConvertFrom-Csv -Delimiter $delimiter)
    } catch {
        Write-BashError -Message "xan: parse error: $($_.Exception.Message)"
        return
    }
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

