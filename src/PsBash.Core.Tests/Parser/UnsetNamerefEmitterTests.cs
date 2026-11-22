using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`unset ref` on a nameref unsets its target; `unset -n ref` drops the reference. Oracle (bash 5.2):
/// x=X; typeset -n ref=x; unset ref → ref= x= ; typeset -n r=y; unset -n r → y keeps its value.</summary>
public class UnsetNamerefEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_UnsetThroughNameref_UnsetsTheTarget()
    {
        var ps = T("x=X; typeset -n ref=x; unset ref");
        Assert.Matches(@"Invoke-BashUnset\s+['""]?x['""]?", ps);
        Assert.DoesNotMatch(@"Invoke-BashUnset\s+['""]?ref", ps);
    }

    [Fact] public void Transpile_UnsetDashN_KeepsTheTargetAndDropsTheReference()
    {
        var ps = T("y=Y; typeset -n r=y; unset -n r; echo $r");
        Assert.DoesNotContain("$y", ps.Substring(ps.IndexOf("echo", StringComparison.Ordinal) < 0 ? 0 : ps.LastIndexOf("Invoke-BashEcho", StringComparison.Ordinal)));
    }

    [Fact] public void Transpile_UnsetPlainVariable_Unchanged()
        => Assert.Matches(@"Invoke-BashUnset\s+['""]?z", T("typeset -n ref=x; unset z"));

    // The options as Invoke-BashUnset reads them (bash 5.2): -n holds for every name after it whatever -v says; -f overrides
    // it. REGRESSION: only `unset -n` as the first word dropped the static reference, so after `unset -v -n r` `r=2` wrote y.
    [Fact] public void Transpile_UnsetDashVDashN_DropsTheReference()
    {
        var ps = T("y=Y; typeset -n r=y; unset -v -n r; r=2");
        Assert.Contains("$env:r = \"2\"", ps);
        Assert.DoesNotContain("$env:y = \"2\"", ps);
    }

    [Fact] public void Transpile_UnsetDashNDashF_KeepsTheReference()
        => Assert.Contains("$env:y = \"2\"", T("y=Y; typeset -n p=y; unset -n -f p; p=2"));

    // The options through UnsetOptions, as Invoke-BashUnset reads them. REGRESSION (abe9d17b): only the exact words
    // -f/-v/-n counted, so `unset -vn r` rewrote to `unset y` (y unset, r still a nameref). Oracle in UnsetOptionsTests.
    [Theory]
    [InlineData("-vn")]
    [InlineData("-nv")]
    [InlineData("-nn")]
    [InlineData("-n -n")]
    [InlineData("-n --")]
    public void Transpile_UnsetClusterWithN_DropsTheReference(string options)
    {
        var ps = T($"y=Y; typeset -n r=y; unset {options} r; r=2");
        Assert.Contains("$env:r = \"2\"", ps);
        Assert.DoesNotMatch(@"Invoke-BashUnset[^;]*\by\b", ps);
    }

    // -f overrides -n (the function r goes, the reference stays); -f with -v unsets nothing.
    [Theory]
    [InlineData("-fn")]
    [InlineData("-nf")]
    [InlineData("-fv")]
    [InlineData("-vf")]
    [InlineData("-fvn")]
    public void Transpile_UnsetClusterWithF_KeepsTheReference(string options)
        => Assert.Contains("$env:y = \"2\"", T($"y=Y; typeset -n r=y; unset {options} r; r=2"));

    // `--` ends the options: the name after it is unset through the reference (its target). unset is on
    // OrderedArgCommands, so `--` reaches the cmdlet quoted: bare, the PowerShell binder ate it (`unset -- -f g` unset
    // the FUNCTION g) and a bare `-v` bound the V decoy out of place.
    [Fact] public void Transpile_UnsetDoubleDash_UnsetsTheTarget()
        => Assert.Matches(@"Invoke-BashUnset\s+'--'\s+['""]?y['""]?", T("y=Y; typeset -n r=y; unset -- r"));

    [Fact] public void Transpile_UnsetOptionWords_ReachTheCmdletQuotedAndInPlace()
        => Assert.Contains("Invoke-BashUnset '-v' x '-f' '--' g", T("unset -v x -f -- g"));

    // What a subshell declares ends with it (EnterSubshellTables), in the emitter's compile-time tables as in the run-time
    // ones. REGRESSION: `( declare -n r=y ); r=3` wrote y, and `declare -p r` after it was folded to the subshell's reference.
    [Theory]
    [InlineData("( declare -n r=y ); r=3", "$env:r = \"3\"")]
    [InlineData("( declare -i x ); x=1+1", "$env:x = \"1+1\"")]
    [InlineData("( declare -u z ); z=abc", "$env:z = \"abc\"")]
    [InlineData("( declare -A m ); m=(k v)", "$m = @(\"k\",\"v\")")]
    [InlineData("( typeset -n r ); r=3", "$env:r = \"3\"")]
    [InlineData("v=$(declare -n r=y); r=3", "$env:r = \"3\"")]
    [InlineData("{ declare -n r=y; } | cat; r=3", "$env:r = \"3\"")]
    public void Transpile_AttributeDeclaredInASubshell_EndsWithIt(string bash, string afterwards)
        => Assert.Contains(afterwards, T(bash));

    [Fact] public void Transpile_AttributeDeclaredOutsideASubshell_HoldsAfterIt()
        => Assert.Contains("Invoke-BashArith", T("declare -i x; ( : ); x=1+1"));
}
