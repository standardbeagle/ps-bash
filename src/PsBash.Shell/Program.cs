using PsBash.Core.Parser;
using PsBash.Core.Runtime;
using PsBash.Core.Transpiler;
using PsBash.Shell;


var debug = Environment.GetEnvironmentVariable("PSBASH_DEBUG") == "1";

var shellArgs = ShellArgs.Parse(args);

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
    return await InteractiveShell.RunAsync(pwshPath);
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
