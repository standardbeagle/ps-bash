using System.Management.Automation;
using System.Management.Automation.Runspaces;
using PsBash.Core.Runtime;

namespace PsBash.Host.Runtime;

/// <summary>
/// IWorker implementation backed by an in-process PowerShell SDK runspace.
/// Executes commands against a shared <see cref="SdkRunspace"/> without
/// spawning an external pwsh process.
/// </summary>
public sealed class SdkWorker : IWorker, ICompletionWorker
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
            return await Task.Run(() => WithDefaultRunspace(() => RunCommand(command, callback, null)), ct);
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
            return await Task.Run(() => WithDefaultRunspace(() => RunCommandCollect(expression)), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Run PowerShell's completion engine against the live runspace. Uses the runspace-scoped
    /// <see cref="CommandCompletion.CompleteInput(string, int, System.Collections.Hashtable)"/>
    /// overload (it reads <see cref="Runspace.DefaultRunspace"/>, which
    /// <see cref="WithDefaultRunspace{T}"/> pins) under the same lock as command execution, so
    /// the runspace is idle when completion runs. Never throws.
    /// </summary>
    async Task<IReadOnlyList<string>> ICompletionWorker.CompleteInputAsync(
        string input, int cursorIndex, CancellationToken ct)
    {
        if (_disposed != 0 || string.IsNullOrEmpty(input))
        {
            return Array.Empty<string>();
        }

        await _lock.WaitAsync(ct);
        try
        {
            return await Task.Run(() => WithDefaultRunspace<IReadOnlyList<string>>(() =>
            {
                try
                {
                    var completion = CommandCompletion.CompleteInput(input, cursorIndex, options: null);
                    var matches = completion.CompletionMatches;
                    if (matches is null || matches.Count == 0)
                    {
                        return Array.Empty<string>();
                    }

                    var results = new List<string>(matches.Count);
                    foreach (var m in matches)
                    {
                        if (!string.IsNullOrEmpty(m.CompletionText))
                        {
                            results.Add(m.CompletionText);
                        }
                    }

                    return results;
                }
                catch (Exception)
                {
                    // Completion is advisory — a parse/discovery failure must never surface.
                    return Array.Empty<string>();
                }
            }), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Used by Connection to pass the callbacks directly (thread-safe — bypasses the shared property).
    // REFACTOR-4: <paramref name="errorOutput"/> is the host's stderr sink; when
    // non-null, every host stderr write (PowerShell error records, parse/runtime
    // exceptions, the emitter's `cmd >&2` rewrite via $Host.UI.WriteErrorLine)
    // routes there instead of the host's detached fd 2.
    internal async Task<int> ExecuteWithOutputAsync(
        string command,
        Action<string>? output,
        Action<string>? errorOutput,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(ct);
        try
        {
            // When ct fires mid-command (e.g. parent-death watcher), stop the PS
            // pipeline so Invoke() returns instead of blocking indefinitely.
            using var stopReg = ct.Register(() => _ps.Stop());
            return await Task.Run(() => WithDefaultRunspace(() => RunCommand(command, output, errorOutput)), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private T WithDefaultRunspace<T>(Func<T> action)
    {
        var prior = Runspace.DefaultRunspace;
        Runspace.DefaultRunspace = _sdkRunspace.Runspace;
        try
        {
            return action();
        }
        finally
        {
            Runspace.DefaultRunspace = prior;
        }
    }

    private int RunCommand(string command, Action<string>? output, Action<string>? errorOutput)
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
        // Convention: `line` always carries its own trailing newline.
        // GetOutputText appends one for BashText/string items; FlushFormatBufferCore
        // appends one to each formatter row. Use Console.Write here (not WriteLine)
        // so we don't double-newline BashText output — the visible symptom was
        // `ls` rendering a blank line between every entry.
        Action<string> deliver = line =>
        {
            try
            {
                if (output is not null) output(line);
                else Console.Write(line);
            }
            catch
            {
                // Output callback failed (e.g. IPC stream closed). Stop the pipeline
                // rather than letting subsequent calls fire into a broken sink.
                _ps.Stop();
            }
        };
        _host.HostUI.SetWriteLineForwarder(deliver);

        // REFACTOR-4: stderr sink. Every host stderr write — PowerShell error
        // records, parse/runtime exception text, and the emitter's `cmd >&2`
        // rewrite (which lands here via $Host.UI.WriteErrorLine →
        // ExitTrackingHostUI.WriteErrorLine → the forwarder set below) — routes
        // through this single sink. When errorOutput is null (the in-process
        // IWorker.ExecuteAsync path) we keep the historical Console.Error
        // fallback. Convention: errorOutput receives a line WITHOUT a trailing
        // newline; the IPC frame writer adds the record boundary.
        Action<string> deliverError = line =>
        {
            try
            {
                if (errorOutput is not null) errorOutput(line);
                else Console.Error.WriteLine(line);
            }
            catch
            {
                // Stderr sink failed (e.g. IPC stream closed). Stop the pipeline
                // rather than firing into a broken sink on every subsequent line.
                _ps.Stop();
            }
        };
        _host.HostUI.SetWriteErrorLineForwarder(errorOutput is not null ? deliverError : null);

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
        var outputLock = new object();
        var processedOutputCount = 0;

        // Render the buffered non-bash PSObjects with PowerShell's own formatting
        // engine (Out-String -Stream) so native cmdlet output gets its registered
        // format.ps1xml views and list/table heuristic — e.g. Test-NetConnection's
        // compact view — exactly like a normal console. Out-String needs the
        // runspace, which is only free at end-of-pipeline; during a mid-stream
        // flush (text interleaved with objects) the runspace is busy, so this
        // returns false and we fall back to the hand-rolled PSObjectFormatter.
        bool TryFormatViaRunspace(List<PSObject> buffer)
        {
            var rs = _ps.Runspace;
            if (rs is null ||
                rs.RunspaceAvailability != System.Management.Automation.Runspaces.RunspaceAvailability.Available)
                return false;
            try
            {
                int width = 200;
                try
                {
                    var w = _host.UI?.RawUI?.BufferSize.Width ?? 0;
                    if (w > 20) width = w;
                }
                catch { /* non-interactive host: keep the 200 default */ }

                using var fmt = System.Management.Automation.PowerShell.Create();
                fmt.Runspace = rs;
                fmt.AddCommand("Out-String")
                   .AddParameter("Stream", true)
                   .AddParameter("Width", width);
                var results = fmt.Invoke(buffer);
                if (fmt.HadErrors) return false;
                foreach (var r in results)
                    deliver((r?.ToString() ?? "") + Environment.NewLine);
                return true;
            }
            catch
            {
                return false;
            }
        }

        void FlushFormatBufferCore()
        {
            if (formatBuffer.Count == 0) return;

            if (TryFormatViaRunspace(formatBuffer))
            {
                formatBuffer.Clear();
                return;
            }

            try
            {
                foreach (var fline in PSObjectFormatter.FormatAsTable(formatBuffer))
                    deliver(fline + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Defensive: a formatter exception must not blow up the
                // worker. Log and continue with raw ToString fallback so the
                // user still sees something rather than silent loss.
                deliverError($"ps-bash: formatter error: {ex.Message}");
                foreach (var raw in formatBuffer)
                    deliver((raw?.ToString() ?? "") + Environment.NewLine);
            }
            formatBuffer.Clear();
        }

        void ProcessItemCore(PSObject? item)
        {
            if (IsTextStreamItem(item))
            {
                FlushFormatBufferCore();
                var line = GetOutputText(item);
                deliver(line);
            }
            else
            {
                formatBuffer.Add(item!);
            }
        }

        void DrainOutputCollectionCore(System.Management.Automation.PSDataCollection<System.Management.Automation.PSObject> col)
        {
            while (processedOutputCount < col.Count)
            {
                var item = col[processedOutputCount];
                processedOutputCount++;
                ProcessItemCore(item);
            }
        }

        outputCollection.DataAdded += (sender, e) =>
        {
            var col = (System.Management.Automation.PSDataCollection<System.Management.Automation.PSObject>)sender!;
            lock (outputLock)
            {
                DrainOutputCollectionCore(col);
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
                deliverError($"ps-bash: parse error: {ex.Message}");
                _ps.Commands.Clear();
                return 1;
            }
            catch (System.Management.Automation.RuntimeException ex)
            {
                deliverError($"ps-bash: {ex.Message}");
                _ps.Commands.Clear();
                return 1;
            }

            // DataAdded can lag the synchronous Invoke() return for the final
            // item. Drain the collection by count before the exit sentinel so
            // single-output commands like `pwd` cannot disappear.
            lock (outputLock)
            {
                DrainOutputCollectionCore(outputCollection);
                FlushFormatBufferCore();
            }

            // Map PowerShell terminating errors that mirror bash exit codes:
            //   - CommandNotFoundException → 127 (bash command-not-found)
            // Detection rules — narrow to avoid catching framework-internal
            // CommandNotFound exceptions from module auto-loading, try/catch
            // probes, completion lookups, etc:
            //   1) The pipeline must have surfaced errors at all
            //      (_ps.HadErrors). Internally-swallowed CommandNotFound
            //      records still hit Streams.Error but don't flip HadErrors.
            //   2) The LAST record in Streams.Error must be the
            //      CommandNotFoundException, so a script that throws then
            //      recovers + writes a different error still returns the
            //      script's chosen exit code (or 0).
            // We still write every error to Console.Error so diagnostics
            // surface even when the script exits 0.
            bool sawCommandNotFoundAsLastError = false;
            var errors = _ps.Streams.Error;
            for (int i = 0; i < errors.Count; i++)
            {
                deliverError(errors[i].ToString());
            }
            if (_ps.HadErrors && errors.Count > 0)
            {
                var last = errors[errors.Count - 1];
                if (last.CategoryInfo?.Reason == "CommandNotFoundException"
                    || last.Exception is System.Management.Automation.CommandNotFoundException)
                {
                    sawCommandNotFoundAsLastError = true;
                }
            }

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
            int finalExit = lec switch
            {
                int i => i,
                long l => (int)l,
                _ => 0,
            };
            // bash: `nonexistent_command` → exit 127. PowerShell's
            // CommandNotFoundException does not set $LASTEXITCODE on its own
            // (it's a terminating .NET exception, not an external-process exit
            // code). Surface a 127 only when no real exit code was set, so a
            // user `exit 5` after a typo still propagates 5.
            if (sawCommandNotFoundAsLastError && finalExit == 0)
                finalExit = 127;
            return finalExit;
        }
        finally
        {
            // Detach the forwarders so a stray Out-Default / WriteErrorLine call
            // from another worker invocation can't leak into a previous caller's
            // output sink.
            _host.HostUI.SetWriteLineForwarder(null);
            _host.HostUI.SetWriteErrorLineForwarder(null);
        }
    }

    private static string GetOutputText(PSObject? item)
    {
        if (item is null)
            return "";

        var bashText = item.Properties["BashText"]?.Value;
        if (bashText is not null)
        {
            var text = bashText.ToString() ?? "";
            var noTrailingNewline = item.Properties["NoTrailingNewline"]?.Value is true;
            return noTrailingNewline ? text : text + Environment.NewLine;
        }

        return item.BaseObject is string s ? s + Environment.NewLine : item.ToString() + Environment.NewLine;
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
