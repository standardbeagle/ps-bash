using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The in-process background-job runspace pool (<c>Get-BashBgRunspacePool</c>, PsBash.psm1).
/// </summary>
public class BackgroundJobPoolTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public BackgroundJobPoolTests(SharedPwshFixture fixture) => _fixture = fixture;

    /// <summary>
    /// REGRESSION (fleet g17, beagle-ab): the pool's session state had no execution policy, so it took the machine's;
    /// where that resolved to Restricted, Open() could not load PsBash.psm1 ("running scripts is disabled on this
    /// system") and every <c>cmd &amp;</c> failed. A test cannot change the machine policy, so this checks the pool's own.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Pool_BypassesTheMachineExecutionPolicy()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "execution policy is Windows-only");
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            "[string](Get-BashBgRunspacePool).InitialSessionState.ExecutionPolicy").AssertSuccess();
        Assert.Equal("Bypass", r.Lines[^1]);
    }
}
