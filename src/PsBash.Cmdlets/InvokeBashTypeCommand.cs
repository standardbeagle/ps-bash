using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashType</c> function
/// (REFACTOR-2 follow-on). Implements the bash <c>type</c> builtin: classify
/// a command name as alias / function / builtin / file, or print the value
/// of a variable in <c>-p</c>-on-bash-declare-style form.
///
/// Behavioral parity oracle: the original psm1 function. Dispatch order:
/// <list type="bullet">
/// <item><c>--help</c> → <c>Show-BashHelp 'type'</c>.</item>
/// <item>No operands → bash-style "type: missing operand" error and return.</item>
/// <item><c>-p</c> mode → read variable from global PS scope then env, emit
/// <c>declare -- name="value"</c> / <c>declare -a/-A …</c>; on miss, emit
/// <c>bash: declare: NAME: not found</c> error.</item>
/// <item>Otherwise → walk built-in list, alias table, then <c>Get-Command</c>
/// for cmdlet/function/file resolution. Emit typed <c>PsBash.TypeOutput</c>
/// PSObjects. <c>-t</c> kind-only, <c>-a</c> all matches, default first hit.
/// Missing → <c>bash: type: NAME: not found</c>.</item>
/// </list>
///
/// Flag collisions per the playbook table:
/// <list type="bullet">
/// <item><c>-t</c> — no PowerShell common-parameter prefix overlap, stays in
/// <c>Arguments</c>.</item>
/// <item><c>-a</c> — prefix-matches <c>-Arguments</c> (the catch-all), so it
/// is declared as an explicit <see cref="SwitchParameter"/> named <c>A</c>.</item>
/// <item><c>-p</c> — prefix-matches <c>-PipelineVariable</c> /
/// <c>-ProgressAction</c>, declared as an explicit <see cref="SwitchParameter"/>
/// named <c>P</c>. Exact-name match beats common-parameter prefix-match.</item>
/// </list>
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

    // Declared because the bare token -a prefix-matches the cmdlet's own
    // -Arguments parameter under PSCmdlet binding (same hazard ls / uname hit).
    [Parameter] public SwitchParameter A { get; set; }

    // Declared because -p prefix-matches -PipelineVariable / -ProgressAction.
    [Parameter] public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "type"))
            {
                WriteObject(line);
            }
            return;
        }

        bool typeOnly = false;
        bool showAll = A.IsPresent;
        bool printMode = P.IsPresent;

        var operands = new List<string>();
        foreach (var arg in args)
        {
            // Case-sensitive comparisons mirror the oracle's `-ceq` slice.
            if (string.Equals(arg, "-t", StringComparison.Ordinal)) typeOnly = true;
            else if (string.Equals(arg, "-a", StringComparison.Ordinal) ||
                     string.Equals(arg, "--all", StringComparison.Ordinal)) showAll = true;
            else if (string.Equals(arg, "-p", StringComparison.Ordinal)) printMode = true;
            else operands.Add(arg);
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "type: missing operand");
            return;
        }

        foreach (var name in operands)
        {
            if (printMode)
            {
                EmitPrintMode(name);
                continue;
            }

            var results = new List<PSObject>();
            var isBuiltin = Builtins.Contains(name);

            if (isBuiltin)
            {
                results.Add(BuildEntry(name, "builtin",
                    typeOnly ? "builtin" : $"{name} is a shell builtin"));

                if (!showAll)
                {
                    WriteObject(results[0]);
                    continue;
                }
            }

            // Alias probe — only emit if the alias is one of the ps-bash
            // runtime aliases (matches the oracle's `-match` predicate).
            var aliasInfo = ResolveAlias(name);
            if (aliasInfo != null)
            {
                results.Add(BuildEntry(name, "alias",
                    typeOnly ? "alias" : $"{name} is aliased to `{aliasInfo}'"));
            }

            // Get-Command lookup — skip if already classified as builtin (oracle).
            if (!isBuiltin)
            {
                var cmd = ResolveCommand(name);
                if (cmd != null)
                {
                    string kind, text;
                    switch (cmd.CommandType)
                    {
                        case CommandTypes.Alias:
                            kind = "alias";
                            text = typeOnly ? kind
                                : $"{name} is aliased to `{((AliasInfo)cmd).Definition}'";
                            break;
                        case CommandTypes.Function:
                            kind = "function";
                            text = typeOnly ? kind : $"{name} is a function";
                            break;
                        default:
                            kind = "file";
                            text = typeOnly ? kind : $"{name} is {cmd.Source}";
                            break;
                    }
                    results.Add(BuildEntry(name, kind, text));
                }
            }

            if (results.Count == 0)
            {
                FileSystemHelpers.WriteBashError(this, $"bash: type: {name}: not found");
                FileSystemHelpers.SetLastExitCode(this, 1);
                continue;
            }

            if (!showAll && !isBuiltin)
            {
                WriteObject(results[0]);
                continue;
            }

            foreach (var r in results) WriteObject(r);
        }
    }

    private void EmitPrintMode(string name)
    {
        // Try global PS variable first, then env. Oracle's exact slice.
        object? val = null;
        string source = "variable";
        try
        {
            var psVar = SessionState.PSVariable.GetValue($"global:{name}");
            if (psVar != null) val = psVar;
        }
        catch
        {
            // Variable doesn't exist; fall through to env probe.
        }

        if (val == null)
        {
            var envVal = Environment.GetEnvironmentVariable(name);
            if (envVal != null) { val = envVal; source = "environment"; }
        }
        _ = source; // parity placeholder (oracle tracked it but did not surface it)

        if (val != null)
        {
            // Oracle: dictionaries → "declare -A NAME=JSON";
            // arrays/lists → "declare -a NAME=JSON";
            // scalars → "declare -- NAME=\"VAL\"".
            string text;
            if (val is System.Collections.IDictionary)
            {
                text = $"declare -A {name}={ToCompactJson(val)}";
            }
            else if (val is System.Collections.IList && val is not string)
            {
                text = $"declare -a {name}={ToCompactJson(val)}";
            }
            else
            {
                text = $"declare -- {name}=\"{val}\"";
            }
            foreach (var line in BashRuntime.EmitBashLines(text))
            {
                WriteObject(line);
            }
        }
        else
        {
            FileSystemHelpers.WriteBashError(this, $"bash: declare: {name}: not found");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    private string ToCompactJson(object val)
    {
        // The oracle pipes through ConvertTo-Json -Compress. Delegate to the
        // PowerShell cmdlet so the format matches byte-for-byte (e.g. integer
        // vs string boxing, key escaping).
        try
        {
            var results = InvokeCommand.InvokeScript(
                "param($v) $v | ConvertTo-Json -Compress", val);
            if (results.Count > 0 && results[0] != null)
            {
                return results[0].ToString() ?? "";
            }
        }
        catch
        {
            // Fall through to empty repr.
        }
        return "";
    }

    private string? ResolveAlias(string name)
    {
        // The psm1 oracle gates emit on definition matching
        // ^Invoke-Bash|^Get-Bash|^Set-Bash|^ConvertFrom-. Preserved here.
        try
        {
            var results = InvokeCommand.InvokeScript(
                "param($n) Get-Alias $n -ErrorAction SilentlyContinue", name);
            if (results.Count == 0) return null;
            var first = results[0];
            if (first == null) return null;
            var aliasInfo = first.BaseObject as AliasInfo;
            if (aliasInfo == null) return null;
            var def = aliasInfo.Definition ?? "";
            if (def.StartsWith("Invoke-Bash", StringComparison.Ordinal) ||
                def.StartsWith("Get-Bash", StringComparison.Ordinal) ||
                def.StartsWith("Set-Bash", StringComparison.Ordinal) ||
                def.StartsWith("ConvertFrom-", StringComparison.Ordinal))
            {
                return def;
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private CommandInfo? ResolveCommand(string name)
    {
        // The oracle calls Get-Command -CommandType Application,Cmdlet,Function
        // — first match wins (or null on miss).
        try
        {
            var results = InvokeCommand.InvokeScript(
                "param($n) Get-Command $n -CommandType Application,Cmdlet,Function -ErrorAction SilentlyContinue",
                name);
            foreach (var r in results)
            {
                if (r?.BaseObject is CommandInfo ci) return ci;
            }
        }
        catch
        {
            return null;
        }
        return null;
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
