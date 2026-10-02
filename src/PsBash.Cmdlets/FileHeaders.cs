namespace PsBash.Cmdlets;

/// <summary>
/// GNU head/tail "==> NAME &lt;==" file headers (coreutils 9.4 <c>head.c</c>/<c>tail.c</c>
/// <c>write_header</c>): printed before every file when more than one operand was given, always with
/// <c>-v</c>, never with <c>-q</c> (the LAST of -q/-v wins). Every header after the first is preceded by
/// one blank-line separator, which is a plain <c>\n</c> in front of the header text — when the previous
/// output had no final newline that <c>\n</c> simply terminates it. A pipeline stdin operand is named
/// "standard input".
/// </summary>
internal static class FileHeaders
{
    internal enum Mode { Default, Always, Never }

    internal const string StandardInput = "standard input";

    /// <summary>Whether headers print for a run with <paramref name="operandCount"/> file operands.</summary>
    internal static bool Wanted(Mode mode, int operandCount) => mode switch
    {
        Mode.Always => true,
        Mode.Never => false,
        _ => operandCount > 1,
    };

    /// <summary>The operand as the user typed it (resolved paths are mapped back by <see cref="OperandDisplay"/>).</summary>
    internal static string Display(System.Management.Automation.PSCmdlet cmdlet, string resolvedPath)
        => OperandDisplay.Rewrite(cmdlet, resolvedPath).Replace('\\', '/');

    /// <summary>The record carrying the header (and the blank separator when it is not the first).</summary>
    internal static object Record(string name, bool first)
        => BashRuntime.TextRecord((first ? "" : "\n") + "==> " + name + " <==", false);
}
