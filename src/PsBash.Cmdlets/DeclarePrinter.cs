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
        if (val is System.Collections.IDictionary dict)
            text = $"declare -A{BashShellState.AttributeLetters(name)} {name}={FormatAssociative(dict)}";
        else if (val is System.Collections.IList list && val is not string)
            text = $"declare -a{BashShellState.AttributeLetters(name)} {name}={FormatIndexed(list)}";
        else
        {
            string letters = BashShellState.AttributeLetters(name);
            text = $"declare -{(letters.Length == 0 ? "-" : letters)} {name}={QuoteValue(val.ToString())}";
        }

        foreach (var line in BashRuntime.EmitBashLines(text + "\n"))
            cmdlet.WriteObject(line);
        return true;
    }

    /// <summary>bash <c>declare -p</c> value text: double-quoted, with <c>"</c>, <c>\</c>, <c>$</c> and backtick escaped.</summary>
    internal static string QuoteValue(string? value)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (char c in value ?? "")
        {
            if (c is (char)34 or (char)92 or (char)36 or (char)96) sb.Append((char)92);
            sb.Append(c);
        }
        return sb.Append((char)34).ToString();
    }

    /// <summary>bash indexed-array form: <c>([0]="a" [1]="b c")</c>.</summary>
    internal static string FormatIndexed(System.Collections.IList list)
    {
        var sb = new System.Text.StringBuilder("(");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append((char)32);
            sb.Append("[").Append(i).Append("]=").Append(QuoteValue(list[i]?.ToString()));
        }
        return sb.Append(")").ToString();
    }

    /// <summary>bash associative-array form: <c>([k]="v" [j]="w" )</c> (note the trailing space bash prints).</summary>
    internal static string FormatAssociative(System.Collections.IDictionary dict)
    {
        var sb = new System.Text.StringBuilder("(");
        foreach (System.Collections.DictionaryEntry e in dict)
            sb.Append("[").Append(e.Key).Append("]=").Append(QuoteValue(e.Value?.ToString())).Append((char)32);
        return sb.Append(")").ToString();
    }

    /// <summary>The bash-defined function NAME, or null when NAME is not one.</summary>
    /// <remarks>
    /// A <c>function:</c> provider read, NOT <c>InvokeCommand.GetCommand</c>: command discovery auto-imports
    /// whatever PSModulePath module exports NAME. In the ps-bash host (psm1 run as a script, so no PsBash module
    /// object) the Bash tool snapshot's <c>typeset -f rg</c> imported the installed PsBash, which re-ran the whole
    /// psm1 mid-session and reset <c>$BASH_VERSION</c> to <c>0.0.0(1)-release</c>. A bash function is defined
    /// in the session, never in a not-yet-loaded module, so the session table is the complete answer.
    /// </remarks>
    internal static FunctionInfo? FindBashFunction(PSCmdlet cmdlet, string name)
    {
        try
        {
            var items = cmdlet.SessionState.InvokeProvider.Item.Get(
                new[] { @"function:\" + name }, force: false, literalPath: true);
            return items.Count > 0 && items[0].BaseObject is FunctionInfo fn && IsBashFunction(fn)
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
}
