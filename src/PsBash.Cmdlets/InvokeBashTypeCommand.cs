using System.Management.Automation;
using PsBash.Core;
using PsBash.Core.Parser;

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
    internal static IReadOnlySet<string> Builtins => BashBuiltins.Names;

    internal static IReadOnlySet<string> Keywords => BashBuiltins.Keywords;

    [Parameter] public SwitchParameter A { get; set; }

    [Parameter] public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private enum Mode { Describe, Kind, Path, PathSearch }

    /// <summary>One way NAME could run: its kind, the default-mode sentence, and its path (files only).</summary>
    internal sealed record Match(string Kind, string Text, string? Path);

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
            var matches = Resolve(this, name, mode == Mode.PathSearch, skipFunctions);
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
                    case Mode.Describe:
                        WriteObject(BuildEntry(name, m.Kind, m.Text));
                        // bash follows "NAME is a function" with the definition; printed when its bash source is known.
                        if (m.Kind == "function" && DeclarePrinter.FindBashFunction(this, name) is { } fn
                            && PsBuild.TryReadFunctionSource(fn.Definition) is not null)
                            DeclarePrinter.PrintFunction(this, fn);
                        break;
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
    internal static List<Match> Resolve(PSCmdlet cmdlet, string name, bool pathSearchOnly, bool skipFunctions)
    {
        var matches = new List<Match>();
        if (!pathSearchOnly)
        {
            if (cmdlet.SessionState.PSVariable.GetValue("BashUserAliases") is System.Collections.IDictionary aliases
                && aliases.Contains(name))
                matches.Add(new Match("alias", $"{name} is aliased to `{aliases[name]}'", null));
            if (Keywords.Contains(name))
                matches.Add(new Match("keyword", $"{name} is a shell keyword", null));
            if (!skipFunctions && DeclarePrinter.FindBashFunction(cmdlet, name) is not null)
                matches.Add(new Match("function", $"{name} is a function", null));
            if (Builtins.Contains(name))
                matches.Add(new Match("builtin", $"{name} is a shell builtin", null));
        }

        // The runtime's own command is what runs (whatever PATH holds), so it leads; -P asks for the PATH search
        // itself, where it is only the fallback when PATH has no such file.
        bool psBashCommand = IsPsBashCommand(cmdlet, name) && !Builtins.Contains(name);
        if (psBashCommand && !pathSearchOnly)
            matches.Add(new Match("file", $"{name} is {name}", name));

        int before = matches.Count;
        foreach (var path in PathFiles(name))
            matches.Add(new Match("file", $"{name} is {path}", path));

        foreach (var cmd in ResolveCommands(cmdlet, name))
        {
            // Applications come from PathFiles; a bash function was handled above, and the runtime's own
            // PowerShell functions are not shell functions.
            if (cmd.CommandType != CommandTypes.Cmdlet || string.IsNullOrEmpty(cmd.Source)) continue;
            matches.Add(new Match("file", $"{name} is {cmd.Source}", cmd.Source));
        }

        if (psBashCommand && pathSearchOnly && matches.Count == before)
            matches.Add(new Match("file", $"{name} is {name}", name));
        return matches;
    }

    /// <summary>
    /// The PATH files NAME names, in PATH order, through the lookup that runs commands
    /// (<see cref="ExecutableLookup"/>: execute bit on Unix, PATHEXT then the bare name on Windows). Each is printed
    /// as its PATH entry was written (<c>_tmp/mv</c>, <c>/tmp/mv</c>), as bash does; the entry is mapped only to
    /// find the file. A NAME with a slash is that file itself, never a directory.
    /// </summary>
    private static List<string> PathFiles(string name)
    {
        var found = new List<string>();
        if (name.Length == 0) return found;
        if (name.Contains('/') || name.Contains('\\'))
        {
            if (ExecutableLookup.IsExecutableFile(BashRuntime.MapPath(name))) found.Add(name);
            return found;
        }
        foreach (var entry in ExecutableLookup.PathDirectories())
        {
            string dir = BashRuntime.MapPath(entry);
            foreach (var hit in ExecutableLookup.FindAll(name, [dir]))
            {
                string file = Path.GetFileName(hit);
                char sep = entry.Contains('\\') ? '\\' : '/';
                found.Add(entry.EndsWith('/') || entry.EndsWith('\\') ? entry + file : entry + sep + file);
            }
        }
        return found;
    }

    /// <summary>True when NAME is a runtime alias to one of the ps-bash command cmdlets.</summary>
    private static bool IsPsBashCommand(PSCmdlet cmdlet, string name)
    {
        try
        {
            return cmdlet.InvokeCommand.GetCommand(name, CommandTypes.Alias) is AliasInfo alias
                && (alias.Definition ?? "").StartsWith("Invoke-Bash", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<CommandInfo> ResolveCommands(PSCmdlet cmdlet, string name)
    {
        // Cmdlets (files come from PathFiles). Parameter-bound: the name never becomes script text.
        var found = new List<CommandInfo>();
        try
        {
            foreach (var r in cmdlet.InvokeCommand.InvokeScript(
                         "param($n) Get-Command $n -CommandType Cmdlet -All -ErrorAction SilentlyContinue", name))
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
