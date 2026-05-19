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
///
/// ============================================================================
/// PERFORMANCE NOTE — SharedPwshFixture is the fast path.
/// ============================================================================
/// PwshTestFixture.Create() is called per-test in 82+ classes. Each call
/// re-parses ~10kLoC PsBash.psm1, re-imports PsBash.Cmdlets.dll, and re-loads
/// format data. That's ~2.5s of overhead per test.
///
/// Prefer <see cref="SharedPwshFixture"/> via xUnit IClassFixture for new and
/// migrated test classes — it creates ONE runspace per test class and resets
/// mutable state between tests instead of re-parsing the module.
///
/// ----------------------------------------------------------------------------
/// MIGRATION RECIPE — converting a class from Create() to SharedPwshFixture
/// ----------------------------------------------------------------------------
/// 1. Add `: IClassFixture&lt;SharedPwshFixture&gt;` to the test class.
/// 2. Add a constructor that takes `SharedPwshFixture fixture` and stores it.
/// 3. Replace each `using var pwsh = PwshTestFixture.Create();` with
///    `var pwsh = _fixture.AcquireFresh();` (NO `using` — the fixture owns the
///    runspace lifetime; AcquireFresh() resets mutable state and returns the
///    shared PowerShell instance).
/// 4. Anywhere the test relied on a fresh `$error` collection, AcquireFresh()
///    already calls `$error.Clear()` for you.
/// 5. If the test changes the current directory (`Set-Location`), env vars
///    that are inspected in later tests, or sets `$global:BashErrorMode`,
///    that's also reset by AcquireFresh().
///
/// Reference conversion: see InvokeBashEnvCommandTests.cs.
///
/// What AcquireFresh() resets (between tests in the same class):
///   - $global:LASTEXITCODE -> $null
///   - $global:BashPositional / $global:BashFlags -> $null
///   - $global:BashBgLastPid / $global:BashLastArg -> $null
///   - Variable: scope user-defined vars beyond the psm1 baseline
///   - Current location -> back to original PWD captured at fixture start
///   - $error.Clear()
///   - BashErrorMode -> 'PowerShell' (so Write-Error surfaces to in-process tests)
///
/// What is NOT reset (intentional):
///   - Loaded modules / cmdlets / psm1 functions (that's the entire speed win)
///   - $env:* variables (process-global — resetting them would race other tests)
///   - File system state created by the test (each test cleans its own temp dirs)
/// ----------------------------------------------------------------------------
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

    /// <summary>
    /// Creates a brand-new PowerShell + Runspace, loads psm1 + cmdlets + format data,
    /// and returns it. The caller owns the lifetime (use `using var`).
    ///
    /// This is the slow per-test path retained for the ~80 unmigrated test classes.
    /// New / migrated tests should use <see cref="SharedPwshFixture"/> instead.
    /// </summary>
    public static PowerShell Create()
    {
        return CreateInternal();
    }

    // Shared implementation used by both Create() and SharedPwshFixture.
    internal static PowerShell CreateInternal()
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

        // 1. Load the script module by reading the .psm1 contents and running
        //    them as a script. We can't:
        //    - Import-Module on the .psd1 manifest (RequiredModules / NestedModules
        //      cannot be resolved in the in-process SDK runspace).
        //    - Dot-source `. 'PsBash.psm1'` (Linux PowerShell rejects the .psm1
        //      extension and tries to exec it).
        //    - Import-Module on the bare .psm1 (hangs the in-process runspace
        //      because module-dependency resolution still kicks in for cmdlet
        //      imports referenced by the script).
        //    The .psm1 no longer sets `Set-StrictMode -Version Latest` at file
        //    scope (REFACTOR-6); strict mode is opted into per-function only.
        //    Running the psm1 body as a script therefore does not leak strict
        //    semantics into the global scope.
        var psm1Path = Path.Combine(baseDir, "PsBash.psm1");
        if (File.Exists(psm1Path))
        {
            var psm1Content = File.ReadAllText(psm1Path);
            pwsh.AddScript(psm1Content).Invoke();
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

/// <summary>
/// xUnit class fixture that creates ONE PsBash-loaded PowerShell instance per
/// test class and resets mutable state between tests. Use via
/// <c>IClassFixture&lt;SharedPwshFixture&gt;</c>.
///
/// Performance: ~2.5s saved per test compared to <see cref="PwshTestFixture.Create"/>.
///
/// See the MIGRATION RECIPE comment block on <see cref="PwshTestFixture"/> for
/// usage. Reference conversion lives in InvokeBashEnvCommandTests.cs.
/// </summary>
public class SharedPwshFixture : IDisposable
{
    private readonly PowerShell _pwsh;
    private readonly HashSet<string> _baselineVariableNames;
    private readonly string _baselinePwd;

    public SharedPwshFixture()
    {
        _pwsh = PwshTestFixture.CreateInternal();

        // Capture the post-module-load variable name set so Reset() can detect
        // and remove test-introduced variables without touching psm1 internals.
        _pwsh.Commands.Clear();
        var baseline = _pwsh.AddScript("Get-Variable -Scope Global | ForEach-Object { $_.Name }").Invoke();
        _baselineVariableNames = new HashSet<string>(
            baseline.Select(o => o?.ToString() ?? string.Empty),
            StringComparer.Ordinal);
        _pwsh.Commands.Clear();

        // Capture starting PWD so Reset() can restore it.
        var pwdResult = _pwsh.AddScript("(Get-Location).Path").Invoke();
        _baselinePwd = pwdResult.FirstOrDefault()?.ToString() ?? Environment.CurrentDirectory;
        _pwsh.Commands.Clear();
    }

    /// <summary>
    /// The shared PowerShell instance. Tests should NOT dispose this — the
    /// fixture owns its lifetime. Always call <see cref="AcquireFresh"/>
    /// instead of using this directly to ensure mutable state is reset.
    /// </summary>
    public PowerShell Pwsh => _pwsh;

    /// <summary>
    /// Resets mutable runspace state to the per-test baseline and returns the
    /// shared PowerShell instance. Equivalent in observable shape to a fresh
    /// <c>PwshTestFixture.Create()</c> but ~2.5s faster.
    ///
    /// Always call this at the start of each test (typically from the test
    /// class constructor — xUnit constructs the test class per-test).
    /// </summary>
    public PowerShell AcquireFresh()
    {
        Reset();
        return _pwsh;
    }

    /// <summary>
    /// Reset hook — clears mutable state that bash builtins / cmdlets stash
    /// in the global / variable scope plus the working directory. Does NOT
    /// reload the psm1 or re-import cmdlets (that's the entire point).
    /// </summary>
    public void Reset()
    {
        _pwsh.Commands.Clear();
        _pwsh.Streams.ClearStreams();

        // Build a script that:
        //   (a) clears LASTEXITCODE + bash-specific globals
        //   (b) removes any Global-scope variable not in the baseline set
        //   (c) restores PWD
        //   (d) clears $error
        //   (e) sets BashErrorMode -> PowerShell (most tests want this; tests
        //       that need 'Bash' mode can set it themselves after AcquireFresh)
        var baselineList = string.Join(",", _baselineVariableNames.Select(n => "'" + n.Replace("'", "''") + "'"));
        var pwdEscaped = _baselinePwd.Replace("'", "''");
        var resetScript = $@"
$global:LASTEXITCODE = $null
$global:BashPositional = $null
$global:BashFlags = $null
$global:BashBgLastPid = $null
$global:BashLastArg = $null
$baseline = @({baselineList})
$baselineSet = [System.Collections.Generic.HashSet[string]]::new($baseline, [System.StringComparer]::Ordinal)
Get-Variable -Scope Global -ErrorAction SilentlyContinue | ForEach-Object {{
    if (-not $baselineSet.Contains($_.Name)) {{
        Remove-Variable -Name $_.Name -Scope Global -Force -ErrorAction SilentlyContinue
    }}
}}
Set-Location -LiteralPath '{pwdEscaped}' -ErrorAction SilentlyContinue
$error.Clear()
try {{ Set-BashErrorMode -Mode PowerShell -ErrorAction SilentlyContinue }} catch {{ }}
";
        _pwsh.AddScript(resetScript).Invoke();
        _pwsh.Commands.Clear();
        _pwsh.Streams.ClearStreams();
    }

    public void Dispose()
    {
        try { _pwsh.Runspace?.Close(); } catch { }
        try { _pwsh.Runspace?.Dispose(); } catch { }
        try { _pwsh.Dispose(); } catch { }
    }
}
