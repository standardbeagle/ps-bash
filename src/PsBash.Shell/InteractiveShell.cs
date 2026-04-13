using System.Text;
using PsBash.Core.Parser;
using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;

namespace PsBash.Shell;

public static class InteractiveShell
{
    private const string Prompt = "$ ";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal);

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

            trimmed = ProcessAliasCommand(trimmed);
            if (trimmed.Length == 0)
                continue;

            trimmed = ExpandAliases(trimmed);

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

    public static string ProcessAliasCommand(string input)
    {
        // Handle: alias name=value
        var aliasMatch = System.Text.RegularExpressions.Regex.Match(
            input, @"^alias\s+((?:[^=\\ ""']+|\\.|""[^""]*""|'[^']*')+)=((?:[^\\ ""']+|\\.|""[^""]*""|'[^']*')*)\s*$");
        if (aliasMatch.Success)
        {
            var name = aliasMatch.Groups[1].Value.Trim();
            var value = aliasMatch.Groups[2].Value.Trim();
            // Strip surrounding quotes from value
            if ((value.StartsWith('"') && value.EndsWith('"')) ||
                (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                value = value[1..^1];
            }
            Aliases[name] = value;
            return "";
        }

        // Handle: alias (bare — list all)
        if (input == "alias" || System.Text.RegularExpressions.Regex.IsMatch(input, @"^alias\s+-p\s*$"))
        {
            foreach (var kvp in Aliases)
                Console.WriteLine($"alias {kvp.Key}='{kvp.Value}'");
            return "";
        }

        // Handle: alias name (show specific)
        var aliasShowMatch = System.Text.RegularExpressions.Regex.Match(input, @"^alias\s+([^\s=]+)\s*$");
        if (aliasShowMatch.Success)
        {
            var name = aliasShowMatch.Groups[1].Value;
            if (Aliases.TryGetValue(name, out var val))
                Console.WriteLine($"alias {name}='{val}'");
            else
                Console.Error.WriteLine($"ps-bash: alias: {name}: not found");
            return "";
        }

        // Handle: unalias name
        var unaliasMatch = System.Text.RegularExpressions.Regex.Match(input, @"^unalias\s+(.+)\s*$");
        if (unaliasMatch.Success)
        {
            var names = unaliasMatch.Groups[1].Value;
            if (names.Trim() == "-a")
            {
                Aliases.Clear();
            }
            else
            {
                foreach (var name in names.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Aliases.Remove(name))
                    {
                        // removed
                    }
                    else
                    {
                        Console.Error.WriteLine($"ps-bash: unalias: {name}: not found");
                    }
                }
            }
            return "";
        }

        return input;
    }

    public static string ExpandAliases(string input)
    {
        if (Aliases.Count == 0)
            return input;

        // Find the first word (command name) respecting quotes
        var sb = new StringBuilder();
        int i = 0;
        // Skip leading whitespace
        while (i < input.Length && char.IsWhiteSpace(input[i]))
            i++;

        if (i >= input.Length)
            return input;

        // Read the first word
        int start = i;
        bool quoted = false;
        char quoteChar = '\0';

        while (i < input.Length)
        {
            char c = input[i];
            if (quoted)
            {
                if (c == quoteChar)
                    quoted = false;
                i++;
            }
            else if (c == '"' || c == '\'')
            {
                quoted = true;
                quoteChar = c;
                i++;
            }
            else if (c == '\\')
            {
                i += 2;
            }
            else if (char.IsWhiteSpace(c) || c == ';' || c == '|' || c == '&' || c == '(' || c == '<' || c == '>')
            {
                break;
            }
            else
            {
                i++;
            }
        }

        var firstWord = input[start..i];

        if (Aliases.TryGetValue(firstWord, out var expansion))
        {
            return expansion + input[i..];
        }

        return input;
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
