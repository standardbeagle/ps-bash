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

# Probe for the companion PsBash.Cmdlets binary module. Many aliases now
# resolve to cmdlets in PsBash.Cmdlets.dll (REFACTOR-2 migrations). When this
# psm1 is loaded directly (e.g. CI Pester via Import-Module PsBash.psd1),
# the host-side load path that normally imports the dll is not in play, so
# we probe a few well-known relative locations. Silent no-op when absent —
# the host always supplies the dll via its own canonical extraction path.
# Skip the Import-Module probe when the host has already ISS-registered the
# PsBash.Cmdlets cmdlets (SdkRunspace.Create path). Probing by name is fast
# (single Get-Command lookup) and saves the ~300 ms Import-Module of a
# 1.5 MB binary module on every host cold start (#9). The probe path stays
# for Pester / standalone `Import-Module PsBash.psd1` callers that have not
# pre-registered.
# Probe order matters. The Runtime.dll variants are deployed by install-local.ps1
# into a real PSModulePath dir and are deliberately renamed from PsBash.Cmdlets.dll
# so that the user's pwsh maps a file path that the dev `dotnet build` output
# (src/PsBash.Cmdlets/bin/...) never writes to. Without this, a live pwsh session
# that has Import-Module PsBash loaded holds an exclusive open on bin/Debug/...DLL
# and the next build fails with MSB3027 ("file is locked by"). Prefer Runtime
# variants whenever they exist; fall back to bin/Debug only for fresh clones
# that have not run install-local.ps1 yet.
#
# Do not use Get-Command as the import guard: it autoloads installed modules from
# PSModulePath, which lets an older user-installed PsBash.Cmdlets shadow this
# source tree during local Pester runs.
$script:__psbashCmdletsDll = @(
    # PSModulePath layout: PsBash and PsBash.Cmdlets are sibling modules.
    (Join-Path $PSScriptRoot '..' 'PsBash.Cmdlets' 'PsBash.Cmdlets.Runtime.dll')
    # PSGallery / dev-staged layout: companion DLL bundled inside the PsBash module dir.
    (Join-Path $PSScriptRoot 'PsBash.Cmdlets.Runtime.dll')
    (Join-Path $PSScriptRoot 'PsBash.Cmdlets.dll')
    # Dev fallback. WARNING: loading from here LOCKS the file against
    # subsequent dotnet builds. Run scripts/install-local.ps1 to deploy the
    # renamed Runtime.dll above and shake the lock loose.
    (Join-Path $PSScriptRoot '..' 'PsBash.Cmdlets' 'bin' 'Debug' 'net8.0' 'PsBash.Cmdlets.dll')
    (Join-Path $PSScriptRoot '..' 'PsBash.Cmdlets' 'bin' 'Release' 'net8.0' 'PsBash.Cmdlets.dll')
    (Join-Path $PSScriptRoot '..' 'PsBash.Cmdlets' 'bin' 'Debug' 'net10.0' 'PsBash.Cmdlets.dll')
    (Join-Path $PSScriptRoot '..' 'PsBash.Cmdlets' 'bin' 'Release' 'net10.0' 'PsBash.Cmdlets.dll')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($script:__psbashCmdletsDll) {
    # -Global so the binary cmdlets are reachable via Get-Command after
    # Import-Module PsBash from any scope (Pester's psd1-import path is
    # the canonical failure mode this guards against).
    $script:__psbashLoadedCmdletsAssembly = [AppDomain]::CurrentDomain.GetAssemblies() |
        Where-Object { $_.GetName().Name -eq 'PsBash.Cmdlets' } |
        Select-Object -First 1
    $script:__psbashCmdletsDllResolved = (Resolve-Path -LiteralPath $script:__psbashCmdletsDll).Path
    if ((-not $script:__psbashLoadedCmdletsAssembly) -or
        ($script:__psbashLoadedCmdletsAssembly.Location -eq $script:__psbashCmdletsDllResolved)) {
        Import-Module $script:__psbashCmdletsDll -Global -ErrorAction SilentlyContinue
    }
}
# Loud failure: without PsBash.Cmdlets.dll the migrated commands (ls, cat, grep, …) AND the
# [PsBash.Cmdlets.BashRuntime] helpers this psm1 calls are all missing — a half-loaded,
# broken module. NEVER fail silently (the old `Install-Module PsBash` bug: aliases registered
# but `Invoke-BashLs` was "not recognized"). The host pre-registers the cmdlets via ISS, so
# this only fires for a genuinely incomplete module-mode install.
if ((-not $script:__psbashLoadedCmdletsAssembly) -and
    (-not (Get-Command Invoke-BashBasename -CommandType Cmdlet -ErrorAction SilentlyContinue))) {
    Write-Warning ("PsBash: companion binary module PsBash.Cmdlets.dll not found beside the module " +
        "('$PSScriptRoot'). Migrated commands (ls, cat, grep, …) will not work. The PsBash package " +
        "should bundle PsBash.Cmdlets.dll; reinstall, or run: Install-Module PsBash.Cmdlets.")
}

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

    # The consumer reads $tmp during the same command and nothing deletes it after,
    # so in a long-lived daemon host these files accumulate unbounded. Best-effort
    # reap any older than a few minutes — well past the lifetime of the command that
    # created them, so an in-flight substitution is never swept out from under a reader.
    try {
        $cutoff = [DateTime]::UtcNow.AddMinutes(-5)
        foreach ($old in [System.IO.Directory]::EnumerateFiles($subDir)) {
            try {
                if ([System.IO.File]::GetLastWriteTimeUtc($old) -lt $cutoff) {
                    [System.IO.File]::Delete($old)
                }
            } catch { }
        }
    } catch { }

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

    $Path = [PsBash.Cmdlets.BashRuntime]::NormalizeWindowsPath($Path)
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

    $Path = [PsBash.Cmdlets.BashRuntime]::NormalizeWindowsPath($Path)
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
    # Literal paths pass through unchanged. Patterns with *, ?, or a [..] character
    # class are resolved. Returns expanded list of file paths.
    param([string[]]$Paths)
    $resolved = [System.Collections.Generic.List[string]]::new()
    foreach ($p in $Paths) {
        # Runtime safety net: on Windows, map unix-style drive paths
        # (/c/.., /mnt/c/..) and native drive variants (c:/..) to a native
        # Windows path via the SAME shared mapper the transpiler uses, so a
        # direct/interactive user who types a unix path gets the file they
        # meant instead of one resolved against C:\c\.. . Routed through the
        # BashRuntime forwarder (NOT a direct [PsBash.Core.WindowsPath]
        # reference): that Transpiler type is not always loaded in a psm1-only
        # runspace (e.g. isolated Pester), but the Cmdlets static always is.
        # No-op off-Windows.
        $p = [PsBash.Cmdlets.BashRuntime]::NormalizeWindowsPath($p)
        # *, ?, or a [..] character class → glob. A lone unmatched '[' resolves to
        # nothing and falls through as a literal path (bash treats it literally too).
        if ($p -match '[*?[]') {
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

# --- echo Command ---
# Invoke-BashEcho migrated to a binary cmdlet
# (PsBash.Cmdlets/InvokeBashEchoCommand.cs). The emitter force-quotes -e/-E so
# they reach the cmdlet's Arguments with case intact (the binder cannot
# disambiguate them); the `Set-Alias echo -> Invoke-BashEcho` below resolves to
# the cmdlet.

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
# Invoke-BashRedirect migrated to a binary cmdlet
# (PsBash.Cmdlets/InvokeBashRedirectCommand.cs). The emitter pipes stdout into
# `Invoke-BashRedirect -Path file [-Append]` for > / >> redirects; -Path/-Append
# are PowerShell-style flags that bind directly to the cmdlet.

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

# Invoke-BashGrep migrated to binary cmdlet PsBash.Cmdlets.InvokeBashGrepCommand (REFACTOR-2 Phase 4 follow-on). Set-Alias 'grep' below resolves to that cmdlet.

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

# Invoke-BashSort migrated to a binary cmdlet in PsBash.Cmdlets.dll
# (REFACTOR-2 follow-on — InvokeBashSortCommand.cs). The psm1 no longer
# defines this function; the cmdlet is the sole implementation and the
# Set-Alias 'sort' line below resolves to it. sort's -c, -d, and -V flags
# prefix-collide with -Confirm / -Debug / -Verbose; they are declared as
# explicit SwitchParameter C / D / V on the cmdlet so the binder routes the
# bare tokens by exact-name match. -t SEP / -k POS value-bearing flags and
# -r / -n / -u / -f / -h / -M / -b / -s short flags (plus bundled forms) all
# stay in $Arguments and are recovered by the manual scan, byte-for-byte
# against the psm1 oracle.

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

# Invoke-BashStat MIGRATED to a binary cmdlet (REFACTOR-2 Phase 4 follow-on).
# See src/PsBash.Cmdlets/InvokeBashStatCommand.cs. The psm1 function (and its
# Format-StatString helper) is intentionally removed: a script function would
# shadow the cmdlet of the same name. The `Set-Alias stat` line below still
# resolves because PsBash.Cmdlets.dll registers the cmdlet before this module
# runs. The psm1 Get-BashFileInfo helper remains because other psm1 functions
# (e.g. Invoke-BashDu pre-migration callers, out-of-tree callers) still
# reference it.

# Invoke-BashCp / Mv / Rm / Mkdir / Rmdir migrated to binary cmdlets (REFACTOR-2):
# see InvokeBashCp/Mv/Rm/Mkdir/RmdirCommand.cs and FileSystemHelpers.cs in PsBash.Cmdlets.

# Invoke-Bashtouch+ln migrated to binary cmdlet (REFACTOR-2): see InvokeBash*Command.cs in PsBash.Cmdlets.


# --- ps Command ---


# Invoke-BashPs MIGRATED to a binary cmdlet (REFACTOR-2 follow-on).
# See src/PsBash.Cmdlets/InvokeBashPsCommand.cs. The psm1 function definition
# is intentionally removed: a script function would shadow the cmdlet of the
# same name (PowerShell function precedence > cmdlet). The `Set-Alias ps ->
# Invoke-BashPs` line below still resolves because Import-Module of
# PsBash.Cmdlets.dll registers the cmdlet before this module runs.

# --- awk Command ---
# Migrated to a binary cmdlet: src/PsBash.Cmdlets/InvokeBashAwkCommand.cs, backed
# by a real recursive-descent AWK interpreter (AwkInterpreter / AwkLexer /
# AwkParser / AwkMachine / AwkValue / AwkPrintf). The psm1 function web
# (Invoke-BashAwk, ConvertFrom-AwkProgram, Split-AwkFields, Read-AwkBlock,
# Test-AwkPattern, Resolve-AwkExpression, Expand-AwkString, Invoke-AwkAction,
# Split-AwkStatements, Split-AwkFuncArgs, Format-AwkPrintf, Resolve-AwkStringFunc)
# was a regex/string-scan approximation; the C# port closes the five differential
# parity gaps it could not express (field string-concat, += accumulation,
# index() in print position, split() into an array, if/else). psm1's Set-Alias
# 'awk' line below resolves to the cmdlet.

# --- cut Command ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashCutCommand.cs (REFACTOR-2 follow-on).

# tr -> InvokeBashTrCommand.cs (REFACTOR-2 follow-on, see PsBash.Cmdlets/InvokeBashTrCommand.cs)

# --- uniq Command --- migrated to InvokeBashUniqCommand.cs (REFACTOR-2 follow-on)

# --- rev Command --- migrated to InvokeBashRevCommand.cs (REFACTOR-2 follow-on)

# --- nl Command --- migrated to InvokeBashNlCommand.cs (REFACTOR-2 follow-on)

# --- diff Command --- migrated to InvokeBashDiffCommand.cs (REFACTOR-2 follow-on)

# comm Command: migrated to InvokeBashCommCommand.cs (REFACTOR-2 follow-on).

# column Command: migrated to InvokeBashColumnCommand.cs (REFACTOR-2 follow-on).

# --- join Command ---
# Migrated to binary cmdlet in src/PsBash.Cmdlets/InvokeBashJoinCommand.cs

# --- paste Command ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashPasteCommand.cs (REFACTOR-2 follow-on).

# --- tee Command ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashTeeCommand.cs (REFACTOR-2 follow-on).

# Invoke-BashLess / Invoke-BashMore migrated to binary cmdlets
# (PsBash.Cmdlets.dll: InvokeBashLessCommand / InvokeBashMoreCommand).
# REFACTOR-2 follow-on. The Set-Alias lines below resolve to the cmdlets.

# --- xargs Command ---

# Invoke-BashXargs migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashXargsCommand.cs (REFACTOR-2 follow-on).

# --- jq Command ---

# JSON string-literal escape (backslash, quote, control chars) + surrounding quotes.
# Shared by string values AND object keys — keys were previously quoted WITHOUT
# escaping, so a key containing " or \ produced invalid JSON.
function ConvertTo-JqJsonStringLiteral {
    param([string]$s)
    $escaped = $s -replace '\\', '\\' -replace '"', '\"' -replace "`n", '\n' -replace "`r", '\r' -replace "`t", '\t' -replace "`b", '\b' -replace "`f", '\f'
    # Any remaining control char (< 0x20) has no short JSON escape -- emit \uXXXX
    # so the result stays valid JSON instead of embedding a raw control byte.
    if ($escaped -match '[\x00-\x1f]') {
        $sb = [System.Text.StringBuilder]::new($escaped.Length)
        foreach ($ch in $escaped.ToCharArray()) {
            $code = [int]$ch
            if ($code -lt 0x20) {
                $sb.Append(('\u{0:x4}' -f $code)) | Out-Null
            } else {
                $sb.Append($ch) | Out-Null
            }
        }
        $escaped = $sb.ToString()
    }
    return "`"$escaped`""
}

function ConvertTo-JqJson {
    param([object]$Value, [bool]$Compact, [bool]$SortKeys, [bool]$RawOutput)

    if ($null -eq $Value) { return 'null' }

    if ($Value -is [bool]) {
        if ($Value) { return 'true' } else { return 'false' }
    }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [decimal]) {
        # Invariant culture: a comma-decimal locale would otherwise render "$Value"
        # as e.g. "1,5" (invalid JSON).
        return $Value.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [double]) {
        return $Value.ToString('R', [System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [string]) {
        if ($RawOutput) { return $Value }
        return ConvertTo-JqJsonStringLiteral $Value
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
            $kJson = ConvertTo-JqJsonStringLiteral ([string]$k)
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
    return ConvertTo-JqJsonStringLiteral ([string]$Value)
}

function Test-JqTruthy {
    # jq/yq truthiness: ONLY null and boolean false are falsy. Every other value —
    # including 0, "", the string "false", [], {} — is TRUTHY. The old `$val -ne $false`
    # test was wrong because PowerShell coerces the RHS to the LHS's type: `0 -ne $false`
    # compares 0 -ne 0 (False → 0 read as falsy), and `"false" -ne $false` compares
    # "false" -ne "False" case-insensitively (equal → read as falsy). So `.count // 99`
    # on count:0 returned 99, and select(.n) dropped every n:0. Test the type explicitly.
    param([object]$Value)
    if ($null -eq $Value) { return $false }
    if ($Value -is [bool]) { return [bool]$Value }
    return $true
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
            if (Test-JqTruthy $val) { return @(, $val) }
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
        # jq `not`: true iff the value is falsy (null or boolean false). The old
        # `-or ($Data -eq $false)` term wrongly made 0 falsy (0 -eq $false → True).
        $falsy = -not (Test-JqTruthy $Data)
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
                        # Resolve the negative index PER ITEM into a local — mutating
                        # the shared $idx baked the first item's Count into every later
                        # item, so .[-1] over [[1,2,3],[4,5]] mis-indexed the 2nd array.
                        $ei = $idx
                        if ($ei -lt 0) { $ei = $item.Count + $ei }
                        if ($ei -ge 0 -and $ei -lt $item.Count) {
                            $next.Add($item[$ei])
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
    return (Test-JqTruthy $val)
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
        $condTrue = ($condVals.Count -gt 0) -and (Test-JqTruthy $condVals[0])

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

# --- Date ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashDateCommand.cs (REFACTOR-2 follow-on).

# --- Seq ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashSeqCommand.cs (REFACTOR-2 follow-on).

# --- Expr ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashExprCommand.cs (REFACTOR-2 follow-on).

# --- du Command ---

# REFACTOR-2 Phase 4 follow-on: Invoke-BashDu migrated to a binary cmdlet
# (PsBash.Cmdlets/InvokeBashDuCommand.cs). The psm1 no longer defines it;
# the `Set-Alias du -> Invoke-BashDu` line below resolves to the cmdlet.

# --- tree Command ---
#
# Invoke-BashTree MIGRATED to a binary cmdlet (REFACTOR-2 follow-on).
# See src/PsBash.Cmdlets/InvokeBashTreeCommand.cs. The `Set-Alias tree`
# line below still resolves because PsBash.Cmdlets.dll registers the
# cmdlet before this module runs.

# --- env / printenv ---
#
# Invoke-BashEnv MIGRATED to a binary cmdlet (REFACTOR-2 follow-on).
# See src/PsBash.Cmdlets/InvokeBashEnvCommand.cs. The `Set-Alias env`
# and `Set-Alias printenv` lines below still resolve because
# PsBash.Cmdlets.dll registers the cmdlet before this module runs.

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
# Migrated to binary cmdlet — see src/PsBash.Cmdlets/InvokeBashUnameCommand.cs (REFACTOR-2).

# --- fold ---
# Migrated to binary cmdlet — see src/PsBash.Cmdlets/InvokeBashFoldCommand.cs (REFACTOR-2).

# --- expand Command ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashExpandCommand.cs (REFACTOR-2 follow-on).

# Invoke-BashUnexpand migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashUnexpandCommand.cs

# Invoke-BashStrings migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashStringsCommand.cs

# Invoke-BashSplit migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashSplitCommand.cs

# --- tac Command ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashTacCommand.cs (REFACTOR-2 follow-on)

# Invoke-BashBase64 migrated to binary cmdlet (REFACTOR-2 follow-on):
# see InvokeBashBase64Command.cs in PsBash.Cmdlets.

# Invoke-BashChecksum / Md5sum / Sha1sum / Sha256sum migrated to binary
# cmdlets (REFACTOR-2): see InvokeBashChecksumCommands.cs in PsBash.Cmdlets.

# Invoke-BashFile migrated to binary cmdlet (REFACTOR-2 follow-on):
# see InvokeBashFileCommand.cs in PsBash.Cmdlets.

# --- rg (ripgrep-style search) ---

# Invoke-BashRg migrated to binary cmdlet PsBash.Cmdlets.InvokeBashRgCommand (REFACTOR-2 Phase 4 follow-on). Set-Alias 'rg' below resolves to that cmdlet.

# --- gzip / gunzip / zcat ---

# Invoke-BashGzip migrated to binary cmdlet PsBash.Cmdlets.InvokeBashGzipCommand
# (REFACTOR-2 follow-on). See src/PsBash.Cmdlets/InvokeBashGzipCommand.cs.
# The aliases below (gzip / gunzip / zcat) resolve to the cmdlet because the
# binary module is imported before this psm1 runs as a script. The cmdlet
# reads $MyInvocation.InvocationName to map gunzip -> '-d' and zcat -> '-dc'
# exactly like the psm1 oracle did.

# --- tar ---

# Invoke-BashTar migrated to binary cmdlet (REFACTOR-2): see InvokeBashTarCommand.cs in PsBash.Cmdlets.

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
    if ($s -match '^\-?\d+$') {
        $longVal = [long]0
        if ([long]::TryParse($s, [System.Globalization.NumberStyles]::Integer, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$longVal)) {
            return $longVal
        }
        # Too big for int64 (e.g. "99999999999999999999"): fall through to double
        # (precision loss, matching jq's arbitrary-size-number handling) rather
        # than throwing OverflowException out of the yq cmdlet.
        $doubleVal = [double]0
        if ([double]::TryParse($s, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$doubleVal)) {
            return $doubleVal
        }
        return $s
    }
    if ($s -match '^\-?\d+\.\d+$') {
        $doubleVal = [double]0
        if ([double]::TryParse($s, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$doubleVal)) {
            return $doubleVal
        }
        return $s
    }
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

# Invoke-BashYq migrated to binary cmdlet (REFACTOR-2 follow-on):
# see src/PsBash.Cmdlets/InvokeBashYqCommand.cs. The psm1 helpers
# ConvertFrom-SimpleYaml / ConvertFrom-YamlValue / ConvertTo-SimpleYaml
# and the jq helpers Invoke-JqFilter / ConvertTo-JqJson remain here
# because the cmdlet delegates to them via parameter-bound InvokeScript
# (no .NET YAML lib in stdlib; the C# JqEngine operates on a
# System.Text.Json graph, not the OrderedDictionary graph that
# ConvertFrom-SimpleYaml produces). Set-Alias 'yq' at the bottom of
# this file resolves to the binary cmdlet.

# --- xan Command ---

# Invoke-BashXan migrated to binary cmdlet (REFACTOR-2): see InvokeBashXanCommand.cs in PsBash.Cmdlets.

# Invoke-Bashsleep migrated to binary cmdlet (REFACTOR-2): see InvokeBash*Command.cs in PsBash.Cmdlets.


# Invoke-BashTime migrated to binary cmdlet (REFACTOR-2): see InvokeBashTimeCommand.cs in PsBash.Cmdlets.

# Invoke-Bashwhich migrated to binary cmdlet (REFACTOR-2): see InvokeBash*Command.cs in PsBash.Cmdlets.


# --- alias / unalias (module mode: dynamic function creation) ---

# BashUserAliases: ownership stays in psm1 module scope but declared at the
# global scope so the migrated InvokeBashAliasCommand binary cmdlet — running
# in a separate binary-module session state — can read/mutate it via
# parameter-bound InvokeScript without scope qualifier collisions.
if (-not $global:BashUserAliases) {
    $global:BashUserAliases = [System.Collections.Generic.Dictionary[string,string]]::new(
        [System.StringComparer]::Ordinal
    )
}
$script:BashUserAliases = $global:BashUserAliases

# Invoke-BashAlias migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashAliasCommand.cs (REFACTOR-2 follow-on, alias/trap batch).
# The $script:BashUserAliases dictionary above stays in psm1 module scope — the cmdlet reads/mutates it via parameter-bound InvokeScript.

# --- trap ---

# BashTrapHandlers: same shape as BashUserAliases above — declared at global
# scope so the migrated InvokeBashTrapCommand binary cmdlet can read/mutate it
# via parameter-bound InvokeScript.
if (-not $global:BashTrapHandlers) {
    $global:BashTrapHandlers = [System.Collections.Generic.Dictionary[string,object]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
}
$script:BashTrapHandlers = $global:BashTrapHandlers

# Invoke-BashTrap migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashTrapCommand.cs (REFACTOR-2 follow-on, alias/trap batch).
# The $script:BashTrapHandlers dictionary above stays in psm1 module scope — the cmdlet reads/mutates it via parameter-bound InvokeScript;
# $global:__BashTrapERR / $global:__BashTrapEXIT are set in the same scripts so the eval pipeline observes them.

# --- read ---

# --- read ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashReadCommand.cs (REFACTOR-2 follow-on).

# --- mapfile / readarray ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashMapfileCommand.cs (REFACTOR-2 follow-on).

# --- readlink ---
# Migrated to PsBash.Cmdlets/InvokeBashReadlinkCommand.cs (REFACTOR-2).

# --- mktemp ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashMktempCommand.cs (REFACTOR-2 follow-on).

# --- type ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashTypeCommand.cs (REFACTOR-2 follow-on).

# --- shopt ---
# Migrated to binary cmdlet: src/PsBash.Cmdlets/InvokeBashShoptCommand.cs (REFACTOR-2 follow-on).

# Invoke-BashBash migrated to binary cmdlet PsBash.Cmdlets.InvokeBashBashCommand (REFACTOR-2 follow-on).
# The Set-Alias 'bash' -> 'Invoke-BashBash' line below resolves to the cmdlet.

# --- Background Process Support ---

# RC-2: background jobs run in-process against a shared RunspacePool of isolated
# runspaces — no pwsh cold start per `&`. Each entry in $script:BashBgJobs is a
# job record: @{ Id; PowerShell; AsyncResult; Done }. $! ($global:BashBgLastPid)
# is a synthetic monotonically-increasing id keyed to the [powershell] instance,
# NOT a real OS pid (documented: bash uses an OS pid; in-process runspaces have
# no separate pid, so a synthetic id is used and is sufficient for `wait $!`).
$script:BashBgJobs = [System.Collections.Generic.List[object]]::new()
# Alias view of $script:BashBgJobs sharing the SAME underlying List instance.
# Tests reference $script:BashBgPids and expect items to expose HasExited and
# Kill(). The job records produced by Invoke-BashBackground carry both via
# ScriptProperty (HasExited proxies $job.Done) and ScriptMethod (Kill calls
# $job.PowerShell.Stop()). Sharing one List means Add/Clear/index from
# either name affects both views — no double-bookkeeping.
$script:BashBgPids = $script:BashBgJobs
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
            # Emit blank lines too — a background command's empty output line is real
            # output. `if ($text)` is false for "", silently dropping blank lines.
            if ($null -ne $text) { Emit-BashLine -Text $text }
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
# The real ps-bash module version (distinct from the emulated $BASH_VERSION above). Binary cmdlets
# and runtime functions read this for `<cmd> --version`, so tooling can identify it is ps-bash.
$global:PsBashVersion = $__bashVer.ToString()
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
    # HasExited / Kill() shape so tests asserting against $script:BashBgPids
    # (the alias for $script:BashBgJobs) see Process-like surface. HasExited
    # reflects $job.Done and also flips true once the AsyncResult completes
    # so a polling test does not have to call Complete-BashBgJob first.
    $job | Add-Member -MemberType ScriptProperty -Name 'HasExited' -Value {
        if ($this.Done) { return $true }
        if ($null -ne $this.AsyncResult -and $this.AsyncResult.IsCompleted) { return $true }
        return $false
    } -Force
    $job | Add-Member -MemberType ScriptMethod -Name 'Kill' -Value {
        try { $this.PowerShell.Stop() } catch { }
        $this.Done = $true
    } -Force
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
        # wait $! / wait <id>: wait for a synthetic background-job id; wait %N: the
        # bash job number (positional, 1-based) shown by `jobs` — NOT the id.
        foreach ($pidArg in $Arguments) {
            $job = $null
            if ($pidArg -like '%*') {
                $wantNum = $pidArg -replace '^%', ''
                if ([int]::TryParse($wantNum, [ref]$null)) {
                    $idx = [int]$wantNum - 1
                    if ($idx -ge 0 -and $idx -lt $script:BashBgJobs.Count) {
                        $job = @($script:BashBgJobs)[$idx]
                    }
                }
            } elseif ([int]::TryParse($pidArg, [ref]$null)) {
                $wantId = [int]$pidArg
                $job = $script:BashBgJobs | Where-Object { $_.Id -eq $wantId } | Select-Object -First 1
            }
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
            # A `%N` jobspec is the bash JOB NUMBER (the [N] shown by `jobs`), which is
            # positional and 1-based — NOT the synthetic $! id (which starts at 1000).
            # Comparing the stripped "1" against $_.Id never matched. Map it to the
            # same positional index as the bare-number form.
            $wantNum = $jobNum -replace '^%', ''
            if ([int]::TryParse($wantNum, [ref]$null)) {
                $idx = [int]$wantNum - 1
                if ($idx -ge 0 -and $idx -lt $script:BashBgJobs.Count) {
                    $target = @($script:BashBgJobs)[$idx]
                }
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

# Invoke-BashShift migrated to InvokeBashShiftCommand.cs (REFACTOR-2 follow-on, vars batch).

# --- realpath ---

# Invoke-BashRealpath migrated to binary cmdlet (REFACTOR-2):
# see InvokeBashRealpathCommand.cs in PsBash.Cmdlets.

# --- command ---

# Invoke-BashCommand: migrated to binary cmdlet
# (InvokeBashCommandCommand in PsBash.Cmdlets.dll) in REFACTOR-2 follow-on.
# The 'command' Set-Alias below resolves to the cmdlet.

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

    # ONE object carrying the whole help text in .BashText. This is the tested,
    # relied-upon contract (`(Show-BashHelp 'ls').BashText -match 'Usage: ls'`, and every
    # command's `--help` path). Consumers that need per-line records (grep/head/wc)
    # defensively split a multi-line BashText themselves (see runtime-functions spec), so
    # emitting per-line here is unnecessary and breaks the single-object callers.
    $text = ($lines -join "`n") + "`n"
    New-BashObject -BashText $text
}

# --- Tab Completion ---

# Bash flag specifications — single source of truth in BashFlagSpecs.json (the same file the
# host's FlagSpecs.cs embeds), loaded from the module directory and reshaped into the
# @(@('-flag','desc')) form Register-BashCompletions consumes. When the JSON is unavailable
# (e.g. the host loads this psm1 as a script string, where $PSScriptRoot is unset — that path
# uses the host's C# completion, not these PS completers) the table is left empty.
$script:BashFlagSpecs = @{}
if ($PSScriptRoot) {
    $bashFlagSpecsPath = Join-Path $PSScriptRoot 'BashFlagSpecs.json'
    if (Test-Path -LiteralPath $bashFlagSpecsPath) {
        try {
            $bashFlagSpecsRaw = Get-Content -Raw -LiteralPath $bashFlagSpecsPath | ConvertFrom-Json
            foreach ($bashFlagSpecProp in $bashFlagSpecsRaw.PSObject.Properties) {
                # cmd -> @(@('-flag','desc'), ...). PowerShell unwraps a single-flag command's
                # one-element array on retrieval; both consumers re-wrap it (see the
                # `$flagEntries[0] -is [string]` guards), so the loader stays simple.
                $script:BashFlagSpecs[$bashFlagSpecProp.Name] = @(
                    foreach ($bashFlagSpecEntry in $bashFlagSpecProp.Value) {
                        , @($bashFlagSpecEntry.flag, $bashFlagSpecEntry.desc)
                    }
                )
            }
        } catch {
            Write-Verbose "PsBash: failed to load BashFlagSpecs.json: $_"
        }
    }
}

$script:BashCompleters = @{}

function Register-BashCompletions {
    [CmdletBinding()]
    param()

    foreach ($commandName in $script:BashFlagSpecs.Keys) {
        $flagEntries = $script:BashFlagSpecs[$commandName]
        if (-not $flagEntries -or $flagEntries.Count -eq 0) { continue }
        # A single-flag command's @(@('-x','desc')) unwraps to @('-x','desc') on retrieval, so
        # re-wrap when the first element is a string (mirrors the guard in the help-text path).
        if ($flagEntries[0] -is [string]) { $flagEntries = @(, $flagEntries) }

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

# Tab-completion registration is meaningless on a non-interactive host (the
# transpiled IPC path never prompts the user for completions) but costs ~1.5 s
# of psm1-load time — one Register-ArgumentCompleter call per command in
# $script:BashFlagSpecs. The interactive REPL (PsBash.Shell / InteractiveShell)
# sets PSBASH_INTERACTIVE=1 before starting the host; `Import-Module PsBash` from a
# user pwsh prompt is ALSO interactive but sets no such variable — the previous
# PSBASH_INTERACTIVE-only gate meant plain-import users never got completers despite
# the documented intent. Register when EITHER signal holds: the env flag (REPL) OR a
# genuinely interactive ConsoleHost session (ad-hoc import). The headless
# per-invocation IPC host is neither UserInteractive nor a ConsoleHost, so it still
# skips the registration (host-startup #9 stays fast).
$__psbashRegisterCompleters = ($env:PSBASH_INTERACTIVE -eq '1') -or
    ([Environment]::UserInteractive -and $Host.Name -eq 'ConsoleHost')
if ($__psbashRegisterCompleters) {
    Register-BashCompletions
}

# --- Aliases ---

# Remove built-in PowerShell aliases that conflict with our bash commands
$script:conflictingAliases = @('echo','ls','cat','cp','mv','rm','mkdir','pwd','sort','diff','sleep','test','kill','time','pushd','popd','type')
# IMPORTANT: this loop runs at module (script) scope, which the host imports into
# the runspace — so a bare top-level loop variable LEAKS and shadows any
# transpiled bash variable of the same name (a leaked `$a` = 'type' once made
# `a=5; echo $((a))` return 0). Wrapping the loop in `& { … }` scopes the loop
# variable to the block so it can never escape. The FindabilityGuardTests guard
# forbids column-0 foreach/for to keep this invariant. Do NOT unwrap.
& {
    foreach ($alias in $script:conflictingAliases) {
        if (Test-Path "Alias:\$alias") {
            Remove-Item "Alias:\$alias" -Force -ErrorAction SilentlyContinue
        }
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
# Wrapper for Invoke-BashSed: PowerShell's binder rejects `-e A -e B` with
# "parameter Expression specified more than once" because Expression is
# declared as a single array parameter (the case-insensitive binder ate -e
# from the test-side, then sees the second -e and errors at bind time —
# before our cmdlet body runs, so MyInvocation.Line reparsing can't help).
# Pre-process $args here to bundle repeated -e values into one -e @(...)
# call, then dispatch to the underlying cmdlet by fully-qualified name.
function Invoke-BashSed {
    # No [CmdletBinding()] — that would add -ErrorAction / -ErrorVariable
    # common parameters which then ambiguously prefix-match `-e` and
    # block the entire purpose of this proxy. Without CmdletBinding we
    # take everything verbatim via $args, and $input gives us pipeline
    # input as an enumerator that we materialize and replay into the
    # underlying cmdlet.

    # Materialize pipeline (if any). $input is an enumerator that's
    # consumable once.
    $piped = @($input)

    # Walk $args, pull out every "-e <value>" pair, keep the rest as-is.
    # `-eq` is case-insensitive, so `-E` (uppercase, the extended-regex
    # flag in GNU sed) collapses to `-e` here. Distinguish via a
    # case-sensitive `-ceq` comparison and set a flag instead of
    # treating -E as an expression-value flag.
    $eValues = [System.Collections.Generic.List[string]]::new()
    $other = [System.Collections.Generic.List[object]]::new()
    $extendedRegex = $false
    $i = 0
    while ($i -lt $args.Count) {
        $a = $args[$i]
        if ($a -ceq '-E' -or $a -ceq '--extended-regexp' -or $a -ceq '--regexp-extended') {
            $extendedRegex = $true
            $i++
            continue
        }
        if (($a -ceq '-e' -or $a -ceq '--expression') -and ($i + 1) -lt $args.Count) {
            # The value may be a single string (`-e 's/a/b/'`, and the repeated
            # `-e A -e B` form) OR a PowerShell array when expressions are passed
            # as the idiomatic comma-list (`-e 's/a/1/','s/b/2/','s/c/3/'`). A
            # bare [string] cast on an array space-joins it into ONE bogus
            # expression, so only the first s/// would apply. Add each element.
            $eVal = $args[$i + 1]
            if ($eVal -isnot [string] -and $eVal -is [System.Collections.IEnumerable]) {
                foreach ($ev in $eVal) { $eValues.Add([string]$ev) }
            } else {
                $eValues.Add([string]$eVal)
            }
            $i += 2
            continue
        }
        $other.Add($args[$i])
        $i++
    }
    if ($extendedRegex) { $other.Add('-r') }

    # Look up the cmdlet (NOT the function we're inside — same name).
    $cmd = Microsoft.PowerShell.Core\Get-Command `
        -Name Invoke-BashSed -CommandType Cmdlet -ErrorAction Stop |
        Select-Object -First 1

    if ($eValues.Count -gt 0) {
        $exprArr = $eValues.ToArray()
        # -Expression (full name) to avoid the -e/-ErrorAction ambiguity.
        if ($piped.Count -gt 0) {
            $piped | & $cmd -Expression $exprArr @other
        } else {
            & $cmd -Expression $exprArr @other
        }
    } else {
        # No -e flags, but we may have rewritten -E -> -r in $other.
        if ($piped.Count -gt 0) {
            $piped | & $cmd @other
        } else {
            & $cmd @other
        }
    }
}

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
Set-Alias -Name 'ping'       -Value 'Invoke-BashPing'       -Force -Scope Global -Option AllScope
Set-Alias -Name 'tracert'    -Value 'Invoke-BashTraceroute' -Force -Scope Global -Option AllScope
Set-Alias -Name 'traceroute' -Value 'Invoke-BashTraceroute' -Force -Scope Global -Option AllScope
# psgit: structured git viewer. Deliberately NOT aliased to `git` — native git stays interactive.
Set-Alias -Name 'psgit'      -Value 'Invoke-BashGit'        -Force -Scope Global -Option AllScope
# gtui: interactive git-status TUI (Strata-gated; resolves only when the styling cmdlets are built).
Set-Alias -Name 'gtui'       -Value 'Invoke-BashGitTui'     -Force -Scope Global -Option AllScope
Set-Alias -Name 'alias'    -Value 'Invoke-BashAlias'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'unalias'  -Value 'Invoke-BashAlias'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'balias'   -Value 'Invoke-BashAlias'    -Force -Scope Global -Option AllScope
Set-Alias -Name 'trap'     -Value 'Invoke-BashTrap'     -Force -Scope Global -Option AllScope
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
Set-Alias -Name 'mapfile'  -Value 'Invoke-BashMapfile'  -Force -Scope Global -Option AllScope
Set-Alias -Name 'readarray' -Value 'Invoke-BashMapfile' -Force -Scope Global -Option AllScope
# --- unset ---

# Invoke-BashUnset migrated to InvokeBashUnsetCommand.cs (REFACTOR-2 follow-on, vars batch).

# --- pushd / popd / dirs ---

# Invoke-BashPushd / Invoke-BashPopd / Invoke-BashDirs migrated to binary cmdlets
# (REFACTOR-2 dir-stack batch): see InvokeBashPushdCommand.cs / InvokeBashPopdCommand.cs
# / InvokeBashDirsCommand.cs in PsBash.Cmdlets. The location stack itself remains
# PowerShell's built-in default stack (the one Push-Location / Pop-Location -Stack /
# Get-Location -Stack manage), exactly as the oracle used it — no $global:BashDirStack
# array is introduced. The Set-Alias lines for pushd / popd / dirs near the bottom of
# this module resolve to the cmdlets.

# --- yes ---

# Invoke-BashYes migrated to binary cmdlet (REFACTOR-2):
# see InvokeBashYesCommand.cs in PsBash.Cmdlets.

# --- tput ---

# Invoke-BashTput migrated to binary cmdlet (REFACTOR-2 follow-on):
# see InvokeBashTputCommand.cs in PsBash.Cmdlets.

# --- kill ---

# --- kill Command ---
# Invoke-BashKill migrated to a binary cmdlet
# (PsBash.Cmdlets/InvokeBashKillCommand.cs). No flag collides with a PowerShell
# common parameter, so the cmdlet takes everything via Arguments; the
# `Set-Alias kill -> Invoke-BashKill` below resolves to the cmdlet.

# --- test (standalone) ---

# Invoke-BashTest is a binary cmdlet (PsBash.Cmdlets.InvokeBashTestCommand) since REFACTOR-2 follow-on.
# Test-BashCondition removed (dead code, verified no in-tree caller of the shipped module/build/tests); superseded by the InvokeBashTestCommand binary cmdlet above.

# --- let ---

# Invoke-BashLet migrated to InvokeBashLetCommand.cs (REFACTOR-2 follow-on, vars batch).

# --- id ---
# Migrated to InvokeBashIdCommand.cs (REFACTOR-2 Phase 4-follow-on). The
# Set-Alias 'id' below resolves to the binary cmdlet.

# --- shuf ---
# Migrated to InvokeBashShufCommand.cs (REFACTOR-2 follow-on). The
# Set-Alias 'shuf' below resolves to the binary cmdlet.

# Invoke-BashInstall migrated to a binary cmdlet (REFACTOR-2):
# src/PsBash.Cmdlets/InvokeBashInstallCommand.cs. The Set-Alias line below
# resolves to the cmdlet.

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

# Register a custom browse adapter so a user's module can drive the `browse`
# workbench for its own object types. The registry ($script:BrowseAdapters)
# lives in THIS module's scope, so a `$script:BrowseAdapters += ...` from another
# module would write the wrong scope — this function is the supported entry point.
# User adapters take precedence over the built-ins; the 'default' fallback stays last.
function Register-BrowseAdapter {
    [CmdletBinding(DefaultParameterSetName = 'Object')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Object', ValueFromPipeline)]
        [PSObject]$Adapter,

        [Parameter(Mandatory, ParameterSetName = 'Inline')]
        [string]$Name,
        [Parameter(Mandatory, ParameterSetName = 'Inline')]
        [string[]]$TypeNames,
        [Parameter(ParameterSetName = 'Inline')]
        [string[]]$DisplayProperties = @(),
        [Parameter(Mandatory, ParameterSetName = 'Inline')]
        [object[]]$Actions
    )

    process {
        Initialize-BrowseAdapters
        if ($PSCmdlet.ParameterSetName -eq 'Inline') {
            $Adapter = New-BrowseAdapter -Name $Name -TypeNames $TypeNames `
                -DisplayProperties $DisplayProperties -Actions $Actions
        }

        $others  = @($script:BrowseAdapters | Where-Object { $_.Name -ne $Adapter.Name -and $_.Name -ne 'default' })
        $default = @($script:BrowseAdapters | Where-Object { $_.Name -eq 'default' })
        # Prepend the user adapter so it wins over built-ins; keep 'default' last.
        $script:BrowseAdapters = @($Adapter) + $others + $default
    }
}

function Get-BrowseAdapter {
    [CmdletBinding()]
    param([string]$Name)

    Initialize-BrowseAdapters
    if ($Name) { return @($script:BrowseAdapters | Where-Object Name -eq $Name) }
    $script:BrowseAdapters
}

function Unregister-BrowseAdapter {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    Initialize-BrowseAdapters
    if ($Name -eq 'default') { throw "Cannot remove the 'default' browse adapter." }
    $script:BrowseAdapters = @($script:BrowseAdapters | Where-Object { $_.Name -ne $Name })
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

# Invoke-BashBrowse is now a binary cmdlet — PsBash.Cmdlets.InvokeBashBrowseCommand.
# The Set-Alias 'browse' line below resolves to the cmdlet; the supporting psm1
# helpers (New-BrowseBinding / ConvertTo-BrowseRow / Invoke-BrowseAction /
# Invoke-BrowseCommand / Invoke-BrowseInteractive plus the *-BrowseAdapter web)
# remain in this module and the cmdlet calls them via parameter-bound InvokeScript.

# --- Prompt Hook Integration ---

# Initialized at module load; tracks last known working directory for chpwd detection.
# MUST be $script: (module scope) to match the prompt wrapper, which reads and writes
# $script:__BashLastCwd. Initializing it as $global: left the script-scoped variable
# $null on the first prompt, so chpwd fired spuriously on shell startup (oldPath $null
# != the initial cwd) — bash does not fire chpwd for the shell's starting directory.
$script:__BashLastCwd = (Get-Location).Path

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
# bash `[` alias. Pester's per-alias introspection (Get-Alias '[') treats
# the bracket as a wildcard pattern and explodes — but production callers
# either go through the transpiler (which emits Invoke-BashTest directly for
# the `[` form, never the alias) or use the cmdlet binder's call operator
# (`& '[' ...`), neither of which hits the wildcard hazard. Any Pester test
# that lists aliases must wrap the name in -LiteralName or fall back to
# Get-Command '[' to avoid the wildcard expansion.
Set-Alias -Name '['        -Value 'Invoke-BashTest'     -Force -Scope Global -Option AllScope
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
# Wrapped in & { } so the loop variable is block-scoped and never leaks into the
# runspace (see the conflicting-alias loop above; guarded by FindabilityGuardTests).
& {
    foreach ($typeName in @(
            'PsBash.TextOutput', 'PsBash.LsEntry', 'PsBash.CatLine', 'PsBash.GrepMatch',
            'PsBash.WcResult', 'PsBash.FindEntry', 'PsBash.StatEntry', 'PsBash.PsEntry',
            'PsBash.DateOutput', 'PsBash.SeqOutput', 'PsBash.ExprOutput', 'PsBash.DuEntry',
            'PsBash.TreeEntry', 'PsBash.EnvEntry', 'PsBash.RgMatch', 'PsBash.GzipListOutput',
            'PsBash.TarListOutput', 'PsBash.TimeOutput', 'PsBash.WhichOutput', 'PsBash.AliasOutput',
            'PsBash.TrapOutput', 'PsBash.ReadlinkOutput', 'PsBash.MktempOutput', 'PsBash.TypeOutput'
        )) {
        Update-TypeData -TypeName $typeName -MemberName ToString -MemberType ScriptMethod -Value { $this.BashText } -Force
    }
}
