using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle: a background job sees the shell's variables as they were at its <c>&amp;</c>. Bash forks there,
/// so a loop variable or a later assignment never reaches a job already launched, and nothing the job assigns reaches
/// the shell. An in-process job shares the shell's process, so its scalars are its own copy taken at the launch
/// (ScalarOverlay, docs/specs/background-jobs.md section 3), and so is the environment of a program it starts.
/// The job sleeps first in the cases where the shell writes after the launch, so the shell's write lands before the
/// job reads: the order is fixed, not raced. Output that bash produces in any order goes through <c>sort</c>.
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

    [SkippableFact]
    public Task AssignmentAfterTheLaunch_DoesNotReachAFunctionJob() =>
        Eq("x=a; f() { sleep 0.3; echo \"x=$x\"; }; f & x=b; wait; echo \"shell x=$x\"");

    [SkippableFact]
    public Task AssignmentAfterTheLaunch_DoesNotReachABraceJob() =>
        Eq("x=a; { sleep 0.3; echo \"job x=$x\"; } & x=b; wait; echo \"shell x=$x\"");

    // A program the job starts gets the job's exported variables, not the shell's process environment: by name
    // (PowerShell's own launcher would hand it the process environment) and through a nested shell.
    [SkippableFact]
    public Task AProgramTheJobStarts_GetsTheJobsExportedValue() =>
        Eq("x=a; export x; { sleep .3; printenv x; sh -c 'echo $x'; } & export x=b; wait");

    // Not exported at the launch: the shell's `export` after it does not reach the job's children (printenv: status 1).
    [SkippableFact]
    public Task AnExportAfterTheLaunch_DoesNotReachAJobsChild() =>
        Eq("x=a; { sleep .3; printenv x; echo \"st=$?\"; sh -c 'echo \"[$x]\"'; } & export x=b; wait");

    [SkippableFact]
    public Task WhatTheJobAssigns_StaysInTheJob() =>
        Eq("y=1; { y=2; export z=9; echo \"in job y=$y\"; } & wait; echo \"after y=$y z=${z-unset}\"");

    [SkippableFact]
    public Task AVariableNobodyExported_StaysOutOfAJobsChild() =>
        Eq("u=secret; { sh -c 'echo u=${u-unset}'; } & wait");

    [SkippableFact]
    public Task ASubshellInAJob_KeepsItsAssignments() =>
        Eq("{ q=1; ( q=2 ); echo \"q=$q\"; } & wait");

    [SkippableFact]
    public Task AJobInAJob_SeesTheOuterJobsValueAtItsLaunch() =>
        Eq("n=1; { { sleep 0.2; echo \"inner n=$n\"; } & n=2; wait; echo \"outer n=$n\"; } & wait");
}
