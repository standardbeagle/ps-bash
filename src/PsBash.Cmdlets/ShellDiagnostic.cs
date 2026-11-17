using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The source prefix bash puts on a diagnostic from the shell itself (bash error.c <c>error_prolog</c>): a
/// non-interactive shell writes <c>NAME: line N: </c>, NAME being <c>${BASH_SOURCE[0]}</c> (the script or sourced
/// file the command came from) or else <c>$0</c> (<c>bash</c> under <c>bash -c</c>), N the line of the command that
/// failed; an interactive shell writes <c>bash: </c>. Every shell-level message (command not found, ...) is built here
/// so the sites cannot drift. The line is the one the emitted code recorded for the running statement
/// (<see cref="VarFrameStack.SetLine"/>); 0 (nothing recorded: a cmdlet called directly) leaves the line out.
/// See docs/specs/error-handling.md "Diagnostic prefix".
/// </summary>
public static class ShellDiagnostic
{
    /// <summary>The prefix, from <c>$global:BASH_SOURCE</c> and <c>$global:BashPositional0</c> (the emitted code's names).</summary>
    public static string Prefix(object? bashSource, object? arg0)
        => Prefix(bashSource, arg0, VarFrameStack.Current.Line, InvokeBashShoptCommand.InteractiveShell);

    /// <summary><see cref="Prefix(object?, object?)"/> for a cmdlet, reading the two variables from its session.</summary>
    public static string Prefix(SessionState session)
        => Prefix(session.PSVariable.GetValue("global:BASH_SOURCE"), session.PSVariable.GetValue("global:BashPositional0"));

    /// <summary>The pure rule (unit-tested): which name and whether a line is shown.</summary>
    public static string Prefix(object? bashSource, object? arg0, int line, bool interactive)
    {
        if (interactive) return "bash: ";
        string name = FirstSource(bashSource);
        if (name.Length == 0) name = Unwrap(arg0) is { } a && a.ToString() is { Length: > 0 } s ? s : "bash";
        return line > 0
            ? name + ": line " + line.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": "
            : name + ": ";
    }

    private static object? Unwrap(object? o) => o is PSObject p ? p.BaseObject : o;

    private static string FirstSource(object? bashSource)
    {
        object? v = Unwrap(bashSource);
        if (v is string s) return s;
        if (v is System.Collections.IList { Count: > 0 } list) return Unwrap(list[0])?.ToString() ?? "";
        return "";
    }
}
