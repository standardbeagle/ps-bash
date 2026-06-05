using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashAlias</c> function
/// (REFACTOR-2 follow-on, alias/trap batch). Implements the bash <c>alias</c>
/// / <c>unalias</c> builtin in module mode — stores alias definitions in the
/// psm1 module-scoped <c>$global:BashUserAliases</c> hashtable and creates
/// dynamic PowerShell functions via <c>Set-Item Function:\NAME</c> so that
/// transpiled bash code calling the alias name routes to the alias body.
///
/// Shell mode (<c>ps-bash</c> interactive) handles aliases separately in C#
/// (InteractiveShell.cs); this cmdlet covers only the module-mode path the
/// psm1 oracle owned.
///
/// State ownership: <c>$global:BashUserAliases</c> stays in psm1 module scope
/// (the dictionary is referenced from one place — this cmdlet). The cmdlet
/// reads / mutates it via parameter-bound <see cref="PSCmdlet.InvokeCommand"/>
/// scripts so the user-supplied names + values never concatenate into a
/// script body (Directive 12).
///
/// <para><b>Colliding flags:</b> <c>-p</c> prefix-collides with
/// <c>-PipelineVariable</c> / <c>-ProgressAction</c> — declared as
/// <see cref="SwitchParameter"/> <c>P</c>. <c>-a</c> prefix-matches the
/// cmdlet's own <c>-Arguments</c> parameter — declared as
/// <see cref="SwitchParameter"/> <c>A</c>. <c>-u</c> has no PowerShell
/// common-parameter prefix overlap and stays in <see cref="Arguments"/>.</para>
///
/// <para><b>Directive 12 / AOT note:</b> the oracle used
/// <c>[scriptblock]::Create("&amp; $aliasValue @args")</c> to wire the dynamic
/// function. That is technically not AOT-safe (ScriptBlock.Create is reflection-
/// heavy), but this project (<c>PsBash.Cmdlets</c>) is published with
/// <c>PublishAot=false</c> precisely because it already uses
/// <c>InvokeScript</c>; the constraint that matters for security is that the
/// alias value is bound positionally through <c>$args</c> and never
/// concatenated into the script body, which is what we do here.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashAlias")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashAliasCommand : PSCmdlet
{
    /// <summary>
    /// <c>-p</c> print. Declared explicitly because the bare token prefix-
    /// matches <c>-PipelineVariable</c> / <c>-ProgressAction</c>. Naming the
    /// parameter literally <c>P</c> wins the binder's exact-name match step
    /// before prefix matching runs. (Per the oracle, <c>-p</c> is accepted but
    /// effectively a no-op — listing still happens via the no-operand path.)
    /// </summary>
    [Parameter]
    public SwitchParameter P { get; set; }

    /// <summary>
    /// <c>-a</c> remove-all (only with <c>-u</c>). Declared explicitly because
    /// the bare token prefix-matches the cmdlet's own <c>-Arguments</c>
    /// parameter under PowerShell parameter binding.
    /// </summary>
    [Parameter]
    public SwitchParameter A { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Read the psm1-owned global dictionary directly via SessionState. Going
    /// through <see cref="PSCmdlet.InvokeCommand"/>'s parameter-bound
    /// InvokeScript path obscured non-empty enumeration under .NET 8
    /// PowerShell SDK; this direct read is the load-bearing path.
    /// </summary>
    private System.Collections.IDictionary? GetAliasDict()
    {
        var v = SessionState.PSVariable.Get("global:BashUserAliases")
                ?? SessionState.PSVariable.Get("BashUserAliases");
        return v?.Value as System.Collections.IDictionary;
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (FileSystemHelpers.TryHandleVersion(this, "alias", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "alias"))
            {
                WriteObject(line);
            }
            return;
        }

        bool unaliasMode = false;
        bool removeAll = A.IsPresent;
        var operands = new List<string>();

        foreach (var arg in args)
        {
            if (arg == "-u") { unaliasMode = true; continue; }
            if (arg == "-a") { removeAll = true; continue; }
            if (arg == "-p") { continue; } // no-op (oracle parity)
            operands.Add(arg);
        }

        if (unaliasMode)
        {
            if (removeAll)
            {
                var dict = GetAliasDict();
                if (dict != null)
                {
                    var keys = new List<string>();
                    foreach (var k in dict.Keys) keys.Add(k?.ToString() ?? "");
                    foreach (var k in keys)
                    {
                        if (k.Length == 0) continue;
                        InvokeCommand.InvokeScript(
                            "param($n) Remove-Item -Path ('Function:\\' + $n) -Force -ErrorAction SilentlyContinue",
                            k);
                    }
                    dict.Clear();
                }
                return;
            }

            foreach (var name in operands)
            {
                var dict = GetAliasDict();
                if (dict != null && dict.Contains(name))
                {
                    dict.Remove(name);
                    // Drop the dynamic function via parameter-bound InvokeScript
                    // (name flows positionally, never into the script body).
                    InvokeCommand.InvokeScript(
                        "param($n) Remove-Item -Path ('Function:\\' + $n) -Force -ErrorAction SilentlyContinue",
                        name);
                }
                else
                {
                    FileSystemHelpers.WriteBashError(this, $"unalias: {name}: not found");
                }
            }
            return;
        }

        if (operands.Count == 0)
        {
            // List all aliases. Read the dictionary directly via PSVariable to
            // bypass the binary-module InvokeScript scope wrapper.
            var dict = GetAliasDict();
            if (dict != null)
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    var name = entry.Key?.ToString() ?? "";
                    var value = entry.Value?.ToString() ?? "";
                    var obj = new PSObject();
                    obj.TypeNames.Insert(0, "PsBash.AliasOutput");
                    obj.Properties.Add(new PSNoteProperty("Name", name));
                    obj.Properties.Add(new PSNoteProperty("Value", value));
                    obj.Properties.Add(new PSNoteProperty("BashText", $"alias {name}='{value}'"));
                    WriteObject(obj);
                }
            }
            return;
        }

        foreach (var arg in operands)
        {
            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                var aliasName = arg.Substring(0, eq);
                var aliasValue = arg.Substring(eq + 1);

                // Write directly into the global dictionary, then wire the
                // dynamic function via parameter-bound InvokeScript so the
                // name + value flow positionally (Directive 12 — never
                // concatenated into a script body).
                var dict = GetAliasDict();
                if (dict != null)
                {
                    dict[aliasName] = aliasValue;
                }
                // Use Function:\global: so the dynamic function lives in
                // the global scope and is reachable via Get-Command from
                // the test's session — not buried in the cmdlet's own
                // session state where the caller can't see it.
                InvokeCommand.InvokeScript(
                    "param($n, $v) " +
                    "$body = [scriptblock]::Create(\"& $v `@args\"); " +
                    "Set-Item -Path ('Function:\\global:' + $n) -Value $body -Force",
                    aliasName, aliasValue);
            }
            else
            {
                // Query an existing alias by name. Check the bash user-alias
                // dict first (alias defined via `alias name=value`); fall back
                // to the live PowerShell alias table so aliases that psm1
                // registered via `Set-Alias` (`rg`, `ll`, `grep`, …) are
                // findable too. Without the fallback, a tool that probes
                // `alias rg` to detect ripgrep sees "not found" on every
                // invocation even though `rg` IS a working alias.
                var dict = GetAliasDict();
                string? val = null;
                if (dict != null && dict.Contains(arg))
                {
                    val = dict[arg]?.ToString();
                }
                if (val == null)
                {
                    var psAlias = SessionState.InvokeCommand.GetCommand(
                        arg, CommandTypes.Alias) as AliasInfo;
                    if (psAlias != null)
                    {
                        val = psAlias.Definition;
                    }
                }
                if (val != null)
                {
                    var obj = new PSObject();
                    obj.TypeNames.Insert(0, "PsBash.AliasOutput");
                    obj.Properties.Add(new PSNoteProperty("Name", arg));
                    obj.Properties.Add(new PSNoteProperty("Value", val));
                    obj.Properties.Add(new PSNoteProperty("BashText", $"alias {arg}='{val}'"));
                    WriteObject(obj);
                }
                else
                {
                    FileSystemHelpers.WriteBashError(this, $"bash: alias: {arg}: not found");
                }
            }
        }
    }
}
