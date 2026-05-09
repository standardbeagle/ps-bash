using PsBash.Host;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Tests for non-interactive launcher PID wiring. Oracle note (Directive 1):
/// no bash oracle — ps-bash-specific host lifecycle contract.
/// </summary>
[Collection("SdkHost")]
public sealed class LauncherPidContractTests
{
    [Fact]
    public void NonInteractiveLauncherPid_UsesEnvironmentFallback()
    {
        WithParentPidEnv("12345", () =>
        {
            Assert.Equal(12345, Program.GetNonInteractiveLauncherPid(Array.Empty<string>()));
        });
    }

    [Fact]
    public void NonInteractiveLauncherPid_ArgumentOverridesEnvironmentFallback()
    {
        WithParentPidEnv("12345", () =>
        {
            Assert.Equal(67890, Program.GetNonInteractiveLauncherPid(["--launcher-pid", "67890"]));
        });
    }

    [Fact]
    public void NonInteractiveLauncherPid_InvalidEnvironmentReturnsNull()
    {
        WithParentPidEnv("not-a-pid", () =>
        {
            Assert.Null(Program.GetNonInteractiveLauncherPid(Array.Empty<string>()));
        });
    }

    private static void WithParentPidEnv(string? value, Action action)
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_HOST_PARENT_PID");
        Environment.SetEnvironmentVariable("PSBASH_HOST_PARENT_PID", value);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_HOST_PARENT_PID", prior);
        }
    }
}
