using System.Management.Automation;
using PsBash.Core.Parser;

namespace PsBash.Cmdlets;

/// <summary>
/// The print forms of <c>declare</c>/<c>typeset</c> (and <c>type -p</c>'s variable form): render a shell
/// variable as a <c>declare</c> line and a bash-defined function as its definition. A bash function is a
/// PowerShell function whose body the emitter wrapped in <see cref="PsBuild.FunctionPrologue"/> /
/// <see cref="PsBuild.FunctionEpilogue"/>; that signature is what separates it from the runtime's own
/// PowerShell functions, which bash would never list.
/// </summary>
internal static class DeclarePrinter
{
    /// <summary>
    /// Emit <c>declare -- NAME="VALUE"</c> (or <c>-a</c>/<c>-A</c> for a list/dictionary) for a shell variable,
    /// looked up as a global PowerShell variable first and then the environment. False (nothing written) when
    /// the variable does not exist.
    /// </summary>
    internal static bool TryPrintVariable(PSCmdlet cmdlet, string name)
    {
        object? val = null;
        try
        {
            val = cmdlet.SessionState.PSVariable.GetValue($"global:{name}");
        }
        catch
        {
            // Not a variable name PowerShell accepts; fall through to the environment.
        }

        val ??= BashVariableStore.Get(name);
        if (val is null) return false;

        string text;
        if (val is System.Collections.IDictionary)
            text = $"declare -A {name}={ToCompactJson(cmdlet, val)}";
        else if (val is System.Collections.IList && val is not string)
            text = $"declare -a {name}={ToCompactJson(cmdlet, val)}";
        else
            text = $"declare -- {name}=\"{val}\"";

        foreach (var line in BashRuntime.EmitBashLines(text + "\n"))
            cmdlet.WriteObject(line);
        return true;
    }

    /// <summary>The bash-defined function NAME, or null when NAME is not one.</summary>
    internal static FunctionInfo? FindBashFunction(PSCmdlet cmdlet, string name)
    {
        try
        {
            return cmdlet.InvokeCommand.GetCommand(name, CommandTypes.Function) is FunctionInfo fn && IsBashFunction(fn)
                ? fn
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every bash-defined function, sorted by name (bash lists them in that order).</summary>
    internal static List<FunctionInfo> AllBashFunctions(PSCmdlet cmdlet)
    {
        var result = new List<FunctionInfo>();
        try
        {
            foreach (var cmd in cmdlet.InvokeCommand.GetCommands("*", CommandTypes.Function, nameIsPattern: true))
            {
                if (cmd is FunctionInfo fn && IsBashFunction(fn)) result.Add(fn);
            }
        }
        catch
        {
            // An unreadable function table lists nothing.
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    internal static bool IsBashFunction(FunctionInfo fn) =>
        fn.Definition is { } def && def.StartsWith(PsBuild.FunctionPrologue, StringComparison.Ordinal);

    /// <summary>
    /// bash's <c>declare -f</c> layout (<c>NAME () </c>, <c>{ </c>, indented body, <c>}</c>). The body is the
    /// function's emitted PowerShell — the bash source is not kept once a function is defined; see
    /// docs/specs/intentional-differences.md.
    /// </summary>
    internal static void PrintFunction(PSCmdlet cmdlet, FunctionInfo fn)
    {
        var def = fn.Definition ?? "";
        var body = def.Substring(PsBuild.FunctionPrologue.Length);
        int end = body.LastIndexOf(PsBuild.FunctionEpilogue, StringComparison.Ordinal);
        if (end >= 0) body = body.Substring(0, end);

        var sb = new System.Text.StringBuilder();
        sb.Append(fn.Name).Append(" () \n{ \n");
        foreach (var line in body.Trim().Split('\n'))
            sb.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
        sb.Append("}\n");
        foreach (var line in BashRuntime.EmitBashLines(sb.ToString()))
            cmdlet.WriteObject(line);
    }

    private static string ToCompactJson(PSCmdlet cmdlet, object val)
    {
        // Delegate to ConvertTo-Json so the format matches the psm1 oracle byte-for-byte (integer vs string
        // boxing, key escaping).
        try
        {
            var results = cmdlet.InvokeCommand.InvokeScript("param($v) $v | ConvertTo-Json -Compress", val);
            if (results.Count > 0 && results[0] != null)
                return results[0].ToString() ?? "";
        }
        catch
        {
            // Fall through to an empty representation.
        }
        return "";
    }
}
