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
        var scriptPath = workerScriptPath ?? await ExtractWorkerScriptAsync(ct);

        var psi = new ProcessStartInfo
        {
            FileName = pwshPath,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-File", scriptPath,
                             "-ModulePath", modulePath ?? "",
                             "-ParentPid", Environment.ProcessId.ToString() },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh worker");

        var worker = new PwshWorker(process);

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
