using System.Text;
using PsBash.Core.Transpiler;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Guard: a builtin that fails with a message keeps its status through the statement-status rules (every command sets
/// the status; the one errexit / ERR check after the statement; the run-time text call). The cmdlet publishes its status
/// through FileSystemHelpers.SetLastExitCode, and nothing the emitter puts after it may overwrite it.
/// <para>Root cause guarded: PsBuild.RunBashTextCall, the one run-time call of bash text that <c>eval</c> and <c>fc</c>
/// share, set status 0 for blank text (eval's rule, <c>false; eval ''</c>). fc leaves no text when it fails, so
/// <c>history -c; fc -s</c> and <c>fc -z</c> / <c>fc --bogus-xyz</c> reported 0 (bash: 1 and 2; also
/// UnknownOptionGuardTests' "fc: status 0"), and <c>set -e</c> went on. A call site now says
/// whether its own command published the status (<c>callerStatus</c>).</para>
/// Every expected value is bash 5.2.21's for this script (qa-rubric Directive 1); end-to-end parity of the same rows is
/// <c>Differential.Tests/BuiltinFailureStatusDifferentialTests</c>.
/// </summary>
[Collection("SdkHost")]
public class BuiltinFailureStatusTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>(bash statement, the line it prints): each statement ends with <c>t NAME $?</c>, which prints NAME=status.</summary>
    internal static readonly (string Bash, string Expected)[] Rows =
    [
        // fc: no text chosen, so the run-time call must keep Invoke-BashFc's status.
        ("history -c; fc -s; t fc_s $?", "fc_s=1"),
        ("fc -z; t fc_z $?", "fc_z=2"),
        ("fc --bogus-xyz; t fc_bogus $?", "fc_bogus=2"),
        ("( fc --bogus-xyz ) </dev/null >/dev/null 2>/dev/null; t fc_bogus_sub $?", "fc_bogus_sub=2"),
        ("g() { history -c; fc -s; }; g; t fn_fcs $?", "fn_fcs=1"),
        ("history -c; fc -s >/dev/null; t redir_fcs $?", "redir_fcs=1"),
        ("history -c; eval 'fc -s'; t eval_fcs $?", "eval_fcs=1"),
        ("history -c; c=fc; $c -s; t dyn_fcs $?", "dyn_fcs=1"),
        ("( set -e; history -c; fc -s; echo after ); t e_fcs $?", "e_fcs=1"),
        ("( set -e; fc -z; echo after ); t e_fcz $?", "e_fcz=2"),
        ("history -c; fc -s || echo caught", "caught"),
        ("f() { history -c; fc -s; echo \"in $?\"; return 4; }; f; t fn_ret $?", "in 1\nfn_ret=4"),
        ("history -s 'false'; fc -s; t rerun_false $?", "rerun_false=1"),
        ("history -s 'true'; false; fc -s; t rerun_true $?", "rerun_true=0"),
        // eval: blank text is 0, whatever came before it.
        ("false; eval ''; t eval_blank $?", "eval_blank=0"),
        ("false; eval; t eval_none $?", "eval_none=0"),
        ("eval 'false'; t eval_false $?", "eval_false=1"),
        ("eval \"$(exit 3)\"; t eval_subst $?", "eval_subst=0"),
        // Siblings that fail with a message only.
        ("history -d 99; t hist_d $?", "hist_d=1"),
        ("history -z; t hist_z $?", "hist_z=2"),
        ("c=history; $c -d 99; t dyn_hist $?", "dyn_hist=1"),
        ("cd /nonexistent_zz; t cd $?", "cd=1"),
        ("c=cd; $c /nonexistent_zz; t dyn_cd $?", "dyn_cd=1"),
        ("popd; t popd $?", "popd=1"),
        ("shift 5; t shift $?", "shift=1"),
        ("type nosuch_zz; t type $?", "type=1"),
        ("hash nosuch_zz; t hash $?", "hash=1"),
        ("alias nosuch_zz; t alias $?", "alias=1"),
        ("unalias nosuch_zz; t unalias $?", "unalias=1"),
        ("kill %9; t kill $?", "kill=1"),
        ("disown %3; t disown $?", "disown=1"),
        ("getopts; t getopts $?", "getopts=2"),
        ("trap 'x' NOSIG_ZZ; t trap $?", "trap=1"),
        ("umask 999; t umask $?", "umask=1"),
        ("enable nosuch_zz; t enable $?", "enable=1"),
        ("export -f nosuch_zz; t exportf $?", "exportf=1"),
        ("declare -p nosuch_zz; t declarep $?", "declarep=1"),
        ("local x; t local $?", "local=1"),
        ("set -o nosuch_zz; t seto $?", "seto=2"),
        ("shopt nosuch_zz; t shopt $?", "shopt=1"),
        ("source /nonexistent_zz; t source $?", "source=1"),
        (". /nonexistent_zz; t dot $?", "dot=1"),
        ("let 0; t let $?", "let=1"),
        ("read -u 9 v; t readu $?", "readu=1"),
        ("[ 1 -gt ]; t bracket $?", "bracket=2"),
    ];

    internal static string Script()
    {
        var sb = new StringBuilder("cd \"$(mktemp -d)\" || exit 1\nt() { echo \"$1=$2\"; }\n");
        foreach (var (bash, _) in Rows) sb.Append(bash).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public async Task FailingBuiltin_KeepsItsStatusThroughTheStatementRules()
    {
        string expected = string.Join("\n", Rows.Select(r => r.Expected));
        var stdout = new StringBuilder();
        var gate = new object();
        SdkWorker worker = _fixture.CreateWorker();
        await worker.ExecuteWithOutputAsync(BashTranspiler.Transpile(Script())!,
            s => { lock (gate) stdout.Append(s); }, _ => { });
        Assert.Equal(expected, stdout.ToString().Replace("\r", "").TrimEnd('\n'));
    }
}
