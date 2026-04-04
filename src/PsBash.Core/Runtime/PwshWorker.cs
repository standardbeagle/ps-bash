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
        var scriptPath = workerScriptPath
            ?? Path.Combine(AppContext.BaseDirectory, "ps-bash-worker.ps1");

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

        throw new InvalidOperationException(
            "Worker process ended without sending exit sentinel");
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
