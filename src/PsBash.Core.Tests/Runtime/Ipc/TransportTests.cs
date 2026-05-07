using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// T03 acceptance tests: 1 KB round-trip on each transport, atomic lock-file
/// read/write/stale-detect, Unix mode 0600, Windows pipe ACL = current user.
/// </summary>
public class TransportTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ps-bash", "test-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    private static byte[] OneKb()
    {
        var buf = new byte[1024];
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i & 0xFF);
        return buf;
    }

    [Fact]
    public async Task UnixSocket_RoundTrips_1KB()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.OSVersion.Version.Build < 17063,
            "AF_UNIX requires Windows 10 1803+");

        var dir = TempPath();
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
    private static async Task VerifyUnixModeAsync()
    {
        var dir = TempPath();
        Directory.CreateDirectory(dir);
        var sockPath = Path.Combine(dir, "t.sock");
        await using var transport = new UnixSocketTransport(sockPath);
        await transport.ListenAsync();

        var mode = File.GetUnixFileMode(sockPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public async Task NamedPipe_RoundTrips_1KB()
    {
        var pipeName = "psbash-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        await using var transport = new NamedPipeTransport(pipeName);
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
            await s.WriteAsync(buf.AsMemory());
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

    [SkippableFact]
    [Trait("Platform", "Windows")]
    [SupportedOSPlatform("windows")]
    public async Task NamedPipe_AclAllowsCurrentUserOnly()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only ACL test");

        var pipeName = "psbash-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        await using var transport = new NamedPipeTransport(pipeName);
        await transport.ListenAsync();

        var acceptTask = transport.AcceptAsync();
        // Connect a client so the server stream materializes; on Windows the
        // server stream is created inside AcceptAsync, so we have to open a
        // separate inspection handle to query the DACL.
        using var client = await transport.ConnectAsync();
        using var server = await acceptTask;

        var pipeServer = (NamedPipeServerStream)server;
        var sec = pipeServer.GetAccessControl();
        var rules = sec.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));

        var currentSid = WindowsIdentity.GetCurrent().User!;
        bool foundCurrentUser = false;
        foreach (PipeAccessRule rule in rules)
        {
            // Any allow rule must be for the current user; reject Everyone/World rules.
            if (rule.AccessControlType == AccessControlType.Allow)
            {
                Assert.Equal(currentSid, rule.IdentityReference);
                foundCurrentUser = true;
            }
        }
        Assert.True(foundCurrentUser, "Expected at least one Allow rule for current user");
    }

}
