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
///   - Location stack -> drained (pushd/popd/dirs share the runspace's default stack)
///   - $error.Clear()
///   - $global:ErrorActionPreference -> 'Continue' (runspace default; isolates
///     error-path tests that flip it)
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

    // Script that makes any installed PsBash undiscoverable for auto-loading and drops
    // any copy already pulled into the session. Run at fixture creation AND before every
    // test (AcquireFresh): PowerShell re-adds the default user module dir to
    // $env:PSModulePath per pipeline, and once any test auto-loads an installed PsBash it
    // stays loaded for the rest of that class's runspace — so a one-shot strip leaks.
    // Re-applying per test keeps the session clean. Auto-loading itself stays ENABLED
    // (the runtime relies on it); only PsBash-shipping path entries are removed.
    //
    // Without this, calling an Invoke-Bash* command mid-test auto-loads a
    // PSGallery/dev-installed PsBash whose psm1 *function* Invoke-Bash* shadows our binary
    // cmdlet (Function > Cmdlet precedence) and runs stale code — e.g. an old per-object
    // `Add-Member ToString` collides with the current type-level ToString ("Cannot force
    // the member ToString ... not an instance extension"), failing hundreds of tests on
    // any machine with an installed PsBash. C# counterpart of tests/EnsureCleanRunspace.ps1.
    private const string CleanInstalledPsBashScript = @"
        $sep = [System.IO.Path]::PathSeparator
        $kept = foreach ($dir in ($env:PSModulePath -split $sep)) {
            if ([string]::IsNullOrWhiteSpace($dir)) { continue }
            # Test-Path throws on an existing-but-unreadable dir (e.g. a root-owned copy on
            # a CI runner). Such a copy is unloadable anyway — keep the entry.
            $shipsPsBash = try { Test-Path (Join-Path $dir 'PsBash') -ErrorAction Stop } catch { $false }
            if ($shipsPsBash) { continue }
            $dir
        }
        $env:PSModulePath = ($kept | Where-Object { $_ }) -join $sep
        # Drop any installed PsBash *script* module a prior pipeline auto-loaded — its
        # functions are what shadow our binary cmdlets. Our source module is run as a
        # script body (never Import-Module'd), so nothing named 'PsBash' is legitimately
        # loaded; only an installed copy shows up. Do NOT touch 'PsBash.Cmdlets' — that
        # name belongs to OUR explicitly-imported binary DLL.
        while (Get-Module PsBash) { Get-Module PsBash | Remove-Module -Force -ErrorAction SilentlyContinue }
    ";

    // Public so SharedPwshFixture.AcquireFresh can re-apply it before each test.
    internal static void CleanInstalledPsBash(PowerShell pwsh)
    {
        pwsh.AddScript(CleanInstalledPsBashScript).Invoke();
        pwsh.Commands.Clear();
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

    // Runs one load step; any terminating exception or error record aborts with the step named.
    // `tolerateErrorRecords`: the psm1 is run as a SCRIPT body (see step 1), so its module-scope
    // probes of $PSScriptRoot emit a fixed set of benign non-terminating "Path is empty string"
    // records. Only a terminating failure (parse error, throw) aborts that step; the command
    // probe in CreateInternal is what proves the load actually registered the module.
    private static void RunLoadStep(PowerShell pwsh, string step, Func<PowerShell, PowerShell> build,
        bool tolerateErrorRecords = false)
    {
        pwsh.Commands.Clear();
        pwsh.Streams.ClearStreams();
        try
        {
            build(pwsh).Invoke();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"PwshTestFixture: '{step}' failed: {ex.Message}", ex);
        }
        var errors = pwsh.Streams.Error.Select(e => e.ToString()).ToArray();
        pwsh.Commands.Clear();
        pwsh.Streams.ClearStreams();
        if (errors.Length > 0 && !tolerateErrorRecords)
            throw new InvalidOperationException(
                $"PwshTestFixture: '{step}' reported errors: {string.Join(" | ", errors)}");
    }

    // Shared implementation used by both Create() and SharedPwshFixture.
    internal static PowerShell CreateInternal() => CreateInternal(AppContext.BaseDirectory);

    /// <summary>
    /// Files the fixture cannot run without. A missing one used to be skipped silently (the
    /// load steps were <c>if (File.Exists(...))</c>), so every test then ran against a bare
    /// runspace and failed with a confusing "command not found" — or passed vacuously. Now
    /// the fixture fails immediately and names the file.
    /// </summary>
    private static readonly string[] RequiredModuleFiles =
        { "PsBash.psm1", "PsBash.Cmdlets.dll", "PsBash.Format.ps1xml" };

    /// <summary>
    /// Builds the runspace from the module files in <paramref name="baseDir"/>. Throws
    /// <see cref="InvalidOperationException"/> naming the file / step when a required module
    /// file is missing or a load step fails — it never continues with a half-loaded module.
    /// (<paramref name="baseDir"/> is a parameter so the fail-fast paths are testable against
    /// a deliberately broken directory.)
    /// </summary>
    internal static PowerShell CreateInternal(string baseDir)
    {
        var missing = RequiredModuleFiles.Where(f => !File.Exists(Path.Combine(baseDir, f))).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"PwshTestFixture: required module file(s) missing from '{baseDir}': {string.Join(", ", missing)}. " +
                "Build the module (tman build) so the test output directory contains the PsBash module files.");

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

        // 0. Make any installed PsBash undiscoverable before loading our source module,
        //    so the module-load self-import and later test commands resolve to our copy.
        CleanInstalledPsBash(pwsh);

        try
        {
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
        RunLoadStep(pwsh, "load PsBash.psm1",
            p => p.AddScript(File.ReadAllText(Path.Combine(baseDir, "PsBash.psm1"))),
            tolerateErrorRecords: true);

        // 2. Load the binary module DLL directly.
        //    Import-Module on the .psd1 would fail due to RequiredModules / NestedModules
        //    referencing other manifests. Loading the DLL directly registers the cmdlets.
        RunLoadStep(pwsh, "Import-Module PsBash.Cmdlets.dll",
            p => p.AddCommand("Import-Module")
                  .AddParameter("Name", Path.Combine(baseDir, "PsBash.Cmdlets.dll"))
                  .AddParameter("ErrorAction", "Stop"));

        // 3. Import the format file so output formatting works correctly.
        RunLoadStep(pwsh, "Update-FormatData PsBash.Format.ps1xml",
            p => p.AddCommand("Update-FormatData")
                  .AddParameter("AppendPath", Path.Combine(baseDir, "PsBash.Format.ps1xml"))
                  .AddParameter("ErrorAction", "Stop"));

        // 4. Sanity probe: one command from each half of the module must resolve, or the
        //    load "succeeded" without registering anything (e.g. an empty psm1).
        foreach (var expected in new[] { "Emit-BashLine", "Invoke-BashEcho" })
        {
            pwsh.Commands.Clear();
            var found = pwsh.AddScript($"[bool](Get-Command '{expected}' -ErrorAction SilentlyContinue)").Invoke();
            pwsh.Commands.Clear();
            if (found.Count == 0 || found[0]?.BaseObject is not true)
                throw new InvalidOperationException(
                    $"PwshTestFixture: module loaded from '{baseDir}' but '{expected}' is not defined.");
        }
        }
        catch
        {
            // Never hand back (or leak) a half-loaded runspace.
            try { pwsh.Runspace?.Dispose(); } catch { }
            try { pwsh.Dispose(); } catch { }
            throw;
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

    /// <summary>
    /// The working directory the TEST PROCESS started in. Captured by a module
    /// initializer — before any test, any fixture, and any cwd-moving test — so it is
    /// the one value no test can have polluted.
    /// </summary>
    private static string _processStartPwd = Environment.CurrentDirectory;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void CaptureProcessStartPwd() => _processStartPwd = Environment.CurrentDirectory;

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

        // The baseline is the PROCESS-START working directory, captured once by
        // CaptureProcessStartPwd below — NOT this runspace's current location. Reset()
        // now writes Environment.CurrentDirectory process-wide, and a new runspace seeds
        // its location FROM that, so a fixture constructed while a serialized member of
        // another class sat in a temp dir used to capture that temp dir as "baseline" and
        // then restore every later test to it.
        _baselinePwd = _processStartPwd;
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
        // Re-clean per test: PowerShell re-adds the default user module dir to
        // PSModulePath per pipeline, and a previous test may have auto-loaded an
        // installed PsBash into this shared runspace. Re-stripping keeps each test
        // resolving Invoke-Bash* to our copy (see CleanInstalledPsBashScript).
        PwshTestFixture.CleanInstalledPsBash(_pwsh);
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
# Drain the default location stack so pushd/popd/dirs tests don't bleed
# state across the shared runspace. Bounded loop guards against infinite
# spin if Get-Location ever lies about a non-empty stack.
$stackGuard = 0
while ((Get-Location -Stack).Count -gt 0 -and $stackGuard -lt 64) {{
    Pop-Location -ErrorAction SilentlyContinue
    $stackGuard++
}}
$error.Clear()
# Reset $ErrorActionPreference to the runspace default so a test that flips it
# (e.g. to 'Stop' or 'Continue' for an error-path probe) cannot bleed into the
# next test sharing this runspace. This is what the per-test PwshTestFixture.Create()
# calls used to provide for free; resetting to the default keeps migrated error-path
# tests isolated. Setting to the default can only improve isolation.
$global:ErrorActionPreference = 'Continue'
try {{ Set-BashErrorMode -Mode PowerShell -ErrorAction SilentlyContinue }} catch {{ }}
";
        _pwsh.AddScript(resetScript).Invoke();
        _pwsh.Commands.Clear();
        _pwsh.Streams.ClearStreams();

        // A bash working directory is BOTH halves — the runspace location (restored
        // above) and the PROCESS-GLOBAL Environment.CurrentDirectory. Since
        // pushd/popd keep the two in sync, a test that moved the location left the
        // process cwd moved too, and unlike the runspace that leak is visible to
        // every other test in the assembly. Restore it here for the same reason the
        // location is restored. This write is itself process-global, so it would
        // race a class that is mid-test on the process cwd; that cannot happen
        // because ProcessWorkingDirectoryCollection disables parallelization and
        // runs alone, after every parallel collection has finished.
        RestoreProcessWorkingDirectory(_baselinePwd);
    }

    /// <summary>
    /// Point <see cref="Environment.CurrentDirectory"/> at <paramref name="path"/>,
    /// best-effort. Tests that <c>pushd</c> into a temp directory MUST call this (or
    /// <see cref="SharedPwshFixture.Reset"/>) before deleting that directory: on Windows
    /// a process cannot remove the directory it is sitting in, so the delete fails with
    /// a sharing violation that reads as an unrelated IOException.
    /// </summary>
    public static void RestoreProcessWorkingDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Environment.CurrentDirectory = path;
        }
        catch
        {
            // Vanished or inaccessible — leave the previous value.
        }
    }

    public void Dispose()
    {
        try { _pwsh.Runspace?.Close(); } catch { }
        try { _pwsh.Runspace?.Dispose(); } catch { }
        try { _pwsh.Dispose(); } catch { }
    }
}
