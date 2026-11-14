using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A variable whose value is an expression is evaluated recursively, as deep as bash's 1024 levels (bash 5.2, `wsl bash`):
/// <c>loop='i&lt;=100&amp;&amp;(s+=i,i++,loop)' s=0 i=0; echo $((a=loop,s))</c> is 5050, and a cycle
/// (<c>a=b b=a; $((a))</c>) fails with "expression recursion level exceeded", status 1. A seen-names guard read the second
/// mention of a name as 0: the recursion stopped after one step, and <c>b+b</c> counted b once.
/// </summary>
public class ArithVariableRecursionTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public ArithVariableRecursionTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(),
        "try { " + script + " } finally { Remove-Item env:zzloop, env:zzs, env:zzi, env:zza, env:zzb, env:zzc -ErrorAction Ignore }");

    [Fact]
    public void Arith_SelfReferencingLoopVariable_RecursesToTheEnd()
        => Assert.Equal("5050", Run("$env:zzloop = 'zzi<=100&&(zzs+=zzi,zzi++,zzloop)'; $env:zzs = '0'; $env:zzi = '0'; "
            + "Invoke-BashArith 'zzloop,zzs'").AssertSuccess().Stdout);

    [Fact]
    public void Arith_NameUsedTwiceInAValue_ReadsBothTimes()
        => Assert.Equal("6", Run("$env:zza = 'zzb+zzb'; $env:zzb = 'zzc'; $env:zzc = '3'; Invoke-BashArith 'zza'").AssertSuccess().Stdout);

    // bash: x='a b' -> "a b: syntax error in expression", x='a[<(echo 42)]=1' -> "operand expected"; both status 1, no value.
    [Theory]
    [InlineData("zza zzb")]
    [InlineData("zza[<(echo 42)]=1")]
    public void Arith_VariableValueThatIsNotAnExpression_FailsTheExpansion(string value)
    {
        var r = Run("$env:zza = '" + value + "'; Invoke-BashArith 'zza'").AssertFailed(1);
        Assert.Equal("", r.Stdout);
    }

    // bash: a='' and a=' ' both read as 0 (oils arith "Dynamic parsing on empty string").
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Arith_BlankVariableValue_IsZero(string value)
        => Assert.Equal("1", Run("$env:zza = '" + value + "'; Invoke-BashArith 'zza+1'").AssertSuccess().Stdout);

    [Fact]
    public void Arith_CyclicVariables_FailWithRecursionLevelExceeded()
        => Run("$env:zza = 'zzb'; $env:zzb = 'zza'; Invoke-BashArith 'zza'").AssertFailed(1, "expression recursion level exceeded");
}
