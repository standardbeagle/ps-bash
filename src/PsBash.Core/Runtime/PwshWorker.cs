using System.Diagnostics;

namespace PsBash.Core.Runtime;

public sealed class PwshWorker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private int _disposed;

    private PwshWorker(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
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

        return worker;
    }

    /// <summary>
    /// Build the combined init script that loads the module in-memory and starts
    /// the command loop. Sent via stdin — no temp files required.
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
            New-Module -Name PsBash -ScriptBlock ([scriptblock]::Create($moduleScript)) -Function * -Alias * | Import-Module -Global
            [Console]::Out.WriteLine("<<<READY>>>")
            [Console]::Out.Flush()
            $__parentPid = {{parentPid}}
            while ($true) {
                if ($__parentPid -gt 0) {
                    try { $null = [System.Diagnostics.Process]::GetProcessById($__parentPid) }
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
                try {
                    $result = Invoke-Expression $command
                    if ($null -ne $result) {
                        foreach ($item in @($result)) {
                            if ($item -is [PSCustomObject] -and $item.PSObject.Properties['BashText']) {
                                $text = $item.BashText -replace "`n$", ''
                                [Console]::Out.WriteLine($text)
                            } else {
                                $item | Out-String -Stream | ForEach-Object { [Console]::Out.WriteLine($_) }
                            }
                        }
                    }
                    $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 0 }
                    [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
                } catch {
                    [Console]::Error.WriteLine($_.Exception.Message)
                    $exitCode = if ($LASTEXITCODE -ne $null) { $LASTEXITCODE } else { 1 }
                    [Console]::Out.WriteLine("<<<EXIT:$exitCode>>>")
                } finally {
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

            string? line;
            while ((line = await _stdout.ReadLineAsync(linked.Token)) is not null)
            {
                if (line.StartsWith("<<<EXIT:", StringComparison.Ordinal)
                    && line.EndsWith(">>>", StringComparison.Ordinal))
                {
                    var code = line["<<<EXIT:".Length..^">>>".Length];
                    return int.TryParse(code, out var n) ? n : 1;
                }
                Console.WriteLine(line);
            }

            // Stdout closed — process exited (e.g. via "exit N"). Return its exit code.
            await _process.WaitForExitAsync(linked.Token);
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
        return TimeSpan.FromSeconds(30);
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

        // Skip extraction if already present and newer than the assembly.
        if (File.Exists(dest))
        {
            var asmPath = asm.Location;
            if (string.IsNullOrEmpty(asmPath) || !File.Exists(asmPath)
                || File.GetLastWriteTimeUtc(dest) >= File.GetLastWriteTimeUtc(asmPath))
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            _stdin.Close();
            await _process.WaitForExitAsync();
        }
        finally
        {
            _process.Dispose();
        }
    }
}
