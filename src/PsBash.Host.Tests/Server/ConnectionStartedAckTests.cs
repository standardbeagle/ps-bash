using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// R05 regression guard: the framed host MUST emit the execution-start
/// acknowledgement (<see cref="HostProtocol.StartedSentinel"/>) before it
/// executes a command and before any output frame. The launcher's pre-output
/// transport-reset retry is gated on this frame; without it, a silent command
/// (<c>echo x &gt;&gt; f</c>, <c>rm</c>, <c>mkdir</c>) that completed on the host
/// but lost its connection re-runs on retry.
///
/// Asserted on the raw wire bytes so this test does not depend on the
/// launcher-side reader consuming the sentinel — a reader that predates the
/// token would otherwise surface it as a data line and hide a regression. The
/// expected token is the wire contract literal, deliberately not the
/// <c>HostProtocol.StartedSentinel</c> constant, so the assertion cannot be
/// weakened by editing the constant.
/// </summary>
[Collection("SdkHost")]
public sealed class ConnectionStartedAckTests
{
    private const string ExpectedStartedFrame = "<<<STARTED>>>";

    [Fact]
    public async Task FramedCommand_EmitsStartedSentinelBeforeAnyOutput()
    {
        await using var pool = new WorkerPool<SdkWorker>(warmTarget: 0, max: 1, SdkWorker.Create);

        await using var stream = new MemoryStream();
        await HostProtocol.WriteRequestAsync(stream, new Mode.Command("Invoke-BashEcho 'hello'"));
        stream.Position = 0;

        var connection = new Connection(stream, pool);
        await connection.HandleAsync(default);

        stream.Position = 0;
        var text = await new StreamReader(stream).ReadToEndAsync();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var endIndex = Array.IndexOf(lines, HostProtocol.EndSentinel);
        Assert.True(endIndex >= 0, "request END sentinel not found in the connection transcript");
        Assert.Equal(ExpectedStartedFrame, lines[endIndex + 1]);
    }
}
