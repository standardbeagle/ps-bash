using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The PRINT forms of the bash <c>declare</c>/<c>typeset</c> builtin (the emitter keeps the assignment forms
/// inline): <c>declare -f [NAME...]</c>, <c>declare -F [NAME...]</c>, <c>declare -p [NAME...]</c>.
/// <para>
/// Oracle: bash 5.2 (<c>wsl bash</c>).
/// </para>
/// <list type="bullet">
/// <item><c>-f NAME</c> prints the function's definition; <c>-F NAME</c> prints just its name. A NAME that is
/// not a function prints NOTHING (no diagnostic) and makes the status 1 — <c>declare -f fn &gt;/dev/null</c>
/// is the stock "is fn defined?" probe. Without names, every bash-defined function is listed (<c>-F</c>:
/// <c>declare -f NAME</c> lines), sorted.</item>
/// <item><c>-p NAME</c> prints the variable as a <c>declare</c> line; a missing one is
/// <c>bash: declare: NAME: not found</c>, status 1. With <c>-f</c>/<c>-F</c>, <c>-p</c> just selects
/// printing. Without names, the environment is listed as <c>declare -x</c> lines.</item>
/// </list>
/// Only bash-defined functions count: the runtime's own PowerShell functions are not shell functions
/// (<see cref="DeclarePrinter.IsBashFunction"/>).
/// <para>
/// <b>Flag collisions</b>: <c>declare</c> is on <c>PsEmitter.OrderedArgCommands</c>, so transpiled calls pass
/// every flag single-quoted in <see cref="Arguments"/>. The <c>P</c> decoy (<c>-p</c> prefix-matches
/// <c>-PipelineVariable</c>/<c>-ProgressAction</c>) exists for direct PowerShell calls only.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashDeclare")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashDeclareCommand : PSCmdlet
{
    [Parameter] public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (P.IsPresent, "-p"));
        FileSystemHelpers.SetLastExitCode(this, 0);

        bool functions = false, namesOnly = false;
        var names = new List<string>();
        bool optionsDone = false;
        foreach (var a in args)
        {
            if (!optionsDone && a == "--") { optionsDone = true; continue; }
            if (!optionsDone && a.Length > 1 && a[0] == '-')
            {
                foreach (var c in a.AsSpan(1))
                {
                    switch (c)
                    {
                        case 'f': functions = true; break;
                        case 'F': functions = true; namesOnly = true; break;
                        case 'p': break;
                        default:
                            FileSystemHelpers.WriteBashError(this, $"bash: declare: -{c}: invalid option\n"
                                + "declare: usage: declare [-aAfFgiIlnrtux] [name[=value] ...] or declare -p [-aAfFilnrtux] [name ...]");
                            FileSystemHelpers.SetLastExitCode(this, 2);
                            return;
                    }
                }
                continue;
            }
            optionsDone = true;
            names.Add(a);
        }

        if (functions) PrintFunctions(names, namesOnly);
        else PrintVariables(names);
    }

    private void PrintFunctions(List<string> names, bool namesOnly)
    {
        if (names.Count == 0)
        {
            foreach (var fn in DeclarePrinter.AllBashFunctions(this))
            {
                if (namesOnly) WriteLine($"declare -f {fn.Name}");
                else DeclarePrinter.PrintFunction(this, fn);
            }
            return;
        }

        foreach (var name in names)
        {
            var fn = DeclarePrinter.FindBashFunction(this, name);
            if (fn is null)
            {
                FileSystemHelpers.SetLastExitCode(this, 1);
                continue;
            }
            if (namesOnly) WriteLine(fn.Name);
            else DeclarePrinter.PrintFunction(this, fn);
        }
    }

    private void PrintVariables(List<string> names)
    {
        if (names.Count == 0)
        {
            var env = Environment.GetEnvironmentVariables();
            var keys = new List<string>();
            foreach (System.Collections.DictionaryEntry e in env) keys.Add((string)e.Key);
            keys.Sort(string.CompareOrdinal);
            foreach (var key in keys)
                WriteLine($"declare -x {key}=\"{env[key]}\"");
            return;
        }

        foreach (var name in names)
        {
            if (DeclarePrinter.TryPrintVariable(this, name)) continue;
            FileSystemHelpers.WriteBashError(this, $"bash: declare: {name}: not found");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    private void WriteLine(string text)
    {
        foreach (var line in BashRuntime.EmitBashLines(text + "\n"))
            WriteObject(line);
    }
}
