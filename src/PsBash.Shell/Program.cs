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

if (shellArgs.Interactive)
{
    return await InteractiveShell.RunAsync(pwshPath);
}

if (shellArgs.ReadFromStdin)
{
    var stdinCommand = await Console.In.ReadToEndAsync();
    shellArgs = shellArgs with { Command = stdinCommand };
}

if (shellArgs.Command is null)
{
    Console.Error.WriteLine("ps-bash: no command specified");
    return 1;
}

var pwshCommand = BashTranspiler.Transpile(shellArgs.Command);

if (debug)
{
    Console.Error.WriteLine($"[ps-bash] input:      {shellArgs.Command}");
    Console.Error.WriteLine($"[ps-bash] transpiled: {pwshCommand}");
    Console.Error.WriteLine($"[ps-bash] pwsh:       {pwshPath}");
}

await using var worker = await PwshWorker.StartAsync(
    pwshPath,
    workerScriptPath: Environment.GetEnvironmentVariable("PSBASH_WORKER"));

var exitCode = await worker.ExecuteAsync(pwshCommand);

if (debug)
{
    Console.Error.WriteLine($"[ps-bash] exit:       {exitCode}");
}

return exitCode;
