using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>Invoke-BashPipeGate</c>, the element in front of a streamed <c>while read</c> pipe stage: it passes records on
/// unchanged, and once the stage after it marks the claimed gate done it stops everything upstream (the endless
/// producer of <c>yes | while read l; do break; done</c>) while the stage after it still completes. Hand-written:
/// the cmdlet is ps-bash plumbing with no bash counterpart (the bash behavior is diffed in
/// ReadLoopStreamingDifferentialTests).
/// </summary>
public class InvokeBashPipeGateTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashPipeGateTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    [Fact]
    public void PipeGate_LoopNeverDone_PassesEveryRecordOnInOrder()
        => Assert.Equal("1\n2\n3",
            Run("1, 2, 3 | Invoke-BashPipeGate | & { begin { $g = [PsBash.Cmdlets.BashPipeGate]::Claim() } process { $_ } }")
                .AssertSuccess().Stdout);

    [Fact]
    public void PipeGate_LoopMarksDone_StopsAnEndlessUpstreamAndTheLoopStillEnds()
        => Assert.Equal("y\nend",
            Run("& { while ($true) { 'y' } } | Invoke-BashPipeGate | "
                + "& { begin { $g = [PsBash.Cmdlets.BashPipeGate]::Claim() } process { if (-not $g.Done) { $_; $g.Done = $true } } end { 'end' } }")
                .AssertSuccess().Stdout);

    [Fact]
    public void PipeGate_LoopMarksDone_StagesAfterTheLoopStillComplete()
        => Assert.Equal("[y]\n[end]",
            Run("& { while ($true) { 'y' } } | Invoke-BashPipeGate | "
                + "& { begin { $g = [PsBash.Cmdlets.BashPipeGate]::Claim() } process { if (-not $g.Done) { $_; $g.Done = $true } } end { 'end' } } | "
                + "& { process { \"[$_]\" } }")
                .AssertSuccess().Stdout);

    private const string Show = " | Invoke-BashPipeGate | & { begin { $g = [PsBash.Cmdlets.BashPipeGate]::Claim() } process { '[' + [PsBash.Cmdlets.BashRuntime]::GetBashText($_) + ']' } }";

    private static string Rec(string text, bool unterminated) =>
        "[PsBash.Cmdlets.BashRuntime]::TextRecord(\"" + text + "\", $" + unterminated.ToString().ToLowerInvariant() + ")";

    [Fact]
    public void PipeGate_UnterminatedEnd_WaitsForTheRecordThatFinishesTheLine()
        => Assert.Equal("[ab\n]\n[cd]",
            Run("& { " + Rec("a", true) + "; " + Rec("b`nc", true) + "; 'd' }" + Show).AssertSuccess().Stdout);

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    public void PipeGate_UnterminatedEndOfTheInput_IsNeverPassedOn(string text)
        => Assert.Equal("", Run("& { " + Rec(text, true) + " }" + Show).AssertSuccess().Stdout);

    [Fact]
    public void PipeGate_UnterminatedRecord_PassesItsWholeLinesThroughTheirNewline()
        => Assert.Equal("[a\n\n]", Run("& { " + Rec("a`n`nb", true) + " }" + Show).AssertSuccess().Stdout);

    [Fact]
    public void PipeGate_ClaimWithoutAGateInFront_ReturnsADetachedOpenGate()
        => Assert.Equal("False",
            Run("$g = [PsBash.Cmdlets.BashPipeGate]::Claim(); $g.Done").AssertSuccess().Stdout);

    [Fact]
    public void PipeGate_AGateIsClaimedOnce_TheNextClaimIsDetached()
        => Assert.Equal("False",
            Run("1 | Invoke-BashPipeGate | & { begin { $g = [PsBash.Cmdlets.BashPipeGate]::Claim(); $g.Done = $true } process { } }; "
                + "[PsBash.Cmdlets.BashPipeGate]::Claim().Done").AssertSuccess().Stdout);
}
