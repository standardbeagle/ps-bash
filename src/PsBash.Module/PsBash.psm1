#Requires -Version 7.0

# NOTE: StrictMode is intentionally NOT set at file scope (REFACTOR-6).
# File-scope `Set-StrictMode -Version Latest` is hazardous in a ~98-function
# library module:
#   1. Forward-reference traps fire at first CALL, not at definition
#      (cf. the Invoke-BashPwd $global:__PsBashCwd crash, commit 3378227).
#   2. A StrictMode trip during a function-body parse can silently abort the
#      rest of the parse pass, leaving later functions unregistered — the
#      leading hypothesis for the RC-3a partial-load gap.
#   3. Many runtime helpers legitimately index possibly-missing dictionary
#      keys or past-end array slots; under strict mode those throw.
# Instead, StrictMode is opted into per-function only where it catches real
# defects — currently the arg-parsing helpers ConvertFrom-BashArgs and
# New-FlagDefs, where strict mode catches typos in flag-definition tables.
# The ModulePartialLoadTests CI guard asserts the full advertised surface
# (FunctionsToExport / AliasesToExport) is Get-Command-resolvable.

# Global state initialized here so bash functions can access these variables
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

function Write-BashHostStderr {
    <#
    .SYNOPSIS
        Emit a line to the host's stderr stream.
    .DESCRIPTION
        REFACTOR-4: all host -> launcher output travels the single IPC channel
        the launcher drains. The host's inherited fd 2 is detached to /dev/null
        (commit cc8bf88's hang fix), so a direct [Console]::Error.WriteLine is
        silently lost. $Host.UI.WriteErrorLine is wired by the host's SdkWorker
        to a STDERR-tagged IPC frame; the launcher routes that frame to its own
        Console.Error. This is the runtime target the emitter rewrites `cmd >&2`
        to, and the sink Write-BashError uses in Bash error mode.
    #>
    param([string]$Message)
    $Host.UI.WriteErrorLine($Message)
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
        Write-BashHostStderr $Message
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

# RC-8a: Invoke-ProcessSubSource was migrated from this psm1 to a binary
# cmdlet in PsBash.Cmdlets (InvokeProcessSubSourceCommand.cs). As a psm1
# function it introduced a script function scope, so source <(...) env vars
# and function defs landed in the function/module scope and were discarded
# on return. Cmdlets do not push a script scope frame, so
# InvokeCommand.InvokeScript(useNewScope:false) from the cmdlet targets the
# eval pipeline's scope — exactly where bash 'source' wants the names to land.

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
    # REFACTOR-2 Phase 2: trailing-\n normalization delegates to the shared C#
    # helper [PsBash.Cmdlets.BashRuntime]::NormalizeBashText.
    param([PSCustomObject]$Object)
    if ($Object.BashText) {
        $Object.BashText = [PsBash.Cmdlets.BashRuntime]::NormalizeBashText($Object.BashText)
    }
    $Object
}

function New-BashObject {
    # REFACTOR-2 Phase 2: thin wrapper delegating to the shared AOT-safe C#
    # helper [PsBash.Cmdlets.BashRuntime]::NewBashObject. The helper reproduces
    # the exact contract: TextOutput fast path returns a plain string; the
    # typed / NoTrailingNewline path returns a PSObject with PSTypeName +
    # BashText (+ optional NoTrailingNewline / Command note properties).
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

    [PsBash.Cmdlets.BashRuntime]::NewBashObject(
        $BashText, $TypeName, [bool]$NoTrailingNewline, $Command)
}

function Emit-BashLine {
    # Splits text on newlines and emits one BashObject per line.
    # Matches bash semantics: stdout is a byte stream, \n is a record boundary.
    # Sources (printf, echo -e, heredocs) call this for text output.
    # New-BashObject stays unchanged for typed objects (LsEntry, CatLine, PsEntry).
    # Accepts -Text parameter (direct call) or pipeline input (heredoc piping).
    # REFACTOR-2 Phase 2: line-splitting delegates to the shared C# helper
    # [PsBash.Cmdlets.BashRuntime]::EmitBashLines.
    param([string]$Text, [string]$Command)
    $pipelineInput = @($input)
    if (-not $Text -and $pipelineInput.Count -gt 0) {
        $Text = $pipelineInput -join "`n"
    }
    if (-not $Text) { return }
    foreach ($obj in [PsBash.Cmdlets.BashRuntime]::EmitBashLines($Text, $Command)) {
        $obj
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
    # REFACTOR-2 Phase 2: thin wrapper delegating to the shared AOT-safe C#
    # helper [PsBash.Cmdlets.BashRuntime]::ConvertFromBashArgs. The helper
    # reproduces the exact contract: -- ends flags, recognized long flags,
    # bundled short flags, an unrecognized bundle char turns the whole token
    # into an operand. The return shape is the same @{ Flags; Operands }
    # hashtable callers already destructure (Flags is an ordinal
    # Dictionary[string,bool]; Operands is a List[string]).
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

    $parsed = [PsBash.Cmdlets.BashRuntime]::ConvertFromBashArgs($Arguments, $FlagDefs)
    @{
        Flags    = $parsed.Flags
        Operands = $parsed.Operands
    }
}

# --- Escape Sequence Processing ---

function Expand-EscapeSequences {
    # REFACTOR-2 Phase 2: thin wrapper delegating to the shared AOT-safe C#
    # helper [PsBash.Cmdlets.BashRuntime]::ExpandEscapeSequences. The helper
    # reproduces the exact sentinel-based two-pass scheme (\\ -> NUL sentinel
    # -> expand \n\t\r\a\b\f\v -> restore sentinel to literal backslash).
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Text
    )

    [PsBash.Cmdlets.BashRuntime]::ExpandEscapeSequences($Text)
}

# --- Case-sensitive flag dictionary helper ---

function New-FlagDefs {
    # REFACTOR-2 Phase 2: thin wrapper delegating to the shared AOT-safe C#
    # helper [PsBash.Cmdlets.BashRuntime]::NewFlagDefs, which builds the same
    # ordinal Dictionary[string,string] from a flat flag/description entry list
    # and throws on an odd-length list.
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.Dictionary[string,string]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        [string[]]$Entries
    )

    [PsBash.Cmdlets.BashRuntime]::NewFlagDefs($Entries)
}

# --- echo Command ---
# REFACTOR-2 Phase 1b note: Invoke-BashEcho was NOT migrated to a binary
# cmdlet. echo's bash flags -e / -n / -E prefix-match PowerShell common
# parameters (-ErrorAction etc.) under PSCmdlet parameter binding — `-e`
# binds ambiguously and never reaches a ValueFromRemainingArguments param.
# The psm1 `param()` form (no [CmdletBinding()]) takes no common parameters,
# so $args receives -e/-n/-E literally. echo therefore stays a psm1 function.
# (Invoke-BashPrintf and Invoke-BashPwd, which have no colliding short flags,
# WERE migrated — see PsBash.Cmdlets/InvokeBashPrintfCommand.cs / InvokeBashPwdCommand.cs.)

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
# REFACTOR-2 Phase 1b: Invoke-BashPrintf migrated to a binary cmdlet
# (PsBash.Cmdlets/InvokeBashPrintfCommand.cs). The psm1 no longer defines it;
# the `Set-Alias printf -> Invoke-BashPrintf` line below resolves to the cmdlet.

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
# ls is a binary cmdlet as of REFACTOR-2 Phase 1d (InvokeBashLsCommand.cs).
# It owns Tier 2 -- the real-filesystem hot path (System.IO streaming, no
# Get-ChildItem / Get-Acl) -- entirely in C#. Tiers 1 and 3 stay here in psm1
# because both touch module-scoped state the cmdlet cannot reach:
#   Tier 1. Custom providers -- user-registered handlers for synthetic paths
#           ($script:BashLsProviders, registered via Register-BashLsProvider).
#   Tier 3. PS provider fallback -- Get-ChildItem for Registry:, Cert:,
#           Variable:, custom PSDrives, etc.
# The cmdlet calls the Get-BashLsProviderEntries shim below for any target
# that is not a real filesystem directory or file.
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

# Invoke-BashLs migrated to a binary cmdlet in PsBash.Cmdlets.dll
# (REFACTOR-2 Phase 1d -- InvokeBashLsCommand.cs). It is the final leaf of
# REFACTOR-2 Phase 1. The psm1 no longer defines Invoke-BashLs or its ls-only
# helper Test-IsExecutable; the cmdlet reimplements the pure helper web
# (Get-LsEntryFromFsi / ConvertTo-PermissionString / Format-BashSize /
# Format-BashDate / Format-LsLine / Test-IsExecutable) in C# and owns Tier 2
# (the real-filesystem hot path) plus the uniform sort + format pass. The
# Set-Alias 'ls' line below resolves to the cmdlet. ls's short flags
# (-l -a -A -h -R -S -t -r -1 -p -d -F -i -s) do not prefix-collide with any
# PowerShell common parameter, so no explicit SwitchParameter was needed.
#
# Tier 1 (custom $script:BashLsProviders) and Tier 3 (PS-provider fallback:
# Registry:, Cert:, custom PSDrives) reference module-scoped psm1 state a
# binary cmdlet cannot reach, so they stay here behind the Get-BashLsProviderEntries
# shim, which the cmdlet calls for any target that is not a real filesystem
# directory or file. The shim returns raw, unsorted, unformatted PsBash.LsEntry
# objects; the cmdlet sorts and formats every tier uniformly.

function Get-BashLsProviderEntries {
    # Tier 1 + Tier 3 of the ls strategy, kept in psm1 because both reference
    # module-scoped state ($script:BashLsProviders) or PS-provider cmdlets.
    # Emits raw PsBash.LsEntry objects (BashText left empty -- the binary
    # cmdlet formats them). On a target that resolves to nothing anywhere,
    # emits a bash-style error via Write-BashError -ExitCode 2 and yields
    # nothing, exactly as the original Invoke-BashLs did.
    [OutputType('PsBash.LsEntry')]
    param(
        [Parameter(Mandatory)][string]$Target,
        [switch]$ShowHidden,
        [switch]$Recursive,
        [switch]$DirOnly
    )

    # Tier 1: custom provider.
    foreach ($cp in $script:BashLsProviders) {
        if (& $cp.Detect $Target) {
            $flags = @{ Long = $false; Hidden = [bool]$ShowHidden; Recursive = [bool]$Recursive }
            foreach ($e in (& $cp.List $Target $flags)) { $e }
            return
        }
    }

    # Tier 3: PS provider fallback (Registry:, Cert:, custom PSDrives).
    $psItem = $null
    try { $psItem = Get-Item -LiteralPath $Target -Force -ErrorAction Stop } catch { }

    if ($null -ne $psItem) {
        if ($psItem.PSIsContainer -and -not $DirOnly) {
            $children = Get-ChildItem -LiteralPath $Target -Force -ErrorAction SilentlyContinue
            foreach ($child in $children) {
                if (-not $ShowHidden -and $child.Name[0] -eq '.') { continue }
                Get-LsEntryFromPsItem -Item $child
            }
        } else {
            Get-LsEntryFromPsItem -Item $psItem
        }
        return
    }

    Write-BashError "ls: cannot access '$Target': No such file or directory" -ExitCode 2
}

# --- cat Command ---

# Invoke-BashCat migrated to a binary cmdlet in PsBash.Cmdlets.dll
# (REFACTOR-2 Phase 1c — InvokeBashCatCommand.cs). The psm1 no longer defines
# this function; the cmdlet is the sole implementation and the Set-Alias 'cat'
# line below resolves to it. cat's -n/-b/-s/-E/-T flags do not collide with any
# PowerShell common-parameter prefix, so a clean PSCmdlet migration was safe.

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
    # REFACTOR-2 Phase 2: thin wrapper delegating to the shared AOT-safe C#
    # helper [PsBash.Cmdlets.BashRuntime]::GetBashText: null -> '', string ->
    # itself, an object exposing a BashText property -> that value, otherwise
    # the object's ToString().
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        $InputObject
    )

    [PsBash.Cmdlets.BashRuntime]::GetBashText($InputObject)
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
    $quietMode = $false          # -q: suppress output, exit 0 if match, 1 if no match
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
        if ($arg -eq '--quiet' -or $arg -eq '--silent') { $quietMode = $true; $i++; continue }
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
                    'q' { $quietMode = $true }
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
                        if ($quietMode) {
                            $global:LASTEXITCODE = 0
                            return
                        }
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
                    if ($quietMode) {
                        $global:LASTEXITCODE = 0
                        return
                    }
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

        if ($quietMode) {
            # No match found: set LASTEXITCODE and signal failure via $? for && / || chains
            $global:LASTEXITCODE = 1
            Write-Error '' -ErrorAction SilentlyContinue
            return
        }

        # Non-quiet pipeline mode: set exit code based on whether any match was found.
        # grep exits 1 when no lines match, 0 when at least one matches.
        if ($matchCount -eq 0) {
            $global:LASTEXITCODE = 1
        } else {
            $global:LASTEXITCODE = 0
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

        if ($quietMode -and $fileMatchCount -gt 0) {
            $global:LASTEXITCODE = 0
            return
        }

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

    if ($quietMode) {
        # No match found in any file: set LASTEXITCODE and signal failure via $? for && / || chains
        $global:LASTEXITCODE = 1
        Write-Error '' -ErrorAction SilentlyContinue
        return
    }

    # Non-quiet file mode: set exit code based on whether any match was found.
    # grep exits 1 when no lines match, 0 when at least one matches.
    if ($totalMatchCount -eq 0) {
        $global:LASTEXITCODE = 1
    } else {
        $global:LASTEXITCODE = 0
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

# Invoke-BashHead migrated to a binary cmdlet in PsBash.Cmdlets.dll
# (REFACTOR-2 Phase 1c — InvokeBashHeadCommand.cs). The psm1 no longer defines
# this function; the cmdlet is the sole implementation and the Set-Alias 'head'
# line below resolves to it. head's -n/-c flags do not collide with any
# PowerShell common-parameter prefix, so a clean PSCmdlet migration was safe.

# --- tail Command ---

# Invoke-BashTail migrated to a binary cmdlet in PsBash.Cmdlets.dll
# (REFACTOR-2 Phase 1c — InvokeBashTailCommand.cs). The psm1 no longer defines
# this function; the cmdlet is the sole implementation and the Set-Alias 'tail'
# line below resolves to it. tail's -n/-c/-f/-s flags do not collide with any
# PowerShell common-parameter prefix, so a clean PSCmdlet migration was safe.
# The -f follow path is implemented in C# via FileInfo polling (Thread.Sleep),
# honoring PSCmdlet.Stopping for Ctrl-C — parity with the psm1 Start-Sleep loop.

# --- wc Command ---

# Invoke-BashWc migrated to a binary cmdlet in PsBash.Cmdlets.dll
# (REFACTOR-2 Phase 1c — InvokeBashWcCommand.cs). The psm1 no longer defines
# this function; the cmdlet is the sole implementation and the Set-Alias 'wc'
# line below resolves to it. wc's -l/-w/-c flags do not collide with any
# PowerShell common-parameter prefix, so a clean PSCmdlet migration was safe.

# Invoke-BashFind migrated to a binary cmdlet in PsBash.Cmdlets.dll
# (REFACTOR-2 Phase 3 follow-on - InvokeBashFindCommand.cs). The psm1 no
# longer defines this function; the cmdlet is the sole implementation and
# the Set-Alias 'find' line below resolves to it. find's predicate flags
# (-name -type -size -maxdepth -mtime -empty -print0 -exec) are full words
# that do not prefix-collide with any PowerShell common parameter, so a
# clean PSCmdlet migration was safe. Get-BashFileInfo stays in psm1 because
# Invoke-BashStat (still a psm1 function) depends on it; the find cmdlet
# duplicates the relevant slice in C# (BuildFileInfo).

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

# Invoke-BashCp / Mv / Rm / Mkdir / Rmdir migrated to binary cmdlets (REFACTOR-2):
# see InvokeBashCp/Mv/Rm/Mkdir/RmdirCommand.cs and FileSystemHelpers.cs in PsBash.Cmdlets.

# Invoke-Bashtouch+ln migrated to binary cmdlet (REFACTOR-2): see InvokeBash*Command.cs in PsBash.Cmdlets.


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

# --- rev Command --- migrated to InvokeBashRevCommand.cs (REFACTOR-2 follow-on)

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

function Invoke-BashLess {
    [OutputType('PsBash.TextOutput')]
    param()

    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') {
        New-BashObject -BashText "usage: less [FILE...]`nMVP keys are provided by native less when ps-bash owns an interactive terminal. Non-interactive contexts pass text through."
        return
    }

    $hasPipelineInput = $pipelineInput.Count -gt 0
    $isInteractive = $env:PSBASH_INTERACTIVE -eq '1' -and
        -not [Console]::IsInputRedirected -and
        -not [Console]::IsOutputRedirected

    if (-not $isInteractive) {
        if ($hasPipelineInput) {
            foreach ($item in $pipelineInput) {
                New-BashObject -BashText (Get-BashText -InputObject $item) -TypeName 'PsBash.TextOutput'
            }
            $global:LASTEXITCODE = 0
            return
        }

        foreach ($path in $Arguments) {
            if ($path.StartsWith('-')) { continue }
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                Write-BashError "less: ${path}: No such file or directory" 1
                return
            }
            Get-Content -LiteralPath $path | ForEach-Object {
                New-BashObject -BashText "$_" -TypeName 'PsBash.TextOutput'
            }
        }
        $global:LASTEXITCODE = 0
        return
    }

    $less = Get-Command less -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $less) {
        Write-BashError 'less: native less executable not found; install less or run in a non-interactive context to pass text through' 127
        return
    }

    $tempFile = $null
    try {
        $pagerArgs = [System.Collections.Generic.List[string]]::new()
        foreach ($arg in $Arguments) {
            $pagerArgs.Add($arg)
        }

        if ($hasPipelineInput) {
            $tempFile = [System.IO.Path]::Combine(
                [System.IO.Path]::GetTempPath(),
                "ps-bash-less-$([System.Guid]::NewGuid().ToString('N')).txt")

            $sb = [System.Text.StringBuilder]::new()
            foreach ($item in $pipelineInput) {
                $text = Get-BashText -InputObject $item
                [void]$sb.Append($text)
                $isPartial = $null -ne $item.PSObject -and
                    $null -ne $item.PSObject.Properties['NoTrailingNewline'] -and
                    [bool]$item.NoTrailingNewline
                if (-not $isPartial -and -not $text.EndsWith("`n")) {
                    [void]$sb.Append("`n")
                }
            }
            [System.IO.File]::WriteAllText($tempFile, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
            $pagerArgs.Insert(0, $tempFile)
        }

        $psi = [System.Diagnostics.ProcessStartInfo]::new($less.Source)
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = (Get-Location).Path
        foreach ($arg in $pagerArgs) {
            [void]$psi.ArgumentList.Add($arg)
        }

        $proc = [System.Diagnostics.Process]::Start($psi)
        if ($null -eq $proc) {
            Write-BashError 'less: failed to start native less executable' 126
            return
        }
        $proc.WaitForExit()
        $global:LASTEXITCODE = $proc.ExitCode
    }
    finally {
        if ($null -ne $tempFile) {
            Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-BashMore {
    [OutputType('PsBash.TextOutput')]
    param()

    $Arguments = [string[]]$args
    $pipelineInput = @($input)
    if ($Arguments -contains '--help') {
        New-BashObject -BashText "usage: more [FILE...]`nDisplay text one screen at a time. Keys: space next page, enter next line, q quit."
        return
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $hadError = $false

    $appendText = {
        param(
            [AllowEmptyString()]
            [string]$Text,
            [bool]$NoTrailingNewline
        )

        $normalized = $Text -replace "`r`n", "`n"
        if ($normalized.Length -eq 0) {
            if (-not $NoTrailingNewline) { $lines.Add('') }
            return
        }

        $parts = [System.Text.RegularExpressions.Regex]::Split($normalized, "`n")
        $limit = $parts.Count
        if (-not $NoTrailingNewline -and $normalized.EndsWith("`n")) {
            $limit--
        }

        for ($i = 0; $i -lt $limit; $i++) {
            $lines.Add($parts[$i])
        }
    }

    if ($pipelineInput.Count -gt 0) {
        foreach ($item in $pipelineInput) {
            $isPartial = $null -ne $item.PSObject -and
                $null -ne $item.PSObject.Properties['NoTrailingNewline'] -and
                [bool]$item.NoTrailingNewline
            & $appendText (Get-BashText -InputObject $item) $isPartial
        }
    }

    $fileOperands = @($Arguments | Where-Object { $_ -ne '-' -and -not $_.StartsWith('-') })
    foreach ($filePath in (Resolve-BashGlob -Paths $fileOperands)) {
        $content = Read-BashFileBytes -Path $filePath -Command 'more'
        if ($null -eq $content) { $hadError = $true; continue }
        & $appendText $content $false
    }

    if ($hadError) {
        $global:LASTEXITCODE = 1
        if ($lines.Count -eq 0) { return }
    }

    $isInteractive = $env:PSBASH_INTERACTIVE -eq '1' -and
        -not [Console]::IsInputRedirected -and
        -not [Console]::IsOutputRedirected

    if (-not $isInteractive) {
        foreach ($line in $lines) {
            New-BashObject -BashText $line -TypeName 'PsBash.TextOutput'
        }
        if (-not $hadError) { $global:LASTEXITCODE = 0 }
        return
    }

    $height = [Math]::Max(1, [Console]::WindowHeight - 1)
    $index = 0
    $pageSize = $height
    while ($index -lt $lines.Count) {
        $target = [Math]::Min($index + $pageSize, $lines.Count)
        while ($index -lt $target) {
            [Console]::Out.WriteLine($lines[$index])
            $index++
        }

        if ($index -ge $lines.Count) { break }

        [Console]::Out.Write('--More--')
        $key = [Console]::ReadKey($true)
        [Console]::Out.Write("`r        `r")

        if ($key.KeyChar -eq 'q' -or $key.Key -eq [ConsoleKey]::Q) { break }
        if ($key.Key -eq [ConsoleKey]::Enter) {
            $pageSize = 1
        } else {
            $pageSize = $height
        }
    }

    if (-not $hadError) { $global:LASTEXITCODE = 0 }
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
                $bound = [System.Collections.Generic.List[object]]::new()
                foreach ($item in $current) {
                    $bound.AddRange(@(Invoke-JqFilter -Data $item -Filter $bindingExpr -Variables $scope))
                }
                $newScope = @{} + $scope
                $newScope[$varName] = $bound.ToArray()
                $current = $current
                $scope = $newScope
                continue
            }
            $next = [System.Collections.Generic.List[object]]::new()
            foreach ($item in $current) {
                $next.AddRange(@(Invoke-JqFilter -Data $item -Filter $seg -Variables $scope))
            }
            $current = $next.ToArray()
        }
        return $current
    }

    # Handle comma (multiple outputs) at top level
    [string[]]$commaSegments = @(Split-JqComma -Filter $filter)
    if ($commaSegments.Count -gt 1) {
        $results = [System.Collections.Generic.List[object]]::new()
        foreach ($seg in $commaSegments) {
            $results.AddRange(@(Invoke-JqFilter -Data $Data -Filter $seg.Trim() -Variables $Variables))
        }
        return $results.ToArray()
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

            $next = [System.Collections.Generic.List[object]]::new()
            if ($inner -eq '') {
                # .[] iterate
                foreach ($item in $current) {
                    if ($item -is [array] -or $item -is [System.Collections.IList]) {
                        foreach ($elem in $item) { $next.Add($elem) }
                    } elseif ($item -is [System.Collections.IDictionary]) {
                        foreach ($val in $item.Values) { $next.Add($val) }
                    } elseif ($item -is [PSCustomObject]) {
                        foreach ($prop in $item.PSObject.Properties) {
                            if ($prop.Name -ne 'PSTypeName') { $next.Add($prop.Value) }
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
                            $next.Add($item[$idx])
                        } else {
                            $next.Add($null)
                        }
                    }
                }
            }
            $current = $next.ToArray()
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

        $next = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $current) {
            $val = $null
            if ($item -is [System.Collections.IDictionary]) {
                if ($item.Contains($fieldName)) { $val = $item[$fieldName] }
            } elseif ($item -is [PSCustomObject]) {
                $prop = $item.PSObject.Properties[$fieldName]
                if ($null -ne $prop) { $val = $prop.Value }
            }
            $next.Add($val)
        }
        $current = $next.ToArray()
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

    $results = [System.Collections.Generic.List[object]]::new()
    $results.Add($Data)
    if ($Data -is [array] -or $Data -is [System.Collections.IList]) {
        foreach ($elem in $Data) {
            $results.AddRange(@(Invoke-JqRecurse -Data $elem))
        }
    } elseif ($Data -is [System.Collections.IDictionary]) {
        foreach ($val in $Data.Values) {
            $results.AddRange(@(Invoke-JqRecurse -Data $val))
        }
    } elseif ($Data -is [PSCustomObject]) {
        foreach ($prop in $Data.PSObject.Properties) {
            if ($prop.Name -ne 'PSTypeName') {
                $results.AddRange(@(Invoke-JqRecurse -Data $prop.Value))
            }
        }
    }
    return $results.ToArray()
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

# Invoke-BashJq migrated to binary cmdlet (PsBash.Cmdlets — REFACTOR-2 Phase F6).

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
                'y' { $sb.Append($DTO.ToString('yy', $ci))   | Out-Null }
                'm' { $sb.Append($DTO.ToString('MM', $ci))   | Out-Null }
                'd' { $sb.Append($DTO.ToString('dd', $ci))   | Out-Null }
                'H' { $sb.Append($DTO.ToString('HH', $ci))   | Out-Null }
                'M' { $sb.Append($DTO.ToString('mm', $ci))   | Out-Null }
                'S' { $sb.Append($DTO.ToString('ss', $ci))   | Out-Null }
                's' { $sb.Append([string]$DTO.ToUnixTimeSeconds()) | Out-Null }
                'F' { $sb.Append($DTO.ToString('yyyy-MM-dd', $ci)) | Out-Null }
                'T' { $sb.Append($DTO.ToString('HH:mm:ss', $ci)) | Out-Null }
                'w' { $sb.Append([int]$DTO.DayOfWeek) | Out-Null }
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
#
# Invoke-BashBasename MIGRATED to a binary cmdlet (REFACTOR-2 Phase 1).
# See src/PsBash.Cmdlets/InvokeBashBasenameCommand.cs. The psm1 function
# definition is intentionally removed: a script function would shadow the
# cmdlet of the same name (PowerShell function precedence > cmdlet). The
# `Set-Alias basename -> Invoke-BashBasename` line below still resolves
# because Import-Module of PsBash.Cmdlets.dll registers the cmdlet.

# --- dirname ---
#
# Invoke-BashDirname MIGRATED to a binary cmdlet (REFACTOR-2 Phase 1).
# See src/PsBash.Cmdlets/InvokeBashDirnameCommand.cs. Same shadowing rationale
# as basename above; the `Set-Alias dirname -> Invoke-BashDirname` line still
# resolves via the cmdlet registration.

# --- pwd ---
# REFACTOR-2 Phase 1b: Invoke-BashPwd migrated to a binary cmdlet
# (PsBash.Cmdlets/InvokeBashPwdCommand.cs). The psm1 no longer defines it;
# the `Set-Alias pwd -> Invoke-BashPwd` line below resolves to the cmdlet.



# --- hostname ---

# Invoke-BashHostname / Invoke-BashWhoami migrated to binary cmdlets
# (REFACTOR-2): see InvokeBashHostnameCommand.cs / InvokeBashWhoamiCommand.cs in
# PsBash.Cmdlets. The Set-Alias lines below resolve to those cmdlets.

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

# Invoke-BashStrings migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashStringsCommand.cs

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

# Invoke-BashChecksum / Md5sum / Sha1sum / Sha256sum migrated to binary
# cmdlets (REFACTOR-2): see InvokeBashChecksumCommands.cs in PsBash.Cmdlets.

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

# Invoke-Bashsleep migrated to binary cmdlet (REFACTOR-2): see InvokeBash*Command.cs in PsBash.Cmdlets.


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

# Invoke-Bashwhich migrated to binary cmdlet (REFACTOR-2): see InvokeBash*Command.cs in PsBash.Cmdlets.


# --- alias / unalias (module mode: dynamic function creation) ---

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
        } else {
            $operands.Add($arg)
        }
        $i++
    }

    if ($unaliasMode) {
        if ($removeAll) {
            foreach ($name in @($script:BashUserAliases.Keys)) {
                Invoke-Expression "Remove-Item 'Function:\$name' -Force -ErrorAction SilentlyContinue"
            }
            $script:BashUserAliases.Clear()
            return
        }
        foreach ($name in $operands) {
            if (-not $script:BashUserAliases.ContainsKey($name)) {
                Write-BashError -Message "unalias: ${name}: not found"
                continue
            }
            Invoke-Expression "Remove-Item 'Function:\$name' -Force -ErrorAction SilentlyContinue"
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
            $aliasName = $Matches[1]
            $aliasValue = $Matches[2]
            $script:BashUserAliases[$aliasName] = $aliasValue
            $body = [scriptblock]::Create("& $aliasValue @args")
            Invoke-Expression "function global:$aliasName { & $aliasValue @args }"
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
                    $global:__BashTrapEXIT = $null
                }
                $script:BashTrapHandlers.Remove($signal) | Out-Null
            }
            continue
        }

        switch ($signal) {
            'EXIT' {
                $sb = [scriptblock]::Create($action)
                $global:__BashTrapEXIT = $sb
                $script:BashTrapHandlers['EXIT'] = $action
            }
            'ERR' {
                $sb = [scriptblock]::Create($action)
                $script:BashTrapHandlers['ERR'] = $action
                $global:__BashTrapERR = $sb
            }
            default {
                $script:BashTrapHandlers[$signal] = $action
            }
        }
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
        # Interactive: read from the process's stdin via [Console]::ReadKey.
        # Under an interactive PTY, ps-bash-host's PSHost.UI.ReadLine() throws
        # NotSupportedException (see ExitTrackingHost), so Read-Host cannot
        # block for input. [Console]::In.ReadLine() also does not work because
        # the PTY slave is in raw mode (the launcher's raw stdin passes bytes
        # through verbatim), so there is no line-buffered TextReader stream
        # for ReadLine to wait on. PTY-11 validated that [Console]::ReadKey
        # ($true) reads bytes directly from the PTY slave fd — assemble a line
        # ourselves: collect printable keys until Enter, echo each character
        # so the user sees what they type, handle Backspace and Ctrl-C.
        if ($promptSet) {
            [Console]::Out.Write("${prompt}: ")
            [Console]::Out.Flush()
        }
        $sb = [System.Text.StringBuilder]::new()
        while ($true) {
            try {
                $key = [Console]::ReadKey($true)
            } catch [InvalidOperationException] {
                # Console handle closed mid-read (terminal disconnect) — treat as EOF.
                return
            }
            if ($key.Key -eq [ConsoleKey]::Enter) {
                # Echo CRLF so the cursor lands on a fresh line, matching the
                # terminal-cooked-mode newline behavior users expect.
                [Console]::Out.Write("`r`n")
                [Console]::Out.Flush()
                break
            }
            if ($key.Key -eq [ConsoleKey]::Backspace) {
                if ($sb.Length -gt 0) {
                    [void]$sb.Remove($sb.Length - 1, 1)
                    # Erase the previous glyph: backspace, space, backspace.
                    [Console]::Out.Write("`b `b")
                    [Console]::Out.Flush()
                }
                continue
            }
            # Ctrl-C: abort read with empty result (matches bash `read` EOF).
            if ($key.Key -eq [ConsoleKey]::C -and
                ($key.Modifiers -band [ConsoleModifiers]::Control)) {
                [Console]::Out.Write("`r`n")
                [Console]::Out.Flush()
                return
            }
            $ch = $key.KeyChar
            if ($ch -and [int]$ch -ge 32) {
                [void]$sb.Append($ch)
                [Console]::Out.Write($ch)
                [Console]::Out.Flush()
            }
        }
        $inputLine = $sb.ToString()
    }

    if ($null -eq $inputLine) { return }

    if ($varNames.Count -eq 1) {
        # Single variable: assign entire line in the caller's scope
        Set-Variable -Name $varNames[0] -Value $inputLine -Scope 1
        Set-Variable -Name $varNames[0] -Value $inputLine -Scope Global
        # bash `read` sets a shell variable visible to subsequent statements.
        # The emitter renders $VAR as $env:VAR, so the value must land in the
        # process environment block to be visible after Invoke-BashRead returns.
        Set-Item -Path "Env:$($varNames[0])" -Value $inputLine
    } else {
        # Multiple variables: split by whitespace
        $parts = $inputLine -split '\s+'
        for ($j = 0; $j -lt $varNames.Count; $j++) {
            if ($j -lt $parts.Count - 1) {
                Set-Variable -Name $varNames[$j] -Value $parts[$j] -Scope 1
                Set-Variable -Name $varNames[$j] -Value $parts[$j] -Scope Global
                Set-Item -Path "Env:$($varNames[$j])" -Value $parts[$j]
            } elseif ($j -eq $varNames.Count - 1) {
                # Last variable gets remaining text
                $remaining = ($parts[$j..($parts.Count - 1)] -join ' ')
                Set-Variable -Name $varNames[$j] -Value $remaining -Scope 1
                Set-Variable -Name $varNames[$j] -Value $remaining -Scope Global
                Set-Item -Path "Env:$($varNames[$j])" -Value $remaining
            } else {
                Set-Variable -Name $varNames[$j] -Value '' -Scope 1
                Set-Variable -Name $varNames[$j] -Value '' -Scope Global
                Set-Item -Path "Env:$($varNames[$j])" -Value ''
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

$script:BashShoptOptions = @{
    'extglob'            = $false
    'globstar'           = $true
    'dotglob'            = $false
    'nullglob'           = $false
    'nocaseglob'         = $false
    'expand_aliases'     = $true
    'cmdhist'            = $true
    'histappend'         = $true
    'checkwinsize'      = $true
    'progcomp'           = $true
    'login_shell'        = $false
    'interactive_comments' = $true
    'sourcepath'         = $true
    'hostcomplete'       = $true
}

function Invoke-BashShopt {
    param()
    $Arguments = [string[]]$args

    $setMode = $false
    $unsetMode = $false
    $printMode = $false
    $queryMode = $false
    $operands = [System.Collections.Generic.List[string]]::new()

    foreach ($arg in $Arguments) {
        switch ($arg) {
            '-s' { $setMode = $true }
            '-u' { $unsetMode = $true }
            '-p' { $printMode = $true }
            '-q' { $queryMode = $true }
            default { $operands.Add($arg) }
        }
    }

    if ($printMode -and $operands.Count -eq 0) {
        foreach ($kv in $script:BashShoptOptions.GetEnumerator() | Sort-Object Key) {
            $val = if ($kv.Value) { 'on' } else { 'off' }
            Emit-BashLine -Text "shopt -s $($kv.Name)"
        }
        return
    }

    foreach ($opt in $operands) {
        if ($setMode) {
            $script:BashShoptOptions[$opt] = $true
        } elseif ($unsetMode) {
            $script:BashShoptOptions[$opt] = $false
        } else {
            if ($script:BashShoptOptions.ContainsKey($opt)) {
                $val = if ($script:BashShoptOptions[$opt]) { 'on' } else { 'off' }
                Emit-BashLine -Text "$opt $val"
            } else {
                Write-BashError -Message "bash: shopt: ${opt}: invalid shell option name"
            }
        }
    }
}

function Invoke-BashType {
    [OutputType('PsBash.TypeOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'type' }

    $typeOnly = $false
    $showAll = $false
    $printMode = $false
    $operands = [System.Collections.Generic.List[string]]::new()
    foreach ($arg in $Arguments) {
        if ($arg -ceq '-t') { $typeOnly = $true }
        elseif ($arg -ceq '-a' -or $arg -ceq '--all') { $showAll = $true }
        elseif ($arg -ceq '-p') { $printMode = $true }
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
        if ($printMode) {
            $val = Get-Variable -Name $name -Scope Global -ValueOnly -ErrorAction SilentlyContinue
            $source = 'variable'
            if ($null -eq $val) {
                $envVal = [System.Environment]::GetEnvironmentVariable($name)
                if ($null -ne $envVal) { $val = $envVal; $source = 'environment' }
            }
            if ($null -ne $val) {
                if ($val -is [System.Collections.IDictionary]) {
                    Emit-BashLine -Text "declare -A $name=$($val | ConvertTo-Json -Compress)"
                } elseif ($val -is [array] -or $val -is [System.Collections.IList]) {
                    Emit-BashLine -Text "declare -a $name=$($val | ConvertTo-Json -Compress)"
                } else {
                    Emit-BashLine -Text "declare -- $name=`"$val`""
                }
            } else {
                Write-Error "bash: declare: ${name}: not found" -ErrorAction Continue
            }
            continue
        }

        $isBuiltin = $builtins -contains $name
        if ($isBuiltin) {
            $kind = 'builtin'
            $text = if ($typeOnly) { $kind } else { "$name is a shell builtin" }
            if (-not $showAll) {
                $obj = [PSCustomObject]@{
                    PSTypeName = 'PsBash.TypeOutput'
                    Command    = $name
                    Kind       = $kind
                    BashText   = $text
                }
                Set-BashDisplayProperty $obj
                continue
            }
        }

        $results = [System.Collections.Generic.List[PSObject]]::new()
        if ($isBuiltin -and $showAll) {
            $results.Add(([PSCustomObject]@{
                PSTypeName = 'PsBash.TypeOutput'
                Command    = $name
                Kind       = 'builtin'
                BashText   = if ($typeOnly) { 'builtin' } else { "$name is a shell builtin" }
            }))
        }

        $alias = Get-Alias $name -ErrorAction SilentlyContinue
        if ($alias) {
            if ($alias.Definition -match '^Invoke-Bash|^Get-Bash|^Set-Bash|^ConvertFrom-') {
                $results.Add(([PSCustomObject]@{
                    PSTypeName = 'PsBash.TypeOutput'
                    Command    = $name
                    Kind       = 'alias'
                    BashText   = if ($typeOnly) { 'alias' } else { "$name is aliased to ``$($alias.Definition)''" }
                }))
            }
        }

        $cmd = Get-Command $name -CommandType Application,Cmdlet,Function -ErrorAction SilentlyContinue
        if ($cmd -and -not $isBuiltin) {
            switch ($cmd.CommandType) {
                'Alias'    { $k = 'alias'; $t = if ($typeOnly) { $k } else { "$name is aliased to ``$($cmd.Definition)''" } }
                'Function' { $k = 'function'; $t = if ($typeOnly) { $k } else { "$name is a function" } }
                default    { $k = 'file'; $t = if ($typeOnly) { $k } else { "$name is $($cmd.Source)" } }
            }
            $results.Add(([PSCustomObject]@{
                PSTypeName = 'PsBash.TypeOutput'
                Command    = $name
                Kind       = $k
                BashText   = $t
            }))
        }

        if ($results.Count -eq 0) {
            Write-Error "bash: type: ${name}: not found" -ErrorAction Continue
            continue
        }

        if (-not $showAll -and -not $isBuiltin) {
            $r = $results[0]
            Set-BashDisplayProperty $r
            continue
        }

        foreach ($r in $results) {
            Set-BashDisplayProperty $r
        }
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
    $__pid = Get-Variable -Name __parentPid -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    if ($__pid -and $__pid -gt 0) {
        try {
            $parent = [System.Diagnostics.Process]::GetProcessById($__pid)
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
        if (-not $version) { $version = '0.7.6' }
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

# RC-2: background jobs run in-process against a shared RunspacePool of isolated
# runspaces — no pwsh cold start per `&`. Each entry in $script:BashBgJobs is a
# job record: @{ Id; PowerShell; AsyncResult; Done }. $! ($global:BashBgLastPid)
# is a synthetic monotonically-increasing id keyed to the [powershell] instance,
# NOT a real OS pid (documented: bash uses an OS pid; in-process runspaces have
# no separate pid, so a synthetic id is used and is sufficient for `wait $!`).
$script:BashBgJobs = [System.Collections.Generic.List[object]]::new()
$script:BashBgNextId = 1000
$script:BashBgPool = $null
$global:BashBgLastPid = $null
# The psm1 is loaded into the host runspace by running its content as a script
# (SdkRunspace.cs: AddScript(psm1Content)), so $PSCommandPath / $PSScriptRoot are
# empty inside it. To make Invoke-Bash* functions available in pooled background
# runspaces, locate the extracted module on disk: ModuleExtractor writes it to
# {temp}/ps-bash/module-{version}/PsBash.psm1.
$script:BashBgModulePath = $null
try {
    $psbDir = Join-Path ([System.IO.Path]::GetTempPath()) 'ps-bash'
    if (Test-Path $psbDir) {
        $candidate = Get-ChildItem -Path $psbDir -Filter 'PsBash.psm1' -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($candidate) { $script:BashBgModulePath = $candidate.FullName }
    }
} catch { }

function Get-BashBgRunspacePool {
    if ($null -eq $script:BashBgPool) {
        $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
        if ($script:BashBgModulePath -and (Test-Path $script:BashBgModulePath)) {
            $iss.ImportPSModule(@($script:BashBgModulePath))
        }
        $pool = [RunspaceFactory]::CreateRunspacePool(1, 8, $iss, $Host)
        $pool.Open()
        $script:BashBgPool = $pool
    }
    return $script:BashBgPool
}

function Complete-BashBgJob {
    <#
    .SYNOPSIS
        Drain a completed background job's output streams and dispose it.
        stdout -> Emit-BashLine; stderr -> Write-BashHostStderr (REFACTOR-4 IPC channel).
    #>
    param([Parameter(Mandatory)]$Job)
    if ($Job.Done) { return }
    $ps = $Job.PowerShell
    try {
        $out = $ps.EndInvoke($Job.AsyncResult)
        foreach ($item in $out) {
            $text = if ($null -ne $item -and $item.PSObject.Properties['BashText']) { $item.BashText } else { "$item" }
            if ($text) { Emit-BashLine -Text $text }
        }
        foreach ($err in $ps.Streams.Error) {
            Write-BashHostStderr ("$err")
        }
    } catch {
        Write-BashHostStderr ("$_")
    } finally {
        $ps.Dispose()
        $Job.Done = $true
    }
}
$global:BashStartTime = [DateTime]::UtcNow
$__bashVer = try { $MyInvocation.MyCommand.Module?.Version ?? [version]'0.8.0' } catch { [version]'0.8.0' }
$global:BashVersion = "$($__bashVer.Major).$($__bashVer.Minor).0(1)-release"
$global:BashVersionInfo = @($__bashVer.Major, $__bashVer.Minor, 0, 1, 'release', "$($__bashVer.Major).$($__bashVer.Minor).0")
# bash default shell flags: h (hash commands), B (brace expansion enabled)
$global:BashFlags = 'hB'
$global:BashLastArg = ''

function Invoke-BashBackground {
    <#
    .SYNOPSIS
        Run a command as a background process (bash & operator).
    #>
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    # RC-2: run the background command as a PowerShell pipeline against a pooled,
    # isolated runspace instead of spawning a fresh pwsh process. This eliminates
    # the ~1-3s pwsh cold start per `&` and the WaitForExit hang in `wait`.
    $pool = Get-BashBgRunspacePool
    $ps = [powershell]::Create()
    $ps.RunspacePool = $pool
    [void]$ps.AddScript($Command.ToString())

    $async = $ps.BeginInvoke()

    $synthId = $script:BashBgNextId
    $script:BashBgNextId++

    $job = [pscustomobject]@{
        Id          = $synthId
        PowerShell  = $ps
        AsyncResult = $async
        Done        = $false
    }
    $script:BashBgJobs.Add($job)
    $global:BashBgLastPid = $synthId
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
        # wait $! / wait <id>: wait for specific synthetic background-job ids.
        foreach ($pidArg in $Arguments) {
            if (-not [int]::TryParse($pidArg, [ref]$null)) { continue }
            $wantId = [int]$pidArg
            $job = $script:BashBgJobs | Where-Object { $_.Id -eq $wantId } | Select-Object -First 1
            if ($job) {
                [void]$job.AsyncResult.AsyncWaitHandle.WaitOne()
                Complete-BashBgJob -Job $job
                [void]$script:BashBgJobs.Remove($job)
            }
        }
    } else {
        # wait (no arg): wait for all pending background jobs.
        foreach ($job in @($script:BashBgJobs)) {
            [void]$job.AsyncResult.AsyncWaitHandle.WaitOne()
            Complete-BashBgJob -Job $job
        }
        $script:BashBgJobs.Clear()
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

    if ($script:BashBgJobs.Count -eq 0) {
        return
    }

    $i = 1
    foreach ($job in $script:BashBgJobs) {
        $status = if ($job.AsyncResult.IsCompleted) { 'Done' } else { 'Running' }
        New-BashObject -BashText "[$i]`t$status`t$($job.Id)`tbash-bg`n"
        $i++
    }
}

function Invoke-BashFg {
    <#
    .SYNOPSIS
        Bring a background job to the foreground (bash fg command).
    #>
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'fg' }

    $target = $null
    if ($Arguments.Count -gt 0) {
        $jobNum = $Arguments[0]
        if ([int]::TryParse($jobNum, [ref]$null)) {
            $idx = [int]$jobNum - 1
            if ($idx -ge 0 -and $idx -lt $script:BashBgJobs.Count) {
                $target = @($script:BashBgJobs)[$idx]
            }
        } else {
            $wantId = $jobNum -replace '^%', ''
            if ([int]::TryParse($wantId, [ref]$null)) {
                $target = @($script:BashBgJobs) | Where-Object { $_.Id -eq [int]$wantId } | Select-Object -First 1
            }
        }
    } else {
        $running = @($script:BashBgJobs | Where-Object { -not $_.AsyncResult.IsCompleted })
        if ($running.Count -gt 0) {
            $target = $running[-1]
        }
    }

    if ($null -eq $target) {
        Write-BashError -Message 'fg: no current job'
        return
    }

    if ($target.AsyncResult.IsCompleted) {
        Write-Host "[$($target.Id)] Done`tbash-bg"
        Complete-BashBgJob -Job $target
        [void]$script:BashBgJobs.Remove($target)
        return
    }

    Write-Host "bash-bg (id $($target.Id))"
    [void]$target.AsyncResult.AsyncWaitHandle.WaitOne()
    Complete-BashBgJob -Job $target
    [void]$script:BashBgJobs.Remove($target)
}

function Invoke-BashBg {
    <#
    .SYNOPSIS
        Resume a stopped job in the background (bash bg command).
    #>
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'bg' }

    Write-BashError -Message 'bg: job control not supported (processes run asynchronously)'
}

# --- shift ---

function Invoke-BashShift {
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'shift' }
    $n = 1
    if ($Arguments.Count -gt 0) {
        if (-not [int]::TryParse($Arguments[0], [ref]$n) -or $n -lt 0) {
            Write-BashError -Message "shift: $($Arguments[0]): numeric argument required"
            return
        }
    }
    $pos = if ($global:BashPositional) { $global:BashPositional } else { @() }
    if ($n -gt $pos.Count) {
        Write-BashError -Message 'shift: cannot shift past end of positional parameters'
        return
    }
    $global:BashPositional = $pos[$n..($pos.Count - 1)]
}

# --- realpath ---

# Invoke-BashRealpath migrated to binary cmdlet (REFACTOR-2):
# see InvokeBashRealpathCommand.cs in PsBash.Cmdlets.

# --- command ---

function Invoke-BashCommand {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'command' }

    $flags = @()
    $operands = [System.Collections.Generic.List[string]]::new()
    foreach ($arg in $Arguments) {
        if ($arg.StartsWith('-')) { $flags += $arg }
        else { $operands.Add($arg) }
    }

    $verbose = $flags -contains '-v' -or $flags -contains '-V'

    foreach ($name in $operands) {
        $found = $false
        $output = $null

        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) {
            $found = $true
            if ($cmd.CommandType -eq 'Alias') {
                $output = $cmd.Definition
            } elseif ($cmd.CommandType -eq 'Function') {
                $output = $cmd.Name
            } else {
                $output = $cmd.Source
            }
        }

        if ($found) {
            if ($verbose) {
                Emit-BashLine -Text $output
            }
        } else {
            $global:LASTEXITCODE = 1
            return
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
    'fg'       = 'Bring a background job to the foreground.'
    'bg'       = 'Resume a stopped job in the background.'
    'shift'    = 'Shift positional parameters.'
    'realpath' = 'Print the resolved path.'
    'command'  = 'Execute a simple command or display information about commands.'
    'source'   = 'Execute commands from a file in the current shell.'
    'unset'    = 'Remove variable or function names.'
    'pushd'    = 'Save and change the current directory.'
    'popd'     = 'Remove entries from the directory stack.'
    'dirs'     = 'Display the directory stack.'
    'yes'      = 'Output a string repeatedly until killed.'
    'tput'     = 'Change terminal characteristics or query the terminfo database.'
    'install'  = 'Copy files and set attributes; handles in-use binaries on Windows.'
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
    'shift'    = @( @('N', 'shift by N positions') )
    'realpath' = @()
    'command'  = @( @('-v', 'print command name or path') )
    'source'   = @()
    'unset'    = @( @('-v', 'treat each NAME as a variable'), @('-f', 'treat each NAME as a function') )
    'pushd'    = @( @('+N', 'rotate stack so Nth dir becomes top') )
    'popd'     = @( @('+N', 'remove Nth entry from stack') )
    'dirs'     = @( @('-c', 'clear directory stack'), @('-p', 'print one per line'), @('-v', 'print with line numbers') )
    'yes'      = @( @('STRING', 'repeated output (default: y)') )
    'tput'     = @( @('CAPNAME', 'terminal capability name') )
    'install'  = @( @('-d', 'create directories'), @('-D', 'create leading path components'), @('-m', 'set mode'), @('-v', 'verbose'), @('-s', 'strip'), @('-t', 'target directory'), @('-S', 'swap suffix') )
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

# Remove built-in PowerShell aliases that conflict with our bash commands
$script:conflictingAliases = @('echo','ls','cat','cp','mv','rm','mkdir','pwd','sort','diff','sleep','test','kill','time','pushd','popd','type')
foreach ($a in $script:conflictingAliases) {
    if (Test-Path "Alias:\$a") {
        Remove-Item "Alias:\$a" -Force -ErrorAction SilentlyContinue
    }
}

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
Set-Alias -Name 'cd'       -Value 'Set-Location'        -Force -Scope Global -Option AllScope
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
Set-Alias -Name 'unalias'  -Value 'Invoke-BashAlias'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'balias'   -Value 'Invoke-BashAlias'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'readlink' -Value 'Invoke-BashReadlink' -Force -Scope Global -Option AllScope
Set-Alias -Name 'mktemp'   -Value 'Invoke-BashMktemp'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'type'     -Value 'Invoke-BashType'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'bash'     -Value 'Invoke-BashBash'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'wait'     -Value 'Invoke-BashWait'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'jobs'     -Value 'Invoke-BashJobs'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'fg'       -Value 'Invoke-BashFg'       -Force -Scope Global -Option AllScope
Set-Alias -Name 'bg'       -Value 'Invoke-BashBg'       -Force -Scope Global -Option AllScope
Set-Alias -Name 'shift'    -Value 'Invoke-BashShift'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'realpath' -Value 'Invoke-BashRealpath' -Force -Scope Global -Option AllScope
# --- unset ---

function Invoke-BashUnset {
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'unset' }

    $mode = 'variable'
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($arg in $Arguments) {
        if ($arg -eq '-f') { $mode = 'function'; continue }
        if ($arg -eq '-v') { $mode = 'variable'; continue }
        if ($arg.StartsWith('-')) { continue }
        $names.Add($arg)
    }

    foreach ($name in $names) {
        if ($mode -eq 'variable') {
            Remove-Variable -Name $name -Scope 1 -ErrorAction SilentlyContinue
            if (Test-Path "Env:\$name") {
                Remove-Item -Path "Env:\$name" -Force -ErrorAction SilentlyContinue
            }
        } else {
            Remove-Item -Path "Function:\$name" -Force -ErrorAction SilentlyContinue
        }
    }
}

# --- pushd / popd / dirs ---

function Invoke-BashPushd {
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'pushd' }

    # +N rotation
    if ($Arguments.Count -gt 0 -and $Arguments[0] -cmatch '^\+(\d+)$') {
        $n = [int]$Matches[1]
        $stack = @(Get-Location -Stack)
        if ($n -ge 0 -and $n -lt $stack.Count) {
            $target = $stack[$n]
            for ($i = 0; $i -le $n; $i++) { Pop-Location -Stack -ErrorAction SilentlyContinue }
            Push-Location -Path $target.Path
        }
        return
    }

    $path = if ($Arguments.Count -gt 0) { $Arguments[0] } else { '.' }
    Push-Location -Path $path
}

function Invoke-BashPopd {
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'popd' }

    if ($Arguments.Count -gt 0 -and $Arguments[0] -cmatch '^\+(\d+)$') {
        $n = [int]$Matches[1]
        for ($i = 0; $i -le $n; $i++) { Pop-Location -Stack -ErrorAction SilentlyContinue }
    } else {
        Pop-Location
    }
}

function Invoke-BashDirs {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'dirs' }

    $clear = $Arguments -contains '-c'
    if ($clear) {
        while (Get-Location -Stack -ErrorAction SilentlyContinue) {
            Pop-Location -Stack -ErrorAction SilentlyContinue
        }
        return
    }

    $onePerLine = $Arguments -contains '-p'
    $withNumbers = $Arguments -contains '-v'
    $stack = @(Get-Location -Stack)
    [array]::Reverse($stack)

    if ($withNumbers) {
        for ($i = 0; $i -lt $stack.Count; $i++) {
            Emit-BashLine -Text "$i  $($stack[$i].Path)"
        }
    } elseif ($onePerLine) {
        foreach ($entry in $stack) {
            Emit-BashLine -Text $entry.Path
        }
    } else {
        $paths = @( (Get-Location).Path ) + ($stack | ForEach-Object { $_.Path })
        Emit-BashLine -Text ($paths -join ' ')
    }
}

# --- yes ---

# Invoke-BashYes migrated to binary cmdlet (REFACTOR-2):
# see InvokeBashYesCommand.cs in PsBash.Cmdlets.

# --- tput ---

function Invoke-BashTput {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'tput' }

    # Passthrough if native tput is available
    $native = Get-Command tput -CommandType Application -ErrorAction SilentlyContinue
    if ($native) {
        try {
            $output = & $native.Source @Arguments 2>$null
            if ($LASTEXITCODE -eq 0) {
                Emit-BashLine -Text ($output -join [Environment]::NewLine)
                return
            }
        } catch {}
    }

    # Fallback for common capabilities
    $cap = $Arguments[0]
    $result = switch ($cap) {
        'cols'  { $Host.UI.RawUI.WindowSize.Width }
        'lines' { $Host.UI.RawUI.WindowSize.Height }
        'clear' { Clear-Host; '' }
        'bold'  { "`e[1m" }
        'sgr0'  { "`e[0m" }
        'setaf' {
            $color = [int]$Arguments[1]
            "`e[38;5;${color}m"
        }
        default { '' }
    }

    if ($result -ne '') {
        Emit-BashLine -Text $result
    }
}

# --- kill ---

function Invoke-BashKill {
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'kill' }

    $signals = @{
        '1'  = 'SIGHUP';  'HUP'  = 'SIGHUP'
        '2'  = 'SIGINT';  'INT'  = 'SIGINT'
        '3'  = 'SIGQUIT'; 'QUIT' = 'SIGQUIT'
        '6'  = 'SIGABRT'; 'ABRT' = 'SIGABRT'
        '9'  = 'SIGKILL'; 'KILL' = 'SIGKILL'
        '14' = 'SIGALRM'; 'ALRM' = 'SIGALRM'
        '15' = 'SIGTERM'; 'TERM' = 'SIGTERM'
        '18' = 'SIGCONT'; 'CONT' = 'SIGCONT'
        '19' = 'SIGSTOP'; 'STOP' = 'SIGSTOP'
        '20' = 'SIGTSTP'; 'TSTP' = 'SIGTSTP'
        '28' = 'SIGWINCH';'WINCH'= 'SIGWINCH'
    }

    $signalName = $null
    $pids = [System.Collections.Generic.List[int]]::new()
    $listJobs = $false

    $i = 0
    while ($i -lt $Arguments.Count) {
        $a = $Arguments[$i]
        if ($a -eq '-l' -or $a -eq '--list') {
            $keys = $signals.Keys | Where-Object { $_ -match '^\d+$' } | Sort-Object { [int]$_ }
            $names = $keys | ForEach-Object { $signals[$_] }
            Emit-BashLine -Text ($names -join [Environment]::NewLine)
            return
        }
        if ($a -eq '-s' -or $a -eq '--signal') {
            $i++
            if ($i -lt $Arguments.Count) {
                $sigArg = $Arguments[$i]
                if ($sigArg -match '^SIG') { $signalName = $sigArg }
                elseif ($signals.ContainsKey($sigArg)) { $signalName = $signals[$sigArg] }
                else { $signalName = "SIG$sigArg" }
            }
            $i++
            continue
        }
        if ($a -match '^-(\d+)$') {
            $sigNum = $Matches[1]
            if ($signals.ContainsKey($sigNum)) { $signalName = $signals[$sigNum] }
            else { $signalName = "SIG$sigNum" }
            $i++
            continue
        }
        if ($a -match '^--signal=(.+)$') {
            $sigArg = $Matches[1]
            if ($sigArg -match '^SIG') { $signalName = $sigArg }
            elseif ($signals.ContainsKey($sigArg)) { $signalName = $signals[$sigArg] }
            else { $signalName = "SIG$sigArg" }
            $i++
            continue
        }
        if ($a -match '^-%?(\d+)$') {
            $pids.Add([int]$Matches[1])
            $i++
            continue
        }
        $intVal = 0
        if ([int]::TryParse($a, [ref]$intVal)) {
            $pids.Add($intVal)
        }
        $i++
    }

    if ($pids.Count -eq 0) {
        Write-BashError -Message 'kill: usage: kill [-s sigspec | -n signum | -sigspec] pid | jobspec ... or kill -l [sigspec]' -ExitCode 2
        return
    }

    foreach ($pid in $pids) {
        try {
            $proc = Get-Process -Id $pid -ErrorAction Stop
            if ($signalName -eq 'SIGKILL') {
                Stop-Process -Id $pid -Force
            } elseif ($signalName -eq 'SIGTERM' -or -not $signalName) {
                Stop-Process -Id $pid
            } elseif ($signalName -eq 'SIGINT') {
                $proc.Kill()
            } else {
                Stop-Process -Id $pid
            }
        } catch {
            Write-BashError -Message "kill: ($pid) - No such process" -ExitCode 1
        }
    }
}

# --- test (standalone) ---

function Invoke-BashTest {
    param()
    $Arguments = [string[]]$args

    $__testResult = Test-BashCondition @Arguments
    if ($__testResult) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 }
    ,$__testResult
}

function Test-BashCondition {
    param()
    $args_list = [string[]]$args
    if ($args_list.Count -eq 0) { return $false }
    if ($args_list.Count -eq 1) { return [bool]$args_list[0] }
    if ($args_list.Count -eq 2) {
        $flag = $args_list[0]
        $val  = $args_list[1]
        switch ($flag) {
            '-f'     { return Test-Path $val -PathType Leaf }
            '-d'     { return Test-Path $val -PathType Container }
            '-e'     { return Test-Path $val }
            '-r'     { try { [System.IO.File]::OpenRead($val).Close(); return $true } catch { return $false } }
            '-w'     { try { [System.IO.File]::OpenWrite($val).Close(); return $true } catch { return $false } }
            '-x'     { return [bool](Get-Command $val -CommandType Application -ErrorAction SilentlyContinue) }
            '-s'     { return (Test-Path $val -PathType Leaf) -and ((Get-Item $val).Length -gt 0) }
            '-L'     { return (Get-Item $val -ErrorAction SilentlyContinue).Attributes -band [System.IO.FileAttributes]::ReparsePoint }
            '-z'     { return [string]::IsNullOrEmpty($val) }
            '-n'     { return -not [string]::IsNullOrEmpty($val) }
            '-eq'    { return ($args_list[0] -eq $args_list[1]) }
            '-ne'    { return ($args_list[0] -ne $args_list[1]) }
            '-lt'    { return ($args_list[0] -lt $args_list[1]) }
            '-le'    { return ($args_list[0] -le $args_list[1]) }
            '-gt'    { return ($args_list[0] -gt $args_list[1]) }
            '-ge'    { return ($args_list[0] -ge $args_list[1]) }
            '!'      { return -not [bool]$val }
            default  { return [bool]$flag }
        }
    }
    if ($args_list.Count -eq 3) {
        $lhs = $args_list[0]
        $op  = $args_list[1]
        $rhs = $args_list[2]
        switch ($op) {
            '='       { return $lhs -eq $rhs }
            '=='      { return $lhs -eq $rhs }
            '!='      { return $lhs -ne $rhs }
            '-eq'     { return [decimal]$lhs -eq [decimal]$rhs }
            '-ne'     { return [decimal]$lhs -ne [decimal]$rhs }
            '-lt'     { return [decimal]$lhs -lt [decimal]$rhs }
            '-le'     { return [decimal]$lhs -le [decimal]$rhs }
            '-gt'     { return [decimal]$lhs -gt [decimal]$rhs }
            '-ge'     { return [decimal]$lhs -ge [decimal]$rhs }
            default   { return $true }
        }
    }

    $i = 0
    $result = $true
    $currentOp = $null
    while ($i -lt $args_list.Count) {
        $tok = $args_list[$i]
        if ($tok -eq '!') {
            $i++
            if ($i -lt $args_list.Count) {
                $nextResult = Test-BashCondition $args_list[$i]
                $result = -not $nextResult
            }
            $i++
            continue
        }
        if ($tok -eq '-a') {
            $currentOp = 'and'
            $i++
            continue
        }
        if ($tok -eq '-o') {
            $currentOp = 'or'
            $i++
            continue
        }
        if ($i + 2 -le $args_list.Count) {
            $check = Test-BashCondition $tok $args_list[$i+1]
        } else {
            $check = [bool]$tok
        }
        if ($currentOp -eq 'and') { $result = $result -and $check }
        elseif ($currentOp -eq 'or') { $result = $result -or $check }
        else { $result = $check }
        $currentOp = $null
        $i += 2
    }
    return $result
}

# --- let ---

function Invoke-BashLet {
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'let' }

    $anyZero = $false
    foreach ($expr in $Arguments) {
        try {
            $psExpr = $expr
            $psExpr = $psExpr -replace '\*\*', '^'
            $psExpr = $psExpr -replace '\b(\w+)\s*\+\+', '+=1'
            $psExpr = $psExpr -replace '\b(\w+)\s*--', '-=1'
            $result = Invoke-Expression $psExpr
            if ($null -ne $result -and [int]$result -eq 0) { $anyZero = $true }
        } catch {
            Write-BashError -Message "let: $expr : expression error" -ExitCode 1
            return
        }
    }

    if ($anyZero) { $global:LASTEXITCODE = 1 } else { $global:LASTEXITCODE = 0 }
}

# --- id ---

function Invoke-BashId {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'id' }

    $showUid   = $false
    $showGid   = $false
    $showGroups = $false
    $showName  = $false
    $showReal  = $false
    $userName  = $null

    $i = 0
    while ($i -lt $Arguments.Count) {
        $a = $Arguments[$i]
        switch ($a) {
            '-u' { $showUid = $true }
            '-g' { $showGid = $true }
            '-G' { $showGroups = $true }
            '-n' { $showName = $true }
            '-r' { $showReal = $true }
            default { $userName = $a }
        }
        $i++
    }

    $identity = if ($userName) {
        [System.Security.Principal.WindowsIdentity]::new($userName)
    } else {
        [System.Security.Principal.WindowsIdentity]::GetCurrent()
    }

    if ($showUid) {
        if ($showName) {
            Emit-BashLine -Text $identity.Name.Split('\')[-1]
        } else {
            $sid = $identity.User.Value
            Emit-BashLine -Text $sid
        }
        return
    }

    if ($showGid) {
        $primaryGroup = $identity.Groups | Select-Object -First 1
        if ($showName) {
            try {
                $grp = $primaryGroup.Translate([System.Security.Principal.NTAccount])
                Emit-BashLine -Text $grp.Value.Split('\')[-1]
            } catch {
                Emit-BashLine -Text $primaryGroup.Value
            }
        } else {
            Emit-BashLine -Text $primaryGroup.Value
        }
        return
    }

    if ($showGroups) {
        $groupNames = foreach ($g in $identity.Groups) {
            if ($showName) {
                try { $g.Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1] }
                catch { $g.Value }
            } else {
                $g.Value
            }
        }
        Emit-BashLine -Text ($groupNames -join ' ')
        return
    }

    $uid = $identity.User.Value
    $uname = $identity.Name
    $gid = ($identity.Groups | Select-Object -First 1).Value
    $groups = ($identity.Groups | ForEach-Object {
        try { $_.Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1] }
        catch { $_.Value }
    }) -join ','
    Emit-BashLine -Text "uid=$uid($uname) gid=$gid groups=$groups"
}

# --- shuf ---

function Invoke-BashShuf {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'shuf' }

    $count = $null
    $inputFile = $null
    $echoMode = $false
    $rangeStart = $null
    $rangeEnd = $null
    $items = [System.Collections.Generic.List[string]]::new()

    $i = 0
    while ($i -lt $Arguments.Count) {
        $a = $Arguments[$i]
        switch ($a) {
            '-n' {
                $i++
                $count = [int]$Arguments[$i]
            }
            '-e' { $echoMode = $true }
            '-i' {
                $i++
                $rangePart = $Arguments[$i]
                $dashIdx = $rangePart.IndexOf('-')
                $rangeStart = [int]$rangePart.Substring(0, $dashIdx)
                $rangeEnd = [int]$rangePart.Substring($dashIdx + 1)
            }
            { $_ -match '^--head-count=(\d+)$' } {
                $count = [int]$Matches[1]
            }
            default {
                if ($a.StartsWith('-') -and $a -ne '-') {
                    Write-BashError -Message "shuf: invalid option '$a'" -ExitCode 1
                    return
                }
                if (-not $inputFile -and $a -ne '-') {
                    $inputFile = $a
                }
            }
        }
        $i++
    }

    if ($echoMode) {
        $items = [System.Collections.Generic.List[string]]::new()
        $i = 0
        while ($i -lt $Arguments.Count) {
            if ($Arguments[$i] -eq '-e') {
                $i++
                while ($i -lt $Arguments.Count -and -not $Arguments[$i].StartsWith('-')) {
                    $items.Add($Arguments[$i])
                    $i++
                }
                continue
            }
            $i++
        }
    } elseif ($null -ne $rangeStart) {
        $items = [System.Collections.Generic.List[string]]::new()
        for ($n = $rangeStart; $n -le $rangeEnd; $n++) {
            $items.Add([string]$n)
        }
    } elseif ($inputFile) {
        $content = Get-Content -Path $inputFile -ErrorAction SilentlyContinue
        foreach ($line in $content) { $items.Add($line) }
    } else {
        $pipelineInput = @($input)
        if ($pipelineInput.Count -gt 0) {
            foreach ($obj in $pipelineInput) {
                $text = if ($obj.PSObject.Properties['BashText']) { $obj.BashText } else { [string]$obj }
                $items.Add($text)
            }
        }
    }

    $rng = [System.Random]::new()
    $shuffled = $items | Sort-Object { $rng.Next() }

    if ($null -ne $count) {
        $shuffled = $shuffled | Select-Object -First $count
    }

    foreach ($item in $shuffled) {
        Emit-BashLine -Text $item
    }
}

function Invoke-BashInstall {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'install' }

    $defs = New-FlagDefs -Entries @(
        '-d', 'create directories'
        '-D', 'create leading path components'
        '-m', 'mode'
        '-o', 'owner'
        '-g', 'group'
        '-v', 'verbose'
        '-s', 'strip'
        '-t', 'target directory'
        '-S', 'swap suffix'
    )

    $parsed = ConvertFrom-BashArgs -Arguments $Arguments -FlagDefs $defs

    $createDirs = $parsed.Flags['-d']
    $createLeading = $parsed.Flags['-D']
    $mode = $parsed.Flags['-m']
    $verbose = $parsed.Flags['-v']
    $targetDir = $parsed.Flags['-t']
    $swapSuffix = if ($parsed.Flags['-S']) { $parsed.Flags['-S'] } else { '.old' }

    if ($createDirs) {
        foreach ($dir in $parsed.Operands) {
            if (-not (Test-Path -LiteralPath $dir)) {
                New-Item -Path $dir -ItemType Directory -Force | Out-Null
                if ($verbose) {
                    New-BashObject -BashText "install: creating directory '$($dir -replace '\\', '/')'`n"
                }
            }
        }
        return
    }

    if ($parsed.Operands.Count -lt 2 -and -not $targetDir) {
        Write-BashError -Message "install: missing file operand"
        return
    }

    if ($targetDir) {
        $dest = $targetDir
        $sources = Resolve-BashGlob -Paths $parsed.Operands
    } else {
        $dest = $parsed.Operands[$parsed.Operands.Count - 1]
        $sources = Resolve-BashGlob -Paths $parsed.Operands[0..($parsed.Operands.Count - 2)]
    }

    if ($createLeading) {
        $destParent = Split-Path $dest -Parent
        if ($destParent -and -not (Test-Path -LiteralPath $destParent)) {
            New-Item -Path $destParent -ItemType Directory -Force | Out-Null
            if ($verbose) {
                New-BashObject -BashText "install: creating directory '$($destParent -replace '\\', '/')'`n"
            }
        }
    }

    $hadError = $false

    foreach ($src in $sources) {
        $srcItem = Get-BashItem -Path $src -Command 'install'
        if ($null -eq $srcItem) {
            $hadError = $true
            continue
        }

        $targetPath = $dest
        if ((Test-Path -LiteralPath $dest) -and (Get-Item -LiteralPath $dest -Force) -is [System.IO.DirectoryInfo]) {
            $targetPath = Join-Path $dest $srcItem.Name
        }

        $targetDir2 = Split-Path $targetPath -Parent
        if ($targetDir2 -and -not (Test-Path -LiteralPath $targetDir2)) {
            New-Item -Path $targetDir2 -ItemType Directory -Force | Out-Null
        }

        $swapped = $false
        if (Test-Path -LiteralPath $targetPath) {
            $oldPath = "$targetPath$swapSuffix"
            try {
                if (Test-Path -LiteralPath $oldPath) {
                    Remove-Item -LiteralPath $oldPath -Force -ErrorAction Stop
                }
                Move-Item -LiteralPath $targetPath -Destination $oldPath -Force -ErrorAction Stop
                $swapped = $true
                if ($verbose) {
                    $bashTarget = $targetPath -replace '\\', '/'
                    $bashOld = $oldPath -replace '\\', '/'
                    New-BashObject -BashText "install: swapped '$bashTarget' -> '$bashOld'`n"
                }
            } catch {
                try {
                    Copy-Item -LiteralPath $src -Destination $targetPath -Force -ErrorAction Stop
                } catch {
                    Write-BashError -Message "install: cannot install '$src': $($_.Exception.Message)"
                    $hadError = $true
                    continue
                }
            }
        }

        try {
            Copy-Item -LiteralPath $src -Destination $targetPath -Force -ErrorAction Stop
        } catch {
            Write-BashError -Message "install: cannot install '$src': $($_.Exception.Message)"
            $hadError = $true
            continue
        }

        if ($swapped) {
            $oldPath = "$targetPath$swapSuffix"
            $scheduled = $false
            if ($IsWindows -or $env:OS -eq 'Windows_NT') {
                try {
                    $code = @"
using System;
using System.Runtime.InteropServices;
public class DeferredDelete {
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
    public static void ScheduleDelete(string path) {
        const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
        if (!MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)) {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }
    }
}
"@
                    Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue
                    [DeferredDelete]::ScheduleDelete((Resolve-Path -LiteralPath $oldPath).Path)
                    $scheduled = $true
                } catch {}
            }
            if (-not $scheduled) {
                try {
                    Remove-Item -LiteralPath $oldPath -Force -ErrorAction SilentlyContinue
                } catch {
                    if ($verbose) {
                        New-BashObject -BashText "install: note: '$($oldPath -replace '\\', '/')' will be deleted when unlocked`n"
                    }
                }
            } elseif ($verbose) {
                New-BashObject -BashText "install: scheduled deletion of '$($oldPath -replace '\\', '/')' on reboot`n"
            }
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

# --- Object Browse Workbench ---

function New-BrowseAction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Script,
        [switch]$Destructive
    )

    [PSCustomObject]@{
        PSTypeName   = 'PsBash.BrowseAction'
        Name         = $Name
        Description  = $Description
        Destructive  = [bool]$Destructive
        Script       = $Script
    }
}

function New-BrowseAdapter {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$TypeNames,
        [string[]]$DisplayProperties = @(),
        [Parameter(Mandatory)][object[]]$Actions
    )

    [PSCustomObject]@{
        PSTypeName         = 'PsBash.BrowseAdapter'
        Name               = $Name
        TypeNames          = $TypeNames
        DisplayProperties  = $DisplayProperties
        Actions            = $Actions
    }
}

function Initialize-BrowseAdapters {
    $existing = Get-Variable -Name BrowseAdapters -Scope Script -ErrorAction SilentlyContinue
    if ($existing -and $existing.Value) { return }

    $inspect = New-BrowseAction -Name 'inspect' -Description 'Show object properties' -Script {
        param($Current, [object[]]$Items)
        foreach ($item in $Items) {
            $item | Select-Object -Property *
        }
    }

    $fileDelete = New-BrowseAction -Name 'delete' -Description 'Remove selected files or directories' -Destructive -Script {
        param($Current, [object[]]$Items)
        foreach ($item in $Items) {
            $path = Get-BrowseTargetText -InputObject $item
            Remove-Item -LiteralPath $path -Force
        }
    }

    $stopProcess = New-BrowseAction -Name 'stop' -Description 'Stop selected processes' -Destructive -Script {
        param($Current, [object[]]$Items)
        foreach ($item in $Items) {
            if ($item.PSObject.Properties['Id']) {
                Stop-Process -Id $item.Id
            } elseif ($item.PSObject.Properties['PID']) {
                Stop-Process -Id $item.PID
            } else {
                Stop-Process -InputObject $item
            }
        }
    }

    $script:BrowseAdapters = @(
        (New-BrowseAdapter -Name 'file' -TypeNames @(
            'System.IO.FileInfo', 'System.IO.DirectoryInfo', 'PsBash.LsEntry', 'PsBash.FindEntry'
        ) -DisplayProperties @('Mode', 'Name', 'Length', 'SizeBytes', 'LastWriteTime', 'FullName', 'Path') -Actions @($inspect, $fileDelete)),
        (New-BrowseAdapter -Name 'process' -TypeNames @(
            'System.Diagnostics.Process', 'PsBash.PsEntry'
        ) -DisplayProperties @('ProcessName', 'Name', 'Id', 'PID', 'CPU', 'WS', 'Path') -Actions @($inspect, $stopProcess)),
        (New-BrowseAdapter -Name 'default' -TypeNames @('*') -DisplayProperties @() -Actions @($inspect))
    )
}

function Resolve-BrowseAdapter {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline, Mandatory)][object]$InputObject)

    process {
        Initialize-BrowseAdapters
        $typeNames = @($InputObject.PSTypeNames)
        $dotNetType = $InputObject.GetType().FullName

        foreach ($adapter in $script:BrowseAdapters) {
            foreach ($pattern in $adapter.TypeNames) {
                if ($pattern -eq '*') { continue }
                if ($typeNames -contains $pattern -or $dotNetType -eq $pattern -or $dotNetType -like $pattern) {
                    return $adapter
                }
            }
        }

        $script:BrowseAdapters | Where-Object Name -eq 'default' | Select-Object -First 1
    }
}

function Get-BrowseDisplayProperties {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$InputObject,
        [object]$Adapter
    )

    if (-not $Adapter) {
        $Adapter = Resolve-BrowseAdapter -InputObject $InputObject
    }

    $props = @($Adapter.DisplayProperties | Where-Object { $InputObject.PSObject.Properties[$_] })
    if ($props.Count -gt 0) { return $props }

    $standardMembers = $InputObject.PSObject.Members['PSStandardMembers']
    if ($standardMembers -and $standardMembers.Members['DefaultDisplayPropertySet']) {
        $defaultDisplay = $standardMembers.Members['DefaultDisplayPropertySet'].ReferencedPropertyNames
        if ($defaultDisplay -and $defaultDisplay.Count -gt 0) {
            return @($defaultDisplay | Where-Object { $InputObject.PSObject.Properties[$_] })
        }
    }

    @($InputObject.PSObject.Properties |
        Where-Object { $_.MemberType -in @('NoteProperty', 'Property', 'AliasProperty') } |
        Select-Object -First 6 -ExpandProperty Name)
}

function Get-BrowseTargetText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$InputObject)

    foreach ($name in @('FullName', 'Path', 'Name', 'ProcessName', 'Id', 'PID', 'BashText')) {
        $prop = $InputObject.PSObject.Properties[$name]
        if ($prop -and $null -ne $prop.Value -and "$($prop.Value)" -ne '') {
            return "$($prop.Value)"
        }
    }

    "$InputObject"
}

function New-BrowseBinding {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Objects,
        [int]$CurrentIndex = 0,
        [int[]]$SelectedIndex = @()
    )

    if ($Objects.Count -eq 0) {
        return [PSCustomObject]@{
            PSTypeName = 'PsBash.BrowseBinding'
            Current    = $null
            Items      = @()
            Indexes    = @()
        }
    }

    if ($CurrentIndex -lt 0 -or $CurrentIndex -ge $Objects.Count) {
        throw "browse: current index $CurrentIndex is outside 0..$($Objects.Count - 1)"
    }

    $indexes = if ($SelectedIndex.Count -gt 0) {
        @($SelectedIndex | Sort-Object -Unique)
    } else {
        @($CurrentIndex)
    }

    foreach ($idx in $indexes) {
        if ($idx -lt 0 -or $idx -ge $Objects.Count) {
            throw "browse: selected index $idx is outside 0..$($Objects.Count - 1)"
        }
    }

    [PSCustomObject]@{
        PSTypeName = 'PsBash.BrowseBinding'
        Current    = $Objects[$CurrentIndex]
        Items      = @($indexes | ForEach-Object { $Objects[$_] })
        Indexes    = $indexes
    }
}

function Test-BrowseCommandRequiresConfirmation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Command,
        [object[]]$Items = @()
    )

    $destructive = '(?i)(^|[\s|;&])(?:rm|del|erase|rmdir|remove-item|stop-process|restart-service|stop-service|set-service|kill|invoke-bashrm|invoke-bashkill)\b'
    if ($Command -match $destructive) { return $true }
    if ($Items.Count -gt 1 -and $Command -match '(?i)\b(?:remove|stop|restart|delete|kill)\b') { return $true }
    return $false
}

function New-BrowseSafetyPreview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][object[]]$Items
    )

    [PSCustomObject]@{
        PSTypeName            = 'PsBash.BrowseSafetyGate'
        RequiresConfirmation  = $true
        Kind                  = $Kind
        Command               = $Command
        Targets               = @($Items | ForEach-Object { Get-BrowseTargetText -InputObject $_ })
        Message               = "browse: $Kind requires -Force"
    }
}

function Invoke-BrowseCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Command,
        [object]$Current,
        [object[]]$Items = @(),
        [switch]$Force
    )

    $boundItems = if ($Items.Count -gt 0) { @($Items) } elseif ($null -ne $Current) { @($Current) } else { @() }
    if ((Test-BrowseCommandRequiresConfirmation -Command $Command -Items $boundItems) -and -not $Force) {
        return New-BrowseSafetyPreview -Kind 'exec' -Command $Command -Items $boundItems
    }

    & {
        param($BrowseCommand, $BrowseCurrent, [object[]]$BrowseItems)
        Set-Variable -Name '1' -Value $BrowseCurrent -Scope Local
        Set-Variable -Name '_' -Value $BrowseCurrent -Scope Local
        Set-Variable -Name 'items' -Value $BrowseItems -Scope Local
        Set-Variable -Name '__browse_current' -Value $BrowseCurrent -Scope Local
        Set-Variable -Name '__browse_items' -Value $BrowseItems -Scope Local
        Invoke-Expression $BrowseCommand
    } $Command $Current $boundItems
}

function ConvertTo-BrowseRow {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$InputObject,
        [Parameter(Mandatory)][int]$Index,
        [int[]]$SelectedIndex = @()
    )

    $adapter = Resolve-BrowseAdapter -InputObject $InputObject
    $props = @(Get-BrowseDisplayProperties -InputObject $InputObject -Adapter $adapter)
    $values = [ordered]@{}
    foreach ($prop in $props) {
        $values[$prop] = $InputObject.PSObject.Properties[$prop].Value
    }

    [PSCustomObject]@{
        PSTypeName       = 'PsBash.BrowseRow'
        Index            = $Index
        Selected         = $SelectedIndex -contains $Index
        Adapter          = $adapter.Name
        TypeName         = $InputObject.PSTypeNames[0]
        Display          = ($values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '  '
        Properties       = $values
        OriginalObject   = $InputObject
    }
}

function Invoke-BrowseAction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][object]$Current,
        [Parameter(Mandatory)][object[]]$Items,
        [switch]$Force
    )

    $adapter = Resolve-BrowseAdapter -InputObject $Current
    $action = @($adapter.Actions | Where-Object Name -eq $Name | Select-Object -First 1)
    if ($action.Count -eq 0) {
        throw "browse: action '$Name' is not available for adapter '$($adapter.Name)'"
    }

    if ($action[0].Destructive -and -not $Force) {
        return New-BrowseSafetyPreview -Kind "action:$Name" -Command $Name -Items $Items
    }

    & $action[0].Script $Current $Items
}

# PTY-11: full-screen browse workbench driven by single-key navigation.
#
# Reads keys with [Console]::ReadKey($true) (no Enter, no echo) and repaints
# only the cells that change (cursor mark + selection mark) using ANSI cursor
# movement instead of a per-keystroke Clear-Host — the per-keystroke full redraw
# was the original "ll | browse never-ending scroll" bug.
function Invoke-BrowseInteractive {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Objects)

    # EOF / non-PTY misroute guard: an interactive workbench needs a real
    # terminal for single-key input. When stdin is a pipe or file (browse
    # misrouted onto a non-tty) ReadKey cannot block — bail with a clear error
    # instead of spinning. Invoke-BashBrowse already gates on this; the guard
    # here is defense-in-depth for any other caller of Invoke-BrowseInteractive.
    if ([Console]::IsInputRedirected) {
        Write-Error "browse: interactive workbench requires a terminal (stdin is redirected). Use 'browse --list' for non-interactive output."
        return
    }

    $current = 0
    $selected = New-Object 'System.Collections.Generic.HashSet[int]'
    $maxRows = [Math]::Min($Objects.Count - 1, 14)

    # ESC[<row>;<col>H is 1-based. The header occupies terminal row 1, so list
    # item with 0-based index $i lives on terminal row ($i + 2).
    $rowOf = { param($i) $i + 2 }

    # Render one list row's text (without the leading cursor/mark cells).
    $rowText = {
        param($i)
        $row = ConvertTo-BrowseRow -InputObject $Objects[$i] -Index $i -SelectedIndex @($selected)
        "{0,3} {1}" -f $row.Index, $row.Display
    }

    # Repaint just the two leading cells (cursor '>' + selection mark '*') for
    # one row, leaving the rest of the line untouched. ESC[<r>;1H homes the
    # cursor to column 1 of that row.
    $paintCell = {
        param($i)
        $cursor = if ($i -eq $current) { '>' } else { ' ' }
        $mark   = if ($selected.Contains($i)) { '*' } else { ' ' }
        [Console]::Write(("`e[{0};1H{1}{2}" -f (& $rowOf $i), $cursor, $mark))
    }

    # Full-screen draw: clear, header, every list row. Used on entry, on resize,
    # and after an action whose output scrolled the screen.
    $drawAll = {
        [Console]::Write("`e[2J`e[H")
        [Console]::Write("browse: $($Objects.Count) object(s). Commands: n/p move, s select, i inspect, a action, e exec, q quit")
        for ($i = 0; $i -le $maxRows; $i++) {
            $cursor = if ($i -eq $current) { '>' } else { ' ' }
            $mark   = if ($selected.Contains($i)) { '*' } else { ' ' }
            [Console]::Write(("`e[{0};1H{1}{2} {3}" -f (& $rowOf $i), $cursor, $mark, (& $rowText $i)))
        }
        # Park the cursor below the list so action output appends cleanly.
        [Console]::Write(("`e[{0};1H" -f (& $rowOf ($maxRows + 1))))
    }

    & $drawAll
    $lastWidth  = [Console]::WindowWidth
    $lastHeight = [Console]::WindowHeight

    while ($true) {
        # SIGWINCH handling: the launcher forwards the resize to the host's PTY
        # (PTY-5 SignalForwarder), which updates Console's window metrics. Browse
        # observes the change by polling them each loop and repaints its layout.
        if ([Console]::WindowWidth -ne $lastWidth -or [Console]::WindowHeight -ne $lastHeight) {
            $lastWidth  = [Console]::WindowWidth
            $lastHeight = [Console]::WindowHeight
            & $drawAll
        }

        $key = [Console]::ReadKey($true)
        $ch = $key.KeyChar

        # Arrow keys map onto n/p navigation.
        if ($key.Key -eq [ConsoleKey]::DownArrow) { $ch = 'n' }
        elseif ($key.Key -eq [ConsoleKey]::UpArrow) { $ch = 'p' }

        switch -CaseSensitive ($ch) {
            'q' { [Console]::Write(("`e[{0};1H" -f (& $rowOf ($maxRows + 1)))); return }
            'n' {
                if ($current -lt $maxRows) {
                    $prev = $current
                    $current++
                    & $paintCell $prev
                    & $paintCell $current
                }
            }
            'p' {
                if ($current -gt 0) {
                    $prev = $current
                    $current--
                    & $paintCell $prev
                    & $paintCell $current
                }
            }
            's' {
                if ($selected.Contains($current)) { [void]$selected.Remove($current) }
                else { [void]$selected.Add($current) }
                & $paintCell $current
            }
            'i' {
                $binding = New-BrowseBinding -Objects $Objects -CurrentIndex $current -SelectedIndex @($selected)
                [Console]::Write(("`e[{0};1H" -f (& $rowOf ($maxRows + 1))))
                Invoke-BrowseAction -Name 'inspect' -Current $binding.Current -Items $binding.Items | Format-List | Out-Host
                Write-Host 'press any key to continue'
                [void][Console]::ReadKey($true)
                & $drawAll
            }
            'a' {
                [Console]::Write(("`e[{0};1H" -f (& $rowOf ($maxRows + 1))))
                $name = Read-Host 'action name'
                if ($name) {
                    $binding = New-BrowseBinding -Objects $Objects -CurrentIndex $current -SelectedIndex @($selected)
                    Invoke-BrowseAction -Name $name -Current $binding.Current -Items $binding.Items | Out-Host
                    Write-Host 'press any key to continue'
                    [void][Console]::ReadKey($true)
                }
                & $drawAll
            }
            'e' {
                [Console]::Write(("`e[{0};1H" -f (& $rowOf ($maxRows + 1))))
                $cmd = Read-Host 'exec command'
                if ($cmd) {
                    $binding = New-BrowseBinding -Objects $Objects -CurrentIndex $current -SelectedIndex @($selected)
                    Invoke-BrowseCommand -Command $cmd -Current $binding.Current -Items $binding.Items | Out-Host
                    Write-Host 'press any key to continue'
                    [void][Console]::ReadKey($true)
                }
                & $drawAll
            }
            default { } # ignore unmapped keys
        }
    }
}

function Invoke-BashBrowse {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline)]
        [object]$InputObject,

        [int]$Inspect = -1,
        [int[]]$Select = @(),
        [string]$Action,
        [string]$Exec,
        [switch]$List,
        [switch]$PassThru,
        [switch]$Force
    )

    begin {
        $objects = [System.Collections.Generic.List[object]]::new()
    }

    process {
        if ($null -ne $InputObject) {
            $objects.Add($InputObject)
        }
    }

    end {
        if ($objects.Count -eq 0) { return }

        $currentIndex = if ($Select.Count -gt 0) { $Select[0] } else { 0 }
        $binding = New-BrowseBinding -Objects @($objects) -CurrentIndex $currentIndex -SelectedIndex $Select

        if ($PassThru) {
            return $binding.Items
        }

        if ($Inspect -ge 0) {
            $inspectBinding = New-BrowseBinding -Objects @($objects) -CurrentIndex $Inspect -SelectedIndex @($Inspect)
            return Invoke-BrowseAction -Name 'inspect' -Current $inspectBinding.Current -Items $inspectBinding.Items
        }

        if ($Action) {
            return Invoke-BrowseAction -Name $Action -Current $binding.Current -Items $binding.Items -Force:$Force
        }

        if ($Exec) {
            return Invoke-BrowseCommand -Command $Exec -Current $binding.Current -Items $binding.Items -Force:$Force
        }

        $isInputRedirected = [Console]::IsInputRedirected
        if (-not $List -and -not $isInputRedirected) {
            return Invoke-BrowseInteractive -Objects @($objects)
        }

        for ($i = 0; $i -lt $objects.Count; $i++) {
            ConvertTo-BrowseRow -InputObject $objects[$i] -Index $i -SelectedIndex $Select
        }
    }
}

# --- Prompt Hook Integration ---

# Initialized at module load; tracks last known working directory for chpwd detection.
$global:__BashLastCwd = (Get-Location).Path

# Error log for hook exceptions; grows unbounded unless cleared by user.
# Unconditionally initialize to ensure it's always a List (idempotent on re-import).
$global:BashHookErrors = [System.Collections.Generic.List[object]]::new()

# Sentinel flag: true while the ps-bash prompt wrapper is installed.
$script:__BashHookPromptEnabled = $false

function Enable-BashHookPrompt {
    <#
    .SYNOPSIS
        Installs the ps-bash prompt wrapper that fires chpwd and prompt hooks.
    .DESCRIPTION
        Captures the current 'prompt' function, then installs a wrapper that:
          1. Detects directory changes and calls HookRegistry.FirePrompt.
          2. Delegates to the original prompt, preserving its output byte-for-byte.
        Idempotent: a second call is a no-op.
    #>
    if ($script:__BashHookPromptEnabled) {
        return
    }

    # Capture original prompt function (may be $null if none is defined).
    $existing = Get-Item -Path Function:\prompt -ErrorAction SilentlyContinue
    $script:__BashOriginalPrompt = if ($null -ne $existing) { $existing.ScriptBlock } else { $null }

    # Install the wrapper as the global 'prompt' function.
    Set-Item -Path Function:\global:prompt -Value {
        $currentPath = (Get-Location).Path
        $oldPath = $script:__BashLastCwd

        # Delegate to HookRegistry.FirePrompt (fires chpwd + prompt hooks).
        [PsBash.Cmdlets.HookRegistry]::Instance.FirePrompt(
            $ExecutionContext.SessionState,
            $oldPath,
            $currentPath)

        $script:__BashLastCwd = $currentPath

        # Delegate to original prompt and return its output.
        if ($null -ne $script:__BashOriginalPrompt) {
            & $script:__BashOriginalPrompt
        } else {
            "PS $($currentPath)> "
        }
    }

    $script:__BashHookPromptEnabled = $true
}

function Disable-BashHookPrompt {
    <#
    .SYNOPSIS
        Removes the ps-bash prompt wrapper and restores the original prompt.
    .DESCRIPTION
        Restores the prompt function that was captured by Enable-BashHookPrompt.
        If no original prompt was captured, removes the prompt function entirely.
        Idempotent: calling when not enabled is a no-op.
    #>
    if (-not $script:__BashHookPromptEnabled) {
        return
    }

    if ($null -ne $script:__BashOriginalPrompt) {
        Set-Item -Path Function:\global:prompt -Value $script:__BashOriginalPrompt
    } else {
        Remove-Item -Path Function:\global:prompt -ErrorAction SilentlyContinue
    }

    $script:__BashHookPromptEnabled = $false
}

Set-Alias -Name 'command'  -Value 'Invoke-BashCommand'  -Force -Scope Global -Option AllScope
Set-Alias -Name 'source'   -Value 'Invoke-BashSource'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'unset'    -Value 'Invoke-BashUnset'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'pushd'    -Value 'Invoke-BashPushd'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'popd'     -Value 'Invoke-BashPopd'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'dirs'     -Value 'Invoke-BashDirs'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'yes'      -Value 'Invoke-BashYes'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'tput'     -Value 'Invoke-BashTput'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'shopt'    -Value 'Invoke-BashShopt'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'kill'     -Value 'Invoke-BashKill'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'test'     -Value 'Invoke-BashTest'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'let'      -Value 'Invoke-BashLet'      -Force -Scope Global -Option AllScope
Set-Alias -Name 'id'       -Value 'Invoke-BashId'       -Force -Scope Global -Option AllScope
Set-Alias -Name 'shuf'     -Value 'Invoke-BashShuf'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'install'  -Value 'Invoke-BashInstall'  -Force -Scope Global -Option AllScope
Set-Alias -Name 'browse'   -Value 'Invoke-BashBrowse'   -Force -Scope Global -Option AllScope
Set-Alias -Name 'less'     -Value 'Invoke-BashLess'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'more'     -Value 'Invoke-BashMore'     -Force -Scope Global -Option AllScope

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
