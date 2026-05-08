#Requires -Version 7.0

Set-StrictMode -Version Latest

# Global state initialized here so strict-mode code can access these variables
# before any bash function runs. Positional params are cleared by `set --` and
# saved/restored by every emitted bash function that references $1..$9 / $@.
if (-not (Get-Variable -Name BashPositional -Scope global -ErrorAction SilentlyContinue)) {
    $global:BashPositional = $null
}

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

# Source-capture variant: runs the producer scriptblock, collects its output
# as bash text, transpiles it to PowerShell via BashTranspiler, and executes
# the result in the caller's scope (source semantics). Used by the emitter for
# 'source <(cmd)'.  Requires [PsBash.Core.Transpiler.BashTranspiler] to be
# available in the AppDomain (true when running inside ps-bash-host or when
# PsBash.Core.dll has been loaded via Add-Type by the worker init script).
function Invoke-ProcessSubSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command
    )
    $output = & $Command
    $sb = [System.Text.StringBuilder]::new()
    foreach ($item in $output) {
        [void]$sb.Append((Get-BashText -InputObject $item).TrimEnd("`r`n"))
        [void]$sb.Append("`n")
    }
    $bashContent = $sb.ToString()
    if ([string]::IsNullOrWhiteSpace($bashContent)) { return }
    try {
        $psCode = [PsBash.Core.Transpiler.BashTranspiler]::Transpile(
            $bashContent,
            [PsBash.Core.Transpiler.TranspileContext]::Eval)
    } catch {
        [Console]::Error.WriteLine("ps-bash: source <(...): transpile error: $_")
        $global:LASTEXITCODE = 1
        return
    }
    if (-not $psCode) { return }
    # Promote function definitions to global scope so they persist after this
    # function returns (bash source semantics: defined names survive in caller scope).
    $psCode = $psCode -replace '\bfunction\s+(\w+)\b', 'function global:$1'
    # Execute the transpiled PowerShell in the global scope so env vars and
    # functions are visible to subsequent commands in the worker loop.
    $PSCmdlet.InvokeCommand.InvokeScript($false, [scriptblock]::Create($psCode), $null, $null)
}

# String-capture variant: runs the producer scriptblock and returns its
# combined bash text output as a single string. Useful for consumers that
# need the raw bash text (e.g. eval <(...)).
function Invoke-ProcessSubString {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command
    )
    $output = & $Command
    $sb = [System.Text.StringBuilder]::new()
    foreach ($item in $output) {
        [void]$sb.Append((Get-BashText -InputObject $item).TrimEnd("`r`n"))
        [void]$sb.Append("`n")
    }
    return $sb.ToString().TrimEnd("`n")
}

# placeholder — activated by T10c when emitter routes mapped-command consumers here
# Pipeline-object variant: runs the producer scriptblock and yields its
# output objects directly into the pipeline. Useful when the consumer is
# a ps-bash mapped command that accepts pipeline objects (e.g. sort, uniq).
# Currently unused by EmitProcessSub (which always emits Invoke-ProcessSub for
# the temp-file path). T10c will teach the emitter to route mapped-command
# consumers through this function so typed pipeline objects survive the seam.
function Invoke-ProcessSubPipeline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command
    )
    & $Command
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
        $isLast = ($li -eq $lines.Count - 1)
        $noNl = ($isLast -and -not $hasTrailingNewline)
        New-BashObject -BashText $lines[$li] @cmdSplat -NoTrailingNewline:$noNl
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

        if ($arg.StartsWith('--') -and $arg.Length -gt 2) {
            if ($flags.ContainsKey($arg)) {
                $flags[$arg] = $true
            } else {
                $operands.Add($arg)
            }
        } elseif ($arg.StartsWith('-') -and $arg.Length -gt 1) {
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

