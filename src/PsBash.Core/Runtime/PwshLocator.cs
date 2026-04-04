namespace PsBash.Core.Runtime;

public static class PwshLocator
{
    public static string Locate() => Locate(SystemEnvironment.Instance);

    public static string Locate(IEnvironment env) =>
        FromEnvironment(env)
        ?? FromPath(env)
        ?? FromSideBySide(env)
        ?? throw new PwshNotFoundException(
            "ps-bash requires PowerShell 7+.\n" +
            "Install: https://aka.ms/powershell\n" +
            "Or set PSBASH_PWSH=/path/to/pwsh");

    private static string? FromEnvironment(IEnvironment env) =>
        env.GetEnvironmentVariable("PSBASH_PWSH") is { Length: > 0 } v ? v : null;

    private static string? FromPath(IEnvironment env)
    {
        var paths = env.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator) ?? [];
        var exeName = env.IsWindows ? "pwsh.exe" : "pwsh";
        return paths
            .Select(p => Path.Combine(p, exeName))
            .FirstOrDefault(env.FileExists);
    }

    private static string? FromSideBySide(IEnvironment env)
    {
        var exeName = env.IsWindows ? "pwsh.exe" : "pwsh";
        var sxs = Path.Combine(env.BaseDirectory, "pwsh", exeName);
        return env.FileExists(sxs) ? sxs : null;
    }
}

public interface IEnvironment
{
    string? GetEnvironmentVariable(string name);
    bool FileExists(string path);
    bool IsWindows { get; }
    string BaseDirectory { get; }
}

internal sealed class SystemEnvironment : IEnvironment
{
    public static readonly SystemEnvironment Instance = new();

    public string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name);

    public bool FileExists(string path) => File.Exists(path);

    public bool IsWindows => OperatingSystem.IsWindows();

    public string BaseDirectory => AppContext.BaseDirectory;
}
