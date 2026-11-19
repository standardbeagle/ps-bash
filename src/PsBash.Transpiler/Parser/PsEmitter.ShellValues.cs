namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>
    /// True when <paramref name="name"/> reads as a non-string PowerShell value: a numeric special parameter
    /// (<c>$? $# $$ $!</c>) or a scalar the shell computes on each read (<see cref="PsBash.Core.ShellVariables.Computed"/>
    /// minus its arrays). Its colon operators must test the text, since PowerShell treats 0 as false.
    /// </summary>
    internal static bool IsNumericShellValue(string name) =>
        name is "?" or "#" or "$" or "!"
        || (PsBash.Core.ShellVariables.Computed.Contains(name) && PsBuild.ShellArrayExpr(name) is null);
}
