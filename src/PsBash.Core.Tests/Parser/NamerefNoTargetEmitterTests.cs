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

    private const string AssignThrough = "[PsBash.Cmdlets.BashVariableLookup]::AssignThroughNameref($ExecutionContext.SessionState, 'r', ";
    private const string ReadThrough = "[PsBash.Cmdlets.BashVariableLookup]::ReadThroughNameref($ExecutionContext.SessionState, 'r')";

    // Any first write names the target, not only a literal one, so a value the transpiler cannot see goes to the run time,
    // and so does every write and read after it. REGRESSION (8bd50899): `r=$(echo tgt); r=5` stored both values in r.
    [Theory]
    [InlineData("declare -n r; r=$(echo tgt); r=5; echo $r")]
    [InlineData("declare -n r; v=t; r=$v; r=5; echo \"$r\"")]
    [InlineData("declare -n r; r+=$1; r=5; echo ${r}")]
    public void Transpile_NamerefWithNoTarget_ANonLiteralFirstWrite_GoesThroughTheRunTime(string bash)
    {
        var ps = T(bash);
        Assert.Equal(2, ps.Split(AssignThrough).Length - 1);
        Assert.Contains(ReadThrough, ps);
        Assert.DoesNotContain("$env:r = ", ps);
    }

    [Fact]
    public void Transpile_LocalNamerefWithNoTarget_ParameterFirstWrite_GoesThroughTheRunTime()
    {
        var ps = T("g() { local -n r; r=$1; r=5; }; g tgt");
        Assert.Equal(2, ps.Split(AssignThrough).Length - 1);
        Assert.DoesNotContain("$env:r = \"5\"", ps);
    }

    // `for r in a b` with r a nameref re-targets r on each pass (bash); the body reads and writes through the run time.
    [Theory]
    [InlineData("declare -n r; for r in a b; do r=1; echo $r; done")]
    [InlineData("declare -n r=x; for r in a b; do r=1; echo $r; done")]
    public void Transpile_ForOverANameref_RetargetsItEachPass(string bash)
    {
        var ps = T(bash);
        Assert.Contains("[PsBash.Cmdlets.BashVariableLookup]::BindLoopNameref($ExecutionContext.SessionState, 'r', [string]($__psbash_lv0))", ps);
        Assert.DoesNotContain("BashVariableStore]::Set('r'", ps);
        Assert.Contains(AssignThrough, ps);
        Assert.Contains(ReadThrough, ps);
        Assert.DoesNotContain("$env:x = ", ps);
    }

    // The literal first write still names the target at transpile time (later writes go straight to it).
    [Fact]
    public void Transpile_NamerefWithNoTarget_ALiteralFirstWrite_StaysStatic()
    {
        var ps = T("declare -n r; r=x; r=5");
        Assert.Contains("$env:x = \"5\"", ps);
        Assert.DoesNotContain(AssignThrough, ps);
    }

    // `declare -n r` again keeps the target r took at run time (bash), so it must not reset it to none.
    [Fact]
    public void Transpile_RedeclaringARuntimeNameref_KeepsItsTarget()
    {
        var ps = T("declare -n r; r=$1; declare -n r; r=5");
        Assert.Equal(1, ps.Split(NoTargetMark).Length - 1);
        Assert.Equal(2, ps.Split(AssignThrough).Length - 1);
    }
}
