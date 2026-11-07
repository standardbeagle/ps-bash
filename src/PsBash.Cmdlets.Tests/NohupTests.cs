using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>`nohup`: GNU exit codes (125 own failure, 127 not found); redirects arrive as private --psbash-* flags.</summary>
public class NohupTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public NohupTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private int Code(string args)
    {
        var pwsh = _fixture.AcquireFresh();
        var r = pwsh.AddScript($"Invoke-BashNohup {args} 2>$null | Out-Null; $global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        return (int)r[0].BaseObject;
    }

    [Fact] public void No_operand_is_125() => Assert.Equal(125, Code(""));
    [Fact] public void Unknown_command_is_127() => Assert.Equal(127, Code("'--psbash-out' '/dev/null' 'psbash-no-such-cmd-xyz'"));
}
