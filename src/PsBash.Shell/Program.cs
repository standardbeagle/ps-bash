using PsBash.Core.Parser;
using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;
using PsBash.Shell;


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

// Auto-detect piped stdin: if no command given and stdin is redirected, try reading it.
if (shellArgs.ReadFromStdin || (!shellArgs.Interactive && shellArgs.Command is null && Console.IsInputRedirected))
{
    var stdinCommand = await Console.In.ReadToEndAsync();
    if (!string.IsNullOrWhiteSpace(stdinCommand))
        shellArgs = shellArgs with { Command = stdinCommand };
}

if (shellArgs.Interactive || shellArgs.Command is null)
{
    return await InteractiveShell.RunAsync(pwshPath, shellArgs.NoProfile);
}

string? pwshCommand;
try
{
    pwshCommand = BashTranspiler.Transpile(shellArgs.Command);
}
catch (ParseException ex)
{
    Console.Error.WriteLine($"ps-bash: parse error: {ex.Message}");
    return 2;
}

if (debug)
{
    Console.Error.WriteLine($"[ps-bash] input:      {shellArgs.Command}");
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
