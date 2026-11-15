using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>`ulimit -f -- -42`: `--` ends options, so -42 is the value, and a negative limit is "invalid number" (rc 1)
/// as in bash 5.2 (oils builtin-process "ulimit negative arg"). Soft/hard limits follow bash on Linux for an
/// unprivileged user (oils builtin-process "ulimit -S ... -H", "Changing resource limit is denied").</summary>
public class UlimitArgumentTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public UlimitArgumentTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private const string ResetLimits = "Remove-Variable -Scope Global __psbashUlimit -ErrorAction SilentlyContinue; ";

    private (string Out, int Code) Run(string args)
    {
        var pwsh = _fixture.AcquireFresh();
        var r = pwsh.AddScript(ResetLimits + $"$o = Invoke-BashUlimit {args} 2>$null | Out-String; \"$o|$global:LASTEXITCODE\"").Invoke();
        pwsh.Commands.Clear();
        var s = ((string)r[0].BaseObject).Split('|');
        return (s[0].Trim(), int.Parse(s[1]));
    }

    /// <summary>Runs each `ulimit` argument list in order in one shell; returns "out:status" per call.</summary>
    private string Sequence(params string[] calls)
    {
        var pwsh = _fixture.AcquireFresh();
        var script = ResetLimits + "$r = @(); "
            + string.Concat(calls.Select(c => $"$o = (Invoke-BashUlimit {c} 2>$null | Out-String).Trim(); $r += \"${{o}}:$global:LASTEXITCODE\"; "))
            + "$r -join ' '";
        var r = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return (string)r[0].BaseObject;
    }

    [Fact] public void Negative_value_after_double_dash_is_invalid_number() => Assert.Equal(("", 1), Run("'-f' '--' '-42'"));
    [Fact] public void Query_still_works() => Assert.Equal(("unlimited", 0), Run("-f"));
    [Fact] public void Setting_a_valid_number_still_succeeds() => Assert.Equal(("", 0), Run("-c 0"));

    // bash: 2^62+1 blocks overflow the byte count -> "limit out of range", status 1, the limit is unchanged.
    [Fact]
    public void Value_overflowing_64_bits_is_out_of_range() =>
        Assert.Equal(":1 unlimited:0", Sequence("-f 4611686018427387905", "-f"));

    [Fact]
    public void Soft_and_hard_are_separate_and_no_flag_sets_both() =>
        Assert.Equal(":0 123456:0 unlimited:0 :0 123456:0 123457:0 :0 123455:0 123455:0",
            Sequence("-S -t 123456", "-S -t", "-H -t", "-H -t 123457", "-t", "-H -t", "-t 123455", "-S -t", "-H -t"));

    // Soft above hard is EINVAL; raising the hard limit is EPERM; either fails with status 1 and changes nothing.
    [Fact]
    public void Soft_above_hard_and_raising_hard_are_denied() =>
        Assert.Equal(":0 :0 :1 :1 :1 :1 90:0 100:0",
            Sequence("-S -H -t 100", "-S -t 90", "-S -t 105", "-S -t unlimited", "-H -t 200", "-H -t 80", "-t", "-H -t"));
}
