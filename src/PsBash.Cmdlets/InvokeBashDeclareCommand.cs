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

        bool functions = false, namesOnly = false, bareList = false;
        var select = new HashSet<char>();
        var names = new List<string>();
        bool optionsDone = false;
        foreach (var a in args)
        {
            if (!optionsDone && a == "--") { optionsDone = true; continue; }
            // Emitted for a bare `set` / `declare` / `typeset`: the plain `name=value` listing, not `declare -x`.
            if (!optionsDone && a == "--bare-list") { bareList = true; continue; }
            if (!optionsDone && a.Length > 1 && a[0] == '-')
            {
                foreach (var c in a.AsSpan(1))
                {
                    switch (c)
                    {
                        case 'f': functions = true; break;
                        case 'F': functions = true; namesOnly = true; break;
                        case 'p' or 'g': break;
                        case 'a' or 'A' or 'n' or 'r' or 'x': select.Add(c); break;
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

        if (bareList && !functions && names.Count == 0) PrintBareVariables();
        else if (functions) PrintFunctions(names, namesOnly);
        else PrintVariables(names, select);
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

    private void PrintBareVariables()
    {
        var env = Environment.GetEnvironmentVariables();
        var keys = new List<string>();
        foreach (System.Collections.DictionaryEntry e in env) keys.Add((string)e.Key);
        foreach (var nr in BashShellState.NamerefNames())
            if (!keys.Contains(nr)) keys.Add(nr);
        var arrays = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (an, av) in AllVariables())
        {
            if (env.Contains(an)) continue;
            string? text = av switch
            {
                BashSparseArray sp => DeclarePrinter.FormatSparse(sp),
                System.Collections.IDictionary d => DeclarePrinter.FormatAssociative(d),
                System.Collections.IList l => DeclarePrinter.FormatIndexed(l),
                _ => null,
            };
            if (text is not null) { arrays[an] = text; keys.Add(an); }
        }
        keys.Sort(string.CompareOrdinal);
        foreach (var key in keys)
        {
            if (arrays.TryGetValue(key, out var arrText)) { WriteLine(key + "=" + arrText); continue; }
            string v = (string?)env[key] ?? BashShellState.NamerefTarget(key) ?? string.Empty;
            bool plain = true;
            foreach (char c in v)
                if (!(char.IsAsciiLetterOrDigit(c) || "_-./:+=@%,".IndexOf(c) >= 0)) { plain = false; break; }
            WriteLine(key + "=" + (plain ? v : BashQuoteText.ForReuse(v)));
        }
    }

    /// <summary>Runtime scratch variables (<c>__psbash*</c>, <c>__Bash*</c>, loop temporaries): never user variables.</summary>
    private static bool IsInternalName(string name)
    {
        if (!name.StartsWith("__", StringComparison.Ordinal)) return false;
        foreach (var p in new[] { "__Bash", "__psb", "__bg", "__lsd", "__dyn", "__bashsplat", "__bashVer", "__bashexit", "__PsBash", "__parentPid" })
            if (name.StartsWith(p, StringComparison.Ordinal)) return true;
        return name is "__ae" or "__bp" or "__in" or "__ind" or "__ir" or "__iv" or "__nrt" or "__pf" or "__pp" or "__prog" or "__rt" or "__s" or "__saved";
    }

    // PowerShell's own globals (and the runtime's), which are not bash variables.
    private static readonly HashSet<string> NotBashVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "Error", "args", "input", "Matches", "PSBoundParameters", "PSCmdlet", "PSItem", "_", "foreach", "switch", "this",
        "PWD", "OLDPWD", "StackTrace", "ExecutionContext", "Host", "PSHOME", "PID", "ShellId", "MyInvocation",
        "PSCommandPath", "PSScriptRoot", "PSCulture", "PSUICulture", "PSVersionTable", "PSEdition", "PSDefaultParameterValues",
        "PSSessionOption", "PSEmailServer", "PSSessionApplicationName", "PSSessionConfigurationName", "NestedPromptLevel",
        "Profile", "ProgressPreference", "VerbosePreference", "WarningPreference", "DebugPreference", "ErrorActionPreference",
        "InformationPreference", "ConfirmPreference", "WhatIfPreference", "ErrorView", "OutputEncoding", "FormatEnumerationLimit",
        "MaximumHistoryCount", "EnabledExperimentalFeatures", "IsLinux", "IsMacOS", "IsWindows", "IsCoreCLR", "LASTEXITCODE",
        "PSNativeCommandUseErrorActionPreference", "PSModuleAutoLoadingPreference", "PSStyle", "PSReadLineOption", "true", "false", "null",
        "HOME", "Home",
    };

    /// <summary>Every bash variable as (name, value): the environment's, then the shell's array / associative globals.</summary>
    private List<(string Name, object? Value)> AllVariables()
    {
        var result = new List<(string, object?)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var env = Environment.GetEnvironmentVariables();
        foreach (System.Collections.DictionaryEntry e in env)
            if (e.Key is string k && !k.StartsWith("__", StringComparison.Ordinal) && seen.Add(k)) result.Add((k, e.Value));
        try
        {
            foreach (var v in SessionState.InvokeProvider.Item.Get(new[] { @"variable:\*" }, force: false, literalPath: false))
            {
                if (v.BaseObject is not PSVariable pv) continue;
                string name = pv.Name;
                if (NotBashVariables.Contains(name) || IsInternalName(name) || name.StartsWith("PS", StringComparison.Ordinal)
                    || name.StartsWith("Bash", StringComparison.Ordinal) || BashShellState.AllGlobalNames.Contains(name)) continue;
                if (pv.Value is System.Collections.IDictionary || (pv.Value is System.Collections.IList && pv.Value is not string))
                {
                    if (seen.Add(name)) result.Add((name, pv.Value));
                }
            }
        }
        catch
        {
            // Variable provider unavailable: environment variables only.
        }
        foreach (var nr in BashShellState.NamerefNames())
            if (seen.Add(nr)) result.Add((nr, null));
        result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return result;
    }

    private void PrintVariables(List<string> names, HashSet<char> select)
    {
        if (names.Count == 0)
        {
            foreach (var (name, value) in AllVariables())
            {
                bool isAssoc = value is System.Collections.IDictionary and not BashSparseArray;
                bool isIndexed = value is BashSparseArray || (value is System.Collections.IList && value is not string);
                string letters = BashShellState.AttributeLetters(name);
                if (select.Contains('a') && !isIndexed) continue;
                if (select.Contains('A') && !isAssoc) continue;
                if (select.Contains('r') && !letters.Contains('r')) continue;
                if (select.Contains('x') && !letters.Contains('x')) continue;
                bool isNameref = BashShellState.NamerefTarget(name) is not null;
                if (select.Contains('n') != isNameref && (select.Contains('n') || select.Count > 0)) continue;
                DeclarePrinter.TryPrintVariable(this, name);
            }
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
