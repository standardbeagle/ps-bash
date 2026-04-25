using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace PsBash.Canary.Tests;

/// <summary>
/// Creates a PowerShell instance with the PsBash module loaded for use by M5/M6 modes.
/// Adapted from PsBash.Cmdlets.Tests.PwshTestFixture — not referenced directly to
/// avoid circular dependency risk.
/// </summary>
internal static class CanaryPwshFixture
{
    private static string? FindSdkModulePath()
    {
        var smaAssembly = typeof(PSObject).Assembly;
        var smaPath = smaAssembly.Location;
        var smaDir = Path.GetDirectoryName(smaPath);
        if (smaDir == null) return null;

        var current = new DirectoryInfo(smaDir);
        for (int i = 0; i < 6 && current != null; i++, current = current.Parent)
        {
            var sdkDir = current.Parent?.GetDirectories("microsoft.powershell.sdk").FirstOrDefault();
            if (sdkDir != null)
            {
                var versionDir = sdkDir.GetDirectories().OrderByDescending(d =>
                {
                    Version.TryParse(d.Name, out var v);
                    return v;
                }).FirstOrDefault();
                if (versionDir != null)
                {
                    var modulesPath = Path.Combine(versionDir.FullName, "contentFiles", "any", "any", "runtimes", "win", "lib", "net8.0", "Modules");
                    if (Directory.Exists(modulesPath))
                        return modulesPath;

                    modulesPath = Path.Combine(versionDir.FullName, "contentFiles", "any", "any", "runtimes", "unix", "lib", "net8.0", "Modules");
                    if (Directory.Exists(modulesPath))
                        return modulesPath;
                }
            }
        }

        var nugetCache = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var sdkPackageDir = Path.Combine(nugetCache, "microsoft.powershell.sdk");
        if (Directory.Exists(sdkPackageDir))
        {
            var versionDir = new DirectoryInfo(sdkPackageDir).GetDirectories()
                .Select(d => new { Dir = d, Version = Version.TryParse(d.Name, out var v) ? v : null })
                .Where(x => x.Version != null)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault()?.Dir;
            if (versionDir != null)
            {
                var runtime = OperatingSystem.IsWindows() ? "win" : "unix";
                var modulesPath = Path.Combine(versionDir.FullName, "contentFiles", "any", "any", "runtimes", runtime, "lib", "net8.0", "Modules");
                if (Directory.Exists(modulesPath))
                    return modulesPath;
            }
        }

        return null;
    }

    public static PowerShell Create()
    {
        var sdkModules = FindSdkModulePath();
        if (sdkModules != null)
        {
            var psModulePath = Environment.GetEnvironmentVariable("PSModulePath") ?? "";
            if (!psModulePath.Contains(sdkModules))
            {
                psModulePath = sdkModules + Path.PathSeparator + psModulePath;
                Environment.SetEnvironmentVariable("PSModulePath", psModulePath);
            }
        }

        var iss = InitialSessionState.CreateDefault2();

        if (OperatingSystem.IsWindows())
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();

        var pwsh = PowerShell.Create();
        pwsh.Runspace = runspace;

        var baseDir = AppContext.BaseDirectory;

        var psm1Path = Path.Combine(baseDir, "PsBash.psm1");
        if (File.Exists(psm1Path))
        {
            var psm1Content = File.ReadAllText(psm1Path);
            pwsh.AddScript(psm1Content).Invoke();
            pwsh.Commands.Clear();
        }

        var dllPath = Path.Combine(baseDir, "PsBash.Cmdlets.dll");
        if (File.Exists(dllPath))
        {
            pwsh.AddCommand("Import-Module").AddParameter("Name", dllPath).Invoke();
            pwsh.Commands.Clear();
        }

        var formatPath = Path.Combine(baseDir, "PsBash.Format.ps1xml");
        if (File.Exists(formatPath))
        {
            pwsh.AddCommand("Update-FormatData").AddParameter("AppendPath", formatPath).Invoke();
            pwsh.Commands.Clear();
        }

        return pwsh;
    }
}
