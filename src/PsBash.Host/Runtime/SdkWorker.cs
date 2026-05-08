using System.Management.Automation;
using System.Text.RegularExpressions;
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
            if (TrySetLocation(command, out var setLocationExitCode))
                return setLocationExitCode;

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
            if (IsCurrentLocationQuery(expression))
                return _ps.Runspace.SessionStateProxy.Path.CurrentFileSystemLocation?.ProviderPath ?? "";

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
        // Partial-line buffer: accumulates NoTrailingNewline fragments (echo -n, printf "%d " x)
        // until a full line arrives, matching the pwsh worker loop's Console.Out.Write / WriteLine split.
        var partialBuffer = new System.Text.StringBuilder();
        // Non-BashText typed PSObjects (FileInfo, DirectoryInfo, PSCustomObject, etc.) are
        // collected during Invoke() and formatted after it returns (property-enumeration for
        // PSCustomObject, BaseObject.ToString() for others). Cannot format during DataAdded
        // because the runspace is occupied by Invoke().
        var deferredObjects = new System.Collections.Generic.List<System.Management.Automation.PSObject>();
        outputCollection.DataAdded += (sender, e) =>
        {
            var col = (System.Management.Automation.PSDataCollection<System.Management.Automation.PSObject>)sender!;
            var pso = col[e.Index];
            // Three output cases:
            // 1. BashText property present: PsBash typed object (LsEntry, CatLine, etc.) — extract BashText.
            // 2. BaseObject is string: New-BashObject fast-path (echo, printf, Write-Output).
            // 3. Other typed PSObject (FileInfo, PSCustomObject): defer to property-enumeration fallback.
            // PSObject.ToString() returns "@{...}" for PSCustomObjects from C# — does NOT invoke
            // the PS ScriptMethod override — so we must never call it for path 1 or 3.
            var bashTextProp = pso?.Properties["BashText"];
            string? lineText = null;
            bool isPartial = false;

            if (bashTextProp != null)
            {
                // Slow-path BashObject (NoTrailingNewline or non-TextOutput typed objects).
                lineText = (bashTextProp.Value?.ToString() ?? "").TrimEnd('\r', '\n');
                var noNlProp = pso!.Properties["NoTrailingNewline"];
                isPartial = noNlProp?.Value is bool b && b;
            }
            else if (pso?.BaseObject is string s)
            {
                // Fast-path: New-BashObject returns [string] for plain text output.
                // This covers Invoke-BashEcho, Invoke-BashPrintf, and any Write-Output.
                lineText = s.TrimEnd('\r', '\n');
            }
            else if (pso != null)
            {
                // Non-BashText typed PSObject (FileInfo, PSCustomObject, etc.): defer to
                // property-enumeration fallback after Invoke() completes.
                deferredObjects.Add(pso);
                return;
            }

            if (lineText is null) return;

            if (isPartial)
            {
                partialBuffer.Append(lineText);
                return;
            }

            var line = partialBuffer.Length > 0 ? partialBuffer.Append(lineText).ToString() : lineText;
            partialBuffer.Clear();
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

        // Flush non-BashText typed PSObjects (PSCustomObject, FileInfo, etc.) collected during
        // Invoke(). Out-String is unavailable in the SDK context (module manifest not on path),
        // so use property-enumeration: PSCustomObject → "Name : Value" per property (Format-List
        // style); all other typed objects → BaseObject.ToString() (full path for FileInfo, etc.).
        foreach (var pso in deferredObjects)
        {
            if (pso?.BaseObject is System.Management.Automation.PSCustomObject)
            {
                foreach (var prop in pso.Properties)
                {
                    try
                    {
                        var propLine = $"{prop.Name} : {prop.Value}";
                        if (output is not null) output(propLine);
                        else Console.WriteLine(propLine);
                    }
                    catch { break; }
                }
            }
            else
            {
                var text = pso?.BaseObject?.ToString() ?? pso?.ToString() ?? "";
                if (string.IsNullOrEmpty(text)) continue;
                try { if (output is not null) output(text); else Console.WriteLine(text); }
                catch { break; }
            }
        }

        // Flush any trailing partial-line fragment (e.g. printf with no final newline).
        if (partialBuffer.Length > 0)
        {
            var remaining = partialBuffer.ToString();
            try { if (output is not null) output(remaining); else Console.WriteLine(remaining); }
            catch { }
        }

        // exit N calls PSHost.SetShouldExit(N) — check before processing output.
        if (_host.ShouldExit)
        {
            _ps.Commands.Clear();
            return _host.ExitCode;
        }

        if (_ps.InvocationStateInfo.State == System.Management.Automation.PSInvocationState.Stopped)
            return 130;

        SyncDotNetCurrentDirectory();

        // Mirror ps-bash-worker.ps1: use $LASTEXITCODE, not HadErrors.
        // HadErrors fires on Write-Error even with -ErrorAction SilentlyContinue;
        // $LASTEXITCODE only changes when an external command or explicit `exit N` runs.
        var lec = _ps.Runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
        if (lec is System.Management.Automation.PSObject lecPso) lec = lecPso.BaseObject;
        if (lec is int exitInt) return exitInt;
        if (lec is long exitLong) return (int)exitLong;
        return 0;
    }

    private void SyncDotNetCurrentDirectory()
    {
        var path = _ps.Runspace.SessionStateProxy.Path.CurrentFileSystemLocation?.ProviderPath;
        if (!string.IsNullOrEmpty(path))
            Environment.CurrentDirectory = path;
    }

    private bool TrySetLocation(string command, out int exitCode)
    {
        exitCode = 0;
        var match = Regex.Match(
            command.Trim(),
            @"^Set-Location(?:\s+-Path)?\s+(?:'((?:''|[^'])*)'|""([^""]*)""|(.+))$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var path = match.Groups[1].Success
            ? match.Groups[1].Value.Replace("''", "'")
            : match.Groups[2].Success
                ? match.Groups[2].Value
                : match.Groups[3].Value.Trim();

        try
        {
            _ps.Runspace.SessionStateProxy.Path.SetLocation(path);
            SyncDotNetCurrentDirectory();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ps-bash: {ex.Message}");
            exitCode = 1;
            return true;
        }
    }

    private static bool IsCurrentLocationQuery(string expression)
        => expression.Trim() is "(Get-Location).Path" or "Get-Location" or "$PWD";

    private static int UnwrapExitCode(object? arg)
    {
        if (arg is System.Management.Automation.PSObject pso) arg = pso.BaseObject;
        if (arg is int code) return code;
        if (arg is long l) return (int)l;
        if (arg != null && int.TryParse(arg.ToString(), out int n)) return n;
        return 0;
    }

    private const int MaxQueryBytes = 4 * 1024 * 1024; // 4 MB — QueryAsync is for small internal expressions

    private string RunCommandCollect(string expression)
    {
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        _ps.AddScript(expression);

        var parts = new System.Text.StringBuilder();
        var outputCollection = new System.Management.Automation.PSDataCollection<System.Management.Automation.PSObject>();
        outputCollection.DataAdded += (sender, e) =>
        {
            var col = (System.Management.Automation.PSDataCollection<System.Management.Automation.PSObject>)sender!;
            var pso = col[e.Index];
            var bashTextProp = pso?.Properties["BashText"];
            string line;
            if (bashTextProp != null)
                line = (bashTextProp.Value?.ToString() ?? "").TrimEnd('\r', '\n');
            else
                line = pso?.ToString() ?? "";
            if (parts.Length + line.Length > MaxQueryBytes)
            {
                _ps.Stop();
                return;
            }
            if (parts.Length > 0) parts.Append('\n');
            parts.Append(line);
        };
        _ps.Invoke(null, outputCollection);
        return parts.ToString();
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
