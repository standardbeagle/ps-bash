using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>Under <c>set -e</c> a compound command whose <c>&lt; file</c> cannot be opened ends the script (bash 5.2); in a condition or <c>||</c> list it does not.</summary>
public class ErrexitInputRedirectTests
{
    [SkippableFact]
    public Task BraceGroup_MissingInput_EndsScript() => AssertOracle.EqualAsync(
        "set -e\n{ { cat; } < /nonexistent/zz.txt; } 2>/dev/null\necho should-not-print");

    [SkippableFact]
    public Task WhileRead_MissingInput_EndsScript() => AssertOracle.EqualAsync(
        "set -e\n{ while read l; do echo $l; done < /nonexistent/zz.txt; } 2>/dev/null\necho should-not-print");

    [SkippableFact]
    public Task MissingInput_InOrList_Continues() => AssertOracle.EqualAsync(
        "set -e\n{ { cat; } < /nonexistent/zz.txt || echo handled; } 2>/dev/null\necho continues");

    [SkippableFact]
    public Task WhileRead_WithoutErrexit_StatusIsOne() => AssertOracle.EqualAsync(
        "{ while read l; do echo $l; done < /nonexistent/zz.txt; echo st=$?; } 2>/dev/null");

    // Every compound kind, nested in a redirected group or not, in a function and a subshell: the failed open ends the
    // (sub)shell; a nested failure the body handled (`|| :`) does not end the outer one. Emitted form:
    // RedirectGroupGuardTests.Transpile_RedirectedCompoundUnderErrexit_IsFollowedByTheOpenFailureCheck.
    [SkippableFact]
    public Task RedirectedCompounds_FailedOpen_EndsTheShellNestedOrNot() => AssertOracle.EqualAsync(
        "( set -e; { { cat; } < /nonexistent/zz; } 2>/dev/null; echo no1 ); echo \"s1=$?\"\n"
        + "( set -e; if true; then :; fi < /nonexistent/zz; echo no2 ) 2>/dev/null; echo \"s2=$?\"\n"
        + "( set -e; for i in 1; do :; done < /nonexistent/zz; echo no3 ) 2>/dev/null; echo \"s3=$?\"\n"
        + "( set -e; case x in x) : ;; esac < /nonexistent/zz; echo no4 ) 2>/dev/null; echo \"s4=$?\"\n"
        + "( set -e; f(){ { { cat; } < /nonexistent/zz; } 2>/dev/null; echo no5; }; f; echo no5b ); echo \"s5=$?\"\n"
        + "( set -e; g(){ { cat; } < /nonexistent/zz; echo no6; }; ( g ); echo no6b ) 2>/dev/null; echo \"s6=$?\"\n"
        + "( set -e; echo hi > in; { { cat; } < /nonexistent/zz || :; } < in; echo leak1 ) 2>/dev/null; echo \"s7=$?\"\n"
        + "( set -e; { { cat; } < /nonexistent/zz || :; } > out; echo leak2 ) 2>/dev/null; echo \"s8=$?\"\n"
        + "( set -e; { { cat; } > /nonexistent/d/o; } 2>/dev/null; echo no9 ); echo \"s9=$?\"\n"
        + "( set -e; echo hi > in; { cat; } < in; echo ok10 ); echo \"s10=$?\"\n"
        + "( set -e; while false; do :; done < /nonexistent/zz; echo no11 ) 2>/dev/null; echo \"s11=$?\"\n");

    // A builtin whose own status errexit ignores (`:`, `true`, `export`, `local`) fails when its redirect cannot open, as
    // a compound with a stderr file does; redirects that cannot fail and a handled failure go on.
    [SkippableFact]
    public Task NeutralBuiltinsAndStderrFiles_FailedOpen_EndsTheShell() => AssertOracle.EqualAsync(
        "( set -e; : > /nonexistent/d/o; echo no1 ) 2>/dev/null; echo \"s1=$?\"\n"
        + "( set -e; true < /nonexistent/zz; echo no2 ) 2>/dev/null; echo \"s2=$?\"\n"
        + "( set -e; { :; } 2> /nonexistent/d/e; echo no3 ) 2>/dev/null; echo \"s3=$?\"\n"
        + "( set -e; export Y=1 > /nonexistent/d/o; echo no4 ) 2>/dev/null; echo \"s4=$?\"\n"
        + "( set -e; local_f(){ local v=1 > /nonexistent/d/o; echo no5; }; local_f ) 2>/dev/null; echo \"s5=$?\"\n"
        + "( set -e; : 2>> /nonexistent/d/e; echo no6 ) 2>/dev/null; echo \"s6=$?\"\n"
        + "( set -e; : > /dev/null; true < /dev/null; { :; } 2>/dev/null; echo ok7 ); echo \"s7=$?\"\n"
        + "( set -e; : > /nonexistent/d/o || echo handled8; echo cont8 ) 2>/dev/null; echo \"s8=$?\"\n"
        + "( set -e; { { :; } 2> /nonexistent/d/e; } 2>/dev/null; echo no9 ); echo \"s9=$?\"\n");

    // The ERR trap runs once for the failed open, nested or not, with $LINENO at the command before a compound and at a
    // simple command's own line (bash 5.2).
    [SkippableFact]
    public Task RedirectedCompounds_FailedOpen_RunsTheErrTrap() => AssertOracle.EqualAsync(
        "trap 'echo \"ERR L=$LINENO\"' ERR\necho a\n{ { cat; } < /nonexistent/zz\n} 2>/dev/null\necho after\n"
        + "{ cat; } 2>/dev/null < /nonexistent/zz\necho end\n"
        + ": 2>/dev/null > /nonexistent/d/o\ntrue 2>/dev/null < /nonexistent/zz\n{ :; } 2>/dev/null 2> /nonexistent/d/e\necho done\n");
}
