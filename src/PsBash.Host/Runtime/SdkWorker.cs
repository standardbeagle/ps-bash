using System.Management.Automation;
using PsBash.Core.Runtime;

namespace PsBash.Host.Runtime;

/// <summary>
/// IWorker implementation backed by an in-process PowerShell SDK runspace.
/// Executes commands against a shared <see cref="SdkRunspace"/> without
/// spawning an external pwsh process.
/// </summary>
public sealed class SdkWorker : IWorker
{
    private readonly SdkRunspace _sdkRunspace;
    // Single PowerShell instance bound to the runspace, serialised by _lock.
    // Creating a new ps.Invoke() per call on different PowerShell instances
    // attached to the same runspace produces empty pipeline output — using a
    // persistent instance (same pattern as PwshTestFixture) avoids the issue.
    private readonly PowerShell _ps;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _disposed;

    public Action<string>? OutputCallback { get; set; }

    public bool HasExited => _disposed != 0;

    private readonly ExitTrackingHost _host;

    private SdkWorker(SdkRunspace runspace)
    {
        _sdkRunspace = runspace;
        _host = runspace.Host;
        _ps = PowerShell.Create();
        _ps.Runspace = runspace.Runspace;
    }

    public static SdkWorker Create()
    {
        var runspace = SdkRunspace.Create();
        return new SdkWorker(runspace);
    }

    public async Task<int> ExecuteAsync(string command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var callback = OutputCallback;
        await _lock.WaitAsync(ct);
        try
        {
            return await Task.Run(() => RunCommand(command, callback), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> QueryAsync(string expression, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(ct);
        try
        {
            return await Task.Run(() => RunCommandCollect(expression), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Used by Connection to pass the callback directly (thread-safe — bypasses the shared property).
    internal async Task<int> ExecuteWithOutputAsync(string command, Action<string>? output, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(ct);
        try
        {
            // When ct fires mid-command (e.g. parent-death watcher), stop the PS
            // pipeline so Invoke() returns instead of blocking indefinitely.
            using var stopReg = ct.Register(() => _ps.Stop());
            return await Task.Run(() => RunCommand(command, output), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private int RunCommand(string command, Action<string>? output)
    {
        _host.Reset();
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        _ps.AddScript(command);

        // Stream output via DataAdded so results are delivered as they arrive rather
        // than buffering the entire Collection<PSObject> in RAM first.
        var outputCollection = new System.Management.Automation.PSDataCollection<System.Management.Automation.PSObject>();
        outputCollection.DataAdded += (sender, e) =>
        {
            var col = (System.Management.Automation.PSDataCollection<System.Management.Automation.PSObject>)sender!;
            var line = col[e.Index]?.ToString() ?? "";
            try
            {
                if (output is not null) output(line);
                else Console.WriteLine(line);
            }
            catch
            {
                // Output callback failed (e.g. IPC stream closed). Stop the pipeline
                // rather than letting DataAdded fire repeatedly into a broken sink.
                _ps.Stop();
            }
        };

        try
        {
            _ps.Invoke(null, outputCollection);
        }
        catch (System.Management.Automation.PipelineStoppedException)
        {
            return 130; // Convention: pipeline stopped (analogous to SIGINT exit code)
        }
        catch (System.Management.Automation.ExitException ex)
        {
            // Defensive: ExitException may be thrown in some PS SDK configurations.
            _ps.Commands.Clear();
            return UnwrapExitCode(ex.Argument);
        }
        catch (System.Management.Automation.ParseException ex)
        {
            Console.Error.WriteLine($"ps-bash: parse error: {ex.Message}");
            _ps.Commands.Clear();
            return 1;
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Console.Error.WriteLine($"ps-bash: {ex.Message}");
            _ps.Commands.Clear();
            return 1;
        }

        // exit N calls PSHost.SetShouldExit(N) — check before processing output.
        if (_host.ShouldExit)
        {
            _ps.Commands.Clear();
            return _host.ExitCode;
        }

        if (_ps.InvocationStateInfo.State == System.Management.Automation.PSInvocationState.Stopped)
            return 130;

        // Mirror ps-bash-worker.ps1: use $LASTEXITCODE, not HadErrors.
        // HadErrors fires on Write-Error even with -ErrorAction SilentlyContinue;
        // $LASTEXITCODE only changes when an external command or explicit `exit N` runs.
        var lec = _ps.Runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
        if (lec is System.Management.Automation.PSObject lecPso) lec = lecPso.BaseObject;
        if (lec is int exitInt) return exitInt;
        if (lec is long exitLong) return (int)exitLong;
        return 0;
    }

    private static int UnwrapExitCode(object? arg)
    {
        if (arg is System.Management.Automation.PSObject pso) arg = pso.BaseObject;
        if (arg is int code) return code;
        if (arg is long l) return (int)l;
        if (arg != null && int.TryParse(arg.ToString(), out int n)) return n;
        return 0;
    }

    private string RunCommandCollect(string expression)
    {
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        _ps.AddScript(expression);

        try
        {
            var results = _ps.Invoke();
            return string.Join('\n', results.Select(r => r?.ToString() ?? ""));
        }
        catch (System.Management.Automation.PipelineStoppedException)
        {
            return "";
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ps.Dispose();
            return _sdkRunspace.DisposeAsync();
        }
        return ValueTask.CompletedTask;
    }
}
