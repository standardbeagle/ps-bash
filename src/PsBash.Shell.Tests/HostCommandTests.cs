using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Shell.Tests;

public sealed class HostCommandTests
{
    [Fact]
    public async Task ControlRequest_WhenHostClosesDuringResponse_ReturnsUnavailableWithoutThrowing()
    {
        var endpoint = $"pipe:psbash-abrupt-status-{Guid.NewGuid():N}";
        await using var transport = IpcTransportFactory.CreateDefault(endpoint);
        await transport.ListenAsync();
        var serverTask = Task.Run(async () =>
        {
            await using var stream = await transport.AcceptAsync();
            _ = await HostProtocol.ReadRequestAsync(stream);
        });

        var (exitCode, lines) = await HostCommands.SendControlRequestAsync(
            new Mode.Health(),
            CancellationToken.None,
            endpoint);

        Assert.Null(exitCode);
        Assert.Empty(lines);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
