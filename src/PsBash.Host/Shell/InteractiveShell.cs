using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;

namespace PsBash.Host.Shell;

public static class InteractiveShell
{
    private const string ContinuationPrompt = "> ";

    // Alias table + expansion live in AliasExpander; the interactive REPL routes
    // alias/unalias lines through it and shares its table with tab completion.

    private static readonly string[] OpenKeywords = ["if", "for", "while", "until", "case", "do", "{", "(", "function"];
    private static readonly string[] CloseKeywords = ["fi", "done", "esac", "}", ")"];

    private static string _homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string _lastDir = Environment.CurrentDirectory;
    private static LineEditor? _lineEditor;
    private static IHistoryStore? _historyStore;
    private static string _sessionId = Guid.NewGuid().ToString();
    private static string? _lastCommand;

    /// <summary>
    /// Run the interactive REPL against an externally-provided worker (the
    /// SdkWorker owned by ps-bash-host). The caller owns the worker lifetime;
    /// this method will not dispose or respawn it. Ctrl+C cancels the in-flight
    /// PS pipeline but keeps the worker alive for the next prompt.
    /// </summary>
    public static async Task<int> RunAsync(IWorker worker, bool noProfile = false)
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        EnsureVirtualTerminalEnabled();

        using var loading = LoadingIndicator.Start("Loading ps-bash");

        var cts = new CancellationTokenSource();
        _currentCts = cts;

        // Initialize history store. PSBASH_HOME overrides the home directory used to
        // locate the history DB so that tests can isolate history to a temp directory
        // without touching the real user profile.
        var historyHomeDir = Environment.GetEnvironmentVariable("PSBASH_HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var psbashDir = Path.Combine(historyHomeDir, ".psbash");
        Directory.CreateDirectory(psbashDir);

        var dbPath = Path.Combine(psbashDir, "history.db");
        _historyStore = new SqliteHistoryStore(dbPath);

        // Initialize LineEditor with history store and the async completion engine. The engine
        // composes the static TabCompleter base set with live, runspace-backed command-name
        // completion (auto-loaded cmdlets, dot-sourced functions, module commands) via the worker.
        var completionEngine = new CompletionEngine(
            AliasExpander.Aliases,
            cwd: () => _lastDir,
            lastCommand: () => _lastCommand,
            history: _historyStore,
            worker: worker);
        CommandAssistProviderRunner? commandAssistRunner = null;
        string? commandAssistConfigError = null;
        if (Environment.GetEnvironmentVariable("PSBASH_AI_DISABLE") == "1")
        {
            commandAssistConfigError = "command assist is disabled by PSBASH_AI_DISABLE=1.";
        }
        else try
        {
            commandAssistRunner = new CommandAssistProviderRunner(CommandAssistConfig.Load());
        }
        catch (CommandAssistProviderException ex)
        {
            commandAssistConfigError = ex.Message;
        }
        _lineEditor = new LineEditor(
            _historyStore,
            (line, cursor, ct) => completionEngine.CompleteAsync(line, cursor, ct),
            cwd: null,
            aliases: AliasExpander.Aliases,
            flagHintProvider: (line, cursor, ct) => completionEngine.GetFlagHintsAsync(line, cursor, ct),
            commandAssist: (request, ct) => commandAssistRunner is not null
                ? RunCommandAssistWithReviewAsync(commandAssistRunner, request, _lastDir, ct)
                : throw new CommandAssistProviderException(commandAssistConfigError ?? "AI provider config is unavailable."));

        if (!noProfile)
        {
            loading.Update("Sourcing ~/.psbashrc");
            if (!await SourceRcFileAsync(worker, cts))
                return 130;
        }

        loading.Finish();

        while (true)
        {
            try
            {
                if (worker.HasExited)
                {
                    Console.Error.WriteLine("[ps-bash] worker exited unexpectedly.");
                    if (_historyStore is IDisposable d0) d0.Dispose();
                    return 1;
                }
                cts.Dispose();
                cts = new CancellationTokenSource();
                _currentCts = cts;

                // Restore console modes before every prompt. A prior command can leave the
                // console in a state the line editor can't drive — most commonly a node-based
                // CLI (e.g. `code .`, which spawns code.cmd -> node) that enables
                // VIRTUAL_TERMINAL_INPUT and never restores it, after which arrow keys arrive
                // as raw ESC sequences and each keystroke triggers a garbled redraw ("redrawing
                // too much"). The TryRunDirect external path already restores after the process
                // exits, but `code .` falls through to the worker path (Process.Start on a .cmd
                // with UseShellExecute=false throws), which did not. Both Ensure* calls are
                // idempotent no-ops when the mode is already correct, so running them per prompt
                // is cheap and covers every execution path uniformly. Windows-only (early-return).
                EnsureVirtualTerminalEnabled();
                EnsureConsoleInputRestored();

                var input = await ReadInputAsync(worker);
                if (input is null)
                {
                    Console.WriteLine();
                    if (_historyStore is IDisposable disposable)
                        disposable.Dispose();
                    return 0;
                }

                var trimmed = input.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (IsExitCommand(trimmed, out var exitCode))
                {
                    if (_historyStore is IDisposable disposable)
                        disposable.Dispose();
                    return exitCode;
                }

                trimmed = ProcessAliasCommand(trimmed);
                if (trimmed.Length == 0)
                    continue;

                // Preserve the pre-expansion input for history (Ctrl+R) and
                // suggester keying so the user sees what they typed (the alias
                // name) rather than the expansion. The expanded form still
                // drives transpile + execute below.
                var originalInput = trimmed;
                trimmed = ExpandAliases(trimmed);

                // Interactive `complete` (bash programmable completion): register/remove a Tier-1
                // word-list spec in the in-process registry the tab completer reads, then move on.
                // There is no `complete` cmdlet to transpile to; intercepting here (like alias) keeps
                // the spec on the prompt side where CompletionEngine can consult it. A cheap prefix
                // check gates the parse.
                if ((trimmed == "complete" || trimmed.StartsWith("complete ", StringComparison.Ordinal))
                    && BashCompletionRegistry.TryApplyCompleteCommand(trimmed))
                {
                    continue;
                }

                // Interactive `source FILE` / `. FILE`: route the file's
                // alias/unalias lines through the same in-process alias table the
                // startup rc path uses, then execute the rest. Without this, an
                // interactive source goes through Invoke-BashSource in the worker
                // and its aliases land only in the worker's module-scope table —
                // never reaching the interactive expander, so the user sees no
                // alias update. Only the simple single-file form is intercepted;
                // complex forms (extra args, redirects, pipelines) fall through to
                // Invoke-BashSource below. A cheap prefix check gates the parse.
                if ((trimmed.StartsWith("source ", StringComparison.Ordinal)
                        || trimmed.StartsWith(". ", StringComparison.Ordinal))
                    && TryGetInteractiveSourceTarget(trimmed, out var sourceTarget)
                    && File.Exists(sourceTarget))
                {
                    var sourceStopwatch = Stopwatch.StartNew();
                    int? sourceExit = null;
                    try
                    {
                        await SourceFileAsync(worker, cts, sourceTarget);
                        await SyncWorkerCwdAsync(worker);
                        try
                        {
                            var ec = await worker.QueryAsync("$LASTEXITCODE");
                            if (int.TryParse(ec?.Trim(), out var code))
                                sourceExit = code;
                        }
                        catch (Exception) { /* routine: worker busy or query raced */ }
                        await RunPromptCommandAsync(worker);
                    }
                    catch (OperationCanceledException)
                    {
                        Console.Error.WriteLine("^C");
                        sourceExit = null;
                    }
                    finally
                    {
                        sourceStopwatch.Stop();
                        await RecordCommandAsync(originalInput, sourceExit, sourceStopwatch.ElapsedMilliseconds);
                    }
                    continue;
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
                    // SdkWorker: _ps.Stop() already fired via ct.Register; runspace still alive.
                }
                finally
                {
                    stopwatch.Stop();
                    await RecordCommandAsync(originalInput, exitCodeResult, stopwatch.ElapsedMilliseconds);
                }
            }
            catch (IOException)
            {
                Console.Error.WriteLine("[ps-bash] worker connection lost; exiting.");
                if (_historyStore is IDisposable d) d.Dispose();
                return 1;
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

        if (cmdName is "cls" or "clear" or "reset")
        {
            Console.Clear();
            exitCode = 0;
            return true;
        }

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

    /// <summary>
    /// Recognizes the simple interactive forms <c>source FILE</c> and <c>. FILE</c>
    /// (exactly one file operand, no redirects/heredocs/extra args) and resolves
    /// the operand to an absolute path — handling a leading <c>~</c>, single/double
    /// quotes, and relative paths (against the current dir). Returns false for any
    /// more complex form so it falls through to the normal Invoke-BashSource path.
    /// </summary>
    internal static bool TryGetInteractiveSourceTarget(string bashInput, out string resolvedPath)
    {
        resolvedPath = "";

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
        if (simple.Redirects.Length > 0 || simple.HereDocs.Length > 0)
            return false;
        if (simple.Words.Length != 2)
            return false;

        var cmd = PsEmitter.GetLiteralValue(simple.Words[0]);
        if (cmd is not ("source" or "."))
            return false;

        return TryResolveWordToPath(simple.Words[1], out resolvedPath);
    }

    /// <summary>
    /// Resolves a <see cref="CompoundWord"/> made only of literal / quoted-literal
    /// parts (plus an optional leading current-user <c>~</c>) to an absolute path.
    /// Returns false when the word contains variable refs, command subs, globs, or a
    /// <c>~user</c> form — cases the caller hands back to the general source path.
    /// </summary>
    private static bool TryResolveWordToPath(CompoundWord word, out string resolvedPath)
    {
        resolvedPath = "";
        var sb = new StringBuilder();

        for (int i = 0; i < word.Parts.Length; i++)
        {
            var part = word.Parts[i];
            switch (part)
            {
                case WordPart.Literal lit:
                    sb.Append(lit.Value);
                    break;
                case WordPart.SingleQuoted sq:
                    sb.Append(sq.Value);
                    break;
                case WordPart.DoubleQuoted dq when dq.Parts.All(p => p is WordPart.Literal):
                    foreach (var p in dq.Parts)
                        sb.Append(((WordPart.Literal)p).Value);
                    break;
                case WordPart.TildeSub ts when i == 0 && ts.User is null:
                    sb.Append(_homeDir);
                    // BashParser consumes the '/' after '~' (so it never reaches the
                    // following Literal), and PsEmitter reinserts a separator. Mirror
                    // that here, otherwise `~/.psbashrc` resolves to
                    // `C:\Users\andyb.psbashrc` (no separator) and File.Exists fails,
                    // silently falling through to Invoke-BashSource.
                    if (i + 1 < word.Parts.Length)
                        sb.Append(Path.DirectorySeparatorChar);
                    break;
                default:
                    return false;
            }
        }

        var raw = sb.ToString();
        if (raw.Length == 0)
            return false;

        // Expand a literal leading ~ as well. Depending on lexer state the parser
        // may surface `~/foo` as a TildeSub part (handled above) or as a plain
        // literal `~/foo`; handle the literal form here so `source ~/.psbashrc`
        // resolves identically in both representations.
        if (raw == "~")
            raw = _homeDir;
        else if (raw.StartsWith("~/", StringComparison.Ordinal) || raw.StartsWith("~\\", StringComparison.Ordinal))
            raw = _homeDir + raw[1..];

        var baseDir = Directory.Exists(_lastDir) ? _lastDir : Environment.CurrentDirectory;
        try
        {
            resolvedPath = Path.GetFullPath(raw, baseDir);
        }
        catch
        {
            return false;
        }
        return true;
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

    private static async Task SyncWorkerCwdAsync(IWorker worker)
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

    private static async Task RunPromptCommandAsync(IWorker worker)
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

    private static async Task<string> BuildPromptAsync(IWorker worker)
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

    private static async Task<string?> GetPS1Async(IWorker worker)
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
        => ExpandPS1(ps1, _lastDir, _homeDir);

    internal static string ExpandPS1(string ps1, string cwd, string home)
    {
        if (cwd.StartsWith(home))
        {
            cwd = "~" + cwd[home.Length..];
            // Normalise the home-prefixed suffix to the platform separator.
            // Bash on POSIX renders \w as ~/foo/bar, never ~\foo\bar — the
            // shell stores _lastDir using PowerShell's separator (which on
            // POSIX is '/' but unit tests deliberately feed Windows-style
            // fixtures cross-platform to validate this normalisation). Only
            // touch the home-prefixed case so non-home paths
            // (BackslashW_CwdNotUnderHome_ReturnsFullPath) round-trip
            // untouched.
            if (Path.DirectorySeparatorChar != '\\')
                cwd = cwd.Replace('\\', Path.DirectorySeparatorChar);
        }

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
                        // Skip \[...\] non-printing marker block (ANSI escapes in bash PS1)
                        var closeIdx = ps1.IndexOf(@"\]", i + 2, StringComparison.Ordinal);
                        if (closeIdx >= 0)
                        {
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

    private static async Task<string?> ReadInputAsync(IWorker worker)
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

    private static async Task<bool> SourceRcFileAsync(IWorker worker, CancellationTokenSource cts)
    {
        // PSBASH_HOME overrides the home directory used to locate .psbashrc.
        // This is used by tests to isolate the rc file without touching the real
        // user profile.  In production this env var is unset and the real home is used.
        var homeDir = Environment.GetEnvironmentVariable("PSBASH_HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rcPath = Path.Combine(homeDir, ".psbashrc");
        if (!File.Exists(rcPath))
            return true;

        return await SourceFileAsync(worker, cts, rcPath, "~/.psbashrc");
    }

    /// <summary>
    /// Source a bash file the way the interactive shell needs it: each
    /// <c>alias</c>/<c>unalias</c> line is routed through <see cref="ProcessAliasCommand"/>
    /// so its definition lands in the in-process <see cref="AliasExpander.Aliases"/> table that
    /// drives pre-transpile alias expansion, then the remaining (non-alias,
    /// non-comment) lines are transpiled as one block and executed in the worker.
    ///
    /// This is the shared path for both startup (~/.psbashrc) and an interactive
    /// <c>source FILE</c> / <c>. FILE</c>. Routing source through here — rather than
    /// the Invoke-BashSource cmdlet that runs entirely in the worker — is what makes
    /// source'd aliases visible to the interactive expander; the cmdlet only updates
    /// the worker's module-scope alias table, which the expander never reads.
    /// Returns false only when execution was cancelled (Ctrl+C); true otherwise.
    /// </summary>
    private static async Task<bool> SourceFileAsync(
        IWorker worker, CancellationTokenSource cts, string path, string? displayName = null)
    {
        var label = displayName ?? path;

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path);
        }
        catch
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(content))
            return true;

        var filtered = new StringBuilder();
        foreach (var rawLine in content.Split('\n'))
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
            return true;

        string pwshCommand;
        try
        {
            pwshCommand = BashTranspiler.Transpile(filtered.ToString());
        }
        catch (ParseException ex)
        {
            Console.Error.WriteLine($"ps-bash: {label}: syntax error: {ex.Message}");
            return true;
        }

        var debug = Environment.GetEnvironmentVariable("PSBASH_DEBUG") == "1";

        if (debug)
        {
            Console.Error.WriteLine("[ps-bash] source bash ----");
            Console.Error.WriteLine(filtered.ToString());
            Console.Error.WriteLine("[ps-bash] source pwsh ----");
            Console.Error.WriteLine(pwshCommand);
            Console.Error.WriteLine("[ps-bash] source end ----");
        }

        try
        {
            await worker.ExecuteAsync(pwshCommand, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("^C");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ps-bash: {label}: {ex.Message}");
        }

        return true;
    }

    /// <summary>Forwards to <see cref="AliasExpander.ProcessAliasCommand"/> (alias logic lives there).</summary>
    public static string ProcessAliasCommand(string input) => AliasExpander.ProcessAliasCommand(input);

    /// <summary>Forwards to <see cref="AliasExpander.ExpandAliases"/> (alias logic lives there).</summary>
    public static string ExpandAliases(string input) => AliasExpander.ExpandAliases(input);

    private static async Task<CommandAssistResponse> RunCommandAssistWithReviewAsync(
        CommandAssistProviderRunner runner,
        CommandAssistRequest request,
        string cwd,
        CancellationToken ct)
    {
        string? providerName = null;
        while (true)
        {
            var result = await runner.GenerateAsync(request, cwd, providerName, ct).ConfigureAwait(false);
            var review = result.ToReviewRequest(cwd);
            var decision = PromptCommandAssistReview(review);
            if (decision.Action == CommandAssistReviewAction.Retry)
                continue;
            if (decision.Action == CommandAssistReviewAction.SwitchProvider)
            {
                providerName = PromptCommandAssistProviderName(runner.ProviderNames);
                if (providerName is not null)
                    continue;
                return CommandAssistResponse.Cancelled;
            }
            return CommandAssistReview.ApplyDecision(review, decision);
        }
    }

    internal static CommandAssistReviewDecision PromptCommandAssistReview(CommandAssistReviewRequest request)
    {
        Console.WriteLine("ps-bash command assist");
        Console.WriteLine($"provider: {request.ProviderName}");
        Console.WriteLine($"cwd: {request.Cwd}");
        if (!string.IsNullOrWhiteSpace(request.Explanation))
            Console.WriteLine($"explanation: {request.Explanation}");
        Console.WriteLine("command:");
        Console.WriteLine(request.Command);
        if (!request.IsExecutable)
            Console.WriteLine("note: provider output is review-only and cannot be executed directly.");
        if (request.Warnings.Count > 0)
        {
            Console.WriteLine("warning: potentially destructive command");
            foreach (var warning in request.Warnings)
                Console.WriteLine($"- {warning.Pattern}: {warning.Reason}");
        }
        Console.Write(request.IsExecutable
            ? "Action [e]xecute, [i]nsert/edit, [r]etry, [s]witch provider, [c]ancel: "
            : "Action [i]nsert/edit, [r]etry, [s]witch provider, [c]ancel: ");
        var action = Console.ReadLine()?.Trim().ToLowerInvariant();
        return action switch
        {
            "e" or "execute" when !request.IsExecutable => CommandAssistReviewDecision.Cancel(),
            "e" or "execute" when request.Warnings.Count == 0 => CommandAssistReviewDecision.Execute(),
            "e" or "execute" => ConfirmDangerousCommand(),
            "i" or "insert" or "edit" => CommandAssistReviewDecision.Insert(),
            "r" or "retry" => CommandAssistReviewDecision.Retry(),
            "s" or "switch" => CommandAssistReviewDecision.SwitchProvider(),
            _ => CommandAssistReviewDecision.Cancel(),
        };
    }

    private static CommandAssistReviewDecision ConfirmDangerousCommand()
    {
        Console.Write("Type EXECUTE to confirm the dangerous command: ");
        var confirm = Console.ReadLine()?.Trim();
        return string.Equals(confirm, "EXECUTE", StringComparison.Ordinal)
            ? CommandAssistReviewDecision.Execute(dangerousConfirmed: true)
            : CommandAssistReviewDecision.Cancel();
    }

    private static string? PromptCommandAssistProviderName(IReadOnlyList<string> providerNames)
    {
        if (providerNames.Count == 0)
        {
            Console.WriteLine("ps-bash: no AI providers are configured.");
            return null;
        }

        Console.WriteLine("configured providers:");
        foreach (var name in providerNames)
            Console.WriteLine($"- {name}");
        Console.Write("Provider name, or empty to cancel: ");
        return SelectCommandAssistProvider(providerNames, Console.ReadLine());
    }

    internal static string? SelectCommandAssistProvider(IReadOnlyList<string> providerNames, string? input)
    {
        var requested = input?.Trim();
        if (string.IsNullOrWhiteSpace(requested))
            return null;
        return providerNames.FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
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
