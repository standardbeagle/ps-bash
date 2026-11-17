using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle: a background job sees the shell's variables as they were at its <c>&amp;</c>. Bash forks there,
/// so a loop variable or a later assignment never reaches a job already launched. An in-process job shares the
/// shell's process (docs/specs/background-jobs.md section 12.5), so its launch-time values are handed over
/// (<c>Invoke-BashBackground -Vars</c>) rather than read when its body runs. The job sleeps first in the cases where
/// the shell writes after the launch, so the shell's write lands before the job reads: the order is fixed, not raced.
/// Output that bash produces in any order goes through <c>sort</c>.
/// </summary>
public class BackgroundJobLaunchValuesDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableTheory]
    [InlineData("{ for i in 1 2 3; do echo $i & done; wait; } | sort")]
    [InlineData("{ for i in 1 2 3; do { sleep 0.2; echo \"job $i\"; } & done; wait; } | sort")]
    [InlineData("{ for i in a b; do for j in 1 2; do { sleep 0.1; echo \"$i$j\"; } & done; done; wait; } | sort")]
    [InlineData("{ for w in \"a b\" 'c$d' \"e'f\"; do { sleep 0.1; echo \"[$w]\"; } & done; wait; } | sort")]
    public Task LoopVariable_EachJobSeesItsOwnIteration(string script) => Eq(script);

    // A job that runs a function gets a host of its own and the environment as of its launch. Not yet covered: a job
    // that runs in-process reads a variable the shell assigns after the launch (`x=a; { sleep 0.3; echo $x; } & x=b`
    // prints x=b), because only loop variables are handed over.
    [SkippableFact]
    public Task AssignmentAfterTheLaunch_DoesNotReachAFunctionJob() =>
        Eq("x=a; f() { sleep 0.3; echo \"x=$x\"; }; f & x=b; wait; echo \"shell x=$x\"");
}
