using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// A background job's shell scalars have one storage: its own store, copied at the <c>&amp;</c> (ScalarOverlay
/// in-process, the environment block of a job host; docs/specs/background-jobs.md section 3). A loop variable used to be
/// handed over a second time as a PowerShell copy (<c>-Vars @{ 'i' = $env:i }</c>) that the job's reads preferred, so
/// <c>for i in 1; do { i=9; echo "$i"; } &amp; done</c> printed 1. Only a function's lexical local, a PowerShell variable
/// the job's runspace lacks, is still handed over. Emitted-text surface; the behavior against bash is in
/// Differential.Tests/BackgroundJobLaunchValuesDifferentialTests.
/// </summary>
public class JobScalarCaptureTests
{
    [Theory]
    [InlineData("for i in 1 2; do { i=9; echo \"$i\"; } & wait; done")]
    [InlineData("for i in 1; do { echo \"$i\"; i=7; echo \"$i\"; } & done; wait")]
    [InlineData("for i in 1; do { (( i += 5 )); echo \"$i\"; } & done; wait")]
    [InlineData("while read -r l; do { l=x; echo \"$l\"; } & done <<< a; wait")]
    [InlineData("for ((n=0; n<1; n++)); do { n=5; echo \"$n\"; } & done; wait")]
    [InlineData("for i in 1; do for j in 2; do { echo \"$i$j\"; } & done; done; wait")]
    public void Transpile_JobInALoopAssigningTheLoopVariable_HasNoPowerShellCopy(string bash)
    {
        var ps = PsEmitter.Transpile(bash);
        Assert.NotNull(ps);
        Assert.DoesNotContain("-Vars", ps);
    }

    // A function's scalar `local` is dynamically scoped, so it lives in the store too: no copy.
    [Fact]
    public void Transpile_JobReadingAFunctionsScalarLocal_HasNoPowerShellCopy()
    {
        var ps = PsEmitter.Transpile("f() { local x=1; { x=2; echo \"$x\"; } & wait; }; f");
        Assert.NotNull(ps);
        Assert.DoesNotContain("-Vars", ps);
    }

    // A function's array local is a PowerShell variable of the function's scope, which the job's runspace does not
    // have: it is still handed over.
    [Fact]
    public void Transpile_JobReadingAFunctionsArrayLocal_IsHandedTheLocal()
    {
        var ps = PsEmitter.Transpile("f() { local -a a=(1 2); { echo \"${a[1]}\"; } & wait; }; f");
        Assert.NotNull(ps);
        Assert.Contains("-Vars @{ 'a' = ", ps);
    }
}