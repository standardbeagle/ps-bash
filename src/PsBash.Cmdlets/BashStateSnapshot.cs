using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Text;
using PsBash.Core.Parser;

namespace PsBash.Cmdlets;

/// <summary>
/// The shell state a background job (<c>cmd &amp;</c>) inherits from its parent, serialised as PowerShell
/// text the job's own process runs before its body (docs/specs/background-jobs.md §5). bash forks, so the
/// child sees a copy of everything; ps-bash starts a fresh host process for the job, so the copy is explicit.
/// <para>
/// The inventory is the same one <see cref="BashShellState"/> saves for subshells. Every
/// <see cref="BashShellState.GlobalNames"/> entry must be decided here — carried, or listed in
/// <see cref="NotInherited"/> — which the inventory guard test enforces.
/// </para>
/// </summary>
public static class BashStateSnapshot
{
    /// <summary>Globals a child does not inherit: caught traps are reset in a subshell, <c>$!</c> is the parent's.</summary>
    internal static readonly string[] NotInherited =
        ["BashTrapHandlers", "__BashTrapERR", "__BashTrapEXIT", "__BashTrapRETURN", "BashBgLastPid"];

    /// <summary>Functions PowerShell or the runspace setup define; they exist in the job's runspace already.</summary>
    private static readonly HashSet<string> RunspaceFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt", "TabExpansion2", "PSConsoleHostReadLine", "Clear-Host", "help", "man", "more", "mkdir", "oss",
        "Pause", "cd..", "cd\\", "Get-Verb", "ImportSystemModules", "Get-FileHash",
    };

    /// <summary>The parent's bash variables (env block), as the job's request environment.</summary>
    public static KeyValuePair<string, string>[] EnvironmentBlock()
        => BashVariableStore.Enumerate().ToArray();

    /// <summary>
    /// PowerShell statements that recreate the parent's non-environment shell state. Values are quoted with
    /// <see cref="PsBuild.SingleQuote"/>; nothing is concatenated from raw text.
    /// </summary>
    public static string BuildPrelude(SessionState session)
    {
        var sb = new StringBuilder();

        // Set-Location moves the PowerShell location only; a relative path handed to .NET (cd, redirects, children) resolves against the process cwd, which a warm spare host still has from its own start.
        var cwd = PsBuild.SingleQuote(session.Path.CurrentFileSystemLocation.ProviderPath);
        sb.Append("Set-Location -LiteralPath ").Append(cwd).Append("; [System.Environment]::CurrentDirectory = ").Append(cwd).Append('\n');

        foreach (var name in BashShellState.InheritedGlobalNames)
        {
            var v = session.PSVariable.Get("global:" + name);
            if (v is null) continue;
            sb.Append("$global:").Append(name).Append(" = ").Append(Literal(v.Value, 0)).Append('\n');
        }

        var shopt = InvokeBashShoptCommand.SnapshotOptions();
        foreach (var (opt, on) in shopt)
            sb.Append("Invoke-BashShopt ").Append(on ? "-s " : "-u ").Append(PsBuild.SingleQuote(opt)).Append(" 2>$null | Out-Null\n");

        var baseline = session.PSVariable.GetValue("global:__BashBaselineFunctions") as HashSet<string>;
        foreach (var info in session.InvokeCommand.GetCommands("*", CommandTypes.Function, nameIsPattern: true))
        {
            if (info is not FunctionInfo fn || fn.Module is not null || fn.ScriptBlock is null) continue;
            // Defined in a script file = part of the runtime (module psm1, runspace setup); the job host has it. User bash functions come from command text (no file).
            if (!string.IsNullOrEmpty(fn.ScriptBlock.File) || baseline?.Contains(fn.Name) == true) continue;
            if (RunspaceFunctions.Contains(fn.Name) || (fn.Name.Length == 2 && fn.Name[1] == ':')) continue;
            sb.Append("Set-Item -LiteralPath ").Append(PsBuild.SingleQuote("function:" + fn.Name))
              .Append(" -Value ([scriptblock]::Create(").Append(PsBuild.SingleQuote(fn.ScriptBlock.ToString())).Append("))\n");
        }

        // Bash arrays and associative arrays are PowerShell variables (object[] / hashtable). Scalars are env vars
        // and travel in EnvironmentBlock(), so only collections are written here. Runspace-owned variables are skipped.
        foreach (var item in session.InvokeProvider.ChildItem.Get("Variable:", false))
        {
            if (item.BaseObject is not PSVariable pv || !IsUserVariable(pv)) continue;
            var raw = pv.Value is PSObject p ? p.BaseObject : pv.Value;
            if (raw is not (object[] or IDictionary)) continue;
            sb.Append("$global:").Append(pv.Name).Append(" = ").Append(Literal(raw, 0)).Append('\n');
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> RunspaceVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "args", "input", "PSBoundParameters", "MyInvocation", "PSCmdlet", "PSScriptRoot", "PSCommandPath", "Error",
        "PSDefaultParameterValues", "PSModuleAutoLoadingPreference", "PSNativeCommandArgumentPassing", "ExecutionContext",
        "Matches", "ConsoleFileName", "PSEdition", "PSHOME", "PWD", "PID", "HOME", "Host", "ShellId", "StackTrace",
        "PSVersionTable", "MaximumHistoryCount", "FormatEnumerationLimit", "OutputEncoding", "PSSessionOption",
    };

    private static bool IsUserVariable(PSVariable v)
        => (v.Options & (ScopedItemOptions.Constant | ScopedItemOptions.ReadOnly | ScopedItemOptions.AllScope)) == 0
           && v.Name.Length > 0 && !v.Name.StartsWith("__", StringComparison.Ordinal)
           && !v.Name.StartsWith("Bash", StringComparison.Ordinal) && !v.Name.StartsWith("PS", StringComparison.Ordinal)
           && !RunspaceVariables.Contains(v.Name);

    private const int MaxDepth = 6;

    /// <summary>A PowerShell literal that evaluates back to <paramref name="value"/> (strings, numbers, booleans, arrays, tables).</summary>
    internal static string Literal(object? value, int depth)
    {
        var raw = value is PSObject pso ? pso.BaseObject : value;
        switch (raw)
        {
            case null: return "$null";
            case bool b: return b ? "$true" : "$false";
            case string s: return PsBuild.SingleQuote(s);
            case char c: return PsBuild.SingleQuote(c.ToString());
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(raw, CultureInfo.InvariantCulture)!;
            case double or float or decimal:
                return Convert.ToString(raw, CultureInfo.InvariantCulture)!;
        }
        if (depth >= MaxDepth) return PsBuild.SingleQuote(raw.ToString() ?? "");

        if (raw is BashAssoc assoc)
        {
            // Replayed oldest-first so the rebuilt table enumerates in the same (bash hash) order.
            var args = new List<string>();
            foreach (var kv in assoc.ReplayOrder()) { args.Add(PsBuild.SingleQuote(kv.Key)); args.Add(Literal(kv.Value, depth + 1)); }
            return "[PsBash.Cmdlets.BashAssoc]::FromPairs(" + string.Join(", ", args) + ")";
        }
        if (raw is IDictionary d)
        {
            var sb = new StringBuilder(raw is System.Collections.Specialized.IOrderedDictionary ? "[ordered]@{" : "@{");
            bool first = true;
            foreach (DictionaryEntry e in d)
            {
                if (!first) sb.Append("; ");
                first = false;
                sb.Append(Literal(e.Key?.ToString() ?? "", depth + 1)).Append(" = ").Append(Literal(e.Value, depth + 1));
            }
            return sb.Append('}').ToString();
        }
        if (raw is IEnumerable e2)
        {
            var parts = new List<string>();
            foreach (var item in e2) parts.Add(Literal(item, depth + 1));
            return parts.Count == 0 ? "@()" : "@(" + string.Join(", ", parts) + ")";
        }
        return PsBuild.SingleQuote(raw.ToString() ?? "");
    }
}
