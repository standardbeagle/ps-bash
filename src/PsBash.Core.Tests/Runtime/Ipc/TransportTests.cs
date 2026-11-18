using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// T03 acceptance tests: 1 KB round-trip on each transport, atomic lock-file
/// read/write/stale-detect, Unix mode 0600.
/// </summary>
public class TransportTests : IDisposable
{
    // One per test; removed after it (it was left behind: hundreds of empty ps-bash/test-* dirs).
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ps-bash", "test-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] OneKb()
    {
        var buf = new byte[1024];
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i & 0xFF);
        return buf;
    }

    [SkippableFact]
    public async Task UnixSocket_RoundTrips_1KB()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.OSVersion.Version.Build < 17063,
            "AF_UNIX requires Windows 10 1803+");

        var dir = _dir;
        Directory.CreateDirectory(dir);
        var sockPath = Path.Combine(dir, "t.sock");
        await using var transport = new UnixSocketTransport(sockPath);
        await transport.ListenAsync();

        var payload = OneKb();
        var serverTask = Task.Run(async () =>
        {
            using var s = await transport.AcceptAsync();
            var buf = new byte[payload.Length];
            int read = 0;
            while (read < buf.Length)
            {
                int n = await s.ReadAsync(buf.AsMemory(read));
                if (n == 0) break;
                read += n;
            }
            Assert.Equal(payload.Length, read);
            Assert.Equal(payload, buf);
            await s.WriteAsync(buf.AsMemory()); // echo back
        });

        using (var client = await transport.ConnectAsync())
        {
            await client.WriteAsync(payload);
            var echo = new byte[payload.Length];
            int read = 0;
            while (read < echo.Length)
            {
                int n = await client.ReadAsync(echo.AsMemory(read));
                if (n == 0) break;
                read += n;
            }
            Assert.Equal(payload, echo);
        }

        await serverTask;
    }

    /// <summary>
    /// REGRESSION: a second host on a live endpoint deleted the live host's socket file before binding. On POSIX the
    /// delete succeeds and the first host kept running unreachable; on Windows the bind died with an unhandled
    /// "address already in use" (a FATAL host-log entry). The loser now gets EndpointInUseException, and the first
    /// host still answers.
    /// </summary>
    [SkippableFact]
    public async Task UnixSocket_SecondListenerOnALiveEndpoint_IsRefused_AndTheFirstStillAnswers()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.OSVersion.Version.Build < 17063,
            "AF_UNIX requires Windows 10 1803+");
        Directory.CreateDirectory(_dir);
        var sockPath = Path.Combine(_dir, "live.sock");
        await using var first = new UnixSocketTransport(sockPath);
        await first.ListenAsync();

        await using var second = new UnixSocketTransport(sockPath);
        var ex = await Assert.ThrowsAsync<EndpointInUseException>(() => second.ListenAsync());
        Assert.Equal(sockPath, ex.Endpoint);
        Assert.True(File.Exists(sockPath), "the live host's socket file was removed");

        var accept = first.AcceptAsync();
        using var client = await first.ConnectAsync();
        using var served = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(served);
    }

    // REGRESSION (macOS, publish v0.15.0 dry run): a connect to a path that is not a socket is ENOTSOCK there, not
    // ECONNREFUSED, and only the latter counted as "nobody listens". Errors that leave a host possibly alive do not count.
    [Theory]
    [InlineData(SocketError.ConnectionRefused, true)]
    [InlineData(SocketError.NotSocket, true)]
    [InlineData(SocketError.TimedOut, false)]
    [InlineData(SocketError.AccessDenied, false)]
    [InlineData(SocketError.AddressNotAvailable, false)]
    public void Probe_CountsOnlyTheErrorsThatProveNobodyListens(SocketError error, bool stale)
        => Assert.Equal(stale, UnixSocketProbe.IsRefused(error));

    /// <summary>A socket file nobody listens on (its host crashed) is still taken over.</summary>
    [SkippableFact]
    public async Task UnixSocket_StaleSocketFile_IsReplaced()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.OSVersion.Version.Build < 17063,
            "AF_UNIX requires Windows 10 1803+");
        Directory.CreateDirectory(_dir);
        var sockPath = Path.Combine(_dir, "stale.sock");
        // A killed host's leftover file. A bound socket closed normally cannot stand in: .NET on Windows deletes the file
        // at close. A plain file at the path refuses a connect on Windows and Linux alike (10061 / ECONNREFUSED).
        File.WriteAllText(sockPath, "");
        Assert.True(UnixSocketProbe.NothingListens(sockPath));

        await using var transport = new UnixSocketTransport(sockPath);
        await transport.ListenAsync();
        var accept = transport.AcceptAsync();
        using var client = await transport.ConnectAsync();
        using var served = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(served);
    }

    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task UnixSocket_FileMode_Is_0600()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only — File.SetUnixFileMode no-op on Windows");
#pragma warning disable CA1416
        await VerifyUnixModeAsync();
#pragma warning restore CA1416
    }

    [UnsupportedOSPlatform("windows")]
    private async Task VerifyUnixModeAsync()
    {
        var dir = _dir;
        Directory.CreateDirectory(dir);
        var sockPath = Path.Combine(dir, "t.sock");
        await using var transport = new UnixSocketTransport(sockPath);
        await transport.ListenAsync();

        var mode = File.GetUnixFileMode(sockPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
