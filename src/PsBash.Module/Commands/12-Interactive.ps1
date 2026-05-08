# --- Background Process Support ---

$script:BashBgPids = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$global:BashBgLastPid = $null
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

    if (-not ('PsBash.BackgroundOutputForwarder' -as [type])) {
        Add-Type -TypeDefinition @'
namespace PsBash {
    public static class BackgroundOutputForwarder {
        public static void WriteOutput(object sender, System.Diagnostics.DataReceivedEventArgs e) {
            if (e.Data != null) { System.Console.WriteLine(e.Data); }
        }

        public static void WriteError(object sender, System.Diagnostics.DataReceivedEventArgs e) {
            if (e.Data != null) { System.Console.Error.WriteLine(e.Data); }
        }
    }
}
'@
    }

    $pwshPath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName

    # Ensure the child process can resolve Invoke-Bash* commands
    $modulePath = $MyInvocation.MyCommand.Module.Path
    if (-not $modulePath) {
        $modulePath = Join-Path $PSScriptRoot 'PsBash.psd1'
    }
    $childScript = "`$WarningPreference = 'SilentlyContinue'; Import-Module '$modulePath' -Force -WarningAction SilentlyContinue; & { $Command }"
    $encodedCmd = [Convert]::ToBase64String(
        [System.Text.Encoding]::Unicode.GetBytes($childScript)
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $pwshPath
    $psi.Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand $encodedCmd"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.add_OutputDataReceived([System.Diagnostics.DataReceivedEventHandler][PsBash.BackgroundOutputForwarder]::WriteOutput)
    $proc.add_ErrorDataReceived([System.Diagnostics.DataReceivedEventHandler][PsBash.BackgroundOutputForwarder]::WriteError)
    [void]$proc.BeginOutputReadLine()
    [void]$proc.BeginErrorReadLine()

    $script:BashBgPids.Add($proc)
    $global:BashBgLastPid = $proc.Id
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
            if ($idx -ge 0 -and $idx -lt $script:BashBgPids.Count) {
                $target = @($script:BashBgPids)[$idx]
            }
        } else {
            $pid = $jobNum -replace '^%', ''
            if ([int]::TryParse($pid, [ref]$null)) {
                $target = @($script:BashBgPids) | Where-Object { $_.Id -eq [int]$pid } | Select-Object -First 1
            }
        }
    } else {
        $running = @($script:BashBgPids | Where-Object { -not $_.HasExited })
        if ($running.Count -gt 0) {
            $target = $running[-1]
        }
    }

    if ($null -eq $target) {
        Write-BashError -Message 'fg: no current job'
        return
    }

    if ($target.HasExited) {
        Write-Host "[$($target.Id)] Done`t$($target.ProcessName)"
        [void]$script:BashBgPids.Remove($target)
        return
    }

    Write-Host "$($target.ProcessName) (PID $($target.Id))"
    $target.WaitForExit()
    [void]$script:BashBgPids.Remove($target)
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

function Invoke-BashRealpath {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'realpath' }
    foreach ($path in $Arguments) {
        if ($path.StartsWith('-')) { continue }
        try {
            $resolved = Resolve-Path -Path $path -ErrorAction Stop
            $full = $resolved.Path
        } catch {
            $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
        }
        Emit-BashLine -Text $full
    }
}

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

function Invoke-BashYes {
    [OutputType('PsBash.TextOutput')]
    param()
    $Arguments = [string[]]$args
    if ($Arguments -contains '--help') { return Show-BashHelp 'yes' }

    $text = if ($Arguments.Count -gt 0) { $Arguments -join ' ' } else { 'y' }
    while ($true) {
        New-BashObject -BashText $text -TypeName 'PsBash.TextOutput'
    }
}

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
