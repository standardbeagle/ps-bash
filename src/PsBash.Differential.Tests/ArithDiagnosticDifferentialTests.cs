using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// An arithmetic error outside <c>let</c> is the shell's diagnostic, in bash's words: the prefix with the line
/// (<c>s.sh: line 4: </c>), the context's label (<c>((: </c> for the command, a <c>for ((;;))</c> clause and an
/// <c>if</c>/<c>while</c> condition, <c>[[: </c> for a <c>[[ -eq ]]</c> operand, none for <c>$(( ))</c>), the expression
/// left-trimmed only and bash's error token. It read <c>division by 0</c> with no prefix, no line, no token, and a
/// condition named the line of the statement before. docs/specs/error-handling.md "Diagnostic prefix". Recorded from
/// bash 5.2 (cassette). Streams are captured apart (<c>&gt;out 2&gt;err</c>): only the order within each is compared.
/// An expansion error abandons the rest of its line in bash and not in ps-bash (error-handling.md "Known differences"), so every
/// failing expansion ends its line.
/// </summary>
public class ArithDiagnosticDifferentialTests
{
    private const string Work = "W=\"$(mktemp -d)\"; cd \"$W\"\n";
    private const string Done = "\ncd /; rm -rf \"$W\"";
    private const string Run = "bash s.sh >out 2>err; cat err; echo ---; cat out";

    /// <summary>Division, negative exponent, bad number and syntax errors of <c>$(( ))</c> and <c>(( ))</c>.</summary>
    [SkippableFact]
    public Task ArithError_IsTheShellsDiagnosticInBashsWords()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n"
            + "echo $((1/0+2))\n"
            + "echo $(( 1 / 0 + 2 ))\n"
            + "echo $(( 10/(1-1) ))\n"
            + "echo $(( 2 ** -1 + 3 ))\n"
            + "echo $(( 08 ))\n"
            + "echo $(( 3#9 ))\n"
            + "x=0; echo $(( 5 / x + 1 ))\n"
            + "echo $(( 1, 2/0, 3 ))\n"
            + "(( x = 4 / 0 ))\n"
            + "echo \"rc=$?\"\n"
            + "((   1+   ))\n"
            + "echo \"rc=$?\"\n"
            + "(( 1 @ 2 ))\n"
            + "echo \"rc=$?\"\n"
            + "echo $(( 2 + 3 4 ))\n"
            + "y=$(( 2 + ))\n"
            + "echo end\n"
            + "EOF\n"
            + Run + Done,
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// The line of an arithmetic error in a condition: a <c>while</c> condition tested again after its body recorded a
    /// later line, a <c>for ((;;))</c> clause, <c>if (( ))</c>, <c>while [[ -eq ]]</c>.
    /// </summary>
    [SkippableFact]
    public Task ArithErrorInACondition_NamesTheConditionsLine()
        => AssertOracle.EqualAsync(Work
            + "cat > s.sh <<'EOF'\n"
            + "i=0\n"
            + "while (( i++ < 2 )); do\n"
            + "  cd /nonexist_zz 2>/dev/null\n"
            + "  (( i == 2 && 1/0 ))\n"
            + "done\n"
            + "for (( i=0; i<1/0; i++ )); do echo no; done\n"
            + "if (( 1/0 )); then echo y; else echo n; fi\n"
            + "while (( 2/0 )); do break; done\n"
            + "if [[ 1/0 -eq 1 ]]; then :; fi\n"
            + "while [[ x -eq 1/0 ]]; do :; done\n"
            + "[[ 1/0 -eq 1 ]]\n"
            + "echo \"rc=$?\"\n"
            + "echo end\n"
            + "EOF\n"
            + Run + Done,
            timeout: TimeSpan.FromSeconds(60));
}
