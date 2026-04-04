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
        CancellationToken ct = default)
    {
        var scriptPath = workerScriptPath ?? await ExtractWorkerScriptAsync(ct);

        var psi = new ProcessStartInfo
        {
            FileName = pwshPath,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-File", scriptPath },
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
        await _stdin.WriteLineAsync(pwshCommand.AsMemory(), ct);
        await _stdin.WriteLineAsync("<<<END>>>".AsMemory(), ct);
        await _stdin.FlushAsync(ct);

        string? line;
        while ((line = await _stdout.ReadLineAsync(ct)) is not null)
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
        await _process.WaitForExitAsync(ct);
        return _process.ExitCode;
    }

    private static async Task<string> ExtractWorkerScriptAsync(CancellationToken ct)
    {
        var dest = Path.Combine(Path.GetTempPath(), "ps-bash-worker.ps1");
        using var stream = typeof(PwshWorker).Assembly
            .GetManifestResourceStream("ps-bash-worker.ps1")!;
        using var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file, ct);
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
