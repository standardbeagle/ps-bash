using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for the parent's state while an in-process background job runs (the default mode for a job
/// that changes no shell state). A subshell body restores the state it saved at its start; in a background
/// runspace that state is the parent's (bash variables are process environment), so the job's end undid what the
/// parent wrote meanwhile: <c>(sleep 0.3) &amp; sleep 0.1; x=1; wait</c> lost <c>x</c>, and <c>wait -n; a=$?</c>
/// was reset to the previous iteration's value when the slower job ended.
/// The wait -n case gives the slow job 1.5 s: a cold Linux host took over 0.5 s to start the second job.
/// The parent sleeps before writing so the job has taken its snapshot first: the order is fixed, not raced.
/// </summary>
public class BackgroundJobStateDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableTheory]
    [InlineData("(sleep 0.4) & sleep 0.1; x=1; wait; echo \"x=$x\"")]
    [InlineData("y=old; (sleep 0.4) & sleep 0.1; y=new; wait; echo \"y=$y\"")]
    [InlineData("z=gone; (sleep 0.4) & sleep 0.1; unset z; wait; echo \"z=${z-unset}\"")]
    [InlineData("for k in 1 2; do (sleep 1.5; exit 3) & (sleep 0.1; exit 5) & wait -n; a=$?; wait -n; b=$?; echo \"$k $a $b\"; done")]
    public Task ParentWritesDuringAJob_Survive(string script) => Eq(script);
}
