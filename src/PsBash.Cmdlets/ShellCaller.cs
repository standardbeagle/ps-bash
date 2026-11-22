using System.Globalization;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>caller</c> builtin (bash 5.2 <c>builtins/caller.def</c>), from the three frame arrays the shell keeps:
/// <c>BASH_LINENO</c> (<see cref="VarFrameStack.BashLineNo"/>), <c>FUNCNAME</c> (<c>Get-BashFuncNames</c>) and
/// <c>BASH_SOURCE</c> (<see cref="VarFrameStack.BashSource"/>).
/// <list type="bullet">
/// <item>no operand: <c>BASH_LINENO[0] BASH_SOURCE[1]</c>, a missing one printed as <c>NULL</c> (<c>0 NULL</c> at a
/// script's top level), status 0;</item>
/// <item><c>caller N</c>: <c>BASH_LINENO[N] FUNCNAME[N+1] BASH_SOURCE[N+1]</c>, status 1 when any is missing; an operand
/// that is not a number is "invalid number" and the usage, status 2 (words after it are ignored).</item>
/// </list>
/// With no frame at all (<c>bash -c</c> top level, BASH_LINENO empty) it fails before reading its words; an option word
/// (<c>-1</c> too) is the usage error, which the caller reports (<c>BuiltinOptionError</c>).
/// REGRESSION: <c>caller</c> was a stub that always failed, so an error handler's stack trace printed nothing.
/// </summary>
public static class ShellCaller
{
    /// <summary>What <c>caller</c> prints and its status; <paramref name="operand"/> is its first word (null: none; an object, as PowerShell hands a string parameter "" for $null).</summary>
    public static Result Report(string[]? lineNo, string[]? funcName, string[]? source, object? operand)
        => Report(lineNo, funcName, source, operand?.ToString());

    /// <summary>The pure rule of <see cref="Report(string[], string[], string[], object)"/> (unit-tested).</summary>
    internal static Result Report(string[]? lineNo, string[]? funcName, string[]? source, string? operand)
    {
        lineNo ??= []; funcName ??= []; source ??= [];
        if (lineNo.Length == 0) return new Result(null, null, 1);
        if (operand is null)
            return new Result(lineNo[0] + " " + (source.Length > 1 ? source[1] : "NULL"), null, 0);
        if (!long.TryParse(operand.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n))
            return new Result(null, "caller: " + operand + ": invalid number", 2);
        if (n < 0 || n >= lineNo.Length || n + 1 >= funcName.Length || n + 1 >= source.Length)
            return new Result(null, null, 1);
        int i = (int)n;
        return new Result(lineNo[i] + " " + funcName[i + 1] + " " + source[i + 1], null, 0);
    }

    /// <summary><c>caller</c>'s stdout line (null: none), its error line without the shell prefix (null: none; the usage follows it), and its status.</summary>
    public readonly record struct Result(string? Output, string? Error, int Status);

    /// <summary>bash's usage line for <c>caller</c>.</summary>
    public const string Usage = "caller: usage: caller [expr]";
}
