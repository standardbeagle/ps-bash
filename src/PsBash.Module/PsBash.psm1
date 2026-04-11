#Requires -Version 7.0

Set-StrictMode -Version Latest

# --- Error Mode ---
# Controls how errors are reported:
#   'Bash'       — errors go to stderr via [Console]::Error, no PS error records,
#                   $global:LASTEXITCODE set on every failure (default)
#   'PowerShell' — errors use Write-Error (PS error records with stack traces)
$script:BashErrorMode = 'Bash'

function Set-BashErrorMode {
    param([ValidateSet('Bash','PowerShell')][string]$Mode)
    $script:BashErrorMode = $Mode
}

function Write-BashError {
    <#
    .SYNOPSIS
        Emit a bash-style error to stderr and set $global:LASTEXITCODE.
        In PowerShell mode, falls back to Write-Error.
    #>
    param(
        [Parameter(Mandatory)][string]$Message,
        [int]$ExitCode = 1
    )
    $global:LASTEXITCODE = $ExitCode
    if ($script:BashErrorMode -eq 'Bash') {
        [Console]::Error.WriteLine($Message)
    } else {
        Write-Error -Message $Message -ErrorAction Continue
    }
}

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

    $subDir = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'ps-bash', 'proc-sub')
    [void][System.IO.Directory]::CreateDirectory($subDir)
    $tmp = [System.IO.Path]::Combine($subDir, [System.IO.Path]::GetRandomFileName())
    try {
        $output = & $Command
        $sb = [System.Text.StringBuilder]::new()
        foreach ($item in $output) {
            [void]$sb.Append((Get-BashText -InputObject $item))
            # Mirror the worker serializer: add \n unless the object signals partial-line output
            $isPartial = $null -ne $item.PSObject -and
                         $null -ne $item.PSObject.Properties['NoTrailingNewline'] -and
                         [bool]$item.NoTrailingNewline
            if (-not $isPartial) {
                [void]$sb.Append("`n")
            }
        }
        [System.IO.File]::WriteAllText($tmp, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
        return $tmp
    }
    catch {
        Remove-Item -Path $tmp -Force -ErrorAction SilentlyContinue
        throw
    }
}

# --- Centralized File I/O Helpers ---

function Read-BashFileBytes {
    <#
    .SYNOPSIS
        Read a file as text with CRLF normalization.
        Uses [IO.File]::ReadAllText() which handles BOM detection internally.
        Returns $null and writes a bash-style error on failure.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Command
    )

    try {
        $rawText = [System.IO.File]::ReadAllText($Path)
    } catch {
        $normalized = $Path -replace '\\', '/'
        $ex = $_.Exception
        $inner = $ex.InnerException
        $isNotFound = ($ex -is [System.IO.FileNotFoundException]) -or
                      ($ex -is [System.IO.DirectoryNotFoundException]) -or
                      ($inner -is [System.IO.FileNotFoundException]) -or
                      ($inner -is [System.IO.DirectoryNotFoundException])
        $msg = if ($isNotFound) { 'No such file or directory' } else { $ex.Message }
        Write-BashError -Message "${Command}: ${normalized}: ${msg}"
        return $null
    }

    $rawText -replace "`r`n", "`n"
}

function Open-BashFileReader {
    <#
    .SYNOPSIS
        Open a StreamReader for a file with BOM-aware UTF-8 decoding.
        Returns $null and writes a bash-style error on failure.
    #>
    [CmdletBinding()]
    [OutputType([System.IO.StreamReader])]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Command
    )

    try {
        $fs = [System.IO.FileStream]::new(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read,
            4096,
            [System.IO.FileOptions]::SequentialScan
        )
    } catch {
        $normalized = $Path -replace '\\', '/'
        $ex = $_.Exception
        $inner = $ex.InnerException
        $isNotFound = ($ex -is [System.IO.FileNotFoundException]) -or
                      ($ex -is [System.IO.DirectoryNotFoundException]) -or
                      ($inner -is [System.IO.FileNotFoundException]) -or
                      ($inner -is [System.IO.DirectoryNotFoundException])
        $msg = if ($isNotFound) { 'No such file or directory' } else { $ex.Message }
        Write-BashError -Message "${Command}: ${normalized}: ${msg}"
        return $null
    }

    # Skip UTF-8 BOM if present
    $bom = [byte[]]::new(3)
    $read = $fs.Read($bom, 0, 3)
    $hasBom = ($read -ge 3 -and $bom[0] -eq 0xEF -and $bom[1] -eq 0xBB -and $bom[2] -eq 0xBF)
    if (-not $hasBom -and $read -gt 0) {
        $null = $fs.Seek(0, 'Begin')
    }

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.StreamReader]::new($fs, $encoding)
}

function Read-BashFileStreaming {
    <#
    .SYNOPSIS
        Stream lines from a file one at a time via the pipeline.
        No string[] allocation — each line is yielded individually.
        Caller must handle $null return (file not found).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Command,

        [int]$MaxLines = 0
    )

    $reader = Open-BashFileReader -Path $Path -Command $Command
    if ($null -eq $reader) { return }

    try {
        $emitted = 0
        while ($null -ne ($line = $reader.ReadLine())) {
            $line
            $emitted++
            if ($MaxLines -gt 0 -and $emitted -ge $MaxLines) { break }
        }
    } finally {
        $reader.Dispose()
    }
}

function Read-BashFileLines {
    <#
    .SYNOPSIS
        Read a file into an array of lines (no trailing newlines on each line).
        Returns $null and writes a bash-style error on failure.
        Uses streaming internally to avoid triple materialization.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Command
    )

    $reader = Open-BashFileReader -Path $Path -Command $Command
    if ($null -eq $reader) { return $null }

    try {
        $lines = [System.Collections.Generic.List[string]]::new()
        while ($null -ne ($line = $reader.ReadLine())) {
            $lines.Add($line)
        }
        # Write-Output -NoEnumerate prevents PowerShell from unwrapping a single-element array
        # to a scalar when the caller assigns the result to a variable. Without this, a file
        # with one line returns a plain string that lacks .Count under Set-StrictMode.
        Write-Output -NoEnumerate $lines.ToArray()
    } finally {
        $reader.Dispose()
    }
}

function Write-BashFileText {
    <#
    .SYNOPSIS
        Write text to a file. Returns $true on success, $false on failure.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Command,

        [switch]$Append
    )

    try {
        if ($Append) {
            [System.IO.File]::AppendAllText($Path, $Text)
        } else {
            [System.IO.File]::WriteAllText($Path, $Text)
        }
        return $true
    } catch {
        $normalized = $Path -replace '\\', '/'
        Write-BashError -Message "${Command}: ${normalized}: $($_.Exception.Message)"
        return $false
    }
}

function Get-BashItem {
    <#
    .SYNOPSIS
        Wrapper around Get-Item -LiteralPath -Force with error handling.
        Returns $null and writes a bash-style error on failure.
    #>
    [CmdletBinding()]
    [OutputType([System.IO.FileSystemInfo])]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter()]
        [string]$Verb = 'cannot access'
    )

    try {
        Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    } catch {
        $normalized = $Path -replace '\\', '/'
        $ex = $_.Exception
        $inner = $ex.InnerException
        $isNotFound = ($ex -is [System.IO.FileNotFoundException]) -or
                      ($ex -is [System.IO.DirectoryNotFoundException]) -or
                      ($inner -is [System.IO.FileNotFoundException]) -or
                      ($inner -is [System.IO.DirectoryNotFoundException]) -or
                      ($ex.GetType().Name -eq 'ItemNotFoundException') -or
                      ($ex.Message -match 'Cannot find path|does not exist|cannot find the path')
        $msg = if ($isNotFound) { 'No such file or directory' } else { $ex.Message }
        Write-BashError -Message "${Command}: ${Verb} '${normalized}': ${msg}"
        return $null
    }
}

function Read-BashFileRaw {
    <#
    .SYNOPSIS
        Read a file as raw bytes. Returns $null and writes a bash-style error on failure.
    #>
    [CmdletBinding()]
    [OutputType([byte[]])]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Command
    )

    try {
        [System.IO.File]::ReadAllBytes($Path)
    } catch {
        $normalized = $Path -replace '\\', '/'
        Write-BashError -Message "${Command}: ${normalized}: $($_.Exception.Message)"
        return $null
    }
}

function Write-BashFileRaw {
    <#
    .SYNOPSIS
        Write raw bytes to a file. Returns $true on success, $false on failure.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [byte[]]$Data,

        [Parameter(Mandatory)]
        [string]$Command
    )

    try {
        [System.IO.File]::WriteAllBytes($Path, $Data)
        return $true
    } catch {
        $normalized = $Path -replace '\\', '/'
        Write-BashError -Message "${Command}: ${normalized}: $($_.Exception.Message)"
        return $false
    }
}

# --- BashObject Factory ---

function Set-BashDisplayProperty {
    # Normalizes BashText by stripping any trailing \n.
    # ToString() is now provided at the type level via Update-TypeData (module init),
    # so per-object ScriptMethod is no longer needed.
    param([PSCustomObject]$Object)
    if ($Object.BashText -and $Object.BashText.EndsWith("`n")) {
        $Object.BashText = $Object.BashText.Substring(0, $Object.BashText.Length - 1)
    }
    $Object
}

function New-BashObject {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$BashText,

        [Parameter()]
        [string]$TypeName = 'PsBash.TextOutput',

        [switch]$NoTrailingNewline,

        [string]$Command
    )

    # Normalize: strip trailing \n — the worker serializer owns line endings
    if ($BashText.EndsWith("`n")) {
        $BashText = $BashText.Substring(0, $BashText.Length - 1)
    }

    # Fast path: plain text output → return string directly.
    # Avoids PSCustomObject allocation for line-by-line text (the common case).
    # NoTrailingNewline and non-TextOutput types use the slow path (PSCustomObject).
    if ($TypeName -eq 'PsBash.TextOutput' -and -not $NoTrailingNewline) {
        return [string]$BashText
    }

    $obj = [PSCustomObject]@{
        PSTypeName = $TypeName
        BashText   = $BashText
    }
    if ($NoTrailingNewline) {
        $obj | Add-Member -NotePropertyName 'NoTrailingNewline' -NotePropertyValue $true
    }
    if ($Command) {
        $obj | Add-Member -NotePropertyName Command -NotePropertyValue $Command
    }
    $obj
}

function Emit-BashLine {
    # Splits text on newlines and emits one BashObject per line.
    # Matches bash semantics: stdout is a byte stream, \n is a record boundary.
    # Sources (printf, echo -e, heredocs) call this for text output.
    # New-BashObject stays unchanged for typed objects (LsEntry, CatLine, PsEntry).
    # Accepts -Text parameter (direct call) or pipeline input (heredoc piping).
    param([string]$Text, [string]$Command)
    $pipelineInput = @($input)
    if (-not $Text -and $pipelineInput.Count -gt 0) {
        $Text = $pipelineInput -join "`n"
    }
    if (-not $Text) { return }
    $hasTrailingNewline = $Text.EndsWith("`n")
    $stripped = if ($hasTrailingNewline) { $Text.Substring(0, $Text.Length - 1) } else { $Text }
    $lines = $stripped -split "`n"
    $cmdSplat = if ($Command) { @{ Command = $Command } } else { @{} }
    for ($li = 0; $li -lt $lines.Count; $li++) {
        if ($li -lt $lines.Count - 1 -or $hasTrailingNewline) {
            New-BashObject -BashText $lines[$li] @cmdSplat
        } else {
            New-BashObject -BashText $lines[$li] -NoTrailingNewline @cmdSplat
        }
    }
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
            # Resolve relative paths against PowerShell's $PWD (not .NET CurrentDirectory)
            $resolved.Add($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($p))
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
        [AllowEmptyString()]
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
        [AllowEmptyString()]
        [AllowEmptyCollection()]
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

    Emit-BashLine -Text $result -Command 'printf'
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

function Invoke-BashLs {
    [OutputType('PsBash.LsEntry')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'ls' }

    $defs = New-FlagDefs -Entries @(
        '-l', 'long listing'
        '-a', 'show hidden (dotfiles + Windows hidden attr)'
        '-h', 'human readable sizes'
        '-R', 'recursive'
        '-S', 'sort by size'
        '-t', 'sort by time'
        '-r', 'reverse sort'
        '-1', 'one per line'
        '-p', 'append / to directories'
        '-d', 'list directories themselves'
    )

    $parsed    = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs
    $longMode  = $parsed.Flags['-l']
    $showHidden= $parsed.Flags['-a']
    $humanSizes= $parsed.Flags['-h']
    $recursive = $parsed.Flags['-R']
    $sortBySize= $parsed.Flags['-S']
    $sortByTime= $parsed.Flags['-t']
    $reverseSort=$parsed.Flags['-r']
    $dirOnly   = $parsed.Flags['-d']
    $classify  = $parsed.Flags['-p'] -or $longMode  # -l always shows type implicitly via perms; -p adds /

    $targets   = if ($parsed.Operands.Count -gt 0) { Resolve-BashGlob -Paths $parsed.Operands } else { @('.') }

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
    foreach ($entry in $sorted) {
        if ($longMode) {
            $entry.BashText = "$(Format-LsLine -Entry $entry -HumanReadable:$humanSizes)`n"
        } else {
            $name = $entry.Name
            if ($classify -and $entry.IsDirectory) { $name += '/' }
            $entry.BashText = "$name`n"
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

# --- grep Command ---

function Invoke-BashGrep {
    [OutputType('PsBash.GrepMatch')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'grep' }

    # Parse arguments manually because grep has value-bearing flags (-A, -B, -C, -m, -e)
    $ignoreCase = $false
    $invertMatch = $false
    $showLineNumbers = $false
    $countOnly = $false
    $recursive = $false
    $filesOnly = $false
    $extendedRegex = $false
    $fixedString = $false
    $wholeWord = $false
    $outputMatchOnly = $false
    $forceFileName = $false      # -H: always show filename
    $suppressFileName = $false   # -h: never show filename
    $maxMatches = [int]::MaxValue
    $afterContext = 0
    $beforeContext = 0
    $patterns = [System.Collections.Generic.List[string]]::new()
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

        # Handle -e pattern (multiple patterns)
        if ($arg -ceq '-e') {
            $i++
            if ($i -lt $Arguments.Count) {
                $patterns.Add($Arguments[$i])
            }
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

        # Handle -m NUM (max matches)
        if ($arg -cmatch '^-m(\d+)$') {
            $maxMatches = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-m') {
            $i++
            if ($i -lt $Arguments.Count) {
                $maxMatches = [int]$Arguments[$i]
            }
            $i++
            continue
        }

        # Long-form flags
        if ($arg -eq '--fixed-strings') { $fixedString = $true; $i++; continue }
        if ($arg -eq '--with-filename') { $forceFileName = $true; $i++; continue }
        if ($arg -eq '--no-filename') { $suppressFileName = $true; $i++; continue }
        if ($arg -eq '--word-regexp') { $wholeWord = $true; $i++; continue }
        if ($arg -eq '--only-matching') { $outputMatchOnly = $true; $i++; continue }
        if ($arg -eq '--max-count') {
            $i++
            if ($i -lt $Arguments.Count) { $maxMatches = [int]$Arguments[$i] }
            $i++
            continue
        }
        if ($arg -cmatch '^--max-count=(\d+)$') {
            $maxMatches = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch -CaseSensitive ($ch) {
                    'i' { $ignoreCase = $true }
                    'v' { $invertMatch = $true }
                    'n' { $showLineNumbers = $true }
                    'c' { $countOnly = $true }
                    'r' { $recursive = $true }
                    'l' { $filesOnly = $true }
                    'E' { $extendedRegex = $true }
                    'F' { $fixedString = $true }
                    'w' { $wholeWord = $true }
                    'o' { $outputMatchOnly = $true }
                    'H' { $forceFileName = $true }
                    'h' { $suppressFileName = $true }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Handle pattern collection: -e patterns or first operand
    if ($patterns.Count -eq 0 -and $operands.Count -gt 0) {
        $patterns.Add($operands[0])
    }

    if ($patterns.Count -eq 0) {
        Write-BashError -Message 'grep: usage: grep [options] pattern [file ...]' -ExitCode 2
        return
    }

    $fileOperands = @(if ($patterns.Count -lt $operands.Count) {
        $operands.GetRange(1, $operands.Count - 1)
    } elseif ($operands.Count -gt 1) {
        $operands.GetRange(1, $operands.Count - 1)
    } else {
        @()
    })

    # Build regex list from patterns (OR logic for multiple -e patterns)
    $regexes = [System.Collections.Generic.List[regex]]::new()
    $regexOpts = [System.Text.RegularExpressions.RegexOptions]::None
    if ($ignoreCase) { $regexOpts = $regexOpts -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase }

    foreach ($pat in $patterns) {
        $regexPattern = if ($fixedString) {
            # Fixed string: escape all regex metacharacters
            [regex]::Escape($pat)
        } elseif (-not $extendedRegex) {
            # Basic grep: . * ^ $ [ ] are special; escape (){}|+?
            $pat -replace '(?<!\\)\(', '\(' -replace '(?<!\\)\)', '\)' -replace '(?<!\\)\{', '\{' -replace '(?<!\\)\}', '\}' -replace '(?<!\\)\|', '\|' -replace '(?<!\\)\+', '\+' -replace '(?<!\\)\?', '\?'
        } else {
            $pat
        }

        # Add word boundaries if -w is set
        if ($wholeWord) {
            $regexPattern = "\b$regexPattern\b"
        }

        $regexes.Add([regex]::new($regexPattern, $regexOpts))
    }

    # --- Pipeline mode ---
    if ($fileOperands.Count -eq 0 -and -not $recursive) {
        $matchCount = 0
        $lineNum = 0

        foreach ($item in $pipelineInput) {
            if ($matchCount -ge $maxMatches) { break }

            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    if ($matchCount -ge $maxMatches) { break }
                    $lineNum++

                    # Check if any regex matches (OR logic for multiple patterns)
                    $isMatch = $false
                    $matchObject = $null
                    foreach ($rx in $regexes) {
                        if ($rx.IsMatch($subLine)) {
                            $isMatch = $true
                            $matchObject = $rx.Match($subLine)
                            break
                        }
                    }

                    if ($invertMatch) { $isMatch = -not $isMatch }
                    if ($isMatch) {
                        $matchCount++
                        if (-not $countOnly) {
                            $outputText = if ($outputMatchOnly -and $matchObject) {
                                $matchObject.Value
                            } else {
                                $subLine
                            }

                            $prefix = ''
                            if ($forceFileName) { $prefix = "<stdin>:" }
                            if ($showLineNumbers) { $prefix = "${prefix}${lineNum}:" }
                            $bashText = "${prefix}${outputText}"

                            New-BashObject -BashText $bashText
                        }
                    }
                }
            } else {
                $lineNum++
                $lineText = $text.TrimEnd("`n".ToCharArray())

                # Check if any regex matches (OR logic for multiple patterns)
                $isMatch = $false
                $matchObject = $null
                foreach ($rx in $regexes) {
                    if ($rx.IsMatch($lineText)) {
                        $isMatch = $true
                        $matchObject = $rx.Match($lineText)
                        break
                    }
                }

                if ($invertMatch) { $isMatch = -not $isMatch }
                if ($isMatch) {
                    $matchCount++
                    if (-not $countOnly) {
                        $outputText = if ($outputMatchOnly -and $matchObject) {
                            $matchObject.Value
                        } else {
                            $lineText
                        }

                        $prefix = ''
                        if ($forceFileName) { $prefix = "<stdin>:" }
                        if ($showLineNumbers) { $prefix = "${prefix}${lineNum}:" }

                        if ($prefix -ne '') {
                            New-BashObject -BashText "${prefix}${outputText}"
                        } elseif ($outputMatchOnly) {
                            New-BashObject -BashText $outputText
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
                Write-BashError -Message "grep: ${fp}: No such file or directory" -ExitCode 2
                continue
            }
            $filePaths.Add((Resolve-Path -LiteralPath $fp).Path)
        }
    }

    $multipleFiles = $filePaths.Count -gt 1 -or $recursive -or $forceFileName
    $matchedFiles = [System.Collections.Generic.List[string]]::new()
    $perFileCounts = [System.Collections.Generic.Dictionary[string,int]]::new()
    $totalMatchCount = 0
    $filesProcessed = 0

    foreach ($filePath in (Resolve-BashGlob -Paths $filePaths)) {
        if ($totalMatchCount -ge $maxMatches) { break }
        $filesProcessed++

        $lines = Read-BashFileLines -Path $filePath -Command 'grep'
        if ($null -eq $lines) { continue }

        $matchIndices = [System.Collections.Generic.List[int]]::new()
        $matchObjects = [System.Collections.Generic.Dictionary[int, System.Text.RegularExpressions.Match]]::new()
        for ($li = 0; $li -lt $lines.Count; $li++) {
            # Check if any regex matches (OR logic for multiple patterns)
            $isMatch = $false
            $matchObj = $null
            foreach ($rx in $regexes) {
                if ($rx.IsMatch($lines[$li])) {
                    $isMatch = $true
                    $matchObj = $rx.Match($lines[$li])
                    break
                }
            }

            if ($invertMatch) { $isMatch = -not $isMatch }
            if ($isMatch) {
                $matchIndices.Add($li)
                if ($matchObj) { $matchObjects[$li] = $matchObj }
            }
        }

        $fileMatchCount = $matchIndices.Count
        $totalMatchCount += $fileMatchCount
        $perFileCounts[$filePath] = $fileMatchCount

        if ($filesOnly) {
            if ($fileMatchCount -gt 0) { $matchedFiles.Add($filePath) }
            continue
        }

        if ($countOnly) { continue }

        # Determine which lines to emit (matches + context, respecting -m limit)
        $emitLines = [System.Collections.Generic.HashSet[int]]::new()
        $emitCount = 0
        foreach ($mi in $matchIndices) {
            if ($emitCount -ge $maxMatches) { break }

            $start = [System.Math]::Max(0, $mi - $beforeContext)
            $end = [System.Math]::Min($lines.Count - 1, $mi + $afterContext)
            for ($li = $start; $li -le $end; $li++) {
                [void]$emitLines.Add($li)
            }
            $emitCount++
        }

        $sortedEmit = $emitLines | Sort-Object
        foreach ($li in $sortedEmit) {
            if ($totalMatchCount -ge $maxMatches) { break }

            $line = $lines[$li]
            $lineNum = $li + 1
            $prefix = ''

            # Determine if filename should be shown
            $showFile = $multipleFiles -and -not $suppressFileName

            if ($outputMatchOnly -and $matchObjects.ContainsKey($li)) {
                $outputText = $matchObjects[$li].Value
            } else {
                $outputText = $line
            }

            if ($showFile) { $prefix = "${filePath}:" }

            $bashText = $outputText
            if ($showLineNumbers) {
                $bashText = "${prefix}${lineNum}:${outputText}"
            } elseif ($showFile) {
                $bashText = "${prefix}${outputText}"
            }

            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.GrepMatch'
                FileName   = $filePath
                LineNumber = $lineNum
                Line       = $line
                BashText   = $bashText
            }
            Set-BashDisplayProperty $obj

            if ($matchIndices -contains $li) {
                $totalMatchCount = $totalMatchCount   # Update count for context lines
            }
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
                if ($perFileCounts.ContainsKey($filePath)) {
                    New-BashObject -BashText "${filePath}:$($perFileCounts[$filePath])"
                }
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

# --- head Command ---

function Invoke-BashHead {
    [OutputType('PsBash.TextOutput')]
    param()
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
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    if ($emitted -ge $count) { break }
                    $subLine
                    $emitted++
                }
            } else {
                $item
                $emitted++
            }
        }
        return
    }

    # File mode — resolve globs, stream lines with early exit
    $resolvedFiles = Resolve-BashGlob -Paths $operands
    foreach ($filePath in $resolvedFiles) {
        $reader = Open-BashFileReader -Path $filePath -Command 'head'
        if ($null -eq $reader) { continue }

        try {
            $li = 0
            while ($li -lt $count -and $null -ne ($line = $reader.ReadLine())) {
                $li++
                $obj = [PSCustomObject]@{
                    PSTypeName = 'PsBash.CatLine'
                    LineNumber = $li
                    Content    = $line
                    FileName   = $filePath
                    BashText   = $line
                }
                Set-BashDisplayProperty $obj
            }
        } finally {
            $reader.Dispose()
        }
    }
}

# --- tail Command ---

function Invoke-BashTail {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'tail' }

    # Manual arg parsing for value-bearing flags
    $count = 10
    $byteCount = $null
    $fromLine = $false
    $followFile = $false     # -f / --follow: follow file for new content
    $sleepInterval = 1.0     # -s / --sleep-interval: poll interval in seconds
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

        if ($arg -ceq '-f' -or $arg -ceq '--follow') {
            $followFile = $true
            $i++
            continue
        }

        if ($arg -ceq '--sleep-interval') {
            $i++
            if ($i -lt $Arguments.Count) {
                $sleepInterval = [double]$Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg -cmatch '^-s([\d.]+)$') {
            $sleepInterval = [double]$Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-s') {
            $i++
            if ($i -lt $Arguments.Count) {
                $sleepInterval = [double]$Arguments[$i]
            }
            $i++
            continue
        }

        # -c +N syntax (from byte N onward)
        if ($arg -cmatch '^-c\+(\d+)$') {
            $byteCount = [int]$Matches[1]
            $fromLine = $true
            $i++
            continue
        }

        if ($arg -cmatch '^-c(\d+)$') {
            $byteCount = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -ceq '-c' -or $arg -ceq '--bytes') {
            $i++
            if ($i -lt $Arguments.Count) {
                $val = $Arguments[$i]
                if ($val.StartsWith('+')) {
                    $byteCount = [int]$val.Substring(1)
                    $fromLine = $true
                } else {
                    $byteCount = [int]$val
                }
            }
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
            # +N mode: skip first N-1 items, emit the rest
            $skip = $count - 1
            $idx = 0
            foreach ($item in $pipelineInput) {
                $text = Get-BashText -InputObject $item
                if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                    foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                        if ($idx -ge $skip) {
                            $subLine
                        }
                        $idx++
                    }
                } else {
                    if ($idx -ge $skip) {
                        $item
                    }
                    $idx++
                }
            }
        } else {
            # -N mode: circular buffer — only keep last N items in memory
            $buf = [object[]]::new($count)
            $bufLen = 0
            $pos = 0

            foreach ($item in $pipelineInput) {
                $text = Get-BashText -InputObject $item
                if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                    foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                        $wrapped = $subLine
                        $buf[$pos] = $wrapped
                        $pos = ($pos + 1) % $count
                        if ($bufLen -lt $count) { $bufLen++ }
                    }
                } else {
                    $buf[$pos] = $item
                    $pos = ($pos + 1) % $count
                    if ($bufLen -lt $count) { $bufLen++ }
                }
            }

            # Emit the circular buffer in order (oldest first)
            $start = if ($bufLen -lt $count) { 0 } else { $pos }
            for ($i = 0; $i -lt $bufLen; $i++) {
                $buf[($start + $i) % $count]
            }
        }
        return
    }

    # File mode — resolve globs
    $resolvedFiles = @(Resolve-BashGlob -Paths $operands)

    if ($resolvedFiles.Count -eq 0) {
        return
    }

    $filePath = $resolvedFiles[0]

    # -c bytes mode
    if ($null -ne $byteCount) {
        $rawText = Read-BashFileBytes -Path $filePath -Command 'tail'
        if ($null -eq $rawText) { return }

        if ($fromLine) {
            # -c +N: output starting from byte offset N
            $startIdx = [System.Math]::Min($byteCount, $rawText.Length)
            Emit-BashLine -Text $rawText.Substring($startIdx)
        } else {
            # -c N: output last N bytes
            $startIdx = [System.Math]::Max(0, $rawText.Length - $byteCount)
            Emit-BashLine -Text $rawText.Substring($startIdx)
        }
        return
    }

    if ($followFile) {
        # Follow mode: continuously monitor file for new lines using polling
        try {
            $lines = Read-BashFileLines -Path $filePath -Command 'tail'
            if ($null -eq $lines) { $lines = @() }

            if ($fromLine) {
                $lastOutputLine = $count - 1
            } else {
                $lastOutputLine = [System.Math]::Max(-1, $lines.Count - $count - 1)
            }

            # Output initial lines
            for ($li = $lastOutputLine + 1; $li -lt $lines.Count; $li++) {
                Emit-BashLine -Text $lines[$li]
            }

            # Track file length to avoid re-reading entire file each poll
            $filePos = [System.IO.FileInfo]::new($filePath).Length

            # Monitor file for new content
            while ($true) {
                Start-Sleep -Seconds $sleepInterval
                $info = [System.IO.FileInfo]::new($filePath)
                if ($info.Length -gt $filePos) {
                    $newContent = $null
                    try {
                        $fs = [System.IO.FileStream]::new($filePath, 'Open', 'Read', 'ReadWrite')
                        try {
                            $sr = [System.IO.StreamReader]::new($fs)
                            try {
                                $null = $fs.Seek($filePos, 'Begin')
                                $newContent = $sr.ReadToEnd()
                                $filePos = $fs.Position
                            } finally {
                                $sr.Dispose()
                            }
                        } finally {
                            $fs.Dispose()
                        }
                    } catch {
                        continue
                    }
                    if ($null -ne $newContent -and $newContent.Length -gt 0) {
                        if ($newContent.EndsWith("`n")) {
                            $newContent = $newContent.Substring(0, $newContent.Length - 1)
                        }
                        $newContent = $newContent -replace "`r`n", "`n"
                        foreach ($line in $newContent.Split("`n")) {
                            Emit-BashLine -Text $line
                        }
                    }
                } elseif ($info.Length -lt $filePos) {
                    # File was truncated or rotated — reset position
                    $filePos = 0
                }
            }
        } catch {
            Write-BashError -Message "tail: cannot follow file: $_" -ExitCode 1
        }
    } else {
        # Normal mode: output last N lines
        foreach ($filePath in $resolvedFiles) {
            if ($fromLine) {
                # Stream and skip first N-1 lines without buffering
                $reader = Open-BashFileReader -Path $filePath -Command 'tail'
                if ($null -eq $reader) { continue }

                try {
                    $li = 0
                    while ($null -ne ($line = $reader.ReadLine())) {
                        $li++
                        if ($li -ge $count) {
                            $obj = [PSCustomObject]@{
                                PSTypeName = 'PsBash.CatLine'
                                LineNumber = $li
                                Content    = $line
                                FileName   = $filePath
                                BashText   = $line
                            }
                            Set-BashDisplayProperty $obj
                        }
                    }
                } finally {
                    $reader.Dispose()
                }
            } else {
                # Circular buffer — stream lines, keep only last N in memory
                $reader = Open-BashFileReader -Path $filePath -Command 'tail'
                if ($null -eq $reader) { continue }

                try {
                    $buf = [string[]]::new($count)
                    $bufLen = 0
                    $total = 0
                    $pos = 0

                    while ($null -ne ($line = $reader.ReadLine())) {
                        $buf[$pos] = $line
                        $pos = ($pos + 1) % $count
                        if ($bufLen -lt $count) { $bufLen++ }
                        $total++
                    }

                    # Emit the circular buffer in order (oldest first)
                    $start = if ($bufLen -lt $count) { 0 } else { $pos }
                    $lineNumOffset = $total - $bufLen
                    for ($i = 0; $i -lt $bufLen; $i++) {
                        $idx = ($start + $i) % $count
                        $obj = [PSCustomObject]@{
                            PSTypeName = 'PsBash.CatLine'
                            LineNumber = $lineNumOffset + $i + 1
                            Content    = $buf[$idx]
                            FileName   = $filePath
                            BashText   = $buf[$idx]
                        }
                        Set-BashDisplayProperty $obj
                    }
                } finally {
                    $reader.Dispose()
                }
            }
        }
    }
}

# --- wc Command ---

function Invoke-BashWc {
    [OutputType('PsBash.WcResult')]
    param()
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

        $wsChars = [char[]]@(' ', "`t", "`n", "`r")
        $splitOpts = [System.StringSplitOptions]::RemoveEmptyEntries
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $totalLines++
                    $totalWords += $subLine.Split($wsChars, $splitOpts).Length
                    $totalBytes += [System.Text.Encoding]::UTF8.GetByteCount($subLine) + 1
                }
            } else {
                $lineText = $text.TrimEnd("`n".ToCharArray())
                $totalLines++
                $totalWords += $lineText.Split($wsChars, $splitOpts).Length
                $totalBytes += [System.Text.Encoding]::UTF8.GetByteCount($lineText) + 1
            }
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
            Write-BashError -Message "wc: ${filePath}: No such file or directory"
            continue
        }

        $rawText = Read-BashFileBytes -Path $filePath -Command 'wc'
        if ($null -eq $rawText) { continue }

        # Byte count: file size minus BOM (Read-BashFileBytes handles BOM for text)
        $fileBytes = [System.IO.FileInfo]::new($filePath).Length
        try {
            $fs = [System.IO.File]::OpenRead($filePath)
            $bom = [byte[]]::new(3)
            if ($fs.Read($bom, 0, 3) -ge 3 -and $bom[0] -eq 0xEF -and $bom[1] -eq 0xBB -and $bom[2] -eq 0xBF) {
                $fileBytes -= 3
            }
            $fs.Dispose()
        } catch {
            # If BOM peek fails, use raw file size
        }
        $lineCount = 0
        foreach ($c in [char[]]$rawText) { if ($c -eq "`n") { $lineCount++ } }
        $wsChars = [char[]]@(' ', "`t", "`n", "`r")
        $wordCount = $rawText.Split($wsChars, [System.StringSplitOptions]::RemoveEmptyEntries).Length

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

    foreach ($target in $resolvedOperands) {
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
            $item.LastWriteTime = $timestamp
            $item.LastAccessTime = $timestamp
        } else {
            $parentDir = Split-Path $file -Parent
            if ($parentDir -and -not (Test-Path -LiteralPath $parentDir)) {
                Write-BashError -Message "touch: cannot touch '$file': No such file or directory"
                continue
            }
            New-Item -Path $file -ItemType File -Force | Out-Null
            $item = Get-BashItem -Path $file -Command 'touch'
            if ($null -eq $item) { continue }
            $item.LastWriteTime = $timestamp
            $item.LastAccessTime = $timestamp
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

# --- sed Command ---

function Invoke-BashSed {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'sed' }

    # Parse flags and expressions
    $suppressDefault = $false
    $inPlace = $false
    $extendedRegex = $false
    $scriptFile = $null
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

        if ($arg -ceq '-f') {
            $i++
            if ($i -lt $Arguments.Count) {
                $scriptFile = $Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and -not $arg.StartsWith('--')) {
            $fConsumed = $false
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'n' { $suppressDefault = $true }
                    'i' { $inPlace = $true }
                    'E' { $extendedRegex = $true }
                    'r' { $extendedRegex = $true }
                    'f' {
                        if (-not $fConsumed) {
                            $i++
                            if ($i -lt $Arguments.Count) {
                                $scriptFile = $Arguments[$i]
                                $fConsumed = $true
                            }
                        }
                    }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Read script file if -f was specified
    if ($null -ne $scriptFile) {
        $resolved = Resolve-Path $scriptFile -ErrorAction SilentlyContinue
        if ($null -eq $resolved) {
            Write-BashError -Message "sed: can't read $scriptFile" -ExitCode 2
            return
        }
        $scriptText = [System.IO.File]::ReadAllText($resolved.Path)
        # Each non-empty line in the script file is a separate sed command
        foreach ($scriptLine in $scriptText -split "`n") {
            $trimmed = $scriptLine.Trim()
            if ($trimmed.Length -gt 0) {
                $expressions.Add($trimmed)
            }
        }
    }

    # First operand is the expression if no -e was used
    if ($expressions.Count -eq 0 -and $operands.Count -gt 0) {
        $expressions.Add($operands[0])
        $operands.RemoveAt(0)
    }

    if ($expressions.Count -eq 0) {
        Write-BashError -Message 'sed: usage: sed [options] expression [file ...]' -ExitCode 2
        return
    }

    # Parse sed commands from expressions
    $commands = [System.Collections.Generic.List[hashtable]]::new()
    foreach ($expr in $expressions) {
        $parsed = ConvertFrom-SedExpression -Expression $expr -ExtendedRegex $extendedRegex
        if ($null -eq $parsed) { return }
        $commands.Add($parsed)
    }

    # Process all lines through sed commands, handling multi-line pattern space
    # $lines is [string[]], returns [string[]] of output lines
    $processLines = {
        param([string[]]$inputLines)

        $outputLines = [System.Collections.Generic.List[string]]::new()
        $totalLines = $inputLines.Count
        $li = 0

        while ($li -lt $totalLines) {
            $patternSpace = $inputLines[$li]
            $lineNum = $li + 1
            $deleted = $false
            $quit = $false
            $quitExitCode = 0
            $replaced = $false

            $restartCycle = $true
            while ($restartCycle) {
                $restartCycle = $false
                $printedLines = [System.Collections.Generic.List[string]]::new()
                $appendTexts = [System.Collections.Generic.List[string]]::new()
                $insertTexts = [System.Collections.Generic.List[string]]::new()
                $deleted = $false
                $replaced = $false

                foreach ($cmd in $commands) {
                    if ($deleted) { break }
                    if ($quit -and $cmd.Type -ne 'q') { continue }

                    # For address matching, use the first line of pattern space
                    $firstLine = if ($patternSpace.Contains("`n")) {
                        $patternSpace.Substring(0, $patternSpace.IndexOf("`n"))
                    } else {
                        $patternSpace
                    }

                    if (-not (Test-SedAddress -Cmd $cmd -Line $firstLine -LineNum $lineNum -TotalLines $totalLines -AllLines $inputLines)) {
                        continue
                    }

                    switch -CaseSensitive ($cmd.Type) {
                        's' {
                            $regex = $cmd.Regex
                            if ($cmd.Global) {
                                $patternSpace = $regex.Replace($patternSpace, $cmd.Replacement)
                            } else {
                                $patternSpace = $regex.Replace($patternSpace, $cmd.Replacement, 1)
                            }
                        }
                        'd' {
                            $deleted = $true
                        }
                        'D' {
                            $nlIdx = $patternSpace.IndexOf("`n")
                            if ($nlIdx -ge 0) {
                                $patternSpace = $patternSpace.Substring($nlIdx + 1)
                            } else {
                                $deleted = $true
                                $patternSpace = ''
                            }
                            if (-not $deleted -and $patternSpace.Length -gt 0) {
                                $restartCycle = $true
                            } elseif ($deleted) {
                                $restartCycle = $false
                            }
                            # Break out of foreach, will check restartCycle below
                            break
                        }
                        'p' {
                            $printedLines.Add($patternSpace)
                        }
                        'P' {
                            $nlIdx = $patternSpace.IndexOf("`n")
                            if ($nlIdx -ge 0) {
                                $printedLines.Add($patternSpace.Substring(0, $nlIdx))
                            } else {
                                $printedLines.Add($patternSpace)
                            }
                        }
                        'N' {
                            $li++
                            if ($li -lt $totalLines) {
                                $patternSpace += "`n" + $inputLines[$li]
                            }
                        }
                        'q' {
                            $quit = $true
                            $quitExitCode = $cmd.ExitCode
                        }
                        'a' {
                            $appendTexts.Add($cmd.Text)
                        }
                        'i' {
                            $insertTexts.Add($cmd.Text)
                        }
                        'c' {
                            $deleted = $true
                            $replaced = $true
                            $appendTexts.Add($cmd.Text)
                        }
                        'y' {
                            $sb = [System.Text.StringBuilder]::new($patternSpace.Length)
                            foreach ($ch in $patternSpace.ToCharArray()) {
                                $idx = $cmd.Source.IndexOf($ch)
                                if ($idx -ge 0) {
                                    [void]$sb.Append($cmd.Dest[$idx])
                                } else {
                                    [void]$sb.Append($ch)
                                }
                            }
                            $patternSpace = $sb.ToString()
                        }
                    }

                    if ($restartCycle) { break }
                }

                if ($restartCycle) { continue }

                # Emit insert texts (before current line)
                foreach ($insText in $insertTexts) {
                    $outputLines.Add($insText)
                }

                # Emit printed lines (from p/P commands)
                foreach ($pLine in $printedLines) {
                    $outputLines.Add($pLine)
                }

                # Emit default output
                if (-not $deleted) {
                    if (-not $suppressDefault) {
                        # Pattern space may have embedded newlines from N
                        if ($patternSpace.Contains("`n")) {
                            foreach ($psLine in $patternSpace.Split("`n")) {
                                $outputLines.Add($psLine)
                            }
                        } else {
                            $outputLines.Add($patternSpace)
                        }
                    }
                }

                # Emit append texts (after current line)
                foreach ($appText in $appendTexts) {
                    $outputLines.Add($appText)
                }
            }

            $li++

            if ($quit) {
                # q prints the current pattern space first (already done above), then stops
                break
            }
        }

        $outputLines.ToArray()
    }

    # --- File mode (including in-place) ---
    if ($operands.Count -gt 0) {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $rawText = Read-BashFileBytes -Path $filePath -Command 'sed'
            if ($null -eq $rawText) { continue }
            $hadTrailingNewline = $rawText.EndsWith("`n")
            if ($hadTrailingNewline) {
                $rawText = $rawText.Substring(0, $rawText.Length - 1)
            }
            $lines = $rawText.Split("`n")

            $outputLines = & $processLines $lines

            if ($inPlace) {
                $outText = ($outputLines -join "`n")
                if ($hadTrailingNewline) { $outText += "`n" }
                if (-not (Write-BashFileText -Path $filePath -Text $outText -Command 'sed')) { return }
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

    $allLines = [System.Collections.Generic.List[string]]::new()
    $origItems = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $pipelineInput) {
        $text = Get-BashText -InputObject $item
        if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
            foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                $allLines.Add($subLine)
                $origItems.Add($null)
            }
        } else {
            $allLines.Add(($text.TrimEnd("`n".ToCharArray())))
            $origItems.Add($item)
        }
    }

    $inputArray = [string[]]$allLines.ToArray()
    $outputLines = @(& $processLines $inputArray)

    # Emit output preserving original objects where possible
    for ($oi = 0; $oi -lt $outputLines.Count; $oi++) {
        if ($oi -lt $origItems.Count) {
            $orig = $origItems[$oi]
            if ($null -ne $orig -and $orig -isnot [string] -and $null -ne $orig.PSObject -and $null -ne $orig.PSObject.Properties['BashText']) {
                $orig.BashText = "$($outputLines[$oi])`n"
                $orig
                continue
            }
        }
        New-BashObject -BashText "$($outputLines[$oi])`n"
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
        if ($endSlash -lt 0) { Write-BashError -Message 'sed: unterminated address regex' -ExitCode 2; return $null }
        $addr = @{ Type = 'regex'; Pattern = $Expression.Substring($pos, $endSlash - $pos) }
        $pos = $endSlash + 1

        # Check for range: /start/,/end/
        if ($pos -lt $Expression.Length -and $Expression[$pos] -eq ',') {
            $pos++
            if ($pos -lt $Expression.Length -and $Expression[$pos] -eq '/') {
                $pos++
                $endSlash2 = $Expression.IndexOf('/', $pos)
                if ($endSlash2 -lt 0) { Write-BashError -Message 'sed: unterminated address regex' -ExitCode 2; return $null }
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
        Write-BashError -Message 'sed: missing command' -ExitCode 2
        return $null
    }

    $cmdChar = $remaining[0]

    switch -CaseSensitive ($cmdChar) {
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

            if ($parts.Count -lt 2) { Write-BashError -Message 'sed: bad substitution' -ExitCode 2; return $null }

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
        'D' {
            @{
                Type    = 'D'
                Address = $addr
            }
        }
        'p' {
            @{
                Type    = 'p'
                Address = $addr
            }
        }
        'P' {
            @{
                Type    = 'P'
                Address = $addr
            }
        }
        'N' {
            @{
                Type    = 'N'
                Address = $addr
            }
        }
        'q' {
            $exitCode = 0
            if ($remaining.Length -gt 1) {
                $qArg = $remaining.Substring(1).Trim()
                if ($qArg.Length -gt 0 -and $qArg -match '^\d+$') {
                    $exitCode = [int]$qArg
                }
            }
            @{
                Type     = 'q'
                Address  = $addr
                ExitCode = $exitCode
            }
        }
        'a' {
            $text = if ($remaining.Length -gt 1) { $remaining.Substring(1) } else { '' }
            $text = $text.TrimStart('\').TrimStart()
            @{
                Type    = 'a'
                Address = $addr
                Text    = $text
            }
        }
        'i' {
            $text = if ($remaining.Length -gt 1) { $remaining.Substring(1) } else { '' }
            $text = $text.TrimStart('\').TrimStart()
            @{
                Type    = 'i'
                Address = $addr
                Text    = $text
            }
        }
        'c' {
            $text = if ($remaining.Length -gt 1) { $remaining.Substring(1) } else { '' }
            $text = $text.TrimStart('\').TrimStart()
            @{
                Type    = 'c'
                Address = $addr
                Text    = $text
            }
        }
        'y' {
            $delim = $remaining[1]
            $parts = $remaining.Substring(2).Split($delim)
            if ($parts.Count -lt 2) { Write-BashError -Message 'sed: bad transliteration' -ExitCode 2; return $null }
            $source = $parts[0]
            $dest = $parts[1]
            if ($source.Length -ne $dest.Length) {
                Write-BashError -Message 'sed: y: source and dest must be the same length' -ExitCode 2; return $null
            }
            @{
                Type    = 'y'
                Address = $addr
                Source  = $source
                Dest    = $dest
            }
        }
        default {
            Write-BashError -Message "sed: unsupported command '$cmdChar'" -ExitCode 2; return $null
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
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'awk' }

    # Parse flags: -F FS, -v VAR=VAL, -f FILE
    $fieldSep = ' '
    $fieldSepIsDefault = $true
    $variables = @{}
    $programText = $null
    $programFiles = [System.Collections.Generic.List[string]]::new()
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

        if ($arg -ceq '-f' -or $arg -ceq '--file') {
            $i++
            if ($i -lt $Arguments.Count) {
                $programFiles.Add($Arguments[$i])
            }
            $i++
            continue
        }

        if ($null -eq $programText) {
            $programText = $arg
        }
        $i++
    }

    # If -f was used, read program from file
    if ($programFiles.Count -gt 0) {
        $fileText = [System.Text.StringBuilder]::new()
        foreach ($pf in $programFiles) {
            if (-not (Test-Path $pf)) {
                Write-BashError -Message "awk: can't open source file ${pf}: No such file or directory" -ExitCode 2
                return
            }
            [void]$fileText.Append([System.IO.File]::ReadAllText($pf))
        }
        $programText = $fileText.ToString()
    }

    if ($null -eq $programText) {
        Write-BashError -Message 'awk: usage: awk [options] program [file ...]' -ExitCode 2
        return
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
    $allLines = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $pipelineInput) {
        $text = Get-BashText -InputObject $item
        if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
            foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                $allLines.Add($subLine)
            }
        } else {
            $allLines.Add(($text.TrimEnd("`n".ToCharArray())))
        }
    }
    for ($idx = 0; $idx -lt $allLines.Count; $idx++) {
        $text = $allLines[$idx]
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
    $funcMatch = [regex]::Match($e, '^(length|substr|tolower|toupper|sprintf|match|strftime|systime|index|split|rand|srand|sin|cos|atan2|exp|log|sqrt|int)\s*\((.*)$')
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

        # delete array[key] or delete array (clear all)
        if ($s -match '^delete\s+') {
            $delTarget = $s.Substring(7).Trim()
            $delMatch = [regex]::Match($delTarget, '^([A-Za-z_]\w*)\[(.+)\]$')
            if ($delMatch.Success) {
                $arrName = $delMatch.Groups[1].Value
                $keyExpr = $delMatch.Groups[2].Value
                $key = Resolve-AwkExpression -Expr $keyExpr -Fields $Fields -Variables $Variables
                $keyStr = "$key"
                $keysToRemove = @($Variables.Keys | Where-Object { $_ -like "$arrName[*]" })
                foreach ($k in $keysToRemove) {
                    $kKey = $k -replace "^$([regex]::Escape($arrName))\[(.+)\]$", '$1'
                    if ($kKey -eq $keyStr) {
                        $Variables.Remove($k)
                    }
                }
            } else {
                $arrName = $delTarget
                $keysToRemove = @($Variables.Keys | Where-Object { $_ -like "$arrName[*]" })
                foreach ($k in $keysToRemove) {
                    $Variables.Remove($k)
                }
                if ($Variables.ContainsKey($arrName)) {
                    $Variables.Remove($arrName)
                }
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

        # Bare function call: match(...), srand(), etc.
        $funcMatch = [regex]::Match($s, '^(gsub|sub|match|srand)\s*\(')
        if ($funcMatch.Success) {
            $funcName = $funcMatch.Groups[1].Value
            $argsStr = $s.Substring($s.IndexOf('(') + 1)
            $argsStr = $argsStr.Substring(0, $argsStr.LastIndexOf(')'))
            $fArgs = @(Split-AwkFuncArgs -Text $argsStr)
            $funcResult = Resolve-AwkStringFunc -FuncName $funcName -FuncArgs $fArgs -Fields $Fields -Variables $Variables
            # gsub/sub need field re-splitting
            if ($funcName -eq 'gsub' -or $funcName -eq 'sub') {
                if ($funcName -eq 'gsub' -and $fArgs.Count -ge 2) {
                    $regex = $fArgs[0].Trim()
                    if ($regex.StartsWith('/') -and $regex.EndsWith('/')) { $regex = $regex.Substring(1, $regex.Length - 2) }
                    $repl = Resolve-AwkExpression -Expr $fArgs[1].Trim() -Fields $Fields -Variables $Variables
                    $target = if ($fArgs.Count -ge 3) {
                        $tExpr = $fArgs[2].Trim()
                        $tVal = Resolve-AwkExpression -Expr $tExpr -Fields $Fields -Variables $Variables
                        "$tVal"
                    } else { $Fields[0] }
                    $newVal = [regex]::Replace($target, $regex, "$repl")
                    if ($fArgs.Count -lt 3) { $Fields[0] = $newVal }
                    $newFields = Split-AwkFields -Line $Fields[0] -FieldSep $FieldSep -IsDefault ($FieldSep -eq ' ')
                    for ($fi = 0; $fi -lt $newFields.Count -and $fi -lt $Fields.Count; $fi++) { $Fields[$fi] = $newFields[$fi] }
                } elseif ($funcName -eq 'sub' -and $fArgs.Count -ge 2) {
                    $regex = $fArgs[0].Trim()
                    if ($regex.StartsWith('/') -and $regex.EndsWith('/')) { $regex = $regex.Substring(1, $regex.Length - 2) }
                    $repl = Resolve-AwkExpression -Expr $fArgs[1].Trim() -Fields $Fields -Variables $Variables
                    $target = if ($fArgs.Count -ge 3) {
                        $tExpr = $fArgs[2].Trim()
                        $tVal = Resolve-AwkExpression -Expr $tExpr -Fields $Fields -Variables $Variables
                        "$tVal"
                    } else { $Fields[0] }
                    $newVal = [regex]::new($regex).Replace($target, "$repl", 1)
                    if ($fArgs.Count -lt 3) { $Fields[0] = $newVal }
                    $newFields = Split-AwkFields -Line $Fields[0] -FieldSep $FieldSep -IsDefault ($FieldSep -eq ' ')
                    for ($fi = 0; $fi -lt $newFields.Count -and $fi -lt $Fields.Count; $fi++) { $Fields[$fi] = $newFields[$fi] }
                }
            }
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
        'sprintf' {
            if ($FuncArgs.Count -ge 1) {
                $fmt = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
                $fmtStr = "$fmt"
                $argVals = @()
                for ($ai = 1; $ai -lt $FuncArgs.Count; $ai++) {
                    $argVals += Resolve-AwkExpression -Expr $FuncArgs[$ai] -Fields $Fields -Variables $Variables
                }
                return Format-AwkPrintf -Format $fmtStr -FormatArgs $argVals
            }
            return ''
        }
        'match' {
            if ($FuncArgs.Count -ge 2) {
                $str = "$(Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables)"
                $regexArg = $FuncArgs[1].Trim()
                if ($regexArg.StartsWith('/') -and $regexArg.EndsWith('/')) {
                    $regexArg = $regexArg.Substring(1, $regexArg.Length - 2)
                }
                $m = [regex]::Match($str, $regexArg)
                if ($m.Success) {
                    $Variables['RSTART'] = $m.Index + 1
                    $Variables['RLENGTH'] = $m.Length
                    return $m.Index + 1
                }
                $Variables['RSTART'] = 0
                $Variables['RLENGTH'] = -1
                return 0
            }
            $Variables['RSTART'] = 0
            $Variables['RLENGTH'] = -1
            return 0
        }
        'strftime' {
            $fmtVal = if ($FuncArgs.Count -ge 1) {
                Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            } else {
                '%Y-%m-%d %H:%M:%S'
            }
            $timestamp = if ($FuncArgs.Count -ge 2) {
                Resolve-AwkExpression -Expr $FuncArgs[1] -Fields $Fields -Variables $Variables
            } else {
                $null
            }
            $epoch = [DateTimeOffset]::UnixEpoch
            $dt = if ($null -ne $timestamp -and "$timestamp" -ne '') {
                $ts = 0.0; [void][double]::TryParse("$timestamp", [ref]$ts)
                $epoch.AddSeconds($ts).DateTime
            } else {
                [DateTimeOffset]::UtcNow.DateTime
            }
            $fmtStr = "$fmtVal"
            # Map C/awk strftime specifiers to .NET format strings
            # Protect %% first to avoid double-replacement
            $fmtStr = $fmtStr -replace '%%', [char]0x01
            $fmtStr = $fmtStr -replace '%Y', $dt.ToString('yyyy')
            $fmtStr = $fmtStr -replace '%m', $dt.ToString('MM')
            $fmtStr = $fmtStr -replace '%d', $dt.ToString('dd')
            $fmtStr = $fmtStr -replace '%H', $dt.ToString('HH')
            $fmtStr = $fmtStr -replace '%M', $dt.ToString('mm')
            $fmtStr = $fmtStr -replace '%S', $dt.ToString('ss')
            $fmtStr = $fmtStr -replace '%j', $dt.DayOfYear.ToString('000')
            $fmtStr = $fmtStr -replace '%w', "$([int]$dt.DayOfWeek)"
            $fmtStr = $fmtStr -replace '%a', $dt.ToString('ddd')
            $fmtStr = $fmtStr -replace '%A', $dt.ToString('dddd')
            $fmtStr = $fmtStr -replace '%b', $dt.ToString('MMM')
            $fmtStr = $fmtStr -replace '%B', $dt.ToString('MMMM')
            $fmtStr = $fmtStr -replace '%p', $dt.ToString('tt')
            $fmtStr = $fmtStr -replace '%I', $dt.ToString('hh')
            $fmtStr = $fmtStr -replace [char]0x01, '%'
            return $fmtStr
        }
        'systime' {
            return [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        }
        'index' {
            if ($FuncArgs.Count -ge 2) {
                $str = "$(Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables)"
                $substr = "$(Resolve-AwkExpression -Expr $FuncArgs[1] -Fields $Fields -Variables $Variables)"
                $idx = $str.IndexOf($substr)
                return if ($idx -ge 0) { $idx + 1 } else { 0 }
            }
            return 0
        }
        'split' {
            if ($FuncArgs.Count -ge 2) {
                $str = "$(Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables)"
                $sepExpr = $FuncArgs[1].Trim()
                $sep = if ($sepExpr.StartsWith('/') -and $sepExpr.EndsWith('/')) {
                    $sepExpr.Substring(1, $sepExpr.Length - 2)
                } else {
                    "$(Resolve-AwkExpression -Expr $sepExpr -Fields $Fields -Variables $Variables)"
                }
                $parts = if ($sep.Length -eq 1 -and $sep -notmatch '[\[\]\(\)\{\}\.\+\*\?\^\$\|]') {
                    $str.Split([char]$sep[0])
                } else {
                    [regex]::Split($str, $sep)
                }
                if ($FuncArgs.Count -ge 3) {
                    $arrName = $FuncArgs[2].Trim()
                    $arr = @()
                    for ($ai = 0; $ai -lt $parts.Count; $ai++) {
                        $arr += "$($parts[$ai])"
                        $Variables["$arrName[$($ai + 1)]"] = "$($parts[$ai])"
                    }
                    $variables[$arrName] = $arr
                }
                return $parts.Count
            }
            return 0
        }
        'int' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            return [int][math]::Truncate($num)
        }
        'rand' { return ($script:AwkRand ?? [System.Random]::Shared).NextDouble() }
        'srand' {
            if ($FuncArgs.Count -ge 1) {
                $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
                $seed = 0; [void][int]::TryParse("$val", [ref]$seed)
                $script:AwkRand = [System.Random]::new($seed)
            } else {
                $script:AwkRand = [System.Random]::new()
            }
            return [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        }
        'sin' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            return [math]::Sin($num)
        }
        'cos' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            return [math]::Cos($num)
        }
        'atan2' {
            if ($FuncArgs.Count -ge 2) {
                $y = 0.0; $x = 0.0
                $yv = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
                $xv = Resolve-AwkExpression -Expr $FuncArgs[1] -Fields $Fields -Variables $Variables
                [void][double]::TryParse("$yv", [ref]$y)
                [void][double]::TryParse("$xv", [ref]$x)
                return [math]::Atan2($y, $x)
            }
            return 0
        }
        'exp' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            return [math]::Exp($num)
        }
        'log' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            if ($num -gt 0) { return [math]::Log($num) }
            return 0
        }
        'sqrt' {
            $val = Resolve-AwkExpression -Expr $FuncArgs[0] -Fields $Fields -Variables $Variables
            $num = 0.0; [void][double]::TryParse("$val", [ref]$num)
            if ($num -ge 0) { return [math]::Sqrt($num) }
            return 0
        }
        default { return '' }
    }
}

# --- cut Command ---

function Invoke-BashCut {
    [OutputType('PsBash.TextOutput')]
    param()
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
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $lines.Add($subLine)
                }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'cut'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) {
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
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'tr' }

    # Parse flags: -d (delete), -s (squeeze), -c/-C/--complement, -t/--truncate-set1
    $deleteMode = $false
    $squeezeMode = $false
    $complementMode = $false
    $truncateMode = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -ceq '--complement') {
            $complementMode = $true
            $i++
            continue
        }

        if ($arg -ceq '--truncate-set1') {
            $truncateMode = $true
            $i++
            continue
        }

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
                    'c' { $complementMode = $true }
                    'C' { $complementMode = $true }
                    't' { $truncateMode = $true }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Expand escape sequences in operands before character class expansion
    for ($ei = 0; $ei -lt $operands.Count; $ei++) {
        $operands[$ei] = Expand-EscapeSequences -Text $operands[$ei]
    }

    # Expand POSIX character classes: [:alpha:], [:digit:], etc.
    $expandPosixClass = {
        param([string]$Spec)
        $posixClasses = @{
            'alpha' = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ'
            'digit' = '0123456789'
            'alnum' = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'
            'upper' = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'
            'lower' = 'abcdefghijklmnopqrstuvwxyz'
            'space' = " `t`n`r`f`v"
            'punct' = '!"#$%&''()*+,-./:;<=>?@[\]^_`{|}~'
        }
        $result = $Spec
        foreach ($kv in $posixClasses.GetEnumerator()) {
            $pattern = "[:$($kv.Key):]"
            $result = $result.Replace($pattern, $kv.Value)
        }
        $result
    }

    $expandClass = {
        param([string]$Spec)
        # First expand POSIX classes
        $Spec = & $expandPosixClass $Spec
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
                $inSet = $set.IndexOf($ch) -ge 0
                if ($complementMode) {
                    # Complement + delete: keep chars that ARE in set
                    if ($inSet) { [void]$sb.Append($ch) }
                } else {
                    # Normal delete: keep chars NOT in set
                    if (-not $inSet) { [void]$sb.Append($ch) }
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
                if ($complementMode) { $inSet = -not $inSet }
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

            # Truncate SET2 to length of SET1
            if ($truncateMode -and $set2.Length -gt $set1.Length) {
                $set2 = $set2.Substring(0, $set1.Length)
            }

            if ($complementMode) {
                # Complement: SET1 becomes all 256 chars minus the original SET1
                $compSb = [System.Text.StringBuilder]::new()
                $set1Hash = [System.Collections.Generic.HashSet[char]]::new($set1.ToCharArray())
                for ($c = 0; $c -le 255; $c++) {
                    $ch = [char]$c
                    if (-not $set1Hash.Contains($ch)) {
                        [void]$compSb.Append($ch)
                    }
                }
                $set1 = $compSb.ToString()
                # Extend SET2 by repeating last char to match complement SET1 length
                if ($set2.Length -gt 0) {
                    while ($set2.Length -lt $set1.Length) {
                        $set2 += $set2[$set2.Length - 1]
                    }
                }
            }

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
            [void]$allText.Append($text + "`n")
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
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'uniq' }

    $countMode = $false
    $duplicatesOnly = $false
    $uniqueOnly = $false
    $ignoreCase = $false
    $skipFields = 0
    $skipChars = 0
    $checkChars = 0
    $operands = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]

        if ($arg -ceq '--') {
            $i++
            while ($i -lt $Arguments.Count) {
                $operands.Add($Arguments[$i])
                $i++
            }
            break
        }

        if ($arg -ceq '--ignore-case') {
            $ignoreCase = $true
            $i++
            continue
        }

        if ($arg -cmatch '^--skip-fields=(\d+)$') {
            $skipFields = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -cmatch '^--skip-chars=(\d+)$') {
            $skipChars = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg -cmatch '^--check-chars=(\d+)$') {
            $checkChars = [int]$Matches[1]
            $i++
            continue
        }

        if ($arg.StartsWith('-') -and $arg.Length -gt 1 -and $arg -notmatch '^-\d') {
            foreach ($ch in $arg.Substring(1).ToCharArray()) {
                switch ($ch) {
                    'c' { $countMode = $true }
                    'd' { $duplicatesOnly = $true }
                    'u' { $uniqueOnly = $true }
                    'i' { $ignoreCase = $true }
                    'f' {
                        $rest = $arg.Substring($arg.IndexOf('f') + 1)
                        if ($rest -match '^\d+') {
                            $skipFields = [int]$rest
                        } else {
                            $i++
                            if ($i -lt $Arguments.Count) {
                                $skipFields = [int]$Arguments[$i]
                            }
                        }
                    }
                    's' {
                        $rest = $arg.Substring($arg.IndexOf('s') + 1)
                        if ($rest -match '^\d+') {
                            $skipChars = [int]$rest
                        } else {
                            $i++
                            if ($i -lt $Arguments.Count) {
                                $skipChars = [int]$Arguments[$i]
                            }
                        }
                    }
                    'w' {
                        $rest = $arg.Substring($arg.IndexOf('w') + 1)
                        if ($rest -match '^\d+') {
                            $checkChars = [int]$rest
                        } else {
                            $i++
                            if ($i -lt $Arguments.Count) {
                                $checkChars = [int]$Arguments[$i]
                            }
                        }
                    }
                }
            }
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    # Build the comparison key from a line
    function Get-UniqKey([string]$Line) {
        $key = $Line
        # Step 1: skip fields (whitespace-delimited)
        if ($skipFields -gt 0) {
            $parts = $key -split '\s+', ($skipFields + 1)
            if ($parts.Count -gt $skipFields) {
                $key = $parts[$skipFields]
            } else {
                $key = ''
            }
        }
        # Step 2: skip characters
        if ($skipChars -gt 0 -and $key.Length -gt $skipChars) {
            $key = $key.Substring($skipChars)
        } elseif ($skipChars -gt 0) {
            $key = ''
        }
        # Step 3: limit characters
        if ($checkChars -gt 0 -and $key.Length -gt $checkChars) {
            $key = $key.Substring(0, $checkChars)
        }
        return $key
    }

    # Collect lines
    $lines = [System.Collections.Generic.List[string]]::new()

    if ($operands.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $lines.Add($subLine)
                }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'uniq'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) {
                $lines.Add($l)
            }
        }
    }

    # Group consecutive identical lines (using key comparison)
    $groups = [System.Collections.Generic.List[object]]::new()
    $prevLine = $null
    $prevKey = $null
    $runCount = 0

    foreach ($line in $lines) {
        $key = Get-UniqKey $line
        $same = if ($ignoreCase) { $key -ieq $prevKey } else { $key -ceq $prevKey }
        if ($same) {
            $runCount++
        } else {
            if ($null -ne $prevLine) {
                $groups.Add(@{ Line = $prevLine; Count = $runCount })
            }
            $prevLine = $line
            $prevKey = $key
            $runCount = 1
        }
    }
    if ($null -ne $prevLine) {
        $groups.Add(@{ Line = $prevLine; Count = $runCount })
    }

    foreach ($group in $groups) {
        if ($duplicatesOnly -and $group.Count -lt 2) { continue }
        if ($uniqueOnly -and $group.Count -gt 1) { continue }

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
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'rev' }

    # Collect lines
    $lines = [System.Collections.Generic.List[string]]::new()

    if ($Arguments.Count -eq 0 -and $pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $lines.Add($subLine)
                }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $Arguments)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'rev'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) {
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
    [OutputType('PsBash.TextOutput')]
    param()
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
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $lines.Add($subLine)
                }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'nl'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) {
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
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'diff' }

    $unified = $false
    $context = $false
    $brief = $false
    $ignoreAllSpace = $false
    $ignoreSpaceChange = $false
    $ignoreBlankLines = $false
    $ignoreCase = $false
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

        if ($arg -ceq '-c') {
            $context = $true
            $i++
            continue
        }

        if ($arg -ceq '-q' -or $arg -ceq '--brief') {
            $brief = $true
            $i++
            continue
        }

        if ($arg -ceq '-w' -or $arg -ceq '--ignore-all-space') {
            $ignoreAllSpace = $true
            $i++
            continue
        }

        if ($arg -ceq '-b' -or $arg -ceq '--ignore-space-change') {
            $ignoreSpaceChange = $true
            $i++
            continue
        }

        if ($arg -ceq '-B' -or $arg -ceq '--ignore-blank-lines') {
            $ignoreBlankLines = $true
            $i++
            continue
        }

        if ($arg -ceq '-i' -or $arg -ceq '--ignore-case') {
            $ignoreCase = $true
            $i++
            continue
        }

        $operands.Add($arg)
        $i++
    }

    if ($operands.Count -lt 2) {
        Write-BashError -Message 'diff: missing operand'
        return
    }

    $path1 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[0])
    $path2 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[1])

    $fileLines1 = Read-BashFileLines -Path $path1 -Command 'diff'
    if ($null -eq $fileLines1) { return }
    [string[]]$lines1 = @($fileLines1)
    $fileLines2 = Read-BashFileLines -Path $path2 -Command 'diff'
    if ($null -eq $fileLines2) { return }
    [string[]]$lines2 = @($fileLines2)

    # Build comparison keys applying whitespace/case/blank-line flags
    $cmp1 = [string[]]::new($lines1.Count)
    for ($xi = 0; $xi -lt $lines1.Count; $xi++) {
        $key = $lines1[$xi]
        if ($ignoreAllSpace) {
            $key = $key -replace '\s', ''
        } elseif ($ignoreSpaceChange) {
            $key = $key -replace '^\s+', '' -replace '\s+$', '' -replace '\s+', ' '
        }
        if ($ignoreCase) { $key = $key.ToLowerInvariant() }
        $cmp1[$xi] = $key
    }
    $cmp2 = [string[]]::new($lines2.Count)
    for ($yi = 0; $yi -lt $lines2.Count; $yi++) {
        $key = $lines2[$yi]
        if ($ignoreAllSpace) {
            $key = $key -replace '\s', ''
        } elseif ($ignoreSpaceChange) {
            $key = $key -replace '^\s+', '' -replace '\s+$', '' -replace '\s+', ' '
        }
        if ($ignoreCase) { $key = $key.ToLowerInvariant() }
        $cmp2[$yi] = $key
    }

    # When -B is set, build indices skipping blank lines for comparison
    $idx1 = if ($ignoreBlankLines) {
        ,@(
            for ($xi = 0; $xi -lt $cmp1.Count; $xi++) {
                if ($cmp1[$xi] -ne '') { $xi }
            }
        )
    } else {
        ,@(
            for ($xi = 0; $xi -lt $cmp1.Count; $xi++) { $xi }
        )
    }
    $idx2 = if ($ignoreBlankLines) {
        ,@(
            for ($yi = 0; $yi -lt $cmp2.Count; $yi++) {
                if ($cmp2[$yi] -ne '') { $yi }
            }
        )
    } else {
        ,@(
            for ($yi = 0; $yi -lt $cmp2.Count; $yi++) { $yi }
        )
    }

    $n = $idx1.Count
    $m = $idx2.Count

    # Compute LCS table on filtered comparison keys
    $dp = [int[,]]::new($n + 1, $m + 1)
    for ($xi = $n - 1; $xi -ge 0; $xi--) {
        for ($yi = $m - 1; $yi -ge 0; $yi--) {
            if ($cmp1[$idx1[$xi]] -ceq $cmp2[$idx2[$yi]]) {
                $dp[$xi, $yi] = $dp[($xi + 1), ($yi + 1)] + 1
            } else {
                $a = $dp[($xi + 1), $yi]
                $b = $dp[$xi, ($yi + 1)]
                $dp[$xi, $yi] = if ($a -ge $b) { $a } else { $b }
            }
        }
    }

    # Build edit script using original line indices
    $edits = [System.Collections.Generic.List[object]]::new()
    $xi = 0; $yi = 0
    while ($xi -lt $n -and $yi -lt $m) {
        if ($cmp1[$idx1[$xi]] -ceq $cmp2[$idx2[$yi]]) {
            $edits.Add(@{ Op = '='; Line1 = $idx1[$xi]; Line2 = $idx2[$yi] })
            $xi++; $yi++
        } elseif ($dp[($xi + 1), $yi] -ge $dp[$xi, ($yi + 1)]) {
            $edits.Add(@{ Op = '-'; Line1 = $idx1[$xi] })
            $xi++
        } else {
            $edits.Add(@{ Op = '+'; Line2 = $idx2[$yi] })
            $yi++
        }
    }
    while ($xi -lt $n) {
        $edits.Add(@{ Op = '-'; Line1 = $idx1[$xi] })
        $xi++
    }
    while ($yi -lt $m) {
        $edits.Add(@{ Op = '+'; Line2 = $idx2[$yi] })
        $yi++
    }

    # Check if files are identical
    $hasDiff = $false
    foreach ($e in $edits) {
        if ($e.Op -ne '=') { $hasDiff = $true; break }
    }
    if (-not $hasDiff) { return }

    # Brief mode: just report whether files differ
    if ($brief) {
        New-BashObject -BashText "Files $($operands[0]) and $($operands[1]) differ"
        return
    }

    # Collect hunks (shared by unified and context formats)
    # For normal format, hunks are emitted inline without context lines
    if ($unified -or $context) {
        $contextLines = 3
        $hunkGroups = [System.Collections.Generic.List[object]]::new()
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
                $hunkGroup = [System.Collections.Generic.List[object]]::new()
                for ($k = $start; $k -lt $end; $k++) {
                    $hunkGroup.Add($edits[$k])
                }
                $hunkGroups.Add($hunkGroup)
                $ei = $end
            } else {
                $ei++
            }
        }

        if ($unified) {
            # Unified format
            New-BashObject -BashText "--- $($operands[0])"
            New-BashObject -BashText "+++ $($operands[1])"
            foreach ($group in $hunkGroups) {
                $l1Start = -1; $l1Count = 0; $l2Start = -1; $l2Count = 0
                $hunkLines = [System.Collections.Generic.List[string]]::new()
                foreach ($e in $group) {
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
            # Context format
            New-BashObject -BashText "*** $($operands[0])"
            New-BashObject -BashText "--- $($operands[1])"
            foreach ($group in $hunkGroups) {
                $l1Start = -1; $l1End = -1; $l2Start = -1; $l2End = -1
                foreach ($e in $group) {
                    switch ($e.Op) {
                        '=' {
                            if ($l1Start -eq -1) { $l1Start = $e.Line1 + 1 }
                            $l1End = $e.Line1 + 1
                            if ($l2Start -eq -1) { $l2Start = $e.Line2 + 1 }
                            $l2End = $e.Line2 + 1
                        }
                        '-' {
                            if ($l1Start -eq -1) { $l1Start = $e.Line1 + 1 }
                            $l1End = $e.Line1 + 1
                        }
                        '+' {
                            if ($l2Start -eq -1) { $l2Start = $e.Line2 + 1 }
                            $l2End = $e.Line2 + 1
                        }
                    }
                }
                New-BashObject -BashText "***************"
                New-BashObject -BashText "*** ${l1Start},${l1End}"
                # Mark deletes that are paired with inserts as changes (!)
                $changeLine1 = @{}
                $gi = 0
                while ($gi -lt $group.Count) {
                    if ($group[$gi].Op -eq '-' -and ($gi + 1) -lt $group.Count -and $group[$gi + 1].Op -eq '+') {
                        $changeLine1[$group[$gi].Line1] = $true
                    }
                    $gi++
                }
                foreach ($e in $group) {
                    switch ($e.Op) {
                        '=' { New-BashObject -BashText "  $($lines1[$e.Line1])" }
                        '-' {
                            if ($changeLine1.ContainsKey($e.Line1)) {
                                New-BashObject -BashText "! $($lines1[$e.Line1])"
                            } else {
                                New-BashObject -BashText "- $($lines1[$e.Line1])"
                            }
                        }
                        '+' { <# shown in --- section #> }
                    }
                }
                New-BashObject -BashText "--- ${l2Start},${l2End}"
                $changeLine2 = @{}
                $gi = 0
                while ($gi -lt $group.Count) {
                    if ($group[$gi].Op -eq '+' -and $gi -gt 0 -and $group[$gi - 1].Op -eq '-') {
                        $changeLine2[$group[$gi].Line2] = $true
                    }
                    $gi++
                }
                foreach ($e in $group) {
                    switch ($e.Op) {
                        '=' { New-BashObject -BashText "  $($lines2[$e.Line2])" }
                        '-' { <# shown in *** section #> }
                        '+' {
                            if ($changeLine2.ContainsKey($e.Line2)) {
                                New-BashObject -BashText "! $($lines2[$e.Line2])"
                            } else {
                                New-BashObject -BashText "+ $($lines2[$e.Line2])"
                            }
                        }
                    }
                }
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
    [OutputType('PsBash.TextOutput')]
    param()
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

    if ($operands.Count -lt 2) {
        Write-BashError -Message 'comm: missing operand'
        return
    }

    $path1 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[0])
    $path2 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[1])

    $fileLines1 = Read-BashFileLines -Path $path1 -Command 'comm'
    if ($null -eq $fileLines1) { return }
    [string[]]$lines1 = @($fileLines1)
    $fileLines2 = Read-BashFileLines -Path $path2 -Command 'comm'
    if ($null -eq $fileLines2) { return }
    [string[]]$lines2 = @($fileLines2)

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
    [OutputType('PsBash.TextOutput')]
    param()
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
            if ($text.TrimEnd("`n".ToCharArray()).Contains("`n")) {
                foreach ($subLine in ($text.TrimEnd("`n".ToCharArray()) -split "`n")) {
                    $lines.Add($subLine)
                }
            } else {
                $lines.Add(($text.TrimEnd("`n".ToCharArray())))
            }
        }
    } else {
        foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
            $fileLines = Read-BashFileLines -Path $filePath -Command 'column'
            if ($null -eq $fileLines) { continue }
            foreach ($l in $fileLines) {
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
    [OutputType('PsBash.TextOutput')]
    param()
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

    if ($operands.Count -lt 2) {
        Write-BashError -Message 'join: missing operand'
        return
    }

    $path1 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[0])
    $path2 = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($operands[1])

    $fileLines1 = Read-BashFileLines -Path $path1 -Command 'join'
    if ($null -eq $fileLines1) { return }
    [string[]]$lines1 = @($fileLines1)
    $fileLines2 = Read-BashFileLines -Path $path2 -Command 'join'
    if ($null -eq $fileLines2) { return }
    [string[]]$lines2 = @($fileLines2)

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
    [OutputType('PsBash.TextOutput')]
    param()
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

    # Read all files
    $allFiles = [System.Collections.Generic.List[string[]]]::new()
    foreach ($filePath in (Resolve-BashGlob -Paths $operands)) {
        $fileLines = Read-BashFileLines -Path $filePath -Command 'paste'
        if ($null -eq $fileLines) { return }
        $allFiles.Add([string[]]@($fileLines))
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
    [OutputType('PsBash.TextOutput')]
    param()
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

    # Write to each file (skip $null which represents /dev/null)
    $resolvedPaths = $operands | Where-Object { $null -ne $_ -and $_ -ne '' }
    foreach ($filePath in (Resolve-BashGlob -Paths $resolvedPaths)) {
        $parentDir = Split-Path -Parent $filePath
        if ($parentDir -and -not (Test-Path -LiteralPath $parentDir)) {
            Write-BashError -Message "tee: ${filePath}: No such file or directory"
            continue
        }
        if ($append) {
            if (-not (Write-BashFileText -Path $filePath -Text $textContent -Command 'tee' -Append)) { continue }
        } else {
            if (-not (Write-BashFileText -Path $filePath -Text $textContent -Command 'tee')) { continue }
        }
    }

    # Pass through original objects
    foreach ($item in $pipelineInput) {
        $item
    }
}

# --- xargs Command ---

function Invoke-BashXargs {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') { return Show-BashHelp 'xargs' }

    $replaceStr = $null
    $maxArgs = 0
    $nullDelim = $false       # -0: use null-delimited input
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

        if ($arg -ceq '-0' -or $arg -ceq '--null') {
            $nullDelim = $true
            $i++
            continue
        }

        if ($arg -ceq '-I') {
            $i++
            if ($i -lt $Arguments.Count) {
                $replaceStr = [string]$Arguments[$i]
            }
            $i++
            continue
        }

        if ($arg.Length -gt 2 -and $arg.StartsWith('-I')) {
            $replaceStr = $arg.Substring(2)
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
        Write-BashError -Message 'xargs: no command specified'
        return
    }

    $cmd = $operands[0]
    # Resolve to Invoke-Bash* if the command has a runtime function
    $bashCmd = 'Invoke-Bash' + ($cmd.Substring(0,1).ToUpper() + $cmd.Substring(1))
    if (Get-Command $bashCmd -ErrorAction SilentlyContinue) { $cmd = $bashCmd }
    $cmdArgs = @()
    if ($operands.Count -gt 1) {
        $cmdArgs = @($operands[1..($operands.Count - 1)])
    }

    # Collect input lines (split by delimiter)
    $inputLines = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $pipelineInput) {
        $text = Get-BashText -InputObject $item

        if ($nullDelim) {
            # -0: split on null characters
            $delim = "`0"
        } else {
            # Default: split on newlines (bash-style)
            $delim = "`n"
        }

        # Remove trailing delimiter if present
        $text = $text -replace "$([regex]::Escape($delim))$", ''

        if ($text -match $([regex]::Escape($delim))) {
            foreach ($subLine in ($text -split $([regex]::Escape($delim)))) {
                if ($subLine -ne '') { $inputLines.Add($subLine) }
            }
        } else {
            if ($text -ne '') { $inputLines.Add($text) }
        }
    }

    if ($null -ne $replaceStr) {
        # Replacement mode: run command once per input line
        foreach ($line in $inputLines) {
            $replacedArgs = @($cmdArgs | ForEach-Object { $_.Replace($replaceStr, $line) })
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
                $bound = @()
                foreach ($item in $current) {
                    $bound += @(Invoke-JqFilter -Data $item -Filter $bindingExpr -Variables $scope)
                }
                $newScope = @{} + $scope
                $newScope[$varName] = $bound
                $current = $current
                $scope = $newScope
                continue
            }
            $next = @()
            foreach ($item in $current) {
                $next += @(Invoke-JqFilter -Data $item -Filter $seg -Variables $scope)
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
            $results += @(Invoke-JqFilter -Data $Data -Filter $seg.Trim() -Variables $Variables)
        }
        return $results
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

    $results = @(, $Data)
    if ($Data -is [array] -or $Data -is [System.Collections.IList]) {
        foreach ($elem in $Data) {
            $results += @(Invoke-JqRecurse -Data $elem)
        }
    } elseif ($Data -is [System.Collections.IDictionary]) {
        foreach ($val in $Data.Values) {
            $results += @(Invoke-JqRecurse -Data $val)
        }
    } elseif ($Data -is [PSCustomObject]) {
        foreach ($prop in $Data.PSObject.Properties) {
            if ($prop.Name -ne 'PSTypeName') {
                $results += @(Invoke-JqRecurse -Data $prop.Value)
            }
        }
    }
    return $results
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

# --- sleep ---

function Invoke-BashSleep {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'sleep' }

    if ($Arguments.Count -eq 0) {
        Write-BashError -Message 'sleep: missing operand'
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
            Write-BashError -Message "sleep: invalid time interval '$arg'"
            return
        }
        if ($val -lt 0) {
            Write-BashError -Message "sleep: invalid time interval '$arg'"
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
    [OutputType('PsBash.TimeOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'time' }

    if ($Arguments.Count -eq 0) {
        Write-BashError -Message 'time: missing command'
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
        foreach ($e in $errors) { Write-BashError -Message "$e" }
        if ($errors.Count -gt 0) { $exitCode = 1 }
    } catch {
        $sw.Stop()
        Write-BashError -Message $_.Exception.Message
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
        Write-BashError -Message "$_"
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
    [OutputType('PsBash.WhichOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'which' }

    $showAll = $false
    $operands = [System.Collections.Generic.List[string]]::new()
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-a') { $showAll = $true }
        else { $operands.Add($arg) }
    }

    if ($operands.Count -eq 0) {
        Write-BashError -Message 'which: missing operand'
        return
    }

    foreach ($name in $operands) {
        $cmds = @(Get-Command $name -ErrorAction SilentlyContinue)
        if ($cmds.Count -eq 0) {
            Write-BashError -Message "which: no $name in PATH"
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
    [OutputType('PsBash.AliasOutput')]
    param()
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
                Write-BashError -Message "unalias: ${name}: not found"
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
                Write-BashError -Message "alias: ${arg}: not found"
            }
        }
    }
}

# --- trap ---

$script:BashTrapHandlers = [System.Collections.Generic.Dictionary[string,object]]::new(
    [System.StringComparer]::OrdinalIgnoreCase
)

function Invoke-BashTrap {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'trap' }

    if ($Arguments.Count -eq 0) {
        foreach ($kvp in $script:BashTrapHandlers.GetEnumerator()) {
            $obj = [PSCustomObject]@{
                PSTypeName = 'PsBash.TrapOutput'
                Signal     = $kvp.Key
                Action     = $kvp.Value
                BashText   = "trap -- '$($kvp.Value)' $($kvp.Key)"
            }
            Set-BashDisplayProperty $obj
        }
        return
    }

    if ($Arguments.Count -eq 1 -and $Arguments[0] -ceq '-l') {
        $signals = @('EXIT', 'ERR', 'INT', 'TERM', 'HUP', 'QUIT', 'PIPE', 'ALRM', 'USR1', 'USR2')
        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.TrapOutput'
            Signal     = $null
            Action     = $null
            BashText   = ($signals -join ' ')
        }
        Set-BashDisplayProperty $obj
        return
    }

    $action = $null
    $signals = [System.Collections.Generic.List[string]]::new()
    $resetMode = $false

    if ($Arguments[0] -ceq '-' -or $Arguments[0] -ceq '--') {
        $resetMode = $true
        for ($i = 1; $i -lt $Arguments.Count; $i++) {
            $signals.Add($Arguments[$i].ToUpper())
        }
    } else {
        $action = $Arguments[0]
        for ($i = 1; $i -lt $Arguments.Count; $i++) {
            $signals.Add($Arguments[$i].ToUpper())
        }
    }

    if ($signals.Count -eq 0) {
        $signals.Add('EXIT')
    }

    foreach ($signal in $signals) {
        if ($resetMode -or ($action -eq '')) {
            if ($script:BashTrapHandlers.ContainsKey($signal)) {
                if ($signal -ceq 'EXIT') {
                    $existing = $script:BashTrapHandlers[$signal]
                    if ($existing -is [System.Management.Automation.PSEventSubscriber]) {
                        Unregister-Event -SubscriptionId $existing.SubscriptionId -ErrorAction SilentlyContinue
                    }
                }
                $script:BashTrapHandlers.Remove($signal) | Out-Null
            }
            continue
        }

        switch ($signal) {
            'EXIT' {
                $sb = [scriptblock]::Create($action)
                $sub = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action $sb
                $script:BashTrapHandlers['EXIT'] = $sub
            }
            'ERR' {
                $sb = [scriptblock]::Create($action)
                $script:BashTrapHandlers['ERR'] = $action
                Set-Variable -Name __BashTrapERR -Value $sb -Scope Global -Force
            }
            default {
                $script:BashTrapHandlers[$signal] = $action
            }
        }
    }
}

# --- eval ---

function Invoke-BashEval {
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    # Join all arguments into a single command string (bash eval behavior)
    $cmdStr = $Arguments -join ' '
    if ([string]::IsNullOrWhiteSpace($cmdStr)) {
        if ($pipelineInput.Count -gt 0) {
            $cmdStr = ($pipelineInput | ForEach-Object { Get-BashText -InputObject $_ }) -join ' '
        }
        if ([string]::IsNullOrWhiteSpace($cmdStr)) { return }
    }

    # Re-invoke ps-bash to transpile and execute the eval'd string
    $psBashExe = $null
    if ($__parentPid -and $__parentPid -gt 0) {
        try {
            $parent = [System.Diagnostics.Process]::GetProcessById($__parentPid)
            $psBashExe = $parent.MainModule.FileName
        } catch {}
    }
    if (-not $psBashExe) {
        $found = Get-Command ps-bash -ErrorAction SilentlyContinue
        if ($found) { $psBashExe = $found.Source }
    }
    if (-not $psBashExe) {
        $psBashExe = [System.IO.Path]::Combine(
            [System.IO.Path]::GetDirectoryName([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName),
            'ps-bash'
        )
        if ($IsWindows) { $psBashExe += '.exe' }
        if (-not (Test-Path $psBashExe)) {
            Write-BashError -Message 'eval: ps-bash executable not found'
            return
        }
    }

    $output = & $psBashExe -c $cmdStr 2>&1
    $exitCode = $LASTEXITCODE
    $global:LASTEXITCODE = $exitCode

    $errors = @($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
    $normal = @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })

    foreach ($e in $errors) {
        [Console]::Error.WriteLine("$e")
    }

    foreach ($item in $normal) {
        $text = if ($item.PSObject.Properties['BashText']) { $item.BashText } else { "$item" }
        Emit-BashLine -Text $text
    }
}

# --- read ---

function Invoke-BashRead {
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $prompt = $null
    $promptSet = $false
    $varNames = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-r') {
            $i++; continue
        }
        if ($arg -ceq '-p' -and ($i + 1) -lt $Arguments.Count) {
            $prompt = $Arguments[$i + 1]
            $promptSet = $true
            $i += 2; continue
        }
        if ($arg -ceq '-a' -and ($i + 1) -lt $Arguments.Count) {
            # read -a arr: read into array
            $varNames.Add($Arguments[$i + 1])
            $i += 2; continue
        }
        $varNames.Add($arg)
        $i++
    }

    if ($varNames.Count -eq 0) { return }

    # Determine input source: pipeline or interactive
    $inputLine = $null
    if ($pipelineInput.Count -gt 0) {
        # Collect text from pipeline input
        $allText = [System.Text.StringBuilder]::new()
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text) { [void]$allText.Append($text) }
        }
        $inputLine = $allText.ToString() -replace "`r`n", "`n" -replace "`n$", ''
    } else {
        # Interactive: use Read-Host
        if ($promptSet) {
            $inputLine = Read-Host $prompt
        } else {
            $inputLine = Read-Host
        }
    }

    if ($null -eq $inputLine) { return }

    if ($varNames.Count -eq 1) {
        # Single variable: assign entire line
        Set-Variable -Name $varNames[0] -Value $inputLine
    } else {
        # Multiple variables: split by whitespace
        $parts = $inputLine -split '\s+'
        for ($j = 0; $j -lt $varNames.Count; $j++) {
            if ($j -lt $parts.Count - 1) {
                Set-Variable -Name $varNames[$j] -Value $parts[$j]
            } elseif ($j -eq $varNames.Count - 1) {
                # Last variable gets remaining text
                $remaining = ($parts[$j..($parts.Count - 1)] -join ' ')
                Set-Variable -Name $varNames[$j] -Value $remaining
            } else {
                Set-Variable -Name $varNames[$j] -Value ''
            }
        }
    }
}

# --- mapfile / readarray ---

function Invoke-BashMapfile {
    param()
    $Arguments = [string[]]$args
    $pipelineInput = @($input)

    $count = $null
    $origin = 0
    $stripTrailing = $false
    $varName = 'MAPFILE'

    $i = 0
    while ($i -lt $Arguments.Count) {
        $arg = $Arguments[$i]
        if ($arg -ceq '-t') {
            $stripTrailing = $true
            $i++; continue
        }
        if ($arg -ceq '-n' -and ($i + 1) -lt $Arguments.Count) {
            $count = [int]$Arguments[$i + 1]
            $i += 2; continue
        }
        if ($arg.StartsWith('-n') -and $arg.Length -gt 2) {
            $count = [int]$arg.Substring(2)
            $i++; continue
        }
        if ($arg -ceq '-O' -and ($i + 1) -lt $Arguments.Count) {
            $origin = [int]$Arguments[$i + 1]
            $i += 2; continue
        }
        if ($arg.StartsWith('-O') -and $arg.Length -gt 2) {
            $origin = [int]$arg.Substring(2)
            $i++; continue
        }
        # -d DELIM: custom delimiter (consumed but currently splits on \n only)
        if ($arg -ceq '-d' -and ($i + 1) -lt $Arguments.Count) {
            $i += 2; continue
        }
        if ($arg.StartsWith('-d') -and $arg.Length -gt 2) {
            $i++; continue
        }
        # Non-flag argument is the variable name
        if (-not $arg.StartsWith('-')) {
            $varName = $arg
        }
        $i++
    }

    # Collect input: pipeline or stdin
    $lines = [System.Collections.Generic.List[string]]::new()

    if ($pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $text = Get-BashText -InputObject $item
            if ($text) {
                foreach ($line in ($text -replace "`r`n", "`n" -split "`n")) {
                    if ($line -ne '') { $lines.Add($line) }
                }
            }
        }
    }

    # Apply count limit
    if ($null -ne $count -and $lines.Count -gt $count) {
        $lines = $lines.GetRange(0, $count)
    }

    # Strip trailing delimiter if requested
    if ($stripTrailing) {
        for ($j = 0; $j -lt $lines.Count; $j++) {
            $lines[$j] = $lines[$j].TrimEnd("`n"[0], "`r"[0])
        }
    }

    # Build result array with origin offset
    if ($origin -gt 0) {
        $result = @(1..$origin | ForEach-Object { '' })
        $result += @($lines)
    } else {
        $result = @($lines)
    }

    Set-Variable -Name $varName -Value $result
}

# --- readlink ---

function Invoke-BashReadlink {
    [OutputType('PsBash.ReadlinkOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'readlink' }

    $canonicalize = $false
    $operands = [System.Collections.Generic.List[string]]::new()
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-f') { $canonicalize = $true }
        else { $operands.Add($arg) }
    }

    if ($operands.Count -eq 0) {
        Write-Error 'readlink: missing operand' -ErrorAction Continue
        return
    }

    foreach ($path in $operands) {
        if ($canonicalize) {
            $resolved = (Resolve-Path -Path $path -ErrorAction SilentlyContinue)
            if (-not $resolved) {
                Write-Error "readlink: ${path}: No such file or directory" -ErrorAction Continue
                continue
            }
            $text = $resolved.Path
        } else {
            $item = Get-Item -Path $path -ErrorAction SilentlyContinue
            if (-not $item) {
                Write-Error "readlink: ${path}: No such file or directory" -ErrorAction Continue
                continue
            }
            $text = if ($item.Target) { $item.Target } else { $item.FullName }
        }
        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.ReadlinkOutput'
            Path       = $text
            BashText   = $text
        }
        Set-BashDisplayProperty $obj
    }
}

# --- mktemp ---

function Invoke-BashMktemp {
    [OutputType('PsBash.MktempOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'mktemp' }

    $makeDir = $false
    $template = $null
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-d') { $makeDir = $true }
        else { $template = $arg }
    }

    $subDir = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'ps-bash', 'proc-sub')
    [void][System.IO.Directory]::CreateDirectory($subDir)

    $name = [System.IO.Path]::GetRandomFileName()
    if ($template) {
        $prefix = $template -replace 'X+$', ''
        $prefix = [System.IO.Path]::GetFileName($prefix)
        $name = $prefix + [System.IO.Path]::GetRandomFileName()
    }

    $fullPath = [System.IO.Path]::Combine($subDir, $name)

    if ($makeDir) {
        [void][System.IO.Directory]::CreateDirectory($fullPath)
    } else {
        [void][System.IO.File]::WriteAllText($fullPath, '')
    }

    $obj = [PSCustomObject]@{
        PSTypeName = 'PsBash.MktempOutput'
        Path       = $fullPath
        BashText   = $fullPath
    }
    Set-BashDisplayProperty $obj
}

# --- type ---

function Invoke-BashType {
    [OutputType('PsBash.TypeOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'type' }

    $typeOnly = $false
    $operands = [System.Collections.Generic.List[string]]::new()
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-t') { $typeOnly = $true }
        else { $operands.Add($arg) }
    }

    if ($operands.Count -eq 0) {
        Write-Error 'type: missing operand' -ErrorAction Continue
        return
    }

    $builtins = @('echo', 'printf', 'type', 'cd', 'exit', 'return', 'export',
                   'unset', 'set', 'shift', 'read', 'eval', 'source', 'trap',
                   'alias', 'unalias', 'test', '[', 'true', 'false')

    foreach ($name in $operands) {
        $isBuiltin = $builtins -contains $name
        if ($isBuiltin) {
            $kind = 'builtin'
            $text = if ($typeOnly) { $kind } else { "$name is a shell builtin" }
        } else {
            $cmd = Get-Command $name -ErrorAction SilentlyContinue
            if (-not $cmd) {
                Write-Error "bash: type: ${name}: not found" -ErrorAction Continue
                continue
            }
            switch ($cmd.CommandType) {
                'Alias'    { $kind = 'alias'; $text = if ($typeOnly) { $kind } else { "$name is aliased to ``$($cmd.Definition)''" } }
                'Function' { $kind = 'function'; $text = if ($typeOnly) { $kind } else { "$name is a function" } }
                default    { $kind = 'file'; $text = if ($typeOnly) { $kind } else { "$name is $($cmd.Source)" } }
            }
        }
        $obj = [PSCustomObject]@{
            PSTypeName = 'PsBash.TypeOutput'
            Command    = $name
            Kind       = $kind
            BashText   = $text
        }
        Set-BashDisplayProperty $obj
    }
}

function Invoke-BashBash {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'bash' }

    # Resolve the ps-bash executable: prefer the parent process path (exact binary),
    # fall back to Get-Command ps-bash.
    $psBashExe = $null
    if ($__parentPid -and $__parentPid -gt 0) {
        try {
            $parent = [System.Diagnostics.Process]::GetProcessById($__parentPid)
            $psBashExe = $parent.MainModule.FileName
        } catch {}
    }
    if (-not $psBashExe) {
        $found = Get-Command ps-bash -ErrorAction SilentlyContinue
        if ($found) { $psBashExe = $found.Source }
    }
    if (-not $psBashExe) {
        # Try the same directory as the current PowerShell executable
        $psBashExe = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName), 'ps-bash')
        if ($IsWindows) { $psBashExe += '.exe' }
        if (-not (Test-Path $psBashExe)) {
            Write-BashError -Message 'bash: ps-bash executable not found'
            return
        }
    }

    # Handle --version: print ps-bash version info
    if ($Arguments -contains '--version') {
        $version = $null
        if ($null -ne (Get-Variable MyInvocation -ValueOnly -ErrorAction SilentlyContinue)) {
            $mod = $MyInvocation.MyCommand.Module
            if ($mod) { $version = $mod.Version.ToString() }
        }
        if (-not $version) { $version = '0.7.1' }
        $text = "ps-bash, version $version`nBash-to-PowerShell transpiler"
        Emit-BashLine -Text $text
        return
    }

    # Forward all arguments to ps-bash executable
    $output = & $psBashExe @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $global:LASTEXITCODE = $exitCode

    $errors = @($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
    $normal = @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })

    foreach ($e in $errors) {
        [Console]::Error.WriteLine("$e")
    }

    foreach ($item in $normal) {
        $text = if ($item.PSObject.Properties['BashText']) { $item.BashText } else { "$item" }
        Emit-BashLine -Text $text
    }
}

# --- Background Process Support ---

$script:BashBgPids = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$global:BashBgLastPid = $null

function Invoke-BashBackground {
    <#
    .SYNOPSIS
        Run a command as a background process (bash & operator).
    #>
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    $encodedCmd = [Convert]::ToBase64String(
        [System.Text.Encoding]::Unicode.GetBytes($Command.ToString())
    )

    $pwshPath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName

    $proc = Start-Process -FilePath $pwshPath -ArgumentList '-NoLogo','-NoProfile','-EncodedCommand',$encodedCmd -PassThru -NoNewWindow

    $script:BashBgPids.Add($proc)
    $global:BashBgLastPid = $proc.Id
    $global:BashBgLastPid
}

function Invoke-BashWait {
    <#
    .SYNOPSIS
        Wait for background processes to finish (bash wait command).
    #>
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'wait' }

    if ($Arguments.Count -gt 0) {
        foreach ($pidArg in $Arguments) {
            if (-not [int]::TryParse($pidArg, [ref]$null)) { continue }
            $pid = [int]$pidArg
            $proc = $script:BashBgPids | Where-Object { $_.Id -eq $pid }
            if ($proc) {
                foreach ($p in @($proc)) {
                    if (-not $p.HasExited) { [void]$p.WaitForExit() }
                    [void]$script:BashBgPids.Remove($p)
                }
            }
        }
    } else {
        foreach ($p in @($script:BashBgPids)) {
            if (-not $p.HasExited) { [void]$p.WaitForExit() }
        }
        $script:BashBgPids.Clear()
    }
}

function Invoke-BashJobs {
    <#
    .SYNOPSIS
        List background processes (bash jobs command).
    #>
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'jobs' }

    if ($script:BashBgPids.Count -eq 0) {
        return
    }

    $i = 1
    foreach ($proc in $script:BashBgPids) {
        $status = if ($proc.HasExited) { 'Done' } else { 'Running' }
        New-BashObject -BashText "[$i]`t$status`t$($proc.Id)`t$($proc.ProcessName)`n"
        $i++
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
    'uname'    = 'Print system information.'
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
    'trap'     = 'Trap signals and other events.'
    'eval'     = 'Evaluate arguments as a bash command.'
    'mapfile'  = 'Read lines from standard input into an array variable.'
    'readarray' = 'Read lines from standard input into an array variable.'
    'readlink' = 'Print resolved symbolic links or canonical file names.'
    'mktemp'   = 'Create a temporary file or directory.'
    'type'     = 'Display information about command type.'
    'bash'     = 'Invoke ps-bash transpiler for nested bash execution.'
    'wait'     = 'Wait for background processes to finish.'
    'jobs'     = 'List background processes and their status.'
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
        @('-C', 'context'),           @('-F', 'fixed strings'),    @('-w', 'word regexp'),
        @('-o', 'only matching'),     @('-H', 'with filename'),    @('-h', 'no filename'),
        @('-e', 'pattern'),           @('-m', 'max count')
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
    'tail'     = @( @('-n', 'number of lines'), @('-f', 'follow file for changes'), @('-c', 'output last N bytes'), @('-s', 'poll interval in seconds') )
    'wc'       = @( @('-l', 'line count'), @('-w', 'word count'), @('-c', 'byte count') )
    'find'     = @(
        @('-name', 'name pattern'),   @('-type', 'file type'),     @('-size', 'file size'),
        @('-maxdepth', 'max depth'),  @('-mtime', 'modify time'),  @('-empty', 'empty files'),
        @('-print0', 'null-delimited output'), @('-exec', 'execute command')
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
    'awk'      = @( @('-F', 'field separator'), @('-v', 'variable'), @('-f', 'program file'), @('--file', 'program file') )
    'cut'      = @( @('-d', 'delimiter'), @('-f', 'fields'), @('-c', 'characters') )
    'tr'       = @( @('-c', 'complement'), @('-C', 'complement'), @('-d', 'delete'), @('-s', 'squeeze'), @('-t', 'truncate SET2') )
    'uniq'     = @( @('-c', 'count'), @('-d', 'duplicates only') )
    'nl'       = @( @('-ba', 'number all lines') )
    'diff'     = @(
        @('-u', 'unified format'),
        @('-c', 'context format'),
        @('-q', 'report only whether files differ'),
        @('-w', 'ignore all whitespace'),
        @('-b', 'ignore changes in whitespace amount'),
        @('-B', 'ignore blank line changes'),
        @('-i', 'case-insensitive comparison')
    )
    'comm'     = @( @('-1', 'suppress col 1'), @('-2', 'suppress col 2'), @('-3', 'suppress col 3') )
    'column'   = @( @('-t', 'table mode'), @('-s', 'separator') )
    'join'     = @( @('-t', 'delimiter'), @('-1', 'field from file 1'), @('-2', 'field from file 2') )
    'paste'    = @( @('-d', 'delimiter'), @('-s', 'serial') )
    'tee'      = @( @('-a', 'append') )
    'xargs'    = @( @('-I', 'replace string'), @('-n', 'max args'), @('-0', 'null-delimited input') )
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
    'uname'    = @( @('-s', 'kernel name'), @('-n', 'hostname'), @('-r', 'release'), @('-m', 'machine'), @('-a', 'all') )
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
    'eval'     = @( @('COMMAND', 'command string to evaluate') )
    'mapfile'  = @( @('-t', 'strip trailing delimiter'), @('-n', 'copy at most N lines'), @('-O', 'start assigning at index N') )
    'readarray' = @( @('-t', 'strip trailing delimiter'), @('-n', 'copy at most N lines'), @('-O', 'start assigning at index N') )
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
Set-Alias -Name 'uname'    -Value 'Invoke-BashUname'    -Force -Scope Global -Option AllScope
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
Set-Alias -Name 'readlink' -Value 'Invoke-BashReadlink' -Force -Scope Global -Option AllScope
Set-Alias -Name 'mktemp'   -Value 'Invoke-BashMktemp'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'type'     -Value 'Invoke-BashType'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'bash'     -Value 'Invoke-BashBash'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'wait'     -Value 'Invoke-BashWait'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'jobs'     -Value 'Invoke-BashJobs'     -Force -Scope Global -Option AllScope

# --- Type-level ToString for BashObject types ---
# Update-TypeData defines ToString() once per type name instead of per-object,
# eliminating the per-object Add-Member ScriptMethod overhead (~6x faster).
foreach ($tn in @(
        'PsBash.TextOutput', 'PsBash.LsEntry', 'PsBash.CatLine', 'PsBash.GrepMatch',
        'PsBash.WcResult', 'PsBash.FindEntry', 'PsBash.StatEntry', 'PsBash.PsEntry',
        'PsBash.DateOutput', 'PsBash.SeqOutput', 'PsBash.ExprOutput', 'PsBash.DuEntry',
        'PsBash.TreeEntry', 'PsBash.EnvEntry', 'PsBash.RgMatch', 'PsBash.GzipListOutput',
        'PsBash.TarListOutput', 'PsBash.TimeOutput', 'PsBash.WhichOutput', 'PsBash.AliasOutput',
        'PsBash.TrapOutput', 'PsBash.ReadlinkOutput', 'PsBash.MktempOutput', 'PsBash.TypeOutput'
    )) {
    Update-TypeData -TypeName $tn -MemberName ToString -MemberType ScriptMethod -Value { $this.BashText } -Force
}
