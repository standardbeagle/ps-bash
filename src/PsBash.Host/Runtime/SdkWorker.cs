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

        // Forward formatted Out-Default lines to the same callback the output
        // stream uses. PowerShell routes pipeline output that doesn't carry a
        // BashText property — i.e. native cmdlet PSObjects from
        // `Get-PnpDevice | Select FriendlyName, Status` and similar — through
        // Out-Default, which renders objects via the formatter and writes the
        // resulting strings to PSHostUserInterface.Write/WriteLine. Without a
        // forwarder, ExitTrackingHostUI was a no-op and these lines silently
        // vanished, so the user saw nothing where native pwsh would have shown
        // a formatted table. The unified callback below preserves the existing
        // BashText/string streaming path (still routed via DataAdded → callback
        // below) and adds formatter output to the same sink. The host UI's
        // forwarder is cleared in the finally block to avoid leaking state
        // across invocations of the shared runspace.
        Action<string> deliver = line =>
        {
            try
            {
                if (output is not null) output(line);
                else Console.WriteLine(line);
            }
            catch
            {
                // Output callback failed (e.g. IPC stream closed). Stop the pipeline
                // rather than letting subsequent calls fire into a broken sink.
                _ps.Stop();
            }
        };
        _host.HostUI.SetWriteLineForwarder(deliver);

        // Stream output via DataAdded so results are delivered as they arrive rather
        // than buffering the entire Collection<PSObject> in RAM first.
        var outputCollection = new System.Management.Automation.PSDataCollection<System.Management.Automation.PSObject>();
        // Streaming policy: text-stream items (BashText fast-path strings,
        // typed BashObject CustomControls) deliver immediately as they arrive;
        // raw PSObjects (e.g. native cmdlet output, [PSCustomObject]@{...})
        // are buffered and formatted as a batch at the end of invocation. The
        // batch is flushed when (a) a text item arrives — preserving relative
        // order between formatted blocks and surrounding text; (b) the
        // pipeline ends. Formatting per-item would lose column-width context
        // (the formatter needs to see all rows before computing widths).
        var formatBuffer = new List<PSObject>();
        void FlushFormatBuffer()
        {
            if (formatBuffer.Count == 0) return;
            try
            {
                foreach (var fline in PSObjectFormatter.FormatAsTable(formatBuffer))
                    deliver(fline);
            }
            catch (Exception ex)
            {
                // Defensive: a formatter exception must not blow up the
                // worker. Log and continue with raw ToString fallback so the
                // user still sees something rather than silent loss.
                Console.Error.WriteLine($"ps-bash: formatter error: {ex.Message}");
                foreach (var raw in formatBuffer)
                    deliver(raw?.ToString() ?? "");
            }
            formatBuffer.Clear();
        }

        outputCollection.DataAdded += (sender, e) =>
        {
            var col = (System.Management.Automation.PSDataCollection<System.Management.Automation.PSObject>)sender!;
            var item = col[e.Index];
            if (IsTextStreamItem(item))
            {
                FlushFormatBuffer();
                var line = GetOutputText(item);
                deliver(line);
            }
            else
            {
                formatBuffer.Add(item!);
            }
        };

        try
        {
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

            // Drain any raw PSObjects buffered for batched formatting.
            FlushFormatBuffer();

            // exit N calls PSHost.SetShouldExit(N) — check before processing output.
            if (_host.ShouldExit)
            {
                _ps.Commands.Clear();
                return _host.ExitCode;
            }

            if (_ps.InvocationStateInfo.State == System.Management.Automation.PSInvocationState.Stopped)
                return 130;

            // Match shell launcher semantics: use $LASTEXITCODE, not HadErrors.
            // HadErrors fires on Write-Error even with -ErrorAction SilentlyContinue;
            // $LASTEXITCODE only changes when an external command or explicit `exit N` runs.
            var lec = _ps.Runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
            if (lec is System.Management.Automation.PSObject lecPso) lec = lecPso.BaseObject;
            if (lec is int exitInt) return exitInt;
            if (lec is long exitLong) return (int)exitLong;
            return 0;
        }
        finally
        {
            // Detach the forwarder so a stray Out-Default call from another
            // worker invocation can't leak into a previous caller's output sink.
            _host.HostUI.SetWriteLineForwarder(null);
        }
    }

    private static string GetOutputText(PSObject? item)
    {
        if (item is null)
            return "";

        var bashText = item.Properties["BashText"]?.Value;
        if (bashText is not null)
            return bashText.ToString() ?? "";

        return item.BaseObject is string s ? s : item.ToString();
    }

    /// <summary>
    /// True for items that should bypass the formatter and stream directly.
    /// Plain strings (the New-BashObject fast path) and BashText-bearing
    /// PSCustomObjects (the slow path with PsBash.Format.ps1xml CustomControl)
    /// are already in render-ready form. Everything else — native cmdlet
    /// output, bare [PSCustomObject]@{...} from a script — needs the
    /// table-formatter pass to gain column layout.
    /// </summary>
    private static bool IsTextStreamItem(PSObject? item)
    {
        if (item is null) return true;
        if (item.BaseObject is string) return true;
        if (item.Properties["BashText"] is not null) return true;
        return false;
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
        // QueryAsync's primary use is ad-hoc value reads (e.g. `(Get-Location).Path`)
        // where the caller wants the .NET object's stringified form, not a formatter
        // table. Keep the simple ToString path here; the formatter wrapping is only
        // needed on ExecuteAsync where output is meant to render as it would in pwsh.
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
