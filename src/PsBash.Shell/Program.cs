using System.Text;
using System.Reflection;
using PsBash.Core.Parser;
using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;
using PsBash.Shell;
using PsBash.Shell.Pty;

const long MaxCommandInputChars = 16 * 1024 * 1024;

// Reliability watchdog: on Windows, attach the current process to a Job Object
// with KILL_ON_JOB_CLOSE so the SDK host (and any other descendants) die
// atomically with ps-bash itself. This is a no-op on Linux/macOS where the
// shell's process group + SIGHUP already handles this.
JobObjectWatchdog.AttachCurrentProcess();

// Emit non-ASCII output as UTF-8 regardless of the inherited console code page
// (Dart z0GXccJmhX2H) — must run before any Console.Write. Harmless to the PTY
// path, which pumps raw bytes via Console.OpenStandardOutput() (unaffected by
// Console.OutputEncoding).
ConsoleEncoding.EnsureUtf8Output();

var debug = Environment.GetEnvironmentVariable("PSBASH_DEBUG") == "1";

// Diagnostic: when PSBASH_TRACE=<path> is set, append a line per invocation
// recording argv (and stdin redirect state) so we can see exactly how a parent
// process — e.g. the Claude Code Bash tool — is invoking us. No behavior change.
var tracePath = Environment.GetEnvironmentVariable("PSBASH_TRACE");
if (!string.IsNullOrEmpty(tracePath))
{
    try
    {
        var quoted = string.Join(' ', args.Select(a =>
            a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
        var line = $"{DateTime.Now:O} pid={Environment.ProcessId} stdinRedir={Console.IsInputRedirected} argc={args.Length} argv=[{quoted}]";
        File.AppendAllText(tracePath, line + Environment.NewLine);
    }
    catch (Exception ex) { Console.Error.WriteLine($"[ps-bash] trace write failed: {ex.Message}"); }
}

if (HostCommands.IsHostCommand(args))
{
    return await HostCommands.RunAsync(args);
}

var shellArgs = ShellArgs.Parse(args);

// Informational flags short-circuit before any host/IPC work. Without this,
// `ps-bash --version` parsed to an empty ShellArgs and fell through to the
// interactive/stdin branch, which blocks forever when no tty is attached
// (a tooling probe of `--version` would hang the caller).
if (shellArgs.ShowVersion)
{
    Console.WriteLine($"ps-bash, version {ResolveLauncherVersion()}");
    Console.WriteLine("Bash-to-PowerShell transpiler");
    return 0;
}
if (shellArgs.ShowHelp)
{
    Console.WriteLine("Usage: ps-bash [options] [script-file [args...]]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -c COMMAND        Run COMMAND (a bash string) and exit.");
    Console.WriteLine("  -i                Force interactive mode.");
    Console.WriteLine("  -l, --login       Run as a login shell (load profile).");
    Console.WriteLine("  -s                Read commands from standard input.");
    Console.WriteLine("  --noprofile,");
    Console.WriteLine("  --norc            Do not load any profile/rc file.");
    Console.WriteLine("  --unix-paths,");
    Console.WriteLine("  --windows-paths   Force unix- or windows-style path translation.");
    Console.WriteLine("  --timeout VALUE   Per-command idle timeout in seconds (default: none —");
    Console.WriteLine("                    unbounded, matching core bash). Resets on each line of");
    Console.WriteLine("                    output, so a command that keeps producing output is");
    Console.WriteLine("                    never killed for being slow. Use 'none' (or 0) to keep");
    Console.WriteLine("                    it disabled; set a positive number to bound idle runs.");
    Console.WriteLine("  --compact-output  Enable opt-in compact output mode for agent contexts.");
    Console.WriteLine("                    Equivalent env: PSBASH_COMPACT_OUTPUT=1.");
    Console.WriteLine("  --no-compact-output");
    Console.WriteLine("                    Disable compact output even when the env var is set.");
    Console.WriteLine("  --version, -V     Print the ps-bash version and exit.");
    Console.WriteLine("  --help            Print this help and exit.");
    return 0;
}

// Path mode: explicit --unix-paths / --windows-paths flag wins; otherwise
// fall back to PSBASH_UNIX_PATHS env var; otherwise default to Windows-native
// paths (no MSYS translation). Propagate the resolved choice as an env var
// so PsEmitter (in PsBash.Core, no direct Shell dependency) can read it.
bool unixPaths = shellArgs.UnixPaths
    ?? Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS") is "1" or "true";
Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", unixPaths ? "1" : "0");

// --timeout <value> sets the per-command idle timeout for this invocation by
// forwarding to PSBASH_TIMEOUT (the single knob IpcWorker reads): seconds, or
// none/0/off/infinite to disable. An explicit flag wins over an inherited env
// var so a caller can override the ambient default per-command.
if (shellArgs.Timeout is { Length: > 0 } timeoutValue)
    Environment.SetEnvironmentVariable("PSBASH_TIMEOUT", timeoutValue);

// Compact output is opt-in. The CLI flag wins over the environment; otherwise
// PSBASH_COMPACT_OUTPUT can enable the same mode for callers that cannot add a
// launcher flag. The resolved value is normalized into the environment so the
// host/output layers can read one stable switch in later pipeline stages.
bool compactOutput = shellArgs.CompactOutput ?? EnvFlags.IsTruthy("PSBASH_COMPACT_OUTPUT");
Environment.SetEnvironmentVariable("PSBASH_COMPACT_OUTPUT", compactOutput ? "1" : "0");

// All non-interactive execution (-c, stdin pipe, script file) goes through
// ps-bash-host over IPC. Default lifetime is Lifetime.Daemon: a single shared
// per-session host is spawned once (single-flighted via HostSpawnLock) and reused by
// every subsequent launcher in the same session, so a -c invocation pays the ~3 s
// runspace cold-start only on the first call — critical when an embedding parent
// (e.g. the Claude Code Bash tool) issues many commands. Per-session (not per-user)
// so independent shells/agents get their own daemon and don't contend on one host
// (see IpcTransportFactory.ResolveEndpoint). The daemon host gives each connection its OWN
// isolated runspace from a warm pool (see WorkerPool), so reuse is fast WITHOUT
// leaking session state between commands, and concurrent launchers run in parallel.
//
// Escape hatch: set PSBASH_PER_INVOCATION=1 to force the old private-host-per-call
// model (Lifetime.PerInvocation) — a fresh host process per invocation, killed on
// dispose. Used by callers that need a hard process boundary per command.
//
// Interactive mode does not use this factory: it spawns ps-bash-host --interactive
// directly so the host inherits the real tty. If the host binary is missing or
// fails to start, the invocation exits non-zero with the underlying error.
var hostLifetime = EnvFlags.IsTruthy("PSBASH_PER_INVOCATION")
    ? Lifetime.PerInvocation
    : Lifetime.Daemon;
Func<Task<IWorker>> workerFactory = async () =>
{
    var hostBinary = ResolveHostBinary()
        ?? throw new HostUnavailableException(
            "ps-bash-host binary not found. Set PSBASH_HOST=<path> or install alongside ps-bash.");

    return await IpcWorker.StartAsync(hostBinary, lifetime: hostLifetime).ConfigureAwait(false);
};

// M3: file-arg mode — ps-bash script.sh [arg1 arg2 ...]
// Check before stdin detection: a script path argument takes priority over
// stdin redirection so `ps-bash script.sh < /dev/null` does not enter stdin mode.
if (shellArgs.ScriptPath is not null)
{
    if (!File.Exists(shellArgs.ScriptPath))
    {
        Console.Error.WriteLine($"ps-bash: {shellArgs.ScriptPath}: No such file or directory");
        return 2;
    }

    if (shellArgs.ScriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
    {
        await using IWorker ps1Worker = await workerFactory();

        if (compactOutput)
            Environment.SetEnvironmentVariable("PSBASH_COMPACT_COMMAND", $"{shellArgs.ScriptPath} {string.Join(' ', shellArgs.ScriptArgs)}");
        var ps1Preamble = BuildPositionalPreamble(shellArgs.ScriptPath, shellArgs.ScriptArgs);
        var escapedPath = shellArgs.ScriptPath.Replace("'", "''");
        return await ps1Worker.ExecuteAsync(BuildInvocationCwdPreamble() + ps1Preamble + ". '" + escapedPath + "'");
    }

    // .sh execution: read, transpile, build positional preamble, execute.
    string scriptContent;
    try
    {
        scriptContent = await ReadFileTextBoundedAsync(
            shellArgs.ScriptPath,
            MaxCommandInputChars,
            "script exceeds the maximum supported command input size.").ConfigureAwait(false);
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"ps-bash: {shellArgs.ScriptPath}: {ex.Message}");
        return 2;
    }

    string? pwshScriptCommand;
    try
    {
        // Script files are stable across runs — cache the transpile on disk (keyed by content
        // hash + build + path mode) so re-running the same script skips parse+emit.
        pwshScriptCommand = TranspileCache.GetOrTranspileFile(scriptContent);
    }
    catch (ParseException ex)
    {
        Console.Error.WriteLine($"ps-bash: parse error: {ex.Message}");
        return 2;
    }

    await using IWorker scriptWorker = await workerFactory();

    if (compactOutput)
        Environment.SetEnvironmentVariable("PSBASH_COMPACT_COMMAND", $"{shellArgs.ScriptPath} {string.Join(' ', shellArgs.ScriptArgs)}");
    var preamble = BuildPositionalPreamble(shellArgs.ScriptPath, shellArgs.ScriptArgs);
    return await scriptWorker.ExecuteAsync(BuildInvocationCwdPreamble() + preamble + pwshScriptCommand);
}

// Auto-detect piped stdin: if no command given and stdin is redirected, try reading it.
if (shellArgs.ReadFromStdin || (!shellArgs.Interactive && shellArgs.Command is null && Console.IsInputRedirected))
{
    string stdinCommand;
    try
    {
        stdinCommand = await ReadTextBoundedAsync(
            Console.In,
            MaxCommandInputChars,
            "stdin command input exceeds the maximum supported size.").ConfigureAwait(false);
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"ps-bash: {ex.Message}");
        return 2;
    }

    if (string.IsNullOrEmpty(stdinCommand) && Console.IsInputRedirected)
    {
        // Parent closed the pipe without sending a command. Exit cleanly
        // rather than falling through to the interactive shell (which would
        // hang forever with no tty). Matches bash behavior: `bash < /dev/null`
        // exits 0 immediately.
        return 0;
    }
    if (!string.IsNullOrWhiteSpace(stdinCommand))
        shellArgs = shellArgs with { Command = stdinCommand };
}

if (shellArgs.Interactive || shellArgs.Command is null)
{
    // Interactive mode: spawn ps-bash-host --interactive so the host inherits
    // the real tty (Console.Clear / WindowWidth / VT all work against the
    // terminal). No fallback: if the host binary is missing, exit non-zero.
    var hostBinary = ResolveHostBinary();
    if (hostBinary is null)
    {
        Console.Error.WriteLine(
            "ps-bash: ps-bash-host binary not found. Set PSBASH_HOST=<path> or install alongside ps-bash.");
        return 127;
    }

    // PTY-2 / PTY-3 path: when PSBASH_PTY=1 (still opt-in), allocate a real
    // pseudo-terminal in the launcher and attach its slave to the host as
    // stdin/stdout/stderr via PtySpawner. The launcher then pumps bytes
    // between its own stdio (in raw mode, per PTY-3) and the PTY master.
    // Gated on the env var so the legacy inherited-stdio path remains the
    // default; pre-Win10-1809 platforms fall back automatically via the
    // PlatformNotSupportedException catch below. Signal forwarding
    // (SIGWINCH, Ctrl-C explicit injection) is intentionally deferred.
    //
    // PTY-12 non-tty fallback: if the launcher's own stdin is redirected
    // (CI log capture, a GUI process piping into ps-bash, `ps-bash < /dev/null`)
    // there are no keystrokes to pump and no terminal signals to forward, so
    // PtyLaunchPolicy declines the PTY path even when PSBASH_PTY=1 is set. The
    // launcher then behaves exactly like the legacy pipe-based interactive
    // harness below.
    var ptyOptIn = Environment.GetEnvironmentVariable("PSBASH_PTY") is "1" or "true";
    if (PtyLaunchPolicy.ShouldUsePty(ptyOptIn, Console.IsInputRedirected))
    {
        try
        {
            return await RunHostUnderPtyAsync(hostBinary, shellArgs);
        }
        catch (PlatformNotSupportedException ex)
        {
            // PTY-3 Windows-legacy fallback: ConPtyAdapter throws
            // PlatformNotSupportedException on pre-Win10-1809 (build < 17763),
            // and PtyAllocator throws it on unrecognized platforms. Fall back
            // to the legacy inherited-stdio path with a warning so the user
            // can still run the shell, just without TUI passthrough.
            Console.Error.WriteLine(
                $"ps-bash: PSBASH_PTY=1 requested but the platform does not support a pseudo-terminal ({ex.Message}). " +
                "Falling back to inherited-stdio mode (TUI apps will be line-buffered).");
        }
    }

    var psi = new System.Diagnostics.ProcessStartInfo(hostBinary)
    {
        UseShellExecute = false,
        // No stdio redirection — host inherits the real tty.
    };
    psi.ArgumentList.Add("--interactive");
    psi.ArgumentList.Add($"--launcher-pid={Environment.ProcessId}");
    if (shellArgs.NoProfile) psi.ArgumentList.Add("--no-profile");

    using var hostProc = System.Diagnostics.Process.Start(psi);
    if (hostProc is null)
    {
        // Process.Start returns null when no new process was started; the old `!`
        // turned that into an NRE on the next line. Fail with a clear diagnostic.
        await Console.Error.WriteLineAsync($"ps-bash: failed to start host process '{hostBinary}'.");
        return 1;
    }
    await hostProc.WaitForExitAsync();
    return hostProc.ExitCode;
}

// For the -c (non-interactive) path, start a parent-death watcher so we never
// become an orphan if the launching process (testhost, Claude Code, CI runner)
// crashes or is force-killed. The Job Object above handles "kill our children
// when we die"; this handles "kill us when our parent dies."
var parentPid = JobObjectWatchdog.GetCurrentParentProcessId();
JobObjectWatchdog.StartParentDeathWatcher(parentPid);

// Parity with interactive shell: expand aliases before transpile. In -c mode
// Aliases is empty (profile loading only happens in the interactive REPL), so
// this is a no-op early-return today — but it means every -c invocation
// follows the same ExpandAliases → Transpile → worker.ExecuteAsync sequence
// as the interactive loop, so future alias wiring stays unified.
var bashCommand = shellArgs.Command;
if (compactOutput)
    Environment.SetEnvironmentVariable("PSBASH_COMPACT_COMMAND", bashCommand);

string? pwshCommand;
if (shellArgs.RawPowerShell)
{
    // PTY-9 follow-on: `--ps` passthrough — forward the command body to the
    // host runspace as-is, bypassing the bash transpiler. This is the
    // in-band entry point for raw PowerShell probe scripts (e.g.
    // `[Console]::ReadKey($true)`) that have no bash equivalent. Parse
    // errors surface from the host runspace, same as any other PowerShell
    // invocation.
    pwshCommand = bashCommand;
}
else
{
    try
    {
        pwshCommand = BashTranspiler.Transpile(bashCommand);
    }
    catch (ParseException ex)
    {
        // The bash parser rejected this input. If it is actually PowerShell
        // (e.g. `if (Test-Path x) { ... }`, `Get-Content`, `[Type]::Method`),
        // run it as PowerShell directly — the host runspace IS PowerShell. This
        // makes ps-bash forgiving of mixed bash/PowerShell input (e.g. an agent
        // whose harness advertises a PowerShell shell). Bash-first is preserved:
        // valid bash always transpiles, so only un-transpilable input reaches
        // here. Opt out with PSBASH_NO_PS_FALLBACK=1 to always surface the bash
        // parse error.
        if (!EnvFlags.IsTruthy("PSBASH_NO_PS_FALLBACK") && LooksLikePowerShell(bashCommand))
        {
            pwshCommand = bashCommand;
            if (debug)
                Console.Error.WriteLine("[ps-bash] note: bash parse failed; input detected as PowerShell, running raw");
        }
        else
        {
            Console.Error.WriteLine($"ps-bash: parse error: {ex.Message}");
            return 2;
        }
    }
}

// bash `-c 'string' name args...` sets $0=name and $1.. from the trailing operands.
// Args.Parse captures them into ScriptArgs; apply them here (they were previously
// captured but never used, so $0/$1/$@/$# were empty in -c mode). No-op when no
// trailing operands are present — the common `ps-bash -c "cmd"` invocation is unchanged.
if (pwshCommand is not null && shellArgs.ScriptArgs.Length > 0)
{
    pwshCommand = BuildPositionalPreamble(
        shellArgs.ScriptArgs[0],
        shellArgs.ScriptArgs.Skip(1).ToArray()) + pwshCommand;
}

if (debug)
{
    // Tag EVERY line of multi-line debug output so AssertOracle's
    // StripDebugLines (which filters by leading "[ps-bash] ") removes
    // all of it. A bare WriteLine of a multi-line transpiled
    // PowerShell script (heredoc-shaped @"..."@ bodies, etc.) would
    // leak the body lines onto stderr as untagged content, and the
    // differential test would flag them as a mismatch.
    static string Tag(string label, string value) =>
        value.Contains('\n')
            ? "[ps-bash] " + label + value.Replace("\n", "\n[ps-bash] ")
            : "[ps-bash] " + label + value;
    Console.Error.WriteLine(Tag("input:      ", bashCommand));
    Console.Error.WriteLine(Tag("transpiled: ", pwshCommand ?? string.Empty));
}

// Infrastructure failures (host won't start, host hangs, IPC breaks) must
// surface as a one-line `ps-bash: ...` diagnostic and a defined exit code —
// never an unhandled-exception stack trace. Before this guard, a host timeout
// propagated a raw OperationCanceledException out of Main and the runtime
// dumped a managed stack trace with exit code 82, which is what an embedding
// parent (e.g. the Claude Code Bash tool) saw when the host wedged.
int exitCode;
try
{
    await using IWorker worker = await workerFactory();
    exitCode = await worker.ExecuteAsync(BuildInvocationCwdPreamble() + pwshCommand);
}
catch (TimeoutException ex)
{
    // Host did not accept a connection / respond within the call budget.
    // Mirror GNU `timeout`'s exit code so callers can detect the condition.
    Console.Error.WriteLine(
        ex.Message.StartsWith("ps-bash:", StringComparison.Ordinal) ? ex.Message : $"ps-bash: {ex.Message}");
    return 124;
}
catch (HostUnavailableException ex)
{
    Console.Error.WriteLine($"ps-bash: {ex.Message}");
    return 125;
}
catch (Exception ex) when (ex is System.IO.IOException
                              or System.Net.Sockets.SocketException
                              or OperationCanceledException)
{
    Console.Error.WriteLine($"ps-bash: host communication failed: {ex.Message}");
    return 125;
}

if (debug)
{
    Console.Error.WriteLine($"[ps-bash] exit:       {exitCode}");
}

return exitCode;

// Builds a PowerShell preamble that sets $global:BashPositional and
// $global:BashPositional0 so that $1..$9, $@, $#, and $0 resolve
// correctly inside a transpiled .sh script.
static string BuildPositionalPreamble(string script0, string[] scriptArgs)
{
    static string QuotePs(string s) =>
        "'" + s.Replace("'", "''") + "'";

    var scriptName = QuotePs(Path.GetFileName(script0));
    var argList = string.Join(", ", scriptArgs.Select(QuotePs));
    var arrayLiteral = scriptArgs.Length == 0 ? "@()" : $"@({argList})";

    return $"$global:BashPositional0 = {scriptName}; $global:BashPositional = {arrayLiteral}; ";
}

static async Task<string> ReadFileTextBoundedAsync(string path, long maxChars, string tooLargeMessage)
{
    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite,
        bufferSize: 8192,
        FileOptions.SequentialScan);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    return await ReadTextBoundedAsync(reader, maxChars, tooLargeMessage).ConfigureAwait(false);
}

static async Task<string> ReadTextBoundedAsync(TextReader reader, long maxChars, string tooLargeMessage)
{
    var sb = new StringBuilder();
    var buffer = new char[4096];
    int read;
    while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
    {
        if (sb.Length + read > maxChars)
            throw new IOException(tooLargeMessage);
        sb.Append(buffer, 0, read);
    }

    return sb.ToString();
}

// Heuristic: does this input look like PowerShell rather than bash? Only consulted
// AFTER the bash transpiler has already failed to parse it, so it need not exclude
// valid bash — it only has to distinguish "PowerShell the user meant" from "a bash
// typo". The signals below are constructs that are unambiguously PowerShell and
// NOT valid bash (so e.g. `[ $a -eq $b ]`, which bash and PowerShell share, is
// deliberately NOT a signal). Pure string/regex so it stays AOT-safe.
static bool LooksLikePowerShell(string input)
{
    if (string.IsNullOrWhiteSpace(input)) return false;

    // A Pascal-case hyphenated cmdlet token: Get-Content, Test-Path, New-Item, …
    // Bash command names are lower-case, so a Capitalized-Capitalized token is a
    // strong PowerShell signal.
    if (System.Text.RegularExpressions.Regex.IsMatch(input, @"\b[A-Z][a-z]+-[A-Z][A-Za-z]+\b"))
        return true;

    // A .NET static member access: [System.IO.Path]::GetFullPath(...), [Console]::…
    if (System.Text.RegularExpressions.Regex.IsMatch(input, @"\[[\w.]+\]::"))
        return true;

    // PowerShell control flow with a brace block: `if (...) { }`, `while (...) {`,
    // `foreach (...) {`, `switch (...) {`. Bash uses `if …; then … fi` / `do … done`
    // and would never put a `{` block directly after `(...)`.
    if (System.Text.RegularExpressions.Regex.IsMatch(
            input, @"\b(if|elseif|while|foreach|switch)\b\s*\([^)]*\)\s*\{",
            System.Text.RegularExpressions.RegexOptions.Singleline))
        return true;

    // A param block (script/function signature) at the start.
    if (System.Text.RegularExpressions.Regex.IsMatch(
            input, @"^\s*param\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        return true;

    // Pipeline automatic variable property access ($_.Prop) or $PSItem.
    if (System.Text.RegularExpressions.Regex.IsMatch(input, @"\$_\.\w|\$PSItem\b"))
        return true;

    return false;
}

static string BuildInvocationCwdPreamble()
{
    var cwd = Environment.CurrentDirectory.Replace("'", "''");
    return
        "$__psbash_invocation_cwd = '" + cwd + "'; " +
        "[System.Environment]::CurrentDirectory = $__psbash_invocation_cwd; " +
        "$env:PWD = $__psbash_invocation_cwd; " +
        "Set-Location -LiteralPath $__psbash_invocation_cwd -ErrorAction SilentlyContinue; ";
}

// Resolve the launcher version from the PsBash.Core assembly's
// InformationalVersion (stamped from <Version> in PsBash.Core.csproj, which the
// release process keeps in sync with the module manifest). Reading an attribute
// avoids spinning up a runspace just to print a banner. Strips any `+<commit>`
// SourceLink build-metadata suffix.
//
// MUST anchor on a PsBash.Core type (IpcWorker), not BashTranspiler: the release
// process only bumps PsBash.Core.csproj's <Version> and the module manifest, so
// every other project's version drifts. PsBash.Transpiler.csproj sat at 0.9.8
// while Core was 0.9.10 — reading BashTranspiler's assembly reported the stale
// 0.9.8 in the `--version` banner.
static string ResolveLauncherVersion()
{
    var asm = typeof(IpcWorker).Assembly;
    var info = asm
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;
    if (!string.IsNullOrEmpty(info))
    {
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
    return asm.GetName().Version?.ToString(3) ?? "0.0.0";
}

static string? ResolveHostBinary()
{
    var overridePath = Environment.GetEnvironmentVariable("PSBASH_HOST");
    if (!string.IsNullOrEmpty(overridePath)) return overridePath;

    var sxs = Path.Combine(AppContext.BaseDirectory, IpcWorker.GetHostBinaryName());
    return File.Exists(sxs) ? sxs : null;
}

// PTY-2: spawn ps-bash-host under a pseudo-terminal allocated by the launcher,
// then pump bytes between the launcher's stdio and the PTY master until the
// host exits. The host receives PSBASH_PTY_ATTACHED=1 in its environment so it
// can branch on "real terminal vs redirected pipe" later (PTY-3).
//
// Window size: best-effort from Console; fall back to 80x24 if the launcher's
// stdio is itself redirected (e.g. running under a parent that doesn't have a
// terminal). PTY-5 wires ongoing resize forwarding (SIGWINCH on POSIX,
// console-resize polling on Windows) plus Ctrl-C / Ctrl-Z signal forwarding
// via SignalForwarder, installed alongside the raw-mode scope below.
static async Task<int> RunHostUnderPtyAsync(string hostBinary, ShellArgs shellArgs)
{
    short cols = 80, rows = 24;
    try
    {
        // Console.WindowWidth/Height throw if there's no console (redirected).
        if (!Console.IsOutputRedirected)
        {
            int w = Console.WindowWidth, h = Console.WindowHeight;
            if (w > 0 && w <= short.MaxValue) cols = (short)w;
            if (h > 0 && h <= short.MaxValue) rows = (short)h;
        }
    }
    catch { /* fall back to defaults */ }

    await using var pty = await PtyAllocator.AllocateAsync(cols, rows);

    var hostArgs = new List<string> { "--interactive", $"--launcher-pid={Environment.ProcessId}" };
    if (shellArgs.NoProfile) hostArgs.Add("--no-profile");

    var env = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PSBASH_PTY_ATTACHED"] = "1",
    };

    await using var spawner = PtySpawner.Spawn(hostBinary, hostArgs, pty, env);

    // PTY-3: switch the launcher's own stdin to raw mode before starting
    // the bidirectional pump. Without this, the kernel line-buffers each
    // keystroke until Enter, and TUI apps (vim, less, fzf) never see the
    // keys as they arrive. The scope is disposed in the outer finally so
    // a crashed pump still restores the user's terminal state.
    //
    using var modeScope = TerminalMode.EnterRawIfTty();

    // PTY-5: forward terminal signals from the launcher (the process actually
    // attached to the user's tty) to the host's foreground process group.
    // Without this, in raw passthrough mode Ctrl-C is dead, Ctrl-Z cannot
    // suspend the running job, and a window resize never reaches vim/htop.
    // Paired with modeScope: installed inside the same raw-mode region and
    // disposed in the outer finally so a crashed pump still restores the
    // user's default signal handlers. modeScope.IsActive doubles as the
    // "launcher stdin is a real tty" probe — a pipe-driven launcher has no
    // terminal signals to forward.
    await using var signalForwarder = SignalForwarder.Install(
        hostPid: spawner.Pid,
        pty: pty,
        isLauncherStdinTty: modeScope.IsActive);

    using var pumpCts = new CancellationTokenSource();
    var stdinTask = Task.Run(async () =>
    {
        try
        {
            await using var launcherStdin = Console.OpenStandardInput();
            await launcherStdin.CopyToAsync(pty.Input, pumpCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* host exited first */ }
        catch (IOException) { /* pipe closed */ }
    });
    var stdoutTask = Task.Run(async () =>
    {
        try
        {
            await using var launcherStdout = Console.OpenStandardOutput();
            await pty.Output.CopyToAsync(launcherStdout, pumpCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* host exited first */ }
        catch (IOException) { /* pipe closed */ }
    });

    int exitCode;
    try
    {
        exitCode = await spawner.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }
    finally
    {
        // Stop the pumps after the child exits. We don't await stdinTask
        // because the launcher's stdin may block on a read forever; cancel
        // signals it and CopyToAsync will return on its own.
        pumpCts.Cancel();
        try { await stdoutTask.ConfigureAwait(false); } catch { /* swallow */ }
    }

    // PTY-7: crash recovery. If the host exited abnormally — a non-zero exit
    // code, or a signal death surfaced as 128+N (POSIX) — it almost certainly
    // did NOT run its own terminal teardown (a `kill -9` while vim is running
    // leaves the alternate screen buffer active, cursor hidden, scroll region
    // set). The `using modeScope` below will still restore termios / console
    // mode, but that alone does not undo the terminal-side screen corruption.
    // EmergencyRestoreAll restores the tty AND emits the reset escape sequence
    // so the user's parent shell redraws clean. It is idempotent: the
    // subsequent `using` dispose of modeScope finds the scope already
    // restored and is a no-op.
    //
    // Only an ABNORMAL termination skips the host's own teardown and needs the
    // emergency reset. A plain non-zero USER exit (a bash command that returned 1,
    // `exit 2`, 126/127 not-found) exited cleanly — firing RIS (ESC c) there wipes
    // the terminal scrollback for nothing. Restrict the reset to the signal-death
    // range (POSIX 128+N) and Windows crash statuses (large / negative codes).
    if (WasAbnormalTermination(exitCode))
    {
        TerminalMode.EmergencyRestoreAll();
    }

    return exitCode;

    // 128+N is the shell convention for "killed by signal N" (129 SIGHUP … 137
    // SIGKILL …); a Windows structured-exception crash surfaces as a large positive
    // (e.g. 0xC0000005) or a negative code. A normal command exit is 0–127.
    static bool WasAbnormalTermination(int code) => code >= 128 || code < 0;
}
