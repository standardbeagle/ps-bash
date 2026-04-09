using PsBash.Core.Parser;
using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;

namespace PsBash.Shell;

public static class InteractiveShell
{
    private const string Prompt = "$ ";

    public static async Task<int> RunAsync(string pwshPath)
    {
        Console.CancelKeyPress += OnCancelKeyPress;

        var cts = new CancellationTokenSource();
        var worker = await StartWorkerAsync(pwshPath);

        while (true)
        {
            cts.Dispose();
            cts = new CancellationTokenSource();
            _currentCts = cts;

            Console.Write(Prompt);
            var line = Console.ReadLine();

            if (line is null)
            {
                Console.WriteLine();
                await DisposeWorkerAsync(worker);
                return 0;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (IsExitCommand(trimmed, out var exitCode))
            {
                await DisposeWorkerAsync(worker);
                return exitCode;
            }

            string pwshCommand;
            try
            {
                pwshCommand = BashTranspiler.Transpile(trimmed);
            }
            catch (ParseException ex)
            {
                Console.Error.WriteLine($"ps-bash: parse error: {ex.Message}");
                continue;
            }

            try
            {
                await worker.ExecuteAsync(pwshCommand, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("^C");
                await DisposeWorkerAsync(worker);
                worker = await StartWorkerAsync(pwshPath);
            }
        }
    }

    private static async Task<PwshWorker> StartWorkerAsync(string pwshPath)
    {
        var modulePath = Environment.GetEnvironmentVariable("PSBASH_MODULE")
            ?? ModuleExtractor.ExtractEmbedded();

        return await PwshWorker.StartAsync(
            pwshPath,
            workerScriptPath: Environment.GetEnvironmentVariable("PSBASH_WORKER"),
            modulePath: modulePath);
    }

    private static async ValueTask DisposeWorkerAsync(PwshWorker worker)
    {
        try { await worker.DisposeAsync(); }
        catch { }
    }

    private static bool IsExitCommand(string input, out int exitCode)
    {
        exitCode = 0;
        if (input is "logout") return true;
        if (input == "exit") return true;
        if (input.StartsWith("exit ", StringComparison.Ordinal))
        {
            var arg = input["exit ".Length..].Trim();
            if (int.TryParse(arg, out var code))
            {
                exitCode = code;
                return true;
            }
        }
        return false;
    }

    private static CancellationTokenSource? _currentCts;

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _currentCts?.Cancel();
    }
}
