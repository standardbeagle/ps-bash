using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Host infrastructure failures map to one diagnostic line and a defined exit code, never a stack trace.
/// Launcher-specific surface (no bash equivalent); hand-written asserts.
/// </summary>
public sealed class HostFailureGuardTests
{
    private static async Task<(int Exit, string Err)> Run(Exception ex)
    {
        var err = new StringWriter();
        int exit = await HostFailureGuard.RunAsync(() => throw ex, err);
        return (exit, err.ToString().TrimEnd());
    }

    [Fact]
    public async Task MalformedEndpointSetting_IsAUsageError_NotACrash()
    {
        // PSBASH_IPC_ENDPOINT=probe (no scheme) used to escape as "Unhandled exception. System.ArgumentException".
        var (exit, err) = await Run(new InvalidEndpointException("PSBASH_IPC_ENDPOINT value 'probe' must be 'scheme:endpoint' (scheme = unix)."));
        Assert.Equal(2, exit);
        Assert.Equal("ps-bash: PSBASH_IPC_ENDPOINT value 'probe' must be 'scheme:endpoint' (scheme = unix).", err);
    }

    [Fact]
    public async Task HostUnavailable_Exits125()
    {
        var (exit, err) = await Run(new HostUnavailableException("no host"));
        Assert.Equal(125, exit);
        Assert.Equal("ps-bash: no host", err);
    }

    [Fact]
    public async Task ProgrammingError_IsNotSwallowed()
        => await Assert.ThrowsAsync<ArgumentException>(() => HostFailureGuard.RunAsync(() => throw new ArgumentException("bug"), new StringWriter()));
}
