using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Four bash behaviours ps-bash approximated statically or through PowerShell's own variables:
/// a nameref target passed on through another function's positionals, <c>$BASHPID</c> in a subshell,
/// <c>PWD=foo</c> (a plain assignment that does not move the shell), and a quoted pattern operand
/// (<c>head 'two-*'</c>), which the cmdlet re-globbed after the shell had already expanded its words.
/// </summary>
public class SubshellIdentityAndDynamicNamerefTests
{
    [SkippableFact]
    public Task Nameref_TargetForwardedThroughDollarAt_WritesCallersLocal() => AssertOracle.EqualAsync(
        "f3() { local -n ref=$1; ref=x; }\nf2() { f3 \"$@\"; }\nf1() { local F1=F1; echo F1=$F1; f2 F1; echo F1=$F1; }\nf1");

    [SkippableFact]
    public Task Nameref_TargetForwardedThroughPositional_ReadsAndWrites() => AssertOracle.EqualAsync(
        "g() { local -n r=$1; echo \"read=$r\"; r=new; }\nh() { g \"$2\"; }\nv=old; w=W\nh ignored v\necho v=$v\nh ignored w\necho w=$w");

    [SkippableFact]
    public Task Bashpid_ChangesInSubshellAndCommandSub_DollarDollarDoesNot() => AssertOracle.EqualAsync(
        "parent=$BASHPID\n"
        + "( child=$BASHPID; [ \"$parent\" != \"$child\" ] && echo sub; [ \"$child\" = \"$BASHPID\" ] && echo stable; "
        + "( [ \"$BASHPID\" != \"$child\" ] && echo nested ); [ \"$$\" = \"$parent\" ] && echo dollar )\n"
        + "c=$(echo $BASHPID); [ \"$c\" != \"$parent\" ] && echo cmdsub\n"
        + "[ \"$parent\" = \"$BASHPID\" ] && [ \"$$\" = \"$BASHPID\" ] && echo top\n"
        + "( kill -0 $BASHPID && echo alive; trap 'echo got' USR1; kill -USR1 $BASHPID; echo after )");

    [SkippableFact]
    public Task PwdAssignment_IsAVariable_PwdBuiltinAndCdUnaffected() => AssertOracle.EqualAsync(
        "d=$(mktemp -d); mkdir \"$d/sub\"; cd \"$d/sub\"\nPWD=foo\necho \"$PWD\" $PWD ${PWD}\n"
        + "case \"$(pwd)\" in */sub) echo pwd-is-dir;; esac\ncd ..; case \"$PWD\" in foo) echo stale;; *) echo cd-resets;; esac");

    [SkippableFact]
    public Task QuotedPatternOperand_IsALiteralName_NotReglobbed() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"; echo hi > two-bar\n"
        // cat/wc: status only — GNU shell-quotes a name with `*` in their message ('two-*'), ps-bash does not yet.
        + "head 'two-*'; echo st=$?\ntail two-\\*; echo st=$?\ncat \"two-*\" 2>/dev/null; echo st=$?\nwc -l 'two-?ar' 2>/dev/null; echo st=$?\n"
        + "p='two-*'; head \"$p\"; echo st=$?\nhead two-*; echo st=$?");
}
