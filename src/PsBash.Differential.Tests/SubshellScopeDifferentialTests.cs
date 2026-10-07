using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for SUBSHELL scope: nothing a <c>( … )</c> or <c>$( … )</c> body changes may leak
/// into the parent (bash forks). ps-bash runs the body in the same process; PsBuild.ShellStateScope +
/// BashShellState save and restore the state a PowerShell child scope does not isolate.
///
/// Covered regressions:
///   - <c>( set -e; true ); false; echo survived</c> stopped the PARENT at <c>false</c> (errexit leaked)
///   - <c>( x=inner ); echo $x</c> printed inner (env-var variables leaked); same for <c>$( q=zz; … )</c>
///   - <c>shopt</c> / <c>trap</c> inside a subshell leaked; <c>$(exit 4)</c> killed the whole script
///   - <c>v=$(set -e; false; echo x)</c> ran past the failure (a body's own set -e was ignored)
///   - <c>set -o pipefail</c> fell through to PowerShell's Set-Variable alias ("Cannot bind parameter")
///   - <c>printf x &gt;&amp;2; echo y &gt;&amp;2</c> wrote "x\ny" (every stderr record got a newline)
/// </summary>
public class SubshellScopeDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task Subshell_SetE_DoesNotLeakIntoParent()
        => Eq("( set -e; true ); false; echo survived; set +e; ( set -e; false; echo not-printed ); echo \"sub rc=$?\"");

    [SkippableFact]
    public Task Subshell_SetE_InsideFunction_LeavesOnlyTheSubshell()
        => Eq("f() { ( set -e; false ); echo \"fn survived rc=$?\"; }; f; set -e; ( set +e; false; echo inner-continues ); echo outer-still-e");

    [SkippableFact]
    public Task Subshell_VariablesCdShoptTrap_AreLocal()
        => Eq("x=outer; ( x=inner; export X2=1 ); echo \"$x [${X2:-unset}]\"; ( cd / ); [ \"$PWD\" != / ] && echo cd-local; "
            + "( shopt -s nullglob ); for f in /nonexist-zz/*; do echo \"glob $f\"; done; ( trap 'echo T' USR1 ); trap -p USR1; echo trap-local");

    [SkippableFact]
    public Task CommandSub_IsASubshell_ForStateAndExit()
        => Eq("w=$(q=zz; echo $q); echo \"[$w] [${q:-unset}]\"; v=$(exit 4); echo \"exit rc=$?\"; v=$( set -e; false; echo after ); echo \"[$v] rc=$?\"");

    [SkippableFact]
    public Task Set_UnsupportedLongOption_IsANoOp()
        => Eq("set -o pipefail; echo \"rc=$?\"; set +o pipefail; echo \"rc=$?\"; false | true; echo \"pipe rc=$?\"");

    [SkippableFact]
    public Task Stderr_UnterminatedRecord_HasNoNewline()
        => Eq("printf x >&2; echo y >&2; f=$(mktemp); { printf z >&2; } 2>\"$f\"; wc -c < \"$f\"; rm -f \"$f\"");
}
