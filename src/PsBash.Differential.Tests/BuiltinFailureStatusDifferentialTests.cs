using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A builtin that fails with a message only keeps its status through the statement rules, against bash 5.2: fc when it
/// chooses no text (the run-time text call keeps Invoke-BashFc's status, PsBuild.RunBashTextCall <c>callerStatus</c>),
/// eval of blank text (0), and the siblings that publish a status through FileSystemHelpers.SetLastExitCode. The
/// messages go to /dev/null: the status is what is compared here (messages: BuiltinDiagnosticDifferentialTests).
/// In-runspace guard of the same rows: Host.Tests/BuiltinFailureStatusTests.
/// </summary>
public class BuiltinFailureStatusDifferentialTests
{
    private static Task Same(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30));

    private const string Prelude = "exec 2>/dev/null\nt() { echo \"$1=$2\"; }\n";

    [SkippableFact]
    public Task FcWithNoText_KeepsItsStatus() => Same(
        Prelude
        + "history -c; fc -s; t fc_s $?\n"
        + "fc -z; t fc_z $?\n"
        + "fc --bogus-xyz; t fc_bogus $?\n"
        + "( fc --bogus-xyz ) </dev/null >/dev/null; t fc_bogus_sub $?\n"
        + "g() { history -c; fc -s; }; g; t fn_fcs $?\n"
        + "history -c; fc -s >/dev/null; t redir_fcs $?\n"
        + "history -c; eval 'fc -s'; t eval_fcs $?\n"
        + "history -c; c=fc; $c -s; t dyn_fcs $?\n"
        + "history -c; fc -s || echo caught\n"
        + "f() { history -c; fc -s; echo \"in $?\"; return 4; }; f; t fn_ret $?\n"
        + "history -s 'false'; fc -s; t rerun_false $?\n"
        + "history -s 'true'; false; fc -s; t rerun_true $?\n");

    [SkippableFact]
    public Task FcWithNoText_EndsTheShellUnderErrexit() => Same(
        Prelude
        + "( set -e; history -c; fc -s; echo after ); t e_fcs $?\n"
        + "( set -e; fc -z; echo after ); t e_fcz $?\n");

    [SkippableFact]
    public Task EvalOfBlankText_IsZero() => Same(
        Prelude
        + "false; eval ''; t eval_blank $?\n"
        + "false; eval; t eval_none $?\n"
        + "eval 'false'; t eval_false $?\n"
        + "eval \"$(exit 3)\"; t eval_subst $?\n"
        + "x=; false; eval \"$x\"; t eval_var $?\n");

    [SkippableFact]
    public Task FailingBuiltins_KeepTheirStatus() => Same(
        Prelude
        + "cd \"$(mktemp -d)\" || exit 1\n"
        + "history -d 99; t hist_d $?\n"
        + "history -z; t hist_z $?\n"
        + "c=history; $c -d 99; t dyn_hist $?\n"
        + "cd /nonexistent_zz; t cd $?\n"
        + "c=cd; $c /nonexistent_zz; t dyn_cd $?\n"
        + "popd; t popd $?\n"
        + "shift 5; t shift $?\n"
        + "type nosuch_zz; t type $?\n"
        + "hash nosuch_zz; t hash $?\n"
        + "alias nosuch_zz; t alias $?\n"
        + "unalias nosuch_zz; t unalias $?\n"
        + "kill %9; t kill $?\n"
        + "disown %3; t disown $?\n"
        + "getopts; t getopts $?\n"
        + "trap 'x' NOSIG_ZZ; t trap $?\n"
        + "umask 999; t umask $?\n"
        + "enable nosuch_zz; t enable $?\n"
        + "export -f nosuch_zz; t exportf $?\n"
        + "declare -p nosuch_zz; t declarep $?\n"
        + "local x; t local $?\n"
        + "set -o nosuch_zz; t seto $?\n"
        + "shopt nosuch_zz; t shopt $?\n"
        + "source /nonexistent_zz; t source $?\n"
        + ". /nonexistent_zz; t dot $?\n"
        + "let 0; t let $?\n"
        + "read -u 9 v; t readu $?\n"
        + "[ 1 -gt ]; t bracket $?\n");
}
