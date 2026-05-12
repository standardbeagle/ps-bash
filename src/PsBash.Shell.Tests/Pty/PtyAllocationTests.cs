using System.Runtime.InteropServices;
using PsBash.Shell.Pty;
using Xunit;

namespace PsBash.Shell.Tests.Pty;

/// <summary>
/// PTY-1 acceptance tests: allocate + dispose a pseudo-terminal on each
/// platform and verify the slave-side handle/fd is connected to the master.
///
/// <para><b>Linux invocation:</b> <c>./scripts/test.sh --filter "FullyQualifiedName~PtyAllocationTests"</c>.
/// Windows-only tests in this class skip with a recorded reason; only the
/// Posix_* and platform-neutral tests execute.</para>
///
/// <para><b>Windows invocation (CI):</b> the <c>build.yml</c> matrix runs
/// the full test suite on <c>windows-latest</c> via <c>actions/setup-dotnet@v4</c>;
/// the Posix_* tests skip there and the Windows_* tests execute. No
/// manual wiring is required for CI runs.</para>
///
/// <para><b>Windows invocation (local, from this WSL2 host):</b> a Windows
/// .NET SDK must be available on the Windows side. If installed, invoke
/// via powershell.exe interop using the win-x64 build directory mapped
/// through <c>\\wsl$</c>:</para>
/// <code>
/// powershell.exe -NoProfile -Command "&amp; 'C:\Program Files\dotnet\dotnet.exe' test '\\wsl$\Ubuntu\home\beagle\work\core\ps-bash\src\PsBash.Shell.Tests\PsBash.Shell.Tests.csproj' --filter 'FullyQualifiedName~PtyAllocationTests'"
/// </code>
/// <para>If the Windows host has only the .NET runtime (no SDK — this is
/// the current state of this developer machine), the local Windows path is
/// blocked. The test surface is exercised by CI in that case. Acceptance
/// path: rely on CI for the Windows leg until a Windows SDK is provisioned
/// locally.</para>
///
/// <para><b>Why Windows round-trip is allocation-only:</b> A ConPTY cannot
/// be exercised end-to-end without a child process attached via
/// <c>STARTUPINFOEX</c> + <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> — that
/// belongs to PTY-2. For PTY-1 we verify allocation succeeds, HPCON is
/// non-zero, both pipe streams are usable, Resize succeeds, and Dispose is
/// clean.</para>
/// </summary>
public class PtyAllocationTests
{
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_Allocate_Then_RoundTrip_Through_Master_Slave()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "POSIX-only test — Windows uses ConPtyAdapter");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);

#pragma warning disable CA1416 // POSIX-guarded by Skip.If above
        Assert.IsType<UnixPtyAdapter>(pty);
#pragma warning restore CA1416
        Assert.True(pty.SlaveFileDescriptor >= 0, "Slave fd should be valid after allocation");
        Assert.Equal(IntPtr.Zero, pty.SlaveHandle);
        Assert.NotNull(pty.Input);
        Assert.NotNull(pty.Output);

        // Round-trip: write directly to the slave fd. The kernel routes
        // those bytes to the master side, where the launcher reads them.
        // This proves the master fd and slave fd are connected as a single
        // PTY pair.
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("ping\n");
        int written = WriteToFd(pty.SlaveFileDescriptor, payload);
        Assert.Equal(payload.Length, written);

        // PTYs in canonical mode echo input back to the master side. To
        // make the test deterministic regardless of termios state, we
        // simply read whatever bytes the master surfaces within a short
        // timeout and assert the payload appears in the prefix. The
        // kernel guarantees the write is delivered before the read can
        // see it.
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        int read = await pty.Output.ReadAsync(buffer.AsMemory(), cts.Token);
        Assert.True(read > 0, "Master side must surface bytes written to slave");
        var observed = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        Assert.Contains("ping", observed);
    }

    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_Resize_Succeeds_On_Live_Pty()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        // Should not throw — proves TIOCSWINSZ wired correctly.
        pty.Resize(cols: 120, rows: 40);
    }

    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_Resize_After_Dispose_Is_Noop()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        await pty.DisposeAsync();

        // Must not throw — dispose contract says Resize becomes a no-op.
        pty.Resize(cols: 120, rows: 40);
    }

    [SkippableFact]
    [Trait("Platform", "Windows")]
    public async Task Windows_Allocate_Returns_Valid_HPCON_And_Pipes()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "Windows-only test — POSIX uses UnixPtyAdapter");
        Skip.If(Environment.OSVersion.Version.Build < 17763,
            $"ConPTY requires Win10 1809 / build 17763+; current build is {Environment.OSVersion.Version.Build}");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);

#pragma warning disable CA1416 // Windows-guarded by Skip.IfNot above
        Assert.IsType<ConPtyAdapter>(pty);
#pragma warning restore CA1416
        Assert.NotEqual(IntPtr.Zero, pty.SlaveHandle);
        Assert.Equal(-1, pty.SlaveFileDescriptor);
        Assert.NotNull(pty.Input);
        Assert.NotNull(pty.Output);
        Assert.True(pty.Input.CanWrite, "Input pipe must be writable for the launcher");
        Assert.True(pty.Output.CanRead, "Output pipe must be readable for the launcher");
    }

    [SkippableFact]
    [Trait("Platform", "Windows")]
    public async Task Windows_Resize_Succeeds_On_Live_Pty()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only");
        Skip.If(Environment.OSVersion.Version.Build < 17763, "ConPTY requires build 17763+");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        pty.Resize(cols: 132, rows: 50);
    }

    [SkippableFact]
    [Trait("Platform", "Windows")]
    public async Task Windows_Resize_After_Dispose_Is_Noop()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only");
        Skip.If(Environment.OSVersion.Version.Build < 17763, "ConPTY requires build 17763+");

        var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        await pty.DisposeAsync();
        pty.Resize(cols: 132, rows: 50);
    }

    [Fact]
    public async Task Allocate_Rejects_NonPositive_Dimensions()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await PtyAllocator.AllocateAsync(cols: 0, rows: 24));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await PtyAllocator.AllocateAsync(cols: 80, rows: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await PtyAllocator.AllocateAsync(cols: -1, rows: 24));
    }

    [Fact]
    public async Task Double_Dispose_Is_Safe()
    {
        var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        await pty.DisposeAsync();
        // Second dispose must not throw or leak — dispose contract.
        await pty.DisposeAsync();
    }

    // ---- POSIX raw fd write helper -----------------------------------

    private static int WriteToFd(int fd, byte[] data)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new InvalidOperationException("WriteToFd is POSIX-only");
        }
#pragma warning disable CA1416 // Validate platform compatibility — guarded above
        // FileStream-from-fd path is the simplest stable way to write to a
        // raw fd without exposing more PInvoke surface to the test.
        using var sfh = new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, ownsHandle: false);
        using var fs = new FileStream(sfh, FileAccess.Write);
        fs.Write(data, 0, data.Length);
        fs.Flush();
        return data.Length;
#pragma warning restore CA1416
    }
}
