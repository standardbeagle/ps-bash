using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A builtin named at run time (<c>"$@"</c>, <c>$cmd</c>, <c>command NAME</c>, <c>builtin NAME</c>) runs as bash runs it,
/// in the caller's shell. REGRESSION: <c>run() { "$@"; }; run eval 'echo E'</c> was <c>eval: command not found</c> (127),
/// as were <c>.</c>, <c>set</c> (it reached PowerShell's Set-Variable) and <c>exec</c>: the run-time lookup knew only
/// the run-time commands, not the builtins the emitter implements. One table now decides (BashBuiltins.RunAsSource),
/// and those run as their own static command (PsBuild.BuiltinAsSourceScript).
/// </summary>
public class DynamicBuiltinDifferentialTests
{
    /// <summary>bash 5.2's <c>compgen -b</c>, fixed here so a builtin the table forgets fails this guard.</summary>
    private const string CompgenB =
        ". : [ alias bg bind break builtin caller cd command compgen complete compopt continue declare dirs disown echo "
        + "enable eval exec exit export false fc fg getopts hash help history jobs kill let local logout mapfile popd "
        + "printf pushd pwd read readarray readonly return set shift shopt source suspend test times trap true type "
        + "typeset ulimit umask unalias unset wait";

    /// <summary>
    /// GUARD: every bash builtin is reachable through <c>"$@"</c>, through a variable and through <c>command</c> (never
    /// 127). Each runs in a subshell with no stdin; <c>suspend</c> (it would stop the shell) and <c>fc</c> (it would start
    /// an editor) get a harmless operand. Unrolled, not a loop: a <c>break</c> that escaped its subshell would end a loop
    /// early and hide every builtin after it.
    /// </summary>
    [SkippableFact]
    public Task EveryBuiltin_NamedAtRunTime_IsFound() => AssertOracle.EqualAsync(EveryBuiltinScript(), timeout: TimeSpan.FromSeconds(120));   // ~190 subshells

    private static string EveryBuiltinScript()
    {
        var sb = new System.Text.StringBuilder("cd \"$(mktemp -d)\" || exit\nrun() { \"$@\"; }\n");
        foreach (var b in CompgenB.Split(' '))
        {
            string word = b == "[" ? "'['" : b;
            string a = b switch { "suspend" => " --help", "fc" => " -l", _ => "" };
            sb.Append("( run ").Append(word).Append(a).Append(" ) >/dev/null 2>&1 </dev/null; [ $? -eq 127 ] && echo '")
              .Append(b).Append(": not found through \"$@\"'\n");
            sb.Append("( c=").Append(word).Append("; $c").Append(a).Append(" ) >/dev/null 2>&1 </dev/null; [ $? -eq 127 ] && echo '")
              .Append(b).Append(": not found through $c'\n");
            sb.Append("( command ").Append(word).Append(a).Append(" ) >/dev/null 2>&1 </dev/null; [ $? -eq 127 ] && echo '")
              .Append(b).Append(": not found through command'\n");
            // A line per builtin: the launcher ends a run that prints nothing for its idle timeout.
            sb.Append("echo 'checked ").Append(b).Append("'\n");
        }
        return sb.Append("echo done\n").ToString();
    }

    [SkippableFact]
    public Task Eval_NamedAtRunTime_RunsInTheCallersShell() => AssertOracle.EqualAsync(
        "run() { \"$@\"; }\n"
        + "run eval 'echo E'; echo \"st=$?\"\n"
        + "c=eval; $c 'echo x'; $c 'v=1'; echo \"v=$v\"\n"
        + "$c 'g() { echo G; }'; g\n"
        + "run eval 'h() { echo H; }'; h\n"
        + "$c 'ea=(5 6)'; echo \"ea=${ea[1]}\"\n"
        + "r() { $c 'return 3'; echo notreached; }; r; echo \"r=$?\"\n"
        + "echo piped | run eval 'read p; echo \"p=$p\"'\n"
        + "command eval 'echo ce'; builtin eval 'echo be'; b=builtin; $b eval 'echo bb'\n"
        + "$c 'echo \"$(echo inner)\" \\$HOME_NOT_SET_X'\n");

    /// <summary>
    /// SIBLING: <c>c=break; $c</c> ran a bare PowerShell <c>break</c>, which left whatever loop was on the call stack and
    /// ended the whole script when there was none (<c>run break</c> in a function, a subshell). Now the lookup is told
    /// what loop control does at the call site (Resolve-BashCommand -LoopMode, PsEmitter.NamesLoopControl).
    /// </summary>
    [SkippableFact]
    public Task LoopControl_NamedAtRunTime_LeavesOnlyALoopInReach() => AssertOracle.EqualAsync(
        "run() { \"$@\"; }\n"
        + "c=break; for i in 1 2 3; do $c; echo \"no$i\"; done; echo \"for=$i\"\n"
        + "k=continue; for i in 1 2; do $k; echo \"no$i\"; done; echo \"cont=$i\"\n"
        + "for i in 1 2; do run break; echo \"fn$i\"; done 2>/dev/null; echo \"fnst=$?\"\n"
        + "$c 2>/dev/null; echo \"top=$?\"\n"
        + "( $c; echo inner ) 2>/dev/null; echo \"sub=$?\"\n"
        + "for i in 1 2; do ( $c; echo \"sub$i\" ); done 2>/dev/null; echo \"loopsub=$i\"\n"
        + "while :; do eval '$c'; echo noeval; done; echo evaled\n"
        + "echo end\n");

    [SkippableFact]
    public Task SetShiftLocal_NamedAtRunTime_ActOnTheCallersScope() => AssertOracle.EqualAsync(
        "c=set; $c -- a b; echo \"pos=$# $*\"\n"
        + "f() { local c=set; $c -- p q; echo \"fpos=$# $*\"; c=shift; $c; echo \"fshift=$*\"; }; f x y z; echo \"after=$*\"\n"
        + "run() { \"$@\"; }; g() { run set -- in run; echo \"g=$# $*\"; }; g x\n"
        + "command set -- one two three; echo \"cmd=$#\"\n"
        + "h() { local c=local; $c lv=7; echo \"in=$lv\"; }; h; echo \"out=${lv-unset}\"\n"
        + "c=export; $c ev=9; echo \"ev=$ev\"\n"
        + "c=unset; $c ev; echo \"ev=${ev-unset}\"\n"
        + "local 2>/dev/null; echo \"bare=$?\"; f2() { local >/dev/null; echo \"fbare=$?\"; run local >/dev/null; echo \"rbare=$?\"; }; f2\n");

    /// <summary>
    /// SIBLING: whether a unit traces (<c>set -x</c>) or checks expansions (<c>set -u</c>) was a regex over the source,
    /// which missed an option named at run time; <c>set -o "$o"</c> was refused outright. Now decided on the parsed
    /// commands (PsEmitter.CanSetXtrace / CanSetNounset), and a run-time option word runs <c>set</c> at run time.
    /// </summary>
    [SkippableFact]
    public Task XtraceNounset_TurnedOnThroughARunTimeWord() => AssertOracle.EqualAsync(
        "run() { \"$@\"; }\n"
        + "( c=set; $c -x; echo traced; set +x ) 2>&1\n"
        + "( o=xtrace; set -o \"$o\"; echo traced2; set +x ) 2>&1\n"
        + "( run set -x; echo traced3 ) 2>&1\n"
        + "( c=set; $c -u; echo \"${nosuch_q}\"; echo survived ) 2>/dev/null; echo \"nu=$?\"\n"
        + "( o=nounset; set -o \"$o\"; echo \"$nosuch_r\"; echo survived ) 2>/dev/null; echo \"nu2=$?\"\n"
        + "echo \"set -x\" | cat; echo 'set -u' >/dev/null; echo \"$nosuch_s\" ok\n");

    [SkippableFact]
    public Task SourceExecExit_NamedAtRunTime() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" || exit\n"
        + "echo 'echo dotted \"$1\"; sv=set' > d.sh\n"
        + "c=.; $c ./d.sh arg1; echo \"sv=$sv\"\n"
        + "run() { \"$@\"; }; run . ./d.sh arg2; run source ./d.sh arg3\n"
        + "c=exec; ( $c echo execd; echo notreached ); echo \"exec=$?\"\n"
        + "( run exec echo viaexec ); echo \"rexec=$?\"\n"
        + "c=exit; ( $c 3; echo notreached ); echo \"exit=$?\"\n"
        + "( run exit 4 ); echo \"rexit=$?\"\n"
        + "( command exit 5 ); echo \"cexit=$?\"\n"
        + "c=.; $c 2>/dev/null; echo \"noarg=$?\"\n"
        + "c=exec; $c 3>fd3.txt; echo b >&3; cat fd3.txt\n"           // only redirections: permanent, as `exec 3>f`
        + "run exec 4>fd4.txt; { echo c >&4; } 2>/dev/null; echo \"run4=$?\"\n"   // the redirect belonged to the run call
        + "c=echo; $c >o.txt; wc -c < o.txt\n");
}
