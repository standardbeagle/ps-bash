using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Shell.Tests;

public sealed class HostCommandTests
{
    [Fact]
    public async Task ControlRequest_WhenHostClosesDuringResponse_ReturnsUnavailableWithoutThrowing()
    {
        // macOS limits AF_UNIX path to 104 chars; the default temp prefix
        // (/var/folders/tb/<14-char-id>/T/CoreFxPipe_) eats ~50 chars before
        // the pipe name even starts. Use the first 8 hex digits of a guid
        // instead of the full 32 to stay under the cap. Collision risk on a
        // single-run test is negligible.
        var endpoint = $"pipe:psbash-abrupt-{Guid.NewGuid():N}".Substring(0, "pipe:psbash-abrupt-".Length + 8);
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
