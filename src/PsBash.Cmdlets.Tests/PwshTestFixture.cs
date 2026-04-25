using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Central fixture for creating a PowerShell instance with the PsBash module loaded.
/// Handles cross-platform differences (e.g. ExecutionPolicy is Windows-only).
///
/// NOTE: The in-process runspace created via Microsoft.PowerShell.SDK needs the
/// SDK's built-in module manifests to be discoverable. We locate them from the
/// NuGet package cache and prepend them to PSModulePath before opening the runspace.
/// </summary>
public static class PwshTestFixture
{
    /// <summary>
    /// Locates the Microsoft.PowerShell.SDK module directory in the NuGet cache.
    /// </summary>
    private static string? FindSdkModulePath()
    {
        // Try to find the SMA assembly location, then navigate to the SDK package
        var smaAssembly = typeof(PSObject).Assembly;
        var smaPath = smaAssembly.Location;
        // smaPath is like: ...\system.management.automation\7.4.6\lib\net8.0\System.Management.Automation.dll
        var smaDir = Path.GetDirectoryName(smaPath);
        if (smaDir == null) return null;

        // Walk up to find the NuGet packages root, then look for microsoft.powershell.sdk
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

                    // Also try unix path for cross-platform
                    modulesPath = Path.Combine(versionDir.FullName, "contentFiles", "any", "any", "runtimes", "unix", "lib", "net8.0", "Modules");
                    if (Directory.Exists(modulesPath))
                        return modulesPath;
                }
            }
        }

        // Fallback: search from NuGet cache root
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
                var tfm = "net8.0";
                var modulesPath = Path.Combine(versionDir.FullName, "contentFiles", "any", "any", "runtimes", runtime, "lib", tfm, "Modules");
                if (Directory.Exists(modulesPath))
                    return modulesPath;
            }
        }

        return null;
    }

    public static PowerShell Create()
    {
        // Prepend SDK module path to PSModulePath so built-in modules can be loaded.
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

        // ExecutionPolicy is a Windows-only concept; setting it on Linux/macOS
        // throws PlatformNotSupportedException during runspace.Open().
        if (OperatingSystem.IsWindows())
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();

        var pwsh = PowerShell.Create();
        pwsh.Runspace = runspace;

        var baseDir = AppContext.BaseDirectory;

        // 1. Load the script module by dot-sourcing the .psm1 directly.
        //    This avoids Import-Module trying to resolve Microsoft.PowerShell.Utility
        //    as a module dependency (which fails in the in-process SDK runspace).
        var psm1Path = Path.Combine(baseDir, "PsBash.psm1");
        if (File.Exists(psm1Path))
        {
            pwsh.AddScript($". '{psm1Path}'").Invoke();
            pwsh.Commands.Clear();
        }

        // 2. Load the binary module DLL directly.
        //    Import-Module on the .psd1 would fail due to RequiredModules / NestedModules
        //    referencing other manifests. Loading the DLL directly registers the cmdlets.
        var dllPath = Path.Combine(baseDir, "PsBash.Cmdlets.dll");
        if (File.Exists(dllPath))
        {
            pwsh.AddCommand("Import-Module").AddParameter("Name", dllPath).Invoke();
            pwsh.Commands.Clear();
        }

        // 3. Import the format file so output formatting works correctly.
        var formatPath = Path.Combine(baseDir, "PsBash.Format.ps1xml");
        if (File.Exists(formatPath))
        {
            pwsh.AddCommand("Update-FormatData").AddParameter("AppendPath", formatPath).Invoke();
            pwsh.Commands.Clear();
        }

        return pwsh;
    }
}
