using System.Diagnostics;
using System.Threading.Channels;

namespace PsBash.Core.Runtime;

public sealed class PwshWorker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly Channel<string> _outputChannel;
    private Task? _readerTask;
    private int _disposed;

    /// <summary>
    /// Optional callback that receives each output line. When set, output is
    /// routed to this callback instead of <see cref="Console.WriteLine(string)"/>.
    /// </summary>
    public Action<string>? OutputCallback { get; set; }

    private PwshWorker(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _outputChannel = Channel.CreateUnbounded<string>();
    }

    private void StartReader()
    {
        _readerTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await _stdout.ReadLineAsync(CancellationToken.None)) is not null)
                {
                    await _outputChannel.Writer.WriteAsync(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally
            {
                _outputChannel.Writer.TryComplete();
            }
        });
    }

    public static async Task<PwshWorker> StartAsync(
        string pwshPath,
        string? workerScriptPath = null,
        string? modulePath = null,
        CancellationToken ct = default)
    {
        // If both overrides are set, use the legacy -File approach.
        // Otherwise, stream module + worker script via stdin (no temp files).
        bool useStdin = workerScriptPath is null && modulePath is null;

        var psi = new ProcessStartInfo
        {
            FileName = pwshPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
        };

        if (useStdin)
        {
            // Stdin mode: pwsh reads a small bootstrap from -Command that
            // then reads the full init script (with base64 module) from stdin.
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            // Bootstrap: read lines from stdin until <<<INIT_END>>>, join, execute.
            // This keeps the command line small while allowing large module content via stdin.
            psi.ArgumentList.Add(
                "$l=[System.Collections.Generic.List[string]]::new();" +
                "while(($r=[Console]::In.ReadLine())-ne'<<<INIT_END>>>'){$l.Add($r)};" +
                "Invoke-Expression($l-join\"`n\")");
        }
        else
        {
            var scriptPath = workerScriptPath ?? await ExtractWorkerScriptAsync(ct);
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-ModulePath");
            psi.ArgumentList.Add(modulePath ?? "");
            psi.ArgumentList.Add("-ParentPid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh worker");

        var worker = new PwshWorker(process);

        if (useStdin)
        {
            // Stream the combined init script via stdin, terminated by sentinel.
            // The bootstrap -Command reads lines until <<<INIT_END>>> then Invoke-Expression.
            var initScript = BuildInitScript();
            await worker._stdin.WriteLineAsync(initScript.AsMemory(), ct);
            await worker._stdin.WriteLineAsync("<<<INIT_END>>>".AsMemory(), ct);
            await worker._stdin.FlushAsync(ct);
        }

        var ready = await worker._stdout.ReadLineAsync(ct);
        if (ready != "<<<READY>>>")
        {
            process.Kill();
            process.Dispose();
            throw new InvalidOperationException(
                $"Worker failed to start. Expected <<<READY>>> but got: {ready}");
        }

        worker.StartReader();
        return worker;
    }

    /// <summary>
    /// Build the combined init script that loads the module in-memory and starts
    /// the command loop. Sent via stdin — no temp files required.
    /// NOTE: The worker loop below must stay in sync with scripts/ps-bash-worker.ps1
    /// (used by the legacy -File path and e2e tests).
    /// </summary>
    private static string BuildInitScript()
    {
        var asm = typeof(PwshWorker).Assembly;

        // Read embedded module content
        using var psm1Stream = asm.GetManifestResourceStream("PsBash.Module/PsBash.psm1")!;
        using var psm1Reader = new StreamReader(psm1Stream);
        var moduleContent = psm1Reader.ReadToEnd();

        // Base64-encode module to avoid quoting issues (content has @' '@ etc.)
        var moduleBase64 = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(moduleContent));

        var parentPid = Environment.ProcessId;

        // Build the init script: decode module, load in-memory, start worker loop
        return $$"""
            $ErrorActionPreference = 'Continue'
            $moduleBytes = [System.Convert]::FromBase64String('{{moduleBase64}}')
            $moduleScript = [System.Text.Encoding]::UTF8.GetString($moduleBytes)
            New-Module -Name PsBash -ScriptBlock ([scriptblock]::Create($moduleScript)) -Function * -Alias * | Import-Module -Global -DisableNameChecking
            [Console]::Out.WriteLine("<<<READY>>>")
            [Console]::Out.Flush()
            $global:__parentPid = {{parentPid}}
            $global:BashFlags = ''
            $global:BashExitCode = 0
            while ($true) {
                if ($global:__parentPid -gt 0) {
                    try { $null = [System.Diagnostics.Process]::GetProcessById($global:__parentPid) }
                    catch { exit 0 }
                }
                $lines = [System.Collections.Generic.List[string]]::new()
                while ($true) {
                    $line = [Console]::In.ReadLine()
                    if ($null -eq $line) { exit 0 }
                    if ($line -eq '<<<END>>>') { break }
                    $lines.Add($line)
                }
                $command = $lines -join "`n"
                $exitCode = 0
                try {
                    $result = Invoke-Expression $command
                    $__partialLine = $false
                    if ($null -ne $result) {
                        foreach ($item in @($result)) {
                            if ($item -is [string]) {
                                [Console]::Out.WriteLine($item)
                                $__partialLine = $false
                            } elseif ($item -is [PSCustomObject] -and $item.PSObject.Properties['BashText']) {
                                $raw = [string]$item.BashText
                                if ($raw.EndsWith("`n")) {
                                    [Console]::Out.WriteLine($raw.Substring(0, $raw.Length - 1))
                                    $__partialLine = $false
                                } else {
                                    [Console]::Out.Write($raw)
                                    $__partialLine = $true
                                }
                            } else {
                                $item | Out-String -Stream | ForEach-Object { [Console]::Out.WriteLine($_) }
                                $__partialLine = $false
                            }
                        }
                    }
                    if ($__partialLine) { [Console]::Out.WriteLine() }
                    $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 0 }
                    [Environment]::CurrentDirectory = (Get-Location).Path
                    $global:BashExitCode = $exitCode
                    [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
                } catch {
                    [Console]::Error.WriteLine($_.Exception.Message)
                    $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 1 }
                    [Environment]::CurrentDirectory = (Get-Location).Path
                    $global:BashExitCode = $exitCode
                    [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
                } finally {
                    if ($global:__BashTrapEXIT) { & $global:__BashTrapEXIT }
                    if ($exitCode -ne 0 -and $global:__BashTrapERR) { & $global:__BashTrapERR }
                    [Console]::Out.Flush()
                }
                $ws = [System.Diagnostics.Process]::GetCurrentProcess().WorkingSet64
                $maxBytes = if ($env:PSBASH_MAX_MEMORY) { [long]$env:PSBASH_MAX_MEMORY * 1MB } else { 512MB }
                if ($ws -gt $maxBytes) {
                    [Console]::Error.WriteLine("ps-bash: worker exceeded memory limit ($([math]::Round($ws/1MB))MB)")
                    exit 137
                }
            }

            """;
    }

    public async Task<int> ExecuteAsync(
        string pwshCommand,
        CancellationToken ct = default)
    {
        var timeout = GetTimeout();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await _stdin.WriteLineAsync(pwshCommand.AsMemory(), linked.Token);
            await _stdin.WriteLineAsync("<<<END>>>".AsMemory(), linked.Token);
            await _stdin.FlushAsync(linked.Token);

            var reader = _outputChannel.Reader;
            while (await reader.WaitToReadAsync(linked.Token))
            {
                while (reader.TryRead(out var line))
                {
                    if (line.StartsWith("<<<EXIT:", StringComparison.Ordinal)
                        && line.EndsWith(">>>", StringComparison.Ordinal))
                    {
                        var code = line["<<<EXIT:".Length..^">>>".Length];
                        return int.TryParse(code, out var n) ? n : 1;
                    }
                    if (OutputCallback is { } cb)
                        cb(line);
                    else
                        Console.WriteLine(line);
                }
            }

            // Stdout closed — process exited (e.g. via "exit N"). Return its exit code.
            await _process.WaitForExitAsync(CancellationToken.None);
            return _process.ExitCode;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            Console.Error.WriteLine($"ps-bash: command timed out after {timeout.TotalSeconds}s");
            _process.Kill();
            return 124;
        }
    }

    private static TimeSpan GetTimeout()
    {
        var envValue = Environment.GetEnvironmentVariable("PSBASH_TIMEOUT");
        if (envValue is not null && int.TryParse(envValue, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(120);
    }

    private static async Task<string> ExtractWorkerScriptAsync(CancellationToken ct)
    {
        // Use the same version-stamped directory as ModuleExtractor so the
        // worker script is cached alongside the module and invalidated together.
        var asm = typeof(PwshWorker).Assembly;
        var version = asm.GetName().Version?.ToString() ?? "0.0.0";
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", $"module-{version}");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "ps-bash-worker.ps1");

        // Skip extraction if already present and content matches embedded resource.
        if (File.Exists(dest))
        {
            using var embedded = asm.GetManifestResourceStream("ps-bash-worker.ps1")!;
            using var existing = File.OpenRead(dest);
            if (embedded.Length == existing.Length && StreamsEqual(embedded, existing))
                return dest;
        }

        // Write with FileShare.Read so concurrent processes can read the file
        // while it's being written. Retry once on IOException (another process writing).
        try
        {
            using var stream = asm.GetManifestResourceStream("ps-bash-worker.ps1")!;
            using var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read);
            await stream.CopyToAsync(file, ct);
        }
        catch (IOException)
        {
            // Another process is writing. Wait briefly then use whatever exists.
            await Task.Delay(500, ct);
        }
        return dest;
    }

    private static bool StreamsEqual(Stream a, Stream b)
    {
        Span<byte> bufA = stackalloc byte[4096];
        Span<byte> bufB = stackalloc byte[4096];
        while (true)
        {
            int readA = a.Read(bufA);
            int readB = b.Read(bufB);
            if (readA != readB) return false;
            if (readA == 0) return true;
            if (!bufA[..readA].SequenceEqual(bufB[..readB])) return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            _stdin.Close();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { _process.Kill(); }
        }
        finally
        {
            _outputChannel.Writer.TryComplete();
            if (_readerTask is not null)
            {
                try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* ignore */ }
            }
            _process.Dispose();
        }
    }
}
