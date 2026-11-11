using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>trap … DEBUG</c> runs before every simple command with that command's <c>$LINENO</c>: <c>[[ ]]</c>, <c>(( ))</c>,
/// assignments and each <c>&amp;&amp;</c>/<c>||</c> operand included; before each <c>for</c> pass, each arithmetic-for
/// clause and <c>case</c>; for every simple pipeline stage (in the parent, before the pipeline); and not inside
/// <c>$( )</c>, a subshell, a compound pipeline stage or a function. ps-bash ran it only before a top-level plain
/// command, and also inside <c>$( )</c> and subshells (so <c>{ a; b; } | wc -l</c> counted the action's lines).
/// </summary>
public class DebugTrapDifferentialTests
{
    private const string Log = "dbg() { echo \"  [$1]\"; }\ntrap 'dbg $LINENO' DEBUG\n";

    [Theory]
    [InlineData("echo a\necho b; echo c\necho d && echo e\necho f || echo g\n(( h = 42 ))\n[[ j == j ]]\nv=1\nreadonly r=2")]
    [InlineData("for x in 1 2; do\n  echo x=$x\ndone\nfor (( i = 0; i < 2; i++ )); do\n  echo i=$i\ndone")]
    [InlineData("n=foo.py\ncase $n in\n  *.py) echo py ;;\nesac\nif test x = x; then echo if; fi\nwhile false; do :; done")]
    [InlineData("{ echo x; echo y; } | wc -l\ndate | cat | wc -l\necho \"r=$(echo sub)\"\n( echo sh )\nf() { echo inf; }\nf")]
    [InlineData("false || false || false\n! true\ntrap - DEBUG\necho off")]
    // The action's words are data: `;`, `$( )` and quotes in an operand are not re-run by the trap.
    [InlineData("x='a; echo pwned'\necho \"$x\" && echo \"$(echo 'q\"')\"")]
    public Task DebugTrap_RunsBeforeEachSimpleCommand(string script) => AssertOracle.EqualAsync(Log + script);

    /// <summary>Under <c>set -e</c> a DEBUG action that fails ends the script with its status (bash: 42 after one run).</summary>
    [Fact]
    public Task DebugTrap_FailingActionUnderErrexit_EndsTheScript() => AssertOracle.EqualAsync(
        "set -o errexit\nd() { echo \"  [$1]\"; return 42; }\ntrap 'd $LINENO' DEBUG\necho A\necho B");
}
