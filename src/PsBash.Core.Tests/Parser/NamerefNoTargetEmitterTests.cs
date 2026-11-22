using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// A nameref declared with no target is recorded at run time with an empty target (BashShellState.SetNameref), so
/// <c>declare -p r</c> prints <c>declare -n r</c>; the first literal assignment names the target. Oracle (bash 5.2) in
/// <c>Differential.Tests/NamerefNoTargetTests</c>; these pin the emitted half.
/// </summary>
public class NamerefNoTargetEmitterTests
{
    private const string NoTargetMark = "[PsBash.Cmdlets.BashShellState]::SetNameref('r', '')";

    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    // REGRESSION: `typeset -n r` emitted only the status, so the run-time table never held r (`declare -p r`: not found).
    [Theory]
    [InlineData("typeset -n r; declare -p r")]
    [InlineData("declare -n r; declare -p r")]
    public void Transpile_NamerefWithNoTarget_IsRecordedAtRunTime(string bash)
        => Assert.Contains(NoTargetMark, T(bash));

    [Fact]
    public void Transpile_NamerefWithNoTarget_FirstAssignmentNamesTheTarget()
    {
        var ps = T("declare -n r; r=x; r=v");
        Assert.Contains("SetNameref('r', 'x')", ps);
        Assert.Contains("$env:x = \"v\"", ps);
    }

    // REGRESSION: `local -n r` with no target was a plain local, so `r=x` set r and `declare -p r` printed `declare -- r`.
    [Fact]
    public void Transpile_LocalNamerefWithNoTarget_FirstAssignmentNamesTheTarget()
    {
        var ps = T("f() { local -n r; r=x; r=v; }");
        Assert.Contains(NoTargetMark, ps);
        Assert.Contains("SetNameref('r', 'x')", ps);
        Assert.Contains("$env:x = \"v\"", ps);
    }

    [Fact]
    public void Transpile_LocalNamerefWithNoTarget_EndsWithTheFunction()
    {
        var ps = T("f() { local -n r; }; f; r=v");
        Assert.EndsWith("$env:r = \"v\"; $global:LASTEXITCODE = 0", ps);
        Assert.DoesNotContain("SetNameref('r', 'v')", ps);
    }

    // The local hides the outer `r -> y`: `r=v` names the local's target instead of writing y.
    [Fact]
    public void Transpile_LocalNamerefWithNoTarget_HidesAnOuterReference()
    {
        var ps = T("declare -n r=y; f() { local -n r; r=v; }");
        Assert.Contains("SetNameref('r', 'v')", ps);
        Assert.DoesNotContain("$env:y = \"v\"", ps);
    }

    // bash: a nameref with no target names no variable, so `unset r` removes the reference and `r=v` is plain afterwards.
    [Fact]
    public void Transpile_UnsetOfANamerefWithNoTarget_MakesTheNamePlain()
    {
        var ps = T("declare -n r; unset r; r=v");
        Assert.Contains("$env:r = \"v\"", ps);
        Assert.DoesNotContain("SetNameref('r', 'v')", ps);
    }
}
