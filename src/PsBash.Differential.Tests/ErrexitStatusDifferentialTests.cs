using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// errexit and the exit status, decided on the parsed commands and set by every command (spec
/// <c>docs/specs/emitter-strategy.md</c> §set, "Every command sets the status"). REGRESSIONS: errexit turned on by a
/// word known only at run time (<c>set -$o</c>) emitted no checks; an assignment reset the status only when the text
/// held <c>$?</c>; failing builtins never ended a <c>set -e</c> shell nor fired ERR; inherit_errexit was ignored beside
/// <c>set -E</c>; <c>true &lt; f</c> made the whole script fail to transpile.
/// </summary>
public class ErrexitStatusDifferentialTests
{
    [SkippableFact]
    public Task Errexit_TurnedOnThroughARunTimeWord() => AssertOracle.EqualAsync(
        "o=e; nm=errexit\n"
        + "( set -$o; false; echo \"n04 WRONG\" ); echo \"n04 $?\"\n"
        + "( set -o $nm; f() { false; echo inf; }; f || echo \"n05 caught\" ); echo \"n05 $?\"\n"
        + "c=set; ( $c -e; false; echo \"n07 WRONG\" ); echo \"n07 $?\"\n"
        + "( x=\"set -e\"; eval \"$x\"; false; echo \"n10 WRONG\" ); echo \"n10 $?\"\n"
        + "printf 'set -e\\n' > lib.sh; ( . ./lib.sh; false; echo survived ); echo \"h1 $?\"");

    [SkippableFact]
    public Task AssignmentAndDefinition_SetStatusZero() => AssertOracle.EqualAsync(
        "f1() { false; y=1; }; if f1; then echo \"m01 ok\"; else echo \"m01 WRONG\"; fi\n"
        + "for i in 1; do false; y=1; done && echo \"m06 ok\" || echo \"m06 WRONG\"\n"
        + "false; g() { :; }; echo \"fndef $?\"\n"
        + "g2() { false; x=1; }; g2; echo \"h3 $?\"");

    [SkippableFact]
    public Task FailingBuiltin_EndsAnErrexitShellAndFiresErr() => AssertOracle.EqualAsync(
        "( set -e; shift 5; echo survived ); echo \"u01 $?\"\n"
        + "( set -e; declare -p nosuch_zz 2>/dev/null; echo survived ); echo \"u02 $?\"\n"
        + "( set -e; local x 2>/dev/null; echo survived ); echo \"u07 $?\"\n"
        + "( set -e; export -f nosuchfn_zz 2>/dev/null; echo survived ); echo \"u08 $?\"\n"
        + "( trap 'echo ERR' ERR; shift 5; echo after ); echo v01\n"
        + "x=1 > /nonexist_zz/x 2>/dev/null; echo \"k35 $?\"\n"
        + "shift 5; echo \"k25 $?\"; export -f nosuchfn_zz 2>/dev/null; echo \"k30c $?\"");

    [SkippableFact]
    public Task SpecialForms_LeaveBashsStatus() => AssertOracle.EqualAsync(
        "false; echo \"k18 $((1+1)) $?\"\n"
        + "arr=( $(exit 3) ); echo \"k46 $?\"\n"
        + "declare -a arr2=( $(exit 3) ); echo \"k47 $?\"\n"
        + "false; eval ''; echo \"k48 $?\"\n"
        + "local zz 2>/dev/null; echo \"k43 $?\"\n"
        + "t4() { trap 'echo \"t04 ret-trap $?\"' RETURN; return 6; }; t4; echo \"t04 after $?\"; trap - RETURN\n"
        + "( set -e; trap 'false; echo intrap' EXIT; true ); echo \"u31 $?\"");

    /// <summary>A signal the shell sends itself: the trap's <c>$?</c> is kill's own status.</summary>
    [SkippableFact]
    public Task SelfSignalTrap_SeesKillsStatus() => AssertOracle.EqualAsync(
        "trap 'echo \"t01 in-trap $?\"' USR1\n"
        + "(exit 3); kill -USR1 $$; echo \"t01 after $?\"");

    [SkippableFact]
    public Task InheritErrexit_HoldsBesideErrtrace() => AssertOracle.EqualAsync(
        "( set -eE; shopt -s inherit_errexit; x=$(false; echo after); echo \"x=$x survived\" ); echo \"s27 $?\"\n"
        + "( set -eE; trap 'echo ERR' ERR; shopt -s inherit_errexit; x=$(false; echo after); echo \"x=$x\" ); echo \"s28 $?\"");

    /// <summary>REGRESSION: in a script that can turn nounset on (<c>set -$o</c>) every arithmetic call is wrapped the way a
    /// word is, so a failed <c>(( 1/0 ))</c> COMMAND was taken for a failed word: the rest of its line was dropped and the
    /// mark broke later commands (<c>f || echo</c> lost f's output).</summary>
    [SkippableFact]
    public Task FailedArithmeticCommand_KeepsItsLine_WhenNounsetCanBeOn() => AssertOracle.EqualAsync(
        "o=e\n"
        + "(( 1/0 )) 2>/dev/null; echo \"DZ $?\"\n"
        + "( set -$o; f() { false; echo inf; }; f || echo caught ); echo \"n05 $?\"\n"
        + "x=$(( 1/0 )) 2>/dev/null; echo \"word $?\"\n"
        + "echo end");

    [SkippableFact]
    public Task StatementBuiltin_WithAStreamRedirect_Runs() => AssertOracle.EqualAsync(
        "printf 'l1\\nl2\\n' > in.txt\n"
        + "true < in.txt; echo \"t1 $?\"\n"
        + ": <<< word; echo \"t3 $?\"\n"
        + "cd /nonexist_zz 2>/dev/null 1>&2; echo \"t4 $?\"\n"
        + "f7() { return 3 <<< w; echo no; }; f7; echo \"t7 $?\"\n"
        + "false <<EOF\nbody\nEOF\necho \"t10 $?\"\n"
        + "( exec <<< word; read w; echo \"t12 $w\" )\n"
        + "echo x | builtin >> out.txt; echo \"t13 $?\"");
}
