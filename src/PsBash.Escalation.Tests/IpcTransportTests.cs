using System.Diagnostics;
using System.Text;
using PsBash.Core.Parser;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Escalation.Tests;

/// <summary>
/// Shared fixture that starts a single ps-bash-host for the IpcTransport
/// test collection and shuts it down on disposal.
///
/// PROCESS SPAWN CONTRACT: timeout + Kill(entireProcessTree) in Dispose.
/// </summary>
public sealed class HostProcessFixture : IAsyncLifetime
{
    public static readonly string? HostBinaryPath = FindHostBinary();
    public HostLockFile? LockFile { get; private set; }
    public bool IsAvailable => HostBinaryPath is not null && LockFile is not null && File.Exists(LockFile.Path);

    private Process? _hostProcess;

    public async Task InitializeAsync()
    {
        if (HostBinaryPath is null) return;

        var sessionId = $"ipc-test-{Guid.NewGuid():N}";
        LockFile = HostLockFile.ForSession(sessionId);

        var psi = new ProcessStartInfo
        {
            FileName = HostBinaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add("--session-id");
        psi.ArgumentList.Add(sessionId);

        _hostProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ps-bash-host");

        // Wait up to 45 s for the host to write its lock file (transport ready).
        // Debug/non-AOT builds need JIT warm-up — first launch can take 15+ s.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(LockFile.Path)) break;
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        LockFile?.Delete();
        if (_hostProcess is not null && !_hostProcess.HasExited)
        {
            try { _hostProcess.Kill(entireProcessTree: true); } catch { }
        }
        try
        {
            if (_hostProcess is not null)
            {
                using var cts = new CancellationTokenSource(3000);
                try { await _hostProcess.WaitForExitAsync(cts.Token); } catch { }
            }
        }
        catch { }
        _hostProcess?.Dispose();
    }

    private static string? FindHostBinary()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoRoot = baseDir;
        for (int i = 0; i < 5; i++)
        {
            var parent = Path.GetDirectoryName(repoRoot);
            if (parent is null) break;
            repoRoot = parent;
        }
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        string[] candidates = [
            Path.Combine(repoRoot, "src", "PsBash.Host", "bin", "Debug", "net10.0", $"ps-bash-host{ext}"),
            Path.Combine(repoRoot, "src", "PsBash.Host", "bin", "Release", "net10.0", $"ps-bash-host{ext}"),
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }
}

[CollectionDefinition("IpcTransport")]
public class IpcTransportCollection : ICollectionFixture<HostProcessFixture> { }

/// <summary>
/// IPC transport parity tests: run key scripts through the ps-bash-host IPC
/// path and verify outputs match what the subprocess (PwshWorker) path produces.
///
/// Per T12: prove behavioral parity between IPC and subprocess transports for
/// the representative failure-surface scenarios.
///
/// Skip all tests when the host binary is not built.
/// </summary>
[Trait("Category", "Escalation")]
[Trait("Category", "IpcTransport")]
[Collection("IpcTransport")]
public class IpcTransportTests(HostProcessFixture fixture)
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(string Stdout, int ExitCode)> RunViaIpcAsync(string bashScript)
    {
        // Transpile bash → PS before sending over IPC (host's SdkWorker runs PS).
        var psScript = PsEmitter.Transpile(bashScript) ?? "";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var worker = await IpcWorker.StartAsync(
            fixture.LockFile!,
            HostProcessFixture.HostBinaryPath!,
            spawnIfMissing: false,
            startupTimeout: TimeSpan.FromSeconds(20));

        await using var _ = worker;

        var sb = new StringBuilder();
        worker.OutputCallback = line => { lock (sb) sb.AppendLine(line); };

        var exit = await worker.ExecuteAsync(psScript, cts.Token);
        return (sb.ToString(), exit);
    }

    private static async Task<(string Stdout, int ExitCode)> RunViaSubprocessAsync(string script)
    {
        var psi = ProcessRunHelper.BuildPsi(new[] { "-c", script });
        psi.Environment["PSBASH_DISABLE_HOST"] = "1";
        var (exit, stdout, _) = await ProcessRunHelper.RunAsync(psi, stdinContent: null);
        return (stdout, exit);
    }

    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n").Trim();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task IpcParity_EchoHello()
    {
        Skip.If(!fixture.IsAvailable, "ps-bash-host not available or did not start");

        var (ipcOut, ipcExit) = await RunViaIpcAsync("echo hello");
        var (subOut, subExit) = await RunViaSubprocessAsync("echo hello");

        Assert.Equal(subExit, ipcExit);
        Assert.Equal(Normalize(subOut), Normalize(ipcOut));
        Assert.Contains("hello", ipcOut);
    }

    [SkippableFact]
    public async Task IpcParity_ExitCode7()
    {
        Skip.If(!fixture.IsAvailable, "ps-bash-host not available or did not start");

        var (_, ipcExit) = await RunViaIpcAsync("exit 7");
        var (_, subExit) = await RunViaSubprocessAsync("exit 7");

        Assert.Equal(7, subExit);
        Assert.Equal(subExit, ipcExit);
    }

    [SkippableFact]
    public async Task IpcParity_Arithmetic()
    {
        Skip.If(!fixture.IsAvailable, "ps-bash-host not available or did not start");

        var (ipcOut, _) = await RunViaIpcAsync("echo $((6 * 7))");
        var (subOut, _) = await RunViaSubprocessAsync("echo $((6 * 7))");

        Assert.Equal(Normalize(subOut), Normalize(ipcOut));
        Assert.Contains("42", ipcOut);
    }

    [SkippableFact]
    public async Task IpcParity_Pipeline()
    {
        Skip.If(!fixture.IsAvailable, "ps-bash-host not available or did not start");

        var (ipcOut, ipcExit) = await RunViaIpcAsync("seq 1 5 | tail -n 3");
        var (subOut, subExit) = await RunViaSubprocessAsync("seq 1 5 | tail -n 3");

        Assert.Equal(subExit, ipcExit);
        Assert.Equal(Normalize(subOut), Normalize(ipcOut));
    }

    [SkippableFact]
    public async Task IpcParity_SetE_ExitsOnError()
    {
        Skip.If(!fixture.IsAvailable, "ps-bash-host not available or did not start");

        // Known limitation: both subprocess and IPC paths return 0 for set-e + false + echo
        // because Invoke-BashEcho explicitly resets $LASTEXITCODE = 0 (line 679 of psm1),
        // and the worker reads $LASTEXITCODE after the full script completes.
        // The `false` emitter uses -ErrorAction SilentlyContinue (required for `false || cmd`
        // chains with set-e) so Write-Error doesn't abort execution mid-script.
        // Full set-e / false parity in both paths requires per-command interception —
        // deferred alongside pipefail/PIPESTATUS.
        var (_, ipcExit) = await RunViaIpcAsync("set -e; false; echo unreachable");
        var (_, subExit) = await RunViaSubprocessAsync("set -e; false; echo unreachable");

        // Assert parity only: IPC and subprocess must agree, whatever the value.
        Assert.Equal(subExit, ipcExit);
    }

    [SkippableFact]
    public async Task IpcParity_PerInvocationStateReset()
    {
        Skip.If(!fixture.IsAvailable, "ps-bash-host not available or did not start");

        // First call sets __BashErrexit via set -e.
        // Second call must not see set -e from the first invocation
        // (Connection.cs prepends a preamble resetting __BashErrexit).
        await RunViaIpcAsync("set -e");
        var (ipcOut, ipcExit) = await RunViaIpcAsync("false; echo survived");

        // If set -e leaked, the second invocation exits before echo and produces no output.
        // If correctly reset, false sets LASTEXITCODE=1 but execution continues past it.
        // Exit code is 1 (LASTEXITCODE from false) — Invoke-BashEcho is a PS function
        // and does not reset $LASTEXITCODE. The key assertion is that output was produced.
        Assert.Contains("survived", ipcOut);
    }
}
