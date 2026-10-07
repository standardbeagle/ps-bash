using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// The pipe transport capped server instances at 16 while HostServer admits 64 concurrent
/// connections: with 16 held, the next listening instance could not be created ("All pipe instances
/// are busy"), the 17th launcher could not connect, and the accept loop spun every 10 ms flooding
/// host.log until one finished.
/// </summary>
[Trait("Platform", "Windows")]
public class NamedPipeInstanceCapTests
{
    [SkippableFact]
    public async Task MoreThanSixteenConcurrentConnections_AllConnect()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "named pipes");
        const int n = 24;
        var name = "psbash-cap-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeTransport(name);
        await server.ListenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var held = new List<Stream>();
        var acceptLoop = Task.Run(async () =>
        {
            for (int i = 0; i < n; i++)
                held.Add(await server.AcceptAsync(cts.Token)); // held open: every instance stays in use
        });

        var clients = new List<NamedPipeTransport>();
        var streams = new List<Stream>();
        try
        {
            for (int i = 0; i < n; i++)
            {
                var c = new NamedPipeTransport(name);
                clients.Add(c);
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                streams.Add(await c.ConnectAsync(connectCts.Token)); // before: the 17th timed out here
            }
            await acceptLoop.WaitAsync(cts.Token);
            Assert.Equal(n, held.Count);
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
            foreach (var s in held) s.Dispose();
            foreach (var c in clients) await c.DisposeAsync();
        }
    }
}
