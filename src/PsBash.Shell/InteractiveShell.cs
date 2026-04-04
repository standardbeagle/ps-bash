using System.Diagnostics;

namespace PsBash.Shell;

public static class InteractiveShell
{
    public static async Task<int> RunAsync(string pwshPath)
    {
        var modulePath = ResolveModulePath();
        var importCommand = modulePath is not null
            ? $"Import-Module '{modulePath}'"
            : "Import-Module ps-bash";

        var psi = new ProcessStartInfo
        {
            FileName = pwshPath,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(importCommand);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh interactive session");

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static string? ResolveModulePath()
    {
        var envModule = Environment.GetEnvironmentVariable("PSBASH_MODULE");
        if (envModule is { Length: > 0 })
            return envModule;

        var sxsModule = Path.Combine(AppContext.BaseDirectory, "Modules", "ps-bash");
        if (Directory.Exists(sxsModule))
            return sxsModule;

        return null;
    }
}
