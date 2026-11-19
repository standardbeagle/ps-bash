using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>source &lt;(producer)</c> (<c>Invoke-ProcessSubSource</c>): the script is the producer's BYTES (the one record →
/// bytes rule, <c>BashRuntime.RecordStreamText</c>) and the sourced commands' stdout is the command's stdout.
/// REGRESSIONS: every record was joined with a newline, so an exact piece ran as its own line
/// (<c>source &lt;(printf 'echo a'; printf 'b\n')</c> ran <c>echo a</c> then <c>b</c>; bash runs <c>echo ab</c>), and
/// the sourced commands' output was discarded (<c>source &lt;(echo 'echo hi')</c> printed nothing).
/// Oracle: <c>RecordBytesDifferentialTests.ScriptFromProcessSubstitutionOrStdin_IsTheProducersBytes</c>; this pins the
/// cmdlet layer without a launcher.
/// </summary>
public class InvokeProcessSubSourceCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public InvokeProcessSubSourceCommandTests(SharedPwshFixture fixture) => _fixture = fixture;

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    [Fact]
    public void ExactPiece_RunsOnIntoTheNextRecord()
    {
        var r = Run("Invoke-ProcessSubSource { Invoke-BashPrintf 'echo a'; Invoke-BashPrintf 'b\\n' } | ConvertTo-BashCapture -Join")
            .AssertSuccess();
        Assert.Equal("ab", r.Stdout);
    }

    [Fact]
    public void LastPieceWithoutNewline_IsStillACommand()
    {
        var r = Run("Invoke-ProcessSubSource { Invoke-BashPrintf 'echo x\\n'; Invoke-BashPrintf 'echo y' } | ConvertTo-BashCapture -Join")
            .AssertSuccess();
        Assert.Equal("x\ny", r.Stdout);
    }

    [Fact]
    public void SourcedDefinitions_StillPersist()
    {
        var r = Run("Invoke-ProcessSubSource { Invoke-BashPrintf 'f() { echo F; }\\n' }; f | ConvertTo-BashCapture -Join")
            .AssertSuccess();
        Assert.Equal("F", r.Stdout);
    }
}
