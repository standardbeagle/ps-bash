using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// A real recursive-descent AWK interpreter (REFACTOR-2 follow-on; the C# port
/// that replaces the psm1 awk function web — <c>Invoke-BashAwk</c>,
/// <c>ConvertFrom-AwkProgram</c>, <c>Split-AwkFields</c>, <c>Read-AwkBlock</c>,
/// <c>Test-AwkPattern</c>, <c>Resolve-AwkExpression</c>, <c>Invoke-AwkAction</c>,
/// <c>Split-AwkStatements</c>, <c>Split-AwkFuncArgs</c>, <c>Format-AwkPrintf</c>,
/// <c>Resolve-AwkStringFunc</c>, <c>Expand-AwkString</c>).
///
/// The psm1 implementation was a regex/string-scan approximation: it could not
/// represent string concatenation of fields (<c>$1 $2</c>), <c>+=</c>
/// accumulation, <c>split()</c> into an array, <c>index()</c> in print position,
/// or <c>if/else</c> control flow — those five cases were the skipped parity
/// targets in <c>AwkDifferentialTests</c>. This interpreter parses the program
/// into an AST (BEGIN/END/pattern rules, full expression grammar with awk
/// precedence and string-vs-number value semantics) and evaluates it, closing
/// all five gaps.
///
/// Oracle: GNU awk via the bash differential suite (<c>AwkDifferentialTests</c>)
/// and the hand-asserted cmdlet surface (<c>InvokeBashAwkFileModeTests</c>).
/// </summary>
internal static class AwkInterpreter
{
    /// <summary>Compile a program string into rules. Throws <see cref="AwkSyntaxException"/>.</summary>
    public static AwkProgram Parse(string program)
    {
        var tokens = AwkLexer.Tokenize(program);
        return new AwkParser(tokens).ParseProgram();
    }

    public sealed class AwkSyntaxException : Exception
    {
        public AwkSyntaxException(string message) : base(message) { }
    }
}
