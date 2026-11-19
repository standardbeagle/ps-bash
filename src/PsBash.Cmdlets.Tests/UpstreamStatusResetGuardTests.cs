using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Guard for the class "a failure status set before the pipeline drains is lost": PowerShell runs a consumer's
/// BeginProcessing BEFORE the upstream stage's records, and that stage (here <c>echo</c>) sets
/// <c>$LASTEXITCODE = 0</c> as it runs, so a status written only at open time came out 0
/// (<c>echo x | tee /no/such/f</c> exited 0; GNU 1). Every consumer that reports an unopenable operand must latch the
/// failure and re-assert it in EndProcessing. Statuses are GNU coreutils 9.4's (WSL Ubuntu-24.04).
/// </summary>
public class UpstreamStatusResetGuardTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public UpstreamStatusResetGuardTests(SharedPwshFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("Invoke-BashTee", "tee: ")]
    [InlineData("Invoke-BashCat", "cat: ")]
    [InlineData("Invoke-BashHead", "head: ")]
    [InlineData("Invoke-BashTail", "tail: ")]
    [InlineData("Invoke-BashShuf", "shuf: ")]
    [InlineData("Invoke-BashTac", "tac: ")]
    [InlineData("Invoke-BashNl", "nl: ")]
    public void UnopenableOperand_AfterUpstreamEcho_Exits1(string cmdlet, string stderrPrefix)
    {
        string missing = Path.Combine(Path.GetTempPath(), "psbash-nosuch-" + Guid.NewGuid().ToString("N"), "f");
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashEcho x | {cmdlet} '{missing}'")
            .AssertFailed(1, stderrPrefix);
    }
}
