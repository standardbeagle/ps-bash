using PsBash.Core.Parser;
using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;
using PsBash.Shell;


// Reliability watchdog: on Windows, attach the current process to a Job Object
// with KILL_ON_JOB_CLOSE so the pwsh worker (and any other descendants) die
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

var shellArgs = ShellArgs.Parse(args);

// Path mode: explicit --unix-paths / --windows-paths flag wins; otherwise
// fall back to PSBASH_UNIX_PATHS env var; otherwise default to Windows-native
// paths (no MSYS translation). Propagate the resolved choice as an env var
// so PsEmitter (in PsBash.Core, no direct Shell dependency) can read it.
bool unixPaths = shellArgs.UnixPaths
    ?? Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS") is "1" or "true";
Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", unixPaths ? "1" : "0");

string pwshPath;
try
{
    pwshPath = PwshLocator.Locate();
}
catch (PwshNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 127;
}

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
        throw new NotImplementedException("ps1 coming in Task C");

    throw new NotImplementedException("sh coming in Task B");
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
    return await InteractiveShell.RunAsync(pwshPath, shellArgs.NoProfile);
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
var bashCommand = InteractiveShell.ExpandAliases(shellArgs.Command);

string? pwshCommand;
try
{
    pwshCommand = BashTranspiler.Transpile(bashCommand);
}
catch (ParseException ex)
{
    Console.Error.WriteLine($"ps-bash: parse error: {ex.Message}");
    return 2;
}

if (debug)
{
    Console.Error.WriteLine($"[ps-bash] input:      {bashCommand}");
    Console.Error.WriteLine($"[ps-bash] transpiled: {pwshCommand}");
    Console.Error.WriteLine($"[ps-bash] pwsh:       {pwshPath}");
}

var modulePath = Environment.GetEnvironmentVariable("PSBASH_MODULE")
    ?? ModuleExtractor.ExtractEmbedded();

await using var worker = await PwshWorker.StartAsync(
    pwshPath,
    workerScriptPath: Environment.GetEnvironmentVariable("PSBASH_WORKER"),
    modulePath: modulePath);

var exitCode = await worker.ExecuteAsync(pwshCommand);

if (debug)
{
    Console.Error.WriteLine($"[ps-bash] exit:       {exitCode}");
}

return exitCode;
