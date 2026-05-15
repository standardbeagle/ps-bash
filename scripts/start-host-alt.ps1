<#
.SYNOPSIS
    Start a ps-bash-host on a private named pipe / unix socket and wire the
    current PowerShell session to it.

.DESCRIPTION
    Bypasses the canonical per-user endpoint (psbash-host-<user> on Windows,
    /tmp/ps-bash/host-<user>.sock on POSIX) by generating a unique endpoint
    name and pointing both the new host and this session at it via the
    PSBASH_IPC_ENDPOINT environment variable.

    Use this when:
      - Stale hosts on the canonical endpoint are blocking new spawns.
      - You want a sandboxed host that won't be reused by other shells.
      - You're reproducing host-lifecycle bugs and need isolation.

    After running, every `ps-bash` invocation in this session uses the
    private host. Other shells still see the canonical host.

.PARAMETER Name
    Endpoint suffix. Defaults to a random 8-char id. Final endpoint becomes
    `psbash-host-alt-<Name>` (named pipe) or
    `<temp>/ps-bash/host-alt-<Name>.sock` (unix socket).

.PARAMETER HostBinary
    Path to ps-bash-host.exe. Defaults to PSBASH_HOST env var, then
    ~/.local/bin/ps-bash-host(.exe), then `ps-bash-host` on PATH.

.PARAMETER PassThru
    Return the launched Process object instead of just printing diagnostics.

.PARAMETER Wait
    Block until you press Enter, then terminate the host. Useful for ad-hoc
    sessions; omit for background daemon-style operation.

.EXAMPLE
    pwsh ./scripts/start-host-alt.ps1
    # Starts a private host, sets PSBASH_IPC_ENDPOINT in this session.
    # Subsequent `ps-bash -c 'echo ok'` uses the private host.

.EXAMPLE
    pwsh ./scripts/start-host-alt.ps1 -Name debug-cd -Wait
    # Foreground host on endpoint `psbash-host-alt-debug-cd`. Ctrl-C / Enter
    # to terminate.
#>
[CmdletBinding()]
param(
    [string]$Name = ([guid]::NewGuid().ToString('N').Substring(0,8)),
    [string]$HostBinary,
    [switch]$PassThru,
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'

function Resolve-HostBinary {
    param([string]$Override)

    if ($Override) {
        if (-not (Test-Path -LiteralPath $Override)) {
            throw "ps-bash-host not found at -HostBinary path: $Override"
        }
        return (Resolve-Path -LiteralPath $Override).Path
    }

    if ($env:PSBASH_HOST -and (Test-Path -LiteralPath $env:PSBASH_HOST)) {
        return (Resolve-Path -LiteralPath $env:PSBASH_HOST).Path
    }

    $exeSuffix = if ($IsWindows -or $env:OS -eq 'Windows_NT') { '.exe' } else { '' }
    $localBin = Join-Path $HOME ".local/bin/ps-bash-host$exeSuffix"
    if (Test-Path -LiteralPath $localBin) {
        return (Resolve-Path -LiteralPath $localBin).Path
    }

    $cmd = Get-Command "ps-bash-host$exeSuffix" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw "Could not locate ps-bash-host. Pass -HostBinary, set PSBASH_HOST, or install to ~/.local/bin."
}

function Resolve-EndpointSpec {
    param([string]$Suffix)

    $isWin = $IsWindows -or $env:OS -eq 'Windows_NT'
    # Mirror IpcTransportFactory: unix sockets supported on Win build >= 17063,
    # but we prefer named pipes on Windows because they're the canonical scheme.
    if ($isWin) {
        return @{ Scheme = 'pipe'; Endpoint = "psbash-host-alt-$Suffix" }
    }

    $sockDir = Join-Path ([System.IO.Path]::GetTempPath()) 'ps-bash'
    New-Item -ItemType Directory -Path $sockDir -Force | Out-Null
    return @{ Scheme = 'unix'; Endpoint = (Join-Path $sockDir "host-alt-$Suffix.sock") }
}

function Test-HostHealthy {
    param([string]$Scheme, [string]$Endpoint, [int]$TimeoutMs = 250)

    try {
        if ($Scheme -eq 'pipe') {
            $client = [System.IO.Pipes.NamedPipeClientStream]::new(
                '.', $Endpoint,
                [System.IO.Pipes.PipeDirection]::InOut,
                [System.IO.Pipes.PipeOptions]::Asynchronous)
            try { $client.Connect($TimeoutMs); return $true }
            finally { $client.Dispose() }
        }
        else {
            # AF_UNIX probe — try to connect via Socket
            $sock = [System.Net.Sockets.Socket]::new(
                [System.Net.Sockets.AddressFamily]::Unix,
                [System.Net.Sockets.SocketType]::Stream,
                [System.Net.Sockets.ProtocolType]::Unspecified)
            try {
                $ep = [System.Net.Sockets.UnixDomainSocketEndPoint]::new($Endpoint)
                $task = $sock.ConnectAsync($ep)
                if ($task.Wait($TimeoutMs)) { return $true }
                return $false
            }
            catch { return $false }
            finally { $sock.Dispose() }
        }
    }
    catch {
        return $false
    }
}

# Resolve everything before launching so errors surface early.
$resolvedHost = Resolve-HostBinary -Override $HostBinary
$endpoint     = Resolve-EndpointSpec -Suffix $Name
$spec         = "$($endpoint.Scheme):$($endpoint.Endpoint)"

Write-Host "ps-bash-host:    $resolvedHost"
Write-Host "Endpoint spec:   $spec"
Write-Host ""

# Refuse to start a second host on the same endpoint — exactly the bug class
# the alternate endpoint is meant to avoid.
if (Test-HostHealthy -Scheme $endpoint.Scheme -Endpoint $endpoint.Endpoint) {
    throw "An existing host is already bound to '$spec'. Pick a different -Name."
}

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName        = $resolvedHost
$psi.UseShellExecute = $false
$psi.CreateNoWindow  = -not $Wait
$psi.ArgumentList.Add("--ipc-endpoint=$spec")

$proc = [System.Diagnostics.Process]::Start($psi)
if (-not $proc) { throw "Process.Start returned null for $resolvedHost" }

# Poll for readiness up to PSBASH_TIMEOUT (default 20s).
$timeoutSec = if ($env:PSBASH_TIMEOUT -and [int]::TryParse($env:PSBASH_TIMEOUT, [ref]$null)) {
    [int]$env:PSBASH_TIMEOUT
} else { 20 }
$deadline = (Get-Date).AddSeconds($timeoutSec)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    if ($proc.HasExited) {
        throw "ps-bash-host exited prematurely (code $($proc.ExitCode)) before binding."
    }
    if (Test-HostHealthy -Scheme $endpoint.Scheme -Endpoint $endpoint.Endpoint) {
        $healthy = $true; break
    }
    Start-Sleep -Milliseconds 200
}
if (-not $healthy) {
    try { if (-not $proc.HasExited) { $proc.Kill($true) } } catch { }
    throw "ps-bash-host did not accept connections within ${timeoutSec}s on '$spec'."
}

# Point this session at the new host. Note: this only affects the current PS
# process; child processes inherit, parent processes do not.
$env:PSBASH_IPC_ENDPOINT = $spec

Write-Host "Host PID:        $($proc.Id)"
Write-Host "PSBASH_IPC_ENDPOINT set for this session."
Write-Host ""
Write-Host "Test it:"
Write-Host "  ps-bash -c 'echo ok'"
Write-Host ""
Write-Host "Stop it:"
Write-Host "  Stop-Process -Id $($proc.Id)"

if ($Wait) {
    Write-Host ""
    Write-Host "Press Enter to terminate the host..."
    [void][System.Console]::ReadLine()
    try { if (-not $proc.HasExited) { $proc.Kill($true) } } catch { }
    Remove-Item Env:PSBASH_IPC_ENDPOINT -ErrorAction SilentlyContinue
    Write-Host "Host terminated."
}

if ($PassThru) { return $proc }
