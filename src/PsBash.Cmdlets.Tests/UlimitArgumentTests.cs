using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>`ulimit -f -- -42`: `--` ends options, so -42 is the value, and a negative limit is "invalid number" (rc 1)
/// as in bash 5.2 (oils builtin-process "ulimit negative arg").</summary>
public class UlimitArgumentTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public UlimitArgumentTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private (string Out, int Code) Run(string args)
    {
        var pwsh = _fixture.AcquireFresh();
        var r = pwsh.AddScript($"$o = Invoke-BashUlimit {args} 2>$null | Out-String; \"$o|$global:LASTEXITCODE\"").Invoke();
        pwsh.Commands.Clear();
        var s = ((string)r[0].BaseObject).Split('|');
        return (s[0].Trim(), int.Parse(s[1]));
    }

    [Fact] public void Negative_value_after_double_dash_is_invalid_number() => Assert.Equal(("", 1), Run("'-f' '--' '-42'"));
    [Fact] public void Query_still_works() => Assert.Equal(("unlimited", 0), Run("-f"));
    [Fact] public void Setting_a_valid_number_still_succeeds() => Assert.Equal(("", 0), Run("-c 0"));
}
