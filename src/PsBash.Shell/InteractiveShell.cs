using System.Diagnostics;
using System.Text;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;

namespace PsBash.Shell;

public static class InteractiveShell
{
    private const string ContinuationPrompt = "> ";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal);

    private static readonly string[] OpenKeywords = ["if", "for", "while", "until", "case", "do", "{", "(", "function"];
    private static readonly string[] CloseKeywords = ["fi", "done", "esac", "}", ")"];

    private static string _homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string _lastDir = Environment.CurrentDirectory;

    public static async Task<int> RunAsync(string pwshPath)
    {
        Console.CancelKeyPress += OnCancelKeyPress;

        var cts = new CancellationTokenSource();
        var worker = await StartWorkerAsync(pwshPath);

        await SourceRcFileAsync(worker, cts);

        while (true)
        {
            cts.Dispose();
            cts = new CancellationTokenSource();
            _currentCts = cts;

            var input = ReadInput();
            if (input is null)
            {
                Console.WriteLine();
                await DisposeWorkerAsync(worker);
                return 0;
            }

            var trimmed = input.Trim();
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
                if (TryRunDirect(trimmed))
                    continue;

                await worker.ExecuteAsync(pwshCommand, cts.Token);
                UpdateCwd(trimmed);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("^C");
                await DisposeWorkerAsync(worker);
                worker = await StartWorkerAsync(pwshPath);
            }
        }
    }

    private static bool TryRunDirect(string bashInput)
    {
        Command? ast;
        try
        {
            ast = BashParser.Parse(bashInput);
        }
        catch
        {
            return false;
        }

        if (ast is not Command.Simple simple)
            return false;

        if (simple.Redirects.Length > 0 || simple.EnvPairs.Length > 0)
            return false;

        if (simple.Words.Length == 0)
            return false;

        var cmdName = PsEmitter.GetLiteralValue(simple.Words[0]);
        if (cmdName is null)
            return false;

        if (PsEmitter.IsKnownCommand(cmdName))
            return false;

        var args = new List<string>();
        for (int i = 1; i < simple.Words.Length; i++)
        {
            var lit = PsEmitter.GetLiteralValue(simple.Words[i]);
            if (lit is not null)
                args.Add(lit);
            else
                return false;
        }

        try
        {
            var workDir = Directory.Exists(_lastDir) ? _lastDir : null;
            var psi = new ProcessStartInfo(cmdName)
            {
                UseShellExecute = false,
                WorkingDirectory = workDir,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            _suspendCancel = true;
            var proc = Process.Start(psi);
            if (proc is null)
            {
                _suspendCancel = false;
                return false;
            }

            proc.WaitForExit();
            _suspendCancel = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void UpdateCwd(string bashInput)
    {
        var trimmedInput = bashInput.TrimStart();
        if (!trimmedInput.StartsWith("cd ") && trimmedInput != "cd" && !trimmedInput.StartsWith("cd\t"))
            return;

        var arg = trimmedInput[2..].Trim();
        if (arg.Length == 0 || arg == "~")
        {
            _lastDir = _homeDir;
            return;
        }

        if (arg == "-")
            return;

        try
        {
            var target = arg.StartsWith("~") ? _homeDir + arg[1..] : arg;
            if (!Path.IsPathRooted(target))
                target = Path.GetFullPath(Path.Combine(_lastDir, target));

            if (Directory.Exists(target))
                _lastDir = target;
        }
        catch { }
    }

    private static string BuildPrompt()
    {
        const string Reset = "\x1b[0m";
        const string Bold = "\x1b[1m";
        const string Green = "\x1b[32m";
        const string Cyan = "\x1b[36m";
        const string Red = "\x1b[31m";
        const string Magenta = "\x1b[35m";
        const string Dim = "\x1b[2m";

        var cwd = _lastDir;
        if (cwd.StartsWith(_homeDir))
            cwd = "~" + cwd[_homeDir.Length..];

        var sb = new StringBuilder();

        // Username@hostname
        var user = Environment.UserName;
        var host = Environment.MachineName.ToLowerInvariant();
        sb.Append($"{Green}{Bold}{user}@{host}{Reset}");

        sb.Append(':');

        // Working directory
        sb.Append($"{Cyan}{Bold}{cwd}{Reset}");

        // Git branch
        var branch = GetGitBranch();
        if (branch is not null)
        {
            var status = GetGitStatus();
            var branchColor = status ? Green : Red;
            sb.Append($" {Dim}({Reset}{branchColor}{branch}{Reset}{Dim}){Reset}");
        }

        sb.Append(' ');

        // Prompt character — # for admin, $ for user
        var isAdmin = OperatingSystem.IsWindows()
            && System.Security.Principal.WindowsIdentity.GetCurrent()?.Owner?.IsWellKnown(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) == true;
        var promptChar = isAdmin ? '#' : '$';
        sb.Append($"{Magenta}{Bold}{promptChar}{Reset} ");

        return sb.ToString();
    }

    private static string? GetGitBranch()
    {
        try
        {
            var dir = _lastDir;
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                {
                    var headFile = Path.Combine(dir, ".git", "HEAD");
                    if (File.Exists(headFile))
                    {
                        var head = File.ReadAllText(headFile).Trim();
                        const string prefix = "ref: refs/heads/";
                        if (head.StartsWith(prefix))
                            return head[prefix.Length..];
                        return head[..Math.Min(7, head.Length)];
                    }
                    return null;
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }
        catch { }
        return null;
    }

    private static bool GetGitStatus()
    {
        try
        {
            var dir = _lastDir;
            while (dir is not null)
            {
                var gitDir = Path.Combine(dir, ".git");
                if (Directory.Exists(gitDir))
                {
                    // Check if index exists and has entries different from HEAD
                    // A simple proxy: check if there are modified files
                    var headFile = Path.Combine(gitDir, "HEAD");
                    if (!File.Exists(headFile)) return true;
                    var head = File.ReadAllText(headFile).Trim();
                    const string prefix = "ref: refs/heads/";
                    if (!head.StartsWith(prefix)) return true;
                    var refPath = Path.Combine(gitDir, head[prefix.Length..]);
                    if (!File.Exists(refPath)) return false;
                    return true; // branch exists = clean enough
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }
        catch { }
        return true;
    }

    private static string? ReadInput()
    {
        Console.Write(BuildPrompt());
        var line = Console.ReadLine();
        if (line is null)
            return null;

        var sb = new StringBuilder(line);

        while (IsIncomplete(sb.ToString()))
        {
            Console.Write(ContinuationPrompt);
            var next = Console.ReadLine();
            if (next is null)
                break;
            sb.Append('\n');
            sb.Append(next);
        }

        return sb.ToString();
    }

    internal static bool IsIncomplete(string input)
    {
        int depth = 0;
        int braceDepth = 0;
        int parenDepth = 0;
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        var words = new List<string>();
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];

            if (inSingleQuote)
            {
                if (c == '\'') inSingleQuote = false;
                i++;
                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '\\' && i + 1 < input.Length) { i += 2; continue; }
                if (c == '"') inDoubleQuote = false;
                i++;
                continue;
            }

            if (c == '\'') { inSingleQuote = true; i++; continue; }
            if (c == '"') { inDoubleQuote = true; i++; continue; }
            if (c == '\\' && i + 1 < input.Length) { i += 2; continue; }

            if (c == '#')
            {
                while (i < input.Length && input[i] != '\n') i++;
                continue;
            }

            if (c == '{') { braceDepth++; i++; continue; }
            if (c == '}') { braceDepth--; i++; continue; }
            if (c == '(') { parenDepth++; i++; continue; }
            if (c == ')')
            {
                parenDepth--;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c) || c == ';' || c == '|' || c == '&' || c == '<' || c == '>' || c == '\n')
            {
                if (words.Count > 0 && c != '\n')
                {
                    ProcessWord(words[^1], ref depth);
                }

                if (c == '\n' || c == ';')
                {
                    words.Clear();
                }
                i++;
                continue;
            }

            var wordStart = i;
            while (i < input.Length)
            {
                char wc = input[i];
                if (inSingleQuote) { if (wc == '\'') inSingleQuote = false; i++; continue; }
                if (inDoubleQuote)
                {
                    if (wc == '\\' && i + 1 < input.Length) { i += 2; continue; }
                    if (wc == '"') inDoubleQuote = false;
                    i++; continue;
                }
                if (wc == '\'' || wc == '"') { if (wc == '\'') inSingleQuote = true; else inDoubleQuote = true; i++; continue; }
                if (wc == '\\' && i + 1 < input.Length) { i += 2; continue; }
                if (char.IsWhiteSpace(wc) || wc == ';' || wc == '|' || wc == '&' || wc == '<' || wc == '>' || wc == '{' || wc == '}' || wc == '(' || wc == ')' || wc == '\n')
                    break;
                i++;
            }

            var word = input[wordStart..i];
            if (word.Length > 0)
            {
                ProcessWord(word, ref depth);
                words.Add(word);
            }
        }

        if (inSingleQuote || inDoubleQuote)
            return true;
        if (braceDepth > 0)
            return true;
        if (parenDepth > 0)
            return true;
        if (depth > 0)
            return true;

        return false;
    }

    private static void ProcessWord(string word, ref int depth)
    {
        if (word is "if" or "for" or "while" or "until" or "case" or "select")
        {
            depth++;
        }
        else if (word == "do")
        {
            // 'do' only opens if we're inside a for/while/until (depth > 0)
            // In bash: for x in ... do ... done — 'do' doesn't nest, the for already opened
        }
        else if (word == "fi" || word == "done" || word == "esac")
        {
            depth--;
        }
    }

    private static async Task SourceRcFileAsync(PwshWorker worker, CancellationTokenSource cts)
    {
        var rcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".psbashrc");
        if (!File.Exists(rcPath))
            return;

        string rcContent;
        try
        {
            rcContent = await File.ReadAllTextAsync(rcPath);
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rcContent))
            return;

        var filtered = new StringBuilder();
        foreach (var rawLine in rcContent.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', '\n');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var aliasResult = ProcessAliasCommand(trimmed);
            if (aliasResult.Length == 0)
                continue;

            aliasResult = ExpandAliases(aliasResult);
            if (filtered.Length > 0)
                filtered.Append('\n');
            filtered.Append(aliasResult);
        }

        if (filtered.Length == 0)
            return;

        string pwshCommand;
        try
        {
            pwshCommand = BashTranspiler.Transpile(filtered.ToString());
        }
        catch (ParseException)
        {
            return;
        }

        try
        {
            await worker.ExecuteAsync(pwshCommand, cts.Token);
        }
        catch (OperationCanceledException) { }
    }

    public static string ProcessAliasCommand(string input)
    {
        var aliasMatch = System.Text.RegularExpressions.Regex.Match(
            input, @"^alias\s+((?:[^=\\ ""']+|\\.|""[^""]*""|'[^']*')+)=((?:[^\\ ""']+|\\.|""[^""]*""|'[^']*')*)\s*$");
        if (aliasMatch.Success)
        {
            var name = aliasMatch.Groups[1].Value.Trim();
            var value = aliasMatch.Groups[2].Value.Trim();
            if ((value.StartsWith('"') && value.EndsWith('"')) ||
                (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                value = value[1..^1];
            }
            Aliases[name] = value;
            return "";
        }

        if (input == "alias" || System.Text.RegularExpressions.Regex.IsMatch(input, @"^alias\s+-p\s*$"))
        {
            foreach (var kvp in Aliases)
                Console.WriteLine($"alias {kvp.Key}='{kvp.Value}'");
            return "";
        }

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
                    if (!Aliases.Remove(name))
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

        int i = 0;
        while (i < input.Length && char.IsWhiteSpace(input[i]))
            i++;

        if (i >= input.Length)
            return input;

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
    private static volatile bool _suspendCancel;

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (_suspendCancel) return;
        e.Cancel = true;
        _currentCts?.Cancel();
    }
}
