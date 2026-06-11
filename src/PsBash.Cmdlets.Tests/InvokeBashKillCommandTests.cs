using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Regression tests for the psm1 <c>Invoke-BashKill</c>.
///
/// HEADLINE REGRESSION (Directive 13): the function looped with
/// <c>foreach ($pid in $pids)</c>, but <c>$PID</c> is a read-only automatic
/// variable — every invocation threw "Cannot overwrite variable PID because it
/// is read-only or constant" before reaching <c>Stop-Process</c>, so kill never
/// killed anything. The loop variable is now <c>$procId</c>.
/// <see cref="Kill_RealProcess_ActuallyTerminatesIt"/> is the end-to-end guard:
/// it spawns a real OS process and asserts the process is gone afterward — the
/// review found kill had effectively zero behavioral coverage, which is how a
/// 100%-reproducible crash survived in-tree.
/// </summary>
public class InvokeBashKillCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashKillCommandTests(SharedPwshFixture fixture) => _fixture = fixture;

    private void Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
    }

    private string[] RunBashText(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result
            .Select(o =>
            {
                var prop = o?.Properties["BashText"];
                return (prop != null ? prop.Value?.ToString() ?? "" : o?.ToString() ?? "")
                    .TrimEnd('\n', '\r');
            })
            .ToArray();
    }

    private string[] RunErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        return errs;
    }

    [Fact]
    public void Kill_RealProcess_ActuallyTerminatesIt()
    {
        using var proc = StartLongLivedProcess();
        Skip.If(proc is null, "no spawnable long-lived process host (pwsh/powershell) available");
        var pid = proc!.Id;
        Assert.False(proc.HasExited, "precondition: spawned process must be running");

        Run($"Invoke-BashKill {pid}");

        Assert.True(proc.WaitForExit(10_000),
            "Invoke-BashKill <pid> must terminate the process (regression: $PID loop-var crash)");
    }

    [Fact]
    public void Kill_NamedSignalKILL_TerminatesProcess()
    {
        using var proc = StartLongLivedProcess();
        Skip.If(proc is null, "no spawnable process host available");
        var pid = proc!.Id;

        // -KILL used to fall through int::TryParse and be silently dropped; the
        // signal token now routes to Stop-Process -Force.
        Run($"Invoke-BashKill -KILL {pid}");

        Assert.True(proc.WaitForExit(10_000), "kill -KILL <pid> must terminate the process");
    }

    [Fact]
    public void Kill_NumericSignal9_TerminatesProcess()
    {
        using var proc = StartLongLivedProcess();
        Skip.If(proc is null, "no spawnable process host available");
        var pid = proc!.Id;

        Run($"Invoke-BashKill -9 {pid}");

        Assert.True(proc.WaitForExit(10_000), "kill -9 <pid> must terminate the process");
    }

    [Fact]
    public void Kill_NoArguments_EmitsUsageError()
    {
        var errs = RunErrors("Invoke-BashKill");
        Assert.Contains(errs, m => m.Contains("usage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Kill_NonexistentPid_EmitsNoSuchProcess()
    {
        // A PID that is overwhelmingly unlikely to exist.
        var errs = RunErrors("Invoke-BashKill 2147483646");
        Assert.Contains(errs, m => m.Contains("No such process", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Kill_ListSignals_IncludesCommonNames()
    {
        var lines = RunBashText("Invoke-BashKill -l");
        var all = string.Join("\n", lines);
        Assert.Contains("SIGKILL", all, StringComparison.Ordinal);
        Assert.Contains("SIGTERM", all, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spawn a process that stays alive for ~60s and is cleanly killable.
    /// Uses pwsh/powershell (guaranteed present where these PowerShell-hosting
    /// tests run). Returns null if none can be started, so callers Skip.
    /// </summary>
    private static Process? StartLongLivedProcess()
    {
        foreach (var exe in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("Start-Sleep -Seconds 60");
                var p = Process.Start(psi);
                if (p is not null && !p.HasExited)
                    return p;
                p?.Dispose();
            }
            catch (Win32Exception) { /* exe not found — try next */ }
            catch (PlatformNotSupportedException) { }
        }
        return null;
    }
}
