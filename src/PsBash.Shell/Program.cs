using PsBash.Core.Parser;
using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;
using PsBash.Shell;
using PsBash.Shell.Pty;


// Reliability watchdog: on Windows, attach the current process to a Job Object
// with KILL_ON_JOB_CLOSE so the SDK host (and any other descendants) die
// atomically with ps-bash itself. This is a no-op on Linux/macOS where the
// shell's process group + SIGHUP already handles this.
JobObjectWatchdog.AttachCurrentProcess();

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

// Path mode: explicit --unix-paths / --windows-paths flag wins; otherwise
// fall back to PSBASH_UNIX_PATHS env var; otherwise default to Windows-native
// paths (no MSYS translation). Propagate the resolved choice as an env var
// so PsEmitter (in PsBash.Core, no direct Shell dependency) can read it.
bool unixPaths = shellArgs.UnixPaths
    ?? Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS") is "1" or "true";
Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", unixPaths ? "1" : "0");

// All non-interactive execution (-c, stdin pipe, script file) goes through
// ps-bash-host over IPC. REFACTOR-7: each invocation gets its own private host
// (Lifetime.PerInvocation) — spawned on a process-local socket and killed when
// the worker is disposed — so the host never outlives its single client. This
// contains the pipe-inheritance hazard within the launcher's lifetime and gives
// every -c/script run a clean PowerShell session by construction. Interactive
// mode does not use this factory: it spawns ps-bash-host --interactive directly
// so the host inherits the real tty. If the host binary is missing or fails to
// start, the invocation exits non-zero with the underlying error.
Func<Task<IWorker>> workerFactory = async () =>
{
    var hostBinary = ResolveHostBinary()
        ?? throw new HostUnavailableException(
            "ps-bash-host binary not found. Set PSBASH_HOST=<path> or install alongside ps-bash.");

    return await IpcWorker.StartAsync(hostBinary, lifetime: Lifetime.PerInvocation).ConfigureAwait(false);
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

        var ps1Preamble = BuildPositionalPreamble(shellArgs.ScriptPath, shellArgs.ScriptArgs);
        var escapedPath = shellArgs.ScriptPath.Replace("'", "''");
        return await ps1Worker.ExecuteAsync(BuildInvocationCwdPreamble() + ps1Preamble + ". '" + escapedPath + "'");
    }

    // .sh execution: read, transpile, build positional preamble, execute.
    var scriptContent = File.ReadAllText(shellArgs.ScriptPath);

    string? pwshScriptCommand;
    try
    {
        pwshScriptCommand = BashTranspiler.Transpile(scriptContent);
    }
    catch (ParseException ex)
    {
        Console.Error.WriteLine($"ps-bash: parse error: {ex.Message}");
        return 2;
    }

    await using IWorker scriptWorker = await workerFactory();

    var preamble = BuildPositionalPreamble(shellArgs.ScriptPath, shellArgs.ScriptArgs);
    return await scriptWorker.ExecuteAsync(BuildInvocationCwdPreamble() + preamble + pwshScriptCommand);
}

// Auto-detect piped stdin: if no command given and stdin is redirected, try reading it.
if (shellArgs.ReadFromStdin || (!shellArgs.Interactive && shellArgs.Command is null && Console.IsInputRedirected))
{
    var stdinCommand = await Console.In.ReadToEndAsync();
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

    using var hostProc = System.Diagnostics.Process.Start(psi)!;
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
        Console.Error.WriteLine($"ps-bash: parse error: {ex.Message}");
        return 2;
    }
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
    Console.Error.WriteLine(Tag("transpiled: ", pwshCommand));
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

static string BuildInvocationCwdPreamble()
{
    var cwd = Environment.CurrentDirectory.Replace("'", "''");
    return
        "$__psbash_invocation_cwd = '" + cwd + "'; " +
        "[System.Environment]::CurrentDirectory = $__psbash_invocation_cwd; " +
        "$env:PWD = $__psbash_invocation_cwd; " +
        "Set-Location -LiteralPath $__psbash_invocation_cwd -ErrorAction SilentlyContinue; ";
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
    using var signalForwarder = SignalForwarder.Install(
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
    // A clean exit (exitCode == 0) takes the normal `using` dispose path with
    // no escape sequence — a full reset there would flicker the screen.
    if (exitCode != 0)
    {
        TerminalMode.EmergencyRestoreAll();
    }

    return exitCode;
}
