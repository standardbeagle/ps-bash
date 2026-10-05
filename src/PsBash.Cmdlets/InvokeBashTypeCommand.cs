using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The bash <c>type</c> builtin: describe how each NAME would be run. Oracle: bash 5.2 (`wsl bash`).
/// <list type="bullet">
/// <item>default: <c>NAME is a shell builtin</c> / <c>NAME is a function</c> / <c>NAME is PATH</c>; a miss prints
/// <c>bash: type: NAME: not found</c>, status 1.</item>
/// <item><c>-t</c>: just the kind (<c>builtin</c> / <c>function</c> / <c>file</c>); a miss prints nothing, status 1.</item>
/// <item><c>-p</c>: the path of a NAME that would run a file; a builtin or function prints NOTHING with status 0
/// (<c>type -p cd</c>); a miss prints nothing, status 1. <c>-P</c>: the PATH search only (a builtin is a miss).</item>
/// <item><c>-a</c>: every match instead of the first. <c>-f</c>: skip functions.</item>
/// </list>
/// A ps-bash command (a runtime alias to an <c>Invoke-Bash*</c> cmdlet, <c>ls</c>) is the shell's own
/// implementation with no file: it is reported as a <c>file</c> whose path is its bash NAME (<c>ls is ls</c>,
/// <c>type -p ls</c> → <c>ls</c>), so availability probes (<c>type -p jq &gt;/dev/null</c>) work and the internal
/// cmdlet name never leaks — the same rule as <c>command -v</c> and <c>which</c>.
/// <para>
/// Flag collisions: <c>type</c> is on <c>PsEmitter.OrderedArgCommands</c>, so transpiled calls pass every flag
/// single-quoted, in order and with its case (<c>-p</c> vs <c>-P</c>). The <c>A</c>/<c>P</c> decoys
/// (<c>-a</c> prefix-matches <c>-Arguments</c>, <c>-p</c> <c>-PipelineVariable</c>) are for direct calls only;
/// the binder folds case, so a direct <c>-P</c> reads as <c>-p</c>.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashType")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashTypeCommand : PSCmdlet
{
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "echo", "printf", "type", "cd", "exit", "return", "export",
        "unset", "set", "shift", "read", "eval", "source", "trap",
        "alias", "unalias", "test", "[", "true", "false",
    };

    [Parameter] public SwitchParameter A { get; set; }

    [Parameter] public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private enum Mode { Describe, Kind, Path, PathSearch }

    /// <summary>One way NAME could run: its kind, the default-mode sentence, and its path (files only).</summary>
    private sealed record Match(string Kind, string Text, string? Path);

    protected override void ProcessRecord()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (A.IsPresent, "-a"), (P.IsPresent, "-p"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "type", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "type"))
                WriteObject(line);
            return;
        }

        var mode = Mode.Describe;
        bool showAll = false, skipFunctions = false, optionsDone = false;
        var operands = new List<string>();
        foreach (var arg in args)
        {
            if (!optionsDone && arg == "--") { optionsDone = true; continue; }
            if (!optionsDone && arg == "--all") { showAll = true; continue; }
            if (!optionsDone && arg.Length > 1 && arg[0] == '-' && arg[1] != '-')
            {
                foreach (var c in arg.AsSpan(1))
                {
                    switch (c)
                    {
                        case 't': mode = Mode.Kind; break;
                        case 'p': if (mode != Mode.PathSearch) mode = Mode.Path; break;
                        case 'P': mode = Mode.PathSearch; break;
                        case 'a': showAll = true; break;
                        case 'f': skipFunctions = true; break;
                        default:
                            FileSystemHelpers.WriteBashError(this, $"bash: type: -{c}: invalid option\n"
                                + "type: usage: type [-afptP] name [name ...]");
                            FileSystemHelpers.SetLastExitCode(this, 2);
                            return;
                    }
                }
                continue;
            }
            optionsDone = true;
            operands.Add(arg);
        }

        if (operands.Count == 0) return; // bash: `type` alone is a no-op, status 0

        foreach (var name in operands)
        {
            var matches = Resolve(name, mode == Mode.PathSearch, skipFunctions);
            if (matches.Count == 0)
            {
                if (mode == Mode.Describe) FileSystemHelpers.WriteBashError(this, $"bash: type: {name}: not found");
                FileSystemHelpers.SetLastExitCode(this, 1);
                continue;
            }

            foreach (var m in showAll ? matches : matches.Take(1))
            {
                switch (mode)
                {
                    case Mode.Describe: WriteObject(BuildEntry(name, m.Kind, m.Text)); break;
                    case Mode.Kind: WriteObject(BuildEntry(name, m.Kind, m.Kind)); break;
                    default:
                        // -p/-P print a path only for a file; a builtin/function prints nothing (status stays 0).
                        if (m.Path is not null) WriteObject(BuildEntry(name, m.Kind, m.Path));
                        break;
                }
            }
        }
    }

    /// <summary>Every way NAME could run, in bash's lookup order (function, builtin, then files).</summary>
    private List<Match> Resolve(string name, bool pathSearchOnly, bool skipFunctions)
    {
        var matches = new List<Match>();
        if (!pathSearchOnly)
        {
            if (!skipFunctions && DeclarePrinter.FindBashFunction(this, name) is not null)
                matches.Add(new Match("function", $"{name} is a function", null));
            if (Builtins.Contains(name))
                matches.Add(new Match("builtin", $"{name} is a shell builtin", null));
        }

        if (IsPsBashCommand(name) && !Builtins.Contains(name))
            matches.Add(new Match("file", $"{name} is {name}", name));

        foreach (var cmd in ResolveCommands(name))
        {
            switch (cmd.CommandType)
            {
                case CommandTypes.Function:
                    // A bash function was handled above; the runtime's own PowerShell functions are not shell functions.
                    break;
                default:
                    var path = cmd.Source;
                    if (string.IsNullOrEmpty(path)) break;
                    matches.Add(new Match("file", $"{name} is {path}", path));
                    break;
            }
        }
        return matches;
    }

    /// <summary>True when NAME is a runtime alias to one of the ps-bash command cmdlets.</summary>
    private bool IsPsBashCommand(string name)
    {
        try
        {
            return InvokeCommand.GetCommand(name, CommandTypes.Alias) is AliasInfo alias
                && (alias.Definition ?? "").StartsWith("Invoke-Bash", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<CommandInfo> ResolveCommands(string name)
    {
        // Applications and cmdlets (all of them, for -a). Parameter-bound: the name never becomes script text.
        var found = new List<CommandInfo>();
        try
        {
            foreach (var r in InvokeCommand.InvokeScript(
                         "param($n) Get-Command $n -CommandType Application,Cmdlet -All -ErrorAction SilentlyContinue", name))
            {
                if (r?.BaseObject is CommandInfo ci) found.Add(ci);
            }
        }
        catch
        {
            // Unresolvable name: no file matches.
        }
        return found;
    }

    private static PSObject BuildEntry(string name, string kind, string text)
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.TypeOutput");
        obj.Properties.Add(new PSNoteProperty("Command", name));
        obj.Properties.Add(new PSNoteProperty("Kind", kind));
        obj.Properties.Add(new PSNoteProperty("BashText", text));
        return obj;
    }
}
