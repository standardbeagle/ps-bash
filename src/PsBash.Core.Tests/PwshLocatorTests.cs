using Xunit;
using PsBash.Core.Runtime;

namespace PsBash.Core.Tests;

public class PwshLocatorTests
{
    private sealed class FakeEnvironment : IEnvironment
    {
        public Dictionary<string, string> EnvVars { get; } = new();
        public HashSet<string> ExistingFiles { get; } = new();
        public bool IsWindows { get; set; }
        public string BaseDirectory { get; set; } = "/app";

        public string? GetEnvironmentVariable(string name) =>
            EnvVars.TryGetValue(name, out var v) ? v : null;

        public bool FileExists(string path) => ExistingFiles.Contains(path);
    }

    [Fact]
    public void Locate_PsBashPwshEnvVar_ReturnsEnvVarPath()
    {
        var env = new FakeEnvironment();
        env.EnvVars["PSBASH_PWSH"] = "/custom/pwsh";

        var result = PwshLocator.Locate(env);

        Assert.Equal("/custom/pwsh", result);
    }

    [Fact]
    public void Locate_PsBashPwshEnvVar_TakesPriorityOverPath()
    {
        var env = new FakeEnvironment();
        env.EnvVars["PSBASH_PWSH"] = "/custom/pwsh";
        env.EnvVars["PATH"] = "/usr/bin";
        env.ExistingFiles.Add("/usr/bin/pwsh");

        var result = PwshLocator.Locate(env);

        Assert.Equal("/custom/pwsh", result);
    }

    [Fact]
    public void Locate_EmptyPsBashPwshEnvVar_FallsThrough()
    {
        var env = new FakeEnvironment();
        env.EnvVars["PSBASH_PWSH"] = "";
        env.EnvVars["PATH"] = "/usr/bin";
        env.ExistingFiles.Add(Path.Combine("/usr/bin", "pwsh"));

        var result = PwshLocator.Locate(env);

        Assert.Equal(Path.Combine("/usr/bin", "pwsh"), result);
    }

    [Fact]
    public void Locate_PwshOnPath_ReturnsPathLocation()
    {
        var env = new FakeEnvironment();
        env.EnvVars["PATH"] = $"/usr/local/bin{Path.PathSeparator}/usr/bin";
        env.ExistingFiles.Add(Path.Combine("/usr/bin", "pwsh"));

        var result = PwshLocator.Locate(env);

        Assert.Equal(Path.Combine("/usr/bin", "pwsh"), result);
    }

    [Fact]
    public void Locate_PwshOnPath_ReturnsFirstMatch()
    {
        var env = new FakeEnvironment();
        env.EnvVars["PATH"] = $"/first/bin{Path.PathSeparator}/second/bin";
        env.ExistingFiles.Add(Path.Combine("/first/bin", "pwsh"));
        env.ExistingFiles.Add(Path.Combine("/second/bin", "pwsh"));

        var result = PwshLocator.Locate(env);

        Assert.Equal(Path.Combine("/first/bin", "pwsh"), result);
    }

    [Fact]
    public void Locate_PwshOnPathWindows_UsesPwshExe()
    {
        var env = new FakeEnvironment { IsWindows = true };
        env.EnvVars["PATH"] = "/pwsh7";
        env.ExistingFiles.Add(Path.Combine("/pwsh7", "pwsh.exe"));

        var result = PwshLocator.Locate(env);

        Assert.Equal(Path.Combine("/pwsh7", "pwsh.exe"), result);
    }

    [Fact]
    public void Locate_SideBySideBundled_ReturnsBundledPath()
    {
        var env = new FakeEnvironment { BaseDirectory = "/app" };
        var expected = Path.Combine("/app", "pwsh", "pwsh");
        env.ExistingFiles.Add(expected);

        var result = PwshLocator.Locate(env);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Locate_SideBySideBundledWindows_UsesPwshExe()
    {
        var env = new FakeEnvironment { IsWindows = true, BaseDirectory = @"C:\app" };
        var expected = Path.Combine(@"C:\app", "pwsh", "pwsh.exe");
        env.ExistingFiles.Add(expected);

        var result = PwshLocator.Locate(env);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Locate_SideBySide_TakesPriorityOverNotFound()
    {
        var env = new FakeEnvironment { BaseDirectory = "/app" };
        env.EnvVars["PATH"] = "/usr/bin";
        var expected = Path.Combine("/app", "pwsh", "pwsh");
        env.ExistingFiles.Add(expected);

        var result = PwshLocator.Locate(env);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Locate_PathTakesPriorityOverSideBySide()
    {
        var env = new FakeEnvironment { BaseDirectory = "/app" };
        env.EnvVars["PATH"] = "/usr/bin";
        env.ExistingFiles.Add(Path.Combine("/usr/bin", "pwsh"));
        env.ExistingFiles.Add(Path.Combine("/app", "pwsh", "pwsh"));

        var result = PwshLocator.Locate(env);

        Assert.Equal(Path.Combine("/usr/bin", "pwsh"), result);
    }

    [Fact]
    public void Locate_NothingFound_ThrowsPwshNotFoundException()
    {
        var env = new FakeEnvironment();

        var ex = Assert.Throws<PwshNotFoundException>(() => PwshLocator.Locate(env));

        Assert.Contains("ps-bash requires PowerShell 7+", ex.Message);
        Assert.Contains("PSBASH_PWSH", ex.Message);
        Assert.Contains("https://aka.ms/powershell", ex.Message);
    }

    [Fact]
    public void Locate_NoPathEnvVar_SkipsPathSearch()
    {
        var env = new FakeEnvironment { BaseDirectory = "/app" };
        var expected = Path.Combine("/app", "pwsh", "pwsh");
        env.ExistingFiles.Add(expected);

        var result = PwshLocator.Locate(env);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Locate_EmptyPathEnvVar_SkipsPathSearch()
    {
        var env = new FakeEnvironment { BaseDirectory = "/app" };
        env.EnvVars["PATH"] = "";
        var expected = Path.Combine("/app", "pwsh", "pwsh");
        env.ExistingFiles.Add(expected);

        var result = PwshLocator.Locate(env);

        Assert.Equal(expected, result);
    }
}
