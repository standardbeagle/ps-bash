<#
.SYNOPSIS
  Runs a command with every process it spawns capped to a per-process committed-memory limit
  (Windows Job Object, JOB_OBJECT_LIMIT_PROCESS_MEMORY) - a constrained-memory environment without a container.

.EXAMPLE
  pwsh -NoProfile -File scripts/run-memcapped.ps1 -LimitMB 2048 -Command 'tman test-proj src/PsBash.Differential.Tests'

.NOTES
  The wrapper assigns ITSELF to the job, so children inherit it (nested jobs need Windows 8+). A child that
  asks for more than the limit gets an allocation failure (OutOfMemory), which is what a small CI box does.
  KILL_ON_JOB_CLOSE reaps leaked hosts when the wrapper exits. Quote any `--filter` containing `|`.
#>
param(
    [int]$LimitMB = 2048,
    [Parameter(Mandatory)][string]$Command
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Job Objects are Windows-only; on Linux use a cgroup (systemd-run --user -p MemoryMax=...).' }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PsBashJobCap {
    [StructLayout(LayoutKind.Sequential)] struct BASIC { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinWs, MaxWs; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] struct IO { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)] struct EXTENDED { public BASIC Basic; public IO Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateJobObjectW(IntPtr a, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, ref EXTENDED info, int len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    const uint PROCESS_MEMORY = 0x100, KILL_ON_CLOSE = 0x2000;
    public static IntPtr CapSelf(ulong bytes) {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var info = new EXTENDED();
        info.Basic.LimitFlags = PROCESS_MEMORY | KILL_ON_CLOSE;
        info.ProcessMemoryLimit = (UIntPtr)bytes;
        if (!SetInformationJobObject(job, 9, ref info, Marshal.SizeOf<EXTENDED>())) throw new System.ComponentModel.Win32Exception();
        if (!AssignProcessToJobObject(job, GetCurrentProcess())) throw new System.ComponentModel.Win32Exception();
        return job;
    }
}
'@

$job = [PsBashJobCap]::CapSelf([uint64]$LimitMB * 1MB)
Write-Host "run-memcapped: every process capped at $LimitMB MB committed; running: $Command"
Invoke-Expression $Command
exit $LASTEXITCODE
