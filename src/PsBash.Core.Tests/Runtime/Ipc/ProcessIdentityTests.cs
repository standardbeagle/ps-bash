using System.Diagnostics;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// The session-anchor identity a launcher hands its daemon (<c>--session-anchor</c>, GH #7).
/// Oracle note (Directive 1): no bash oracle — ps-bash host lifecycle surface.
/// </summary>
public class ProcessIdentityTests
{
    [Fact]
    public void ArgumentValue_RoundTrips()
    {
        var id = new ProcessIdentity(4242, 638_000_000_000_000_000);
        Assert.Equal("4242:638000000000000000", id.ToArgumentValue());
        Assert.True(ProcessIdentity.TryParse(id.ToArgumentValue(), out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4242")]
    [InlineData(":123")]
    [InlineData("0:123")]
    [InlineData("-1:123")]
    [InlineData("12:")]
    [InlineData("12:abc")]
    [InlineData("12:-5")]
    [InlineData("12:3:4")]
    public void TryParse_Malformed_ReturnsFalse(string? value)
        => Assert.False(ProcessIdentity.TryParse(value, out _));

    [Fact]
    public void IsRunning_CurrentProcessWithItsStartTime_True()
    {
        using var self = Process.GetCurrentProcess();
        var id = new ProcessIdentity(self.Id, ProcessIdentity.StartTicksOf(self));
        Assert.True(id.IsRunning());
    }

    [Fact]
    public void IsRunning_LivePidWithDifferentStartTime_False()
    {
        // A recycled pid: same number, different process. Must not count as the anchor.
        using var self = Process.GetCurrentProcess();
        var id = new ProcessIdentity(self.Id, ProcessIdentity.StartTicksOf(self) - TimeSpan.TicksPerSecond);
        Assert.False(id.IsRunning());
    }

    [Fact]
    public void IsRunning_ExitedProcess_False()
    {
        // Long-lived, so its start time is readable before it ends; then end it.
        var si = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 }
            : new ProcessStartInfo("sleep", "60") { UseShellExecute = false };
        using var p = Process.Start(si)!;
        var id = new ProcessIdentity(p.Id, ProcessIdentity.StartTicksOf(p));
        Assert.True(id.IsRunning());
        p.Kill(entireProcessTree: true);
        p.WaitForExit();
        Assert.False(id.IsRunning());
    }

    // Another user's process: Windows System (4), init/launchd (1). Its start time can be refused (macOS always,
    // Windows unelevated); the identity of a started child must still come back.
    [Fact]
    public void Of_AnotherUsersProcess_NeverThrows()
    {
        int pid = OperatingSystem.IsWindows() ? 4 : 1;
        Assert.Equal(pid, ProcessIdentity.Of(pid).Pid);
    }

    [Fact]
    public void Of_APidThatDoesNotExist_HasZeroStartTicks()
        => Assert.Equal(new ProcessIdentity(int.MaxValue, 0), ProcessIdentity.Of(int.MaxValue));

    /// <summary>
    /// Every spawn takes its child's identity from <see cref="ProcessIdentity.Of"/>. The spawns read the start time
    /// themselves once, in two copies; the POSIX copy did not catch EPERM, so on macOS every setuid program
    /// (<c>/bin/ps</c>, <c>sudo</c>) failed to start and stayed unreaped. Oracle note (Directive 1): a source invariant.
    /// </summary>
    [Fact]
    public void EverySpawn_TakesItsChildsIdentityFromOf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var processes = Path.Combine(dir!.FullName, "src", "PsBash.Transpiler", "Processes");
        var offenders = Directory.EnumerateFiles(processes, "JobProcess*.cs")
            .Where(f => File.ReadAllText(f).Contains("StartTicksOf(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(offenders.Count == 0, "read a child's start time through ProcessIdentity.Of: " + string.Join(", ", offenders));
    }
}
