using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private static LineEditor? _lineEditor;
    private static IHistoryStore? _historyStore;
    private static string _sessionId = Guid.NewGuid().ToString();
    private static string? _lastCommand;

    public static async Task<int> RunAsync(string pwshPath, bool noProfile = false)
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        EnsureVirtualTerminalEnabled();

        using var loading = LoadingIndicator.Start("Loading ps-bash");

        if (!noProfile)
        {
            loading.Update("Loading PowerShell profile");
            MergeProfilePath(pwshPath);
        }

        loading.Update("Starting PowerShell worker");
        var cts = new CancellationTokenSource();
        // Expose the startup cts so Ctrl+C during the loading phase (worker
        // start, rc sourcing) cancels the in-flight operation instead of being
        // silently dropped.
        _currentCts = cts;
        var worker = await StartWorkerAsync(pwshPath);

        // Initialize history store. PSBASH_HOME overrides the home directory used to
        // locate the history DB so that tests can isolate history to a temp directory
        // without touching the real user profile.
        var historyHomeDir = Environment.GetEnvironmentVariable("PSBASH_HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var psbashDir = Path.Combine(historyHomeDir, ".psbash");
        Directory.CreateDirectory(psbashDir);

        var dbPath = Path.Combine(psbashDir, "history.db");
        _historyStore = new SqliteHistoryStore(dbPath);

        // Initialize LineEditor with history store and tab completer
        _lineEditor = new LineEditor(_historyStore, (line, cursor) =>
            TabCompleter.Complete(line, cursor, Aliases, _lastDir, _lastCommand, _historyStore));

        if (!noProfile)
        {
            loading.Update("Sourcing ~/.psbashrc");
            await SourceRcFileAsync(worker, cts);
        }

        loading.Finish();

        while (true)
        {
            try
            {
                worker = await EnsureWorkerAsync(worker, pwshPath);
                cts.Dispose();
                cts = new CancellationTokenSource();
                _currentCts = cts;

                var input = await ReadInputAsync(worker);
                if (input is null)
                {
                    Console.WriteLine();
                    await DisposeWorkerAsync(worker);
                    if (_historyStore is IDisposable disposable)
                        disposable.Dispose();
                    return 0;
                }

                var trimmed = input.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (IsExitCommand(trimmed, out var exitCode))
                {
                    await DisposeWorkerAsync(worker);
                    if (_historyStore is IDisposable disposable)
                        disposable.Dispose();
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

                var stopwatch = Stopwatch.StartNew();
                int? exitCodeResult = null;

                try
                {
                    if (TryRunDirect(trimmed, out var directExitCode))
                    {
                        stopwatch.Stop();
                        exitCodeResult = directExitCode;
                        await SyncWorkerCwdAsync(worker);
                        await RunPromptCommandAsync(worker);
                        continue;
                    }

                    await worker.ExecuteAsync(pwshCommand, cts.Token);
                    await SyncWorkerCwdAsync(worker);

                    try
                    {
                        var exitCodeStr = await worker.QueryAsync("$LASTEXITCODE");
                        if (int.TryParse(exitCodeStr?.Trim(), out var code))
                            exitCodeResult = code;
                    }
                    catch (Exception) { /* routine: worker busy or query raced; last exit code is best-effort */ }

                    await RunPromptCommandAsync(worker);
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("^C");
                    stopwatch.Stop();
                    exitCodeResult = null;
                    await DisposeWorkerAsync(worker);
                    worker = await StartWorkerAsync(pwshPath);
                }
                finally
                {
                    stopwatch.Stop();
                    await RecordCommandAsync(trimmed, exitCodeResult, stopwatch.ElapsedMilliseconds);
                }
            }
            catch (IOException)
            {
                // Worker stdin/stdout pipe closed (process exited/crashed).
                // Dispose the dead worker and respawn transparently.
                Console.Error.WriteLine("[ps-bash] worker connection lost; respawning...");
                try { await DisposeWorkerAsync(worker); } catch { }
                worker = await StartWorkerAsync(pwshPath);
            }
            catch (Exception ex)
            {
                // Top-level guard: never crash the shell. Log the unexpected
                // failure to stderr and continue to the next prompt.
                Console.Error.WriteLine($"ps-bash: internal error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static async Task RecordCommandAsync(string command, int? exitCode, long durationMs)
    {
        if (_historyStore == null || _lineEditor == null) return;

        try
        {
            await _lineEditor.RecordCommandAsync(
                command,
                Environment.CurrentDirectory,
                exitCode,
                durationMs,
                _sessionId);

            _lastCommand = command;
        }
        catch (Exception) { /* routine: history is best-effort, DB may be locked/busy */ }
    }

    private static bool TryRunDirect(string bashInput, out int exitCode)
    {
        exitCode = 0;
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

        if (simple.Redirects.Length > 0)
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
            var resolved = ResolveWord(simple.Words[i]);
            if (resolved is not null)
                args.AddRange(resolved);
            else
                return false;
        }

        try
        {
            var workDir = Directory.Exists(_lastDir) ? _lastDir : null;
            var resolvedCmd = ResolveCommand(cmdName, workDir);
            if (resolvedCmd is null)
                return false;

            var psi = new ProcessStartInfo(resolvedCmd)
            {
                UseShellExecute = false,
                WorkingDirectory = workDir,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            foreach (var envPair in simple.EnvPairs)
            {
                if (envPair.Value is not null)
                {
                    var val = PsEmitter.GetLiteralValue(envPair.Value);
                    if (val is not null)
                        psi.Environment[envPair.Name] = val;
                }
            }

            _suspendCancel = true;
            var proc = Process.Start(psi);
            if (proc is null)
            {
                _suspendCancel = false;
                return false;
            }

proc.WaitForExit();
_suspendCancel = false;
EnsureVirtualTerminalEnabled();
EnsureConsoleInputRestored();
            exitCode = proc.ExitCode;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<string>? ResolveWord(CompoundWord word)
    {
        var lit = PsEmitter.GetLiteralValue(word);
        if (lit is not null)
        {
            if (HasGlobChars(lit))
            {
                try
                {
                    var dir = Directory.Exists(_lastDir) ? _lastDir : ".";
                    var pattern = Path.IsPathRooted(lit) ? lit : Path.Combine(dir, lit);
                    var dirPart = Path.GetDirectoryName(pattern) ?? ".";
                    var filePart = Path.GetFileName(pattern);
                    var matches = Directory.GetFiles(dirPart, filePart);
                    if (matches.Length == 0)
                        return new List<string> { lit };
                    return matches
                        .Select(m => Path.GetRelativePath(dir, m))
                        .ToList();
                }
                catch
                {
                    return new List<string> { lit };
                }
            }
            return new List<string> { lit };
        }

        if (word.Parts.Length == 1 && word.Parts[0] is WordPart.SimpleVarSub sv)
        {
            var val = Environment.GetEnvironmentVariable(sv.Name);
            if (val is not null)
                return new List<string> { val };
        }

        return null;
    }

    private static bool HasGlobChars(string value) =>
        value.Contains('*') || value.Contains('?') || value.Contains('[');

    private static void MergeProfilePath(string pwshPath)
    {
        const string begin = "<<<PSBASH_PATH_BEGIN>>>";
        const string end = "<<<PSBASH_PATH_END>>>";
        try
        {
            var psi = new ProcessStartInfo(pwshPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "-NoLogo", "-Command",
                    $"[Console]::Out.WriteLine('{begin}' + $env:PATH + '{end}')" },
            };
            using var proc = Process.Start(psi);
            if (proc is null) return;
            // Read stdout and stderr concurrently to avoid the classic pipe
            // deadlock where a full stderr buffer blocks the child before
            // stdout is consumed. No hard timeout — a slow profile is the
            // user's profile; let it finish. The LoadingIndicator surfaces
            // elapsed time and offers a bail-out prompt.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            Task.WaitAll(stdoutTask, stderrTask);
            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;
            proc.WaitForExit();

            // Surface anything the profile emitted (warnings on stdout OR stderr)
            // as a clearly-labelled ps-bash message, instead of letting it contaminate PATH.
            ReportProfileNoise(stdout, begin, end);
            ReportProfileNoise(stderr, null, null);

            var bi = stdout.IndexOf(begin, StringComparison.Ordinal);
            var ei = stdout.IndexOf(end, StringComparison.Ordinal);
            if (bi < 0 || ei <= bi) return;
            var profilePath = stdout.Substring(bi + begin.Length, ei - bi - begin.Length).Trim();
            if (string.IsNullOrEmpty(profilePath)) return;

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var currentDirs = new HashSet<string>(
                currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

            var profileDirs = profilePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var merged = new StringBuilder(currentPath);
            foreach (var dir in profileDirs)
            {
                if (!currentDirs.Contains(dir))
                {
                    merged.Append(Path.PathSeparator);
                    merged.Append(dir);
                }
            }
            Environment.SetEnvironmentVariable("PATH", merged.ToString());
        }
        catch (Exception ex) { Console.Error.WriteLine($"[ps-bash] warning: failed to merge profile PATH: {ex.Message}"); }
    }

    // Extract anything in `text` that isn't our PATH payload and surface it as a
    // profile warning, so module-load warnings etc. reach the user clearly rather
    // than corrupting PATH or being silently dropped.
    private static void ReportProfileNoise(string? text, string? begin, string? end)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string noise = text;
        if (begin is not null && end is not null)
        {
            var bi = noise.IndexOf(begin, StringComparison.Ordinal);
            var ei = noise.IndexOf(end, StringComparison.Ordinal);
            if (bi >= 0 && ei > bi)
                noise = noise.Remove(bi, (ei - bi) + end.Length);
        }
        foreach (var rawLine in noise.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            Console.Error.WriteLine($"[ps-bash] profile warning: {line}");
        }
    }

    internal static string? ResolveCommand(string cmdName, string? workDir)
    {
        if (Path.IsPathRooted(cmdName))
            return File.Exists(cmdName) ? cmdName : null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT")?.Split(';') ?? [".EXE", ".CMD", ".BAT"])
                .Concat([".PS1"])
                .DistinctBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : null;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var searchDirs = workDir is not null
            ? [workDir, .. pathDirs]
            : pathDirs;

        foreach (var dir in searchDirs)
        {
            if (extensions is not null)
            {
                foreach (var ext in extensions)
                {
                    var full = Path.Combine(dir, cmdName + ext);
                    if (File.Exists(full))
                        return full;
                }
                var exact = Path.Combine(dir, cmdName);
                if (File.Exists(exact))
                    return exact;
            }
            else
            {
                var full = Path.Combine(dir, cmdName);
                if (File.Exists(full))
                    return full;
            }
        }

        return null;
    }

    private static async Task SyncWorkerCwdAsync(PwshWorker worker)
    {
        try
        {
            var pwd = await worker.QueryAsync("(Get-Location).Path");
            if (!string.IsNullOrWhiteSpace(pwd))
            {
                var path = pwd.Trim().Replace('/', '\\');
                if (Directory.Exists(path))
                    _lastDir = path;
            }
        }
        catch (Exception) { /* routine: cwd may have been removed underneath us */ }
    }

    private static async Task RunPromptCommandAsync(PwshWorker worker)
    {
        try
        {
            var cmd = await worker.QueryAsync("if ($env:PROMPT_COMMAND) { $env:PROMPT_COMMAND } else { '' }");
            if (!string.IsNullOrWhiteSpace(cmd))
            {
                cmd = cmd.Trim();
                try
                {
                    var pwshCmd = BashTranspiler.Transpile(cmd);
                    await worker.ExecuteAsync(pwshCmd, CancellationToken.None);
                }
                catch (Exception ex) { Console.Error.WriteLine($"[ps-bash] warning: PROMPT_COMMAND failed: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[ps-bash] warning: prompt command failed: {ex.Message}"); }
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
        catch (Exception) { /* routine: cd target inaccessible; shell continues */ }
    }

    private static async Task<string> BuildPromptAsync(PwshWorker worker)
    {
        // Check if user has set PS1
        var ps1 = await GetPS1Async(worker);
        if (ps1 is not null)
            return ExpandPS1(ps1);

        // Fall back to built-in prompt
        return BuildBuiltinPrompt();
    }

    private static string BuildBuiltinPrompt()
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

    private static async Task<string?> GetPS1Async(PwshWorker worker)
    {
        try
        {
            var result = await worker.QueryAsync("$env:PS1");
            if (string.IsNullOrWhiteSpace(result))
                return null;
            return result.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string ExpandPS1(string ps1)
    {
        var cwd = _lastDir;
        if (cwd.StartsWith(_homeDir))
            cwd = "~" + cwd[_homeDir.Length..];

        var user = Environment.UserName;
        var host = Environment.MachineName.ToLowerInvariant();

        var isAdmin = OperatingSystem.IsWindows()
            && System.Security.Principal.WindowsIdentity.GetCurrent()?.Owner?.IsWellKnown(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) == true;
        var promptChar = isAdmin ? '#' : '$';

        var sb = new StringBuilder();
        int i = 0;
        while (i < ps1.Length)
        {
            if (ps1[i] == '\\' && i + 1 < ps1.Length)
            {
                switch (ps1[i + 1])
                {
                    case 'u':
                        sb.Append(user);
                        i += 2;
                        continue;
                    case 'h':
                        sb.Append(host);
                        i += 2;
                        continue;
                    case 'w':
                        sb.Append(cwd);
                        i += 2;
                        continue;
                    case 'W':
                        // Basename of cwd
                        sb.Append(Path.GetFileName(cwd));
                        i += 2;
                        continue;
                    case '$':
                        sb.Append(promptChar);
                        i += 2;
                        continue;
                    case 'd':
                        // Date in weekday month date format
                        sb.Append(DateTime.Now.ToString("ddd MMM dd"));
                        i += 2;
                        continue;
                    case 't':
                        // 24-hour time HH:MM:SS
                        sb.Append(DateTime.Now.ToString("HH:mm:ss"));
                        i += 2;
                        continue;
                    case 'T':
                        // 12-hour time HH:MM:SS
                        sb.Append(DateTime.Now.ToString("hh:mm:ss"));
                        i += 2;
                        continue;
                    case '@':
                        // 12-hour time with am/pm
                        sb.Append(DateTime.Now.ToString("hh:mmtt").ToLowerInvariant());
                        i += 2;
                        continue;
                    case 'n':
                        sb.AppendLine();
                        i += 2;
                        continue;
                    case 's':
                        sb.Append("ps-bash");
                        i += 2;
                        continue;
                    case 'v':
                    case 'V':
                        // Version (not really applicable)
                        i += 2;
                        continue;
                    case '[':
                        // Begin non-printing chars (for ANSI escape handling)
                        // Find closing ]
                        var closeIdx = ps1.IndexOf('\\', i + 2);
                        if (closeIdx > 0 && closeIdx + 1 < ps1.Length && ps1[closeIdx + 1] == ']')
                        {
                            // Skip the content between \[ and \]
                            i = closeIdx + 2;
                            continue;
                        }
                        goto default;
                    default:
                        // Unknown escape, treat as literal
                        sb.Append(ps1[i]);
                        i++;
                        continue;
                }
            }
            else
            {
                sb.Append(ps1[i]);
                i++;
            }
        }
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
        catch (Exception) { /* routine: not a git repo or HEAD unreadable; prompt falls back */ }
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
        catch (Exception) { /* routine: git index unreadable; prompt reports clean */ }
        return true;
    }

    private static async Task<string?> ReadInputAsync(PwshWorker worker)
    {
        var prompt = await BuildPromptAsync(worker);
        var line = _lineEditor is not null
            ? await _lineEditor.ReadLineAsync(prompt)
            : Console.ReadLine();
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

        // Track the last non-whitespace character so we can detect trailing
        // pipe/and-or operators (|, &) that make a statement incomplete.
        int lastNonWsPos = -1;
        char lastNonWsChar = '\0';
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];

            if (inSingleQuote)
            {
                if (c == '\'') inSingleQuote = false;
                lastNonWsPos = i; lastNonWsChar = c;
                i++;
                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '\\' && i + 1 < input.Length) { lastNonWsPos = i; lastNonWsChar = c; i += 2; continue; }
                if (c == '"') inDoubleQuote = false;
                lastNonWsPos = i; lastNonWsChar = c;
                i++;
                continue;
            }

            if (c == '\'') { inSingleQuote = true; lastNonWsPos = i; lastNonWsChar = c; i++; continue; }
            if (c == '"') { inDoubleQuote = true; lastNonWsPos = i; lastNonWsChar = c; i++; continue; }
            if (c == '\\' && i + 1 < input.Length) { lastNonWsPos = i; lastNonWsChar = c; i += 2; continue; }

            if (c == '#')
            {
                while (i < input.Length && input[i] != '\n') i++;
                continue;
            }

            if (c == '{') { braceDepth++; lastNonWsPos = i; lastNonWsChar = c; i++; continue; }
            if (c == '}') { braceDepth--; lastNonWsPos = i; lastNonWsChar = c; i++; continue; }
            if (c == '(') { parenDepth++; lastNonWsPos = i; lastNonWsChar = c; i++; continue; }
            if (c == ')')
            {
                parenDepth--;
                lastNonWsPos = i; lastNonWsChar = c;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c) || c == ';' || c == '|' || c == '&' || c == '<' || c == '>' || c == '\n')
            {
                if (!char.IsWhiteSpace(c))
                {
                    lastNonWsPos = i;
                    lastNonWsChar = c;
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
                lastNonWsPos = wordStart;
                lastNonWsChar = word[^1];
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

        // Detect trailing pipe or and-or operator: the last non-whitespace
        // character is '|' or '&', making the statement incomplete.
        if (lastNonWsPos >= 0)
        {
            if (lastNonWsChar == '|' || lastNonWsChar == '&')
                return true;
        }

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
        // PSBASH_HOME overrides the home directory used to locate .psbashrc.
        // This is used by tests to isolate the rc file without touching the real
        // user profile.  In production this env var is unset and the real home is used.
        var homeDir = Environment.GetEnvironmentVariable("PSBASH_HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rcPath = Path.Combine(homeDir, ".psbashrc");
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
        catch (ParseException ex)
        {
            Console.Error.WriteLine($"ps-bash: ~/.psbashrc: syntax error: {ex.Message}");
            return;
        }

        var rcTimeoutSecs = 10;
        var envTimeout = Environment.GetEnvironmentVariable("PSBASH_RC_TIMEOUT");
        if (int.TryParse(envTimeout, out var parsed) && parsed > 0) rcTimeoutSecs = parsed;
        else if (envTimeout == "0") rcTimeoutSecs = 0; // 0 = disable timeout
        var debug = Environment.GetEnvironmentVariable("PSBASH_DEBUG") == "1";

        if (debug)
        {
            Console.Error.WriteLine("[ps-bash] rc bash ----");
            Console.Error.WriteLine(filtered.ToString());
            Console.Error.WriteLine("[ps-bash] rc pwsh ----");
            Console.Error.WriteLine(pwshCommand);
            Console.Error.WriteLine("[ps-bash] rc end ----");
        }

        using var rcCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var execTask = worker.ExecuteAsync(pwshCommand, rcCts.Token);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(rcTimeoutSecs), rcCts.Token);
        var winner = await Task.WhenAny(execTask, timeoutTask);
        if (winner == execTask)
        {
            try { await execTask; } catch (OperationCanceledException) { }
            if (debug) Console.Error.WriteLine($"[ps-bash] rc sourced in {sw.ElapsedMilliseconds}ms");
        }
        else
        {
            rcCts.Cancel();
            try { execTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            Console.Error.WriteLine(
                $"[ps-bash] warning: ~/.psbashrc exceeded {rcTimeoutSecs}s and was abandoned. " +
                $"Shell will continue; some rc settings may not have applied. " +
                $"Set PSBASH_RC_TIMEOUT=<seconds> to extend, or PSBASH_DEBUG=1 to see what was sent.");
            // Intentionally do not await execTask — if ExecuteAsync ignores
            // cancellation, we must still return so the prompt can appear.
        }
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

        var sb = new StringBuilder();
        int pos = 0;

        while (pos < input.Length)
        {
            // Skip leading whitespace
            while (pos < input.Length && char.IsWhiteSpace(input[pos]))
                sb.Append(input[pos++]);

            if (pos >= input.Length)
                break;

            // Extract the next word
            int start = pos;
            bool quoted = false;
            char quoteChar = '\0';

            while (pos < input.Length)
            {
                char c = input[pos];
                if (quoted)
                {
                    if (c == quoteChar) quoted = false;
                    pos++;
                }
                else if (c == '"' || c == '\'')
                {
                    quoted = true;
                    quoteChar = c;
                    pos++;
                }
                else if (c == '\\')
                {
                    pos += 2;
                }
                else if (char.IsWhiteSpace(c) || c == ';' || c == '|' || c == '(' || c == '<' || c == '>')
                {
                    break;
                }
                else if (c == '&')
                {
                    if (pos + 1 < input.Length && input[pos + 1] == '&')
                        break;
                    break;
                }
                else
                {
                    pos++;
                }
            }

            var word = input[start..pos];

            if (Aliases.TryGetValue(word, out var expansion))
                sb.Append(expansion);
            else
                sb.Append(word);

            // Copy separator until next word
            while (pos < input.Length)
            {
                char c = input[pos];
                if (c == '&' && pos + 1 < input.Length && input[pos + 1] == '&')
                {
                    sb.Append("&&");
                    pos += 2;
                    break;
                }
                if (c == '|')
                {
                    if (pos + 1 < input.Length && input[pos + 1] == '|')
                    {
                        sb.Append("||");
                        pos += 2;
                        break;
                    }
                    sb.Append('|');
                    pos++;
                    break;
                }
                if (c == ';')
                {
                    sb.Append(';');
                    pos++;
                    break;
                }
                if (c == '(' || c == '<' || c == '>')
                {
                    sb.Append(c);
                    pos++;
                    break;
                }
                if (char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                    pos++;
                    continue;
                }
                break;
            }
        }

        return sb.ToString();
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

    private static async Task<PwshWorker> EnsureWorkerAsync(PwshWorker worker, string pwshPath)
    {
        if (worker.HasExited)
        {
            try { await worker.DisposeAsync(); } catch { }
            Console.Error.WriteLine("[ps-bash] worker died; respawning...");
            return await StartWorkerAsync(pwshPath);
        }
        return worker;
    }

    private static async ValueTask DisposeWorkerAsync(PwshWorker worker)
    {
        try { await worker.DisposeAsync(); }
        catch (Exception) { /* routine: worker may already be dead on shutdown */ }
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    private static void EnsureVirtualTerminalEnabled()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (GetConsoleMode(handle, out uint mode))
                SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[ps-bash] warning: failed to enable virtual terminal: {ex.Message}"); }
    }

    // LineEditor reads keys via Console.ReadKey and dispatches on ConsoleKey.UpArrow,
    // Backspace, etc. Those values are only populated when Windows delivers cooked
    // key records — i.e., VIRTUAL_TERMINAL_INPUT is OFF. Child processes (e.g. node
    // CLIs that exit via Ctrl+C without restoring state) commonly leave VT input
    // enabled, after which arrows arrive as raw ESC sequences the editor can't parse.
    internal static uint ComputeRestoredInputMode(uint current)
    {
        return (current | ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT)
               & ~ENABLE_VIRTUAL_TERMINAL_INPUT;
    }

    private static void EnsureConsoleInputRestored()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = GetStdHandle(STD_INPUT_HANDLE);
            if (GetConsoleMode(handle, out uint mode))
            {
                var desired = ComputeRestoredInputMode(mode);
                if (mode != desired)
                    SetConsoleMode(handle, desired);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[ps-bash] warning: failed to restore console input mode: {ex.Message}"); }
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (!_suspendCancel)
            _currentCts?.Cancel();
    }
}
