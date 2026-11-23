using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Which background jobs run in a host process of their own (<c>Invoke-BashBackground -Isolated</c>,
/// <c>PsEmitter.JobNeedsOwnProcess</c>, docs/specs/background-jobs.md section 12.6). Hand-written: the choice is ps-bash
/// surface (bash forks every job); <c>BackgroundJobLaunchValuesDifferentialTests</c> holds both paths to bash's output.
/// On Windows a job host costs seconds to start against milliseconds for a job runspace, so a job goes there only when it
/// can change what an in-process job shares with the shell. Writing a scalar or an array is not such a change: the job's
/// scalars are its own copy (ScalarOverlay) and its arrays live in its own runspace. Loops and <c>${x=..}</c> went to a
/// job host before that copy existed, and <c>for i in 1 2 3; do echo $i; done &amp;</c> took 5 s to start (beagle-ab).
/// </summary>
public class JobProcessChoiceEmitterTests
{
    private static bool Isolated(string bash) => PsEmitter.Transpile(bash)!.Contains("Invoke-BashBackground -Isolated");

    [Theory]
    [InlineData("sleep 1 &")]
    [InlineData("make all &")]
    [InlineData("{ cat f | wc -l; } &")]
    [InlineData("{ echo x; } &")]
    // Scalar and array writes: the job's own copy.
    [InlineData("x=1 &")]
    [InlineData("{ x=1; sleep 1; } &")]
    [InlineData("a=(1 2) &")]
    [InlineData("{ a+=(3); echo ${#a[@]}; } &")]
    [InlineData("export x=1 &")]
    [InlineData("FOO=1 prog &")]
    [InlineData("echo ${bar=2} &")]
    [InlineData("echo \"$((n += 2))\" &")]
    [InlineData("(( n++ )) &")]
    [InlineData("let n=n+1 &")]
    // Loops set their variable in the job's store.
    [InlineData("for i in 1 2 3; do echo $i; sleep 0.0$i; done &")]
    [InlineData("for ((i = 0; i < 3; i++)); do echo $i; done &")]
    [InlineData("while [[ $n -lt 3 ]]; do n=$((n + 1)); done &")]
    [InlineData("for f in $(ls); do echo \"$f\"; done &")]
    [InlineData("if [[ -n $x ]]; then y=1; fi &")]
    [InlineData("case $x in a) y=1 ;; esac &")]
    [InlineData("(x=1; echo $x) &")]
    [InlineData("x=$(date) &")]
    [InlineData("if (( i<(n-1) )); then echo a; fi &")]
    // `command`/`builtin` are judged by what they run. REGRESSION: `echo in | { command cat & wait; }` printed nothing:
    // the job went to a host process, which does not read the compound's stdin.
    [InlineData("command cat &")]
    [InlineData("command -p /usr/bin/cat -n &")]
    [InlineData("command -- command cat &")]
    [InlineData("builtin echo x &")]
    [InlineData("command &")]
    public void Transpile_JobThatOnlyRunsProgramsOrWritesVariables_RunsInProcess(string bash)
        => Assert.False(Isolated(bash), bash);

    [Theory]
    // The working directory is the process's.
    [InlineData("cd other &")]
    [InlineData("(cd other && make) &")]
    [InlineData("for d in a b; do cd $d; done &")]
    [InlineData("pushd /tmp &")]
    // A cd in a command or process substitution moves the job's process, which is the shell's.
    [InlineData("echo $(cd /; pwd) &")]
    [InlineData("for f in $(cd x && ls); do echo $f; done &")]
    [InlineData("x=$(cd /; pwd) &")]
    [InlineData("FOO=$(cd /) prog &")]
    [InlineData("cat <(cd /; ls) &")]
    [InlineData("echo ${x:-$(cd /)} &")]
    [InlineData("(( n = $(cd /; echo 1) )) &")]
    // The readonly, attribute and nameref tables are process-wide.
    [InlineData("readonly r=1 &")]
    [InlineData("declare -i n=1 &")]
    [InlineData("declare -A m=([k]=v) &")]
    [InlineData("{ typeset -n ref=x; } &")]
    // An in-process job looks a program up in the process PATH.
    [InlineData("PATH=/opt/bin:$PATH &")]
    [InlineData("PATH=/opt/bin prog &")]
    [InlineData("for PATH in /a /b; do prog; done &")]
    // Options, traps, descriptors, stdin readers, text run as code, unknown commands.
    [InlineData("set -e &")]
    [InlineData("shopt -s nullglob &")]
    [InlineData("trap 'echo t' EXIT &")]
    [InlineData("exec 3>f &")]
    [InlineData("while read -r l; do echo \"$l\"; done < f &")]
    [InlineData("eval 'x=1' &")]
    [InlineData("source ./lib.sh &")]
    [InlineData("$cmd arg &")]
    [InlineData("command cd x &")]
    [InlineData("builtin cd x &")]
    [InlineData("command -p cd x &")]
    [InlineData("command $cmd &")]
    [InlineData("command eval 'x=1' &")]
    // The job runspace does not have the shell's functions.
    [InlineData("f() { :; }; f &")]
    [InlineData("f() { :; }; for i in 1 2; do f $i; done &")]
    [InlineData("{ g() { :; }; g; } &")]
    [InlineData("select x in a b; do break; done &")]
    public void Transpile_JobThatCanChangeWhatTheShellShares_RunsInItsOwnProcess(string bash)
        => Assert.True(Isolated(bash), bash);
}
