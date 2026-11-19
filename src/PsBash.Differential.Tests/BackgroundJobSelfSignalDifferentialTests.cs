using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A background job that signals itself (<c>{ kill -USR1 $BASHPID; } &amp; wait $!</c>) ends with 128+N and only the job
/// ends; <c>wait</c> reports the signal on stderr (silent for TERM and PIPE), and a non-interactive shell's job ignores
/// INT and QUIT (status 0). REGRESSION: the in-process job's <c>$BASHPID</c> was the shell's pid, so the signal killed the
/// whole script (exit 138, nothing printed), and a job that ended itself showed EndInvoke's "pipeline has been stopped"
/// text with status 1. Signal numbers follow OsSignals (Linux numbering on this oracle). The report's line prefix and
/// job text differ from bash's, so stderr is compared through the signal description only.
/// </summary>
public class BackgroundJobSelfSignalDifferentialTests
{
    [SkippableTheory]
    [InlineData("USR1")]
    [InlineData("USR2")]
    [InlineData("HUP")]
    [InlineData("TERM")]
    [InlineData("INT")]
    [InlineData("QUIT")]
    [InlineData("PIPE")]
    [InlineData("KILL")]
    public Task Job_SignallingItself_EndsWith128PlusN(string signal) => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\" || exit; { kill -" + signal + " $BASHPID; echo after; } & wait $! 2>err; echo \"" + signal
        + " $?\"; grep -o -e 'User defined signal [12]' -e Hangup -e Killed -e Terminated -e Interrupt -e Quit -e 'Broken pipe' err; "
        + "grep -c -i -e exception -e EndInvoke err; echo still-running");

    [SkippableTheory]
    // Output before the signal is kept: live (the shell's stdout), redirected, inside a function, in $( ).
    [InlineData("{ echo a; kill -USR1 $BASHPID; echo b; } & wait 2>/dev/null; echo \"all $?\"")]
    [InlineData("cd \"$(mktemp -d)\" || exit; { echo c; kill -TERM $BASHPID; echo d; } > out & wait $!; echo \"redir $?\"; cat out")]
    [InlineData("fn() { { echo f1; kill -USR1 $BASHPID; echo f2; } & wait $! 2>/dev/null; echo \"fn $?\"; }; fn")]
    [InlineData("x=$( { echo s1; kill -TERM $BASHPID; echo s2; } & wait $!; echo \"cs $?\" ); echo \"$x\"")]
    // A job that ended during `sleep` is reported by bash on the shell's stderr, not wait's: stderr discarded.
    [InlineData("exec 2>/dev/null; { kill -HUP $BASHPID; } & sleep 1; wait $!; echo \"late $?\"; { echo e1; exit 3; } & wait $!; echo \"exit $?\"")]
    // The job is its own process: its $BASHPID is not the shell's.
    [InlineData("cd \"$(mktemp -d)\" || exit; echo $BASHPID > a; { echo $BASHPID > b; } & wait; [ \"$(cat a)\" != \"$(cat b)\" ] && echo distinct")]
    public Task Job_SignallingItself_KeepsItsOutputAndTheShell(string script) => AssertOracle.EqualAsync(script);

    // The same rule for a foreground subshell, and a subshell inside a job: `( kill -X $BASHPID )` ends only that
    // subshell, 128+N (REGRESSION: the whole script ended with 143). `kill $$` from a subshell still ends the shell
    // (not a case here: the oracle's exit code for a shell killed by a signal is the raw 15, ps-bash's 143). bash's report of a foreground child killed by a signal ("Terminated", on the shell's own stderr) is not made, so
    // the shell's stderr is discarded here.
    [SkippableTheory]
    [InlineData("exec 2>/dev/null; ( kill -TERM $BASHPID; echo no ); echo \"sub $?\"; echo after")]
    [InlineData("exec 2>/dev/null; f() { kill -USR1 $BASHPID; echo no; }; ( f ); echo \"fsub $?\"")]
    [InlineData("exec 2>/dev/null; ( ( kill -HUP $BASHPID ); echo \"inner $?\" ); echo \"outer $?\"")]
    [InlineData("exec 2>/dev/null; { ( kill -USR1 $BASHPID ); echo \"jobsub $?\"; } & wait $!; echo \"jobsubwait $?\"")]
    public Task Subshell_SignallingItself_EndsOnlyThatSubshell(string script) => AssertOracle.EqualAsync(script);
}
