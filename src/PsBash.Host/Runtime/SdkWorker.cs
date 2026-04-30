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

    private SdkWorker(SdkRunspace runspace)
    {
        _sdkRunspace = runspace;
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
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        _ps.AddScript(command);

        System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> results;
        try
        {
            results = _ps.Invoke();
        }
        catch (System.Management.Automation.PipelineStoppedException)
        {
            return 130; // Convention: pipeline stopped (analogous to SIGINT exit code)
        }

        foreach (var r in results)
        {
            var line = r?.ToString() ?? "";
            if (output is not null) output(line);
            else Console.WriteLine(line);
        }

        if (_ps.InvocationStateInfo.State == System.Management.Automation.PSInvocationState.Stopped)
            return 130;
        return _ps.HadErrors ? 1 : 0;
    }

    private string RunCommandCollect(string expression)
    {
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        _ps.AddScript(expression);

        var results = _ps.Invoke();
        return string.Join("\n", results.Select(r => r?.ToString() ?? ""));
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
