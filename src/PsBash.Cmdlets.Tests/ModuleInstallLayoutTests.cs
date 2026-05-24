using System.Diagnostics;
using System.Management.Automation;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Guards the "Install-Module PsBash" package layout — the failure mode where the psm1 registers
/// aliases (ls -> Invoke-BashLs) but the binary cmdlets in PsBash.Cmdlets.dll never load, so
/// `ls` reports "Invoke-BashLs: not recognized" and the [PsBash.Cmdlets.BashRuntime] helpers
/// the psm1 calls are missing.
///
/// Why this was missed: PwshTestFixture loads PsBash.Cmdlets.dll EXPLICITLY (its own step), so no
/// prior test exercised the psm1's own $PSScriptRoot probe — the thing that runs under a real
/// Install-Module. These tests stage a PACKAGE-LAYOUT module dir and import it via the psm1's
/// own loading in an ISOLATED pwsh, mirroring PSGallery.
///
/// Oracle note (qa-rubric Directive 1): module-load layout is ps-bash-specific, no bash oracle.
/// </summary>
public class ModuleInstallLayoutTests
{
    private static readonly string[] PackageFiles =
        ["PsBash.psd1", "PsBash.psm1", "PsBash.Format.ps1xml", "BashFlagSpecs.json"];
    private static readonly string[] RuntimeDlls =
        ["PsBash.Cmdlets.dll", "PsBash.Transpiler.dll", "Parlot.dll"];

    private static string? FindPwsh()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe", "pwsh" } : new[] { "pwsh" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var name in names)
            {
                try
                {
                    var full = Path.Combine(dir, name);
                    if (File.Exists(full)) return full;
                }
                catch { /* malformed PATH entry */ }
            }
        }

        return null;
    }

    /// <summary>Stage a PsBash module dir under {root}/Modules/PsBash mirroring the PSGallery package.</summary>
    private static string StageModules(string root, bool includeDll)
    {
        var modules = Path.Combine(root, "Modules");
        var moduleDir = Path.Combine(modules, "PsBash");
        Directory.CreateDirectory(moduleDir);
        var bin = AppContext.BaseDirectory;

        foreach (var f in PackageFiles)
        {
            var src = Path.Combine(bin, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(moduleDir, f), overwrite: true);
        }

        if (includeDll)
        {
            foreach (var f in RuntimeDlls)
            {
                var src = Path.Combine(bin, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(moduleDir, f), overwrite: true);
            }
        }

        return modules;
    }

    private static (string Stdout, string Stderr, int Exit) RunPwsh(string pwsh, string modulesDir, string script)
    {
        var psi = new ProcessStartInfo(pwsh)
        {
            // Redirect ALL three streams + no window: the child must be fully detached from the
            // parent's console/PTY. Sharing stdin with an interactive host's PTY lets the child
            // grab/close it and take the terminal down (process-spawn-in-PTY hazard).
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        // Isolate the module path to the staged dir + pwsh's built-ins ($PSHOME/Modules) ONLY —
        // NOT the machine's user/system module dirs, or an already-installed PsBash.Cmdlets would
        // satisfy the import and mask the missing-DLL case the negative test exercises.
        var escaped = modulesDir.Replace("'", "''");
        var fullScript =
            $"$env:PSModulePath = '{escaped}' + [System.IO.Path]::PathSeparator + (Join-Path $PSHOME 'Modules'); "
            + script;
        psi.ArgumentList.Add(fullScript);

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close(); // EOF immediately — the child never reads the parent's stdin.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("pwsh Import-Module did not finish in 60s.");
        }

        return (outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult(), proc.ExitCode);
    }

    [SkippableFact]
    public void PackageLayout_WithBundledDll_LoadsMigratedCmdletsAndAliases()
    {
        var pwsh = FindPwsh();
        Skip.If(pwsh is null, "pwsh not on PATH");

        var temp = Path.Combine(Path.GetTempPath(), "psbash-pkg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var modules = StageModules(temp, includeDll: true);
            // Import via the psm1's OWN probe (not the test fixture's explicit DLL load).
            var script =
                "Import-Module PsBash -Force -ErrorAction Stop; " +
                "'CMDLET=' + (Get-Command Invoke-BashLs -ErrorAction SilentlyContinue).Name; " +
                "'ALIAS=' + (Get-Alias ls -ErrorAction SilentlyContinue).ResolvedCommandName";

            var (stdout, stderr, exit) = RunPwsh(pwsh!, modules, script);

            Assert.True(exit == 0, $"pwsh exited {exit}. stderr: {stderr}");
            Assert.Contains("CMDLET=Invoke-BashLs", stdout);
            Assert.Contains("ALIAS=Invoke-BashLs", stdout);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void PackageLayout_MissingDll_WarnsLoudly_NeverSilent()
    {
        var pwsh = FindPwsh();
        Skip.If(pwsh is null, "pwsh not on PATH");

        var temp = Path.Combine(Path.GetTempPath(), "psbash-nodll-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var modules = StageModules(temp, includeDll: false);
            var script =
                "Import-Module PsBash -Force -WarningVariable w -WarningAction SilentlyContinue -ErrorAction Stop; " +
                "'WARN=' + ($w -join ' '); " +
                "'CMDLET=' + (Get-Command Invoke-BashLs -ErrorAction SilentlyContinue).Name";

            var (stdout, stderr, exit) = RunPwsh(pwsh!, modules, script);

            Assert.True(exit == 0, $"pwsh exited {exit}. stderr: {stderr}");
            // The whole point: a broken (DLL-less) install must announce itself, not fail silently.
            Assert.Contains("PsBash.Cmdlets.dll not found", stdout);
            // And the migrated cmdlet is genuinely absent in that state (no resolved name printed).
            Assert.DoesNotContain("CMDLET=Invoke-BashLs", stdout);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EveryInvokeBashAlias_ResolvesToAFunctionOrCmdlet()
    {
        // "Similar issue" guard: an alias whose Invoke-Bash* target exists nowhere (typo, deleted
        // function, un-migrated cmdlet) is a dangling alias — the same shape as the shipped bug.
        var psm1 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PsBash.psm1"));

        var psm1Functions = new HashSet<string>(
            Regex.Matches(psm1, @"(?m)^\s*function\s+(Invoke-Bash[\w-]+)").Select(m => m.Groups[1].Value),
            StringComparer.OrdinalIgnoreCase);

        var cmdlets = new HashSet<string>(
            typeof(PsBash.Cmdlets.BashRuntime).Assembly.GetTypes()
                .Select(t => t.GetCustomAttribute<CmdletAttribute>())
                .Where(a => a is not null)
                .Select(a => $"{a!.VerbName}-{a.NounName}"),
            StringComparer.OrdinalIgnoreCase);

        var aliasTargets = Regex.Matches(psm1, @"Set-Alias\b[^\r\n]*?(Invoke-Bash[\w-]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(aliasTargets); // sanity: we actually parsed some aliases

        var dangling = aliasTargets
            .Where(t => !psm1Functions.Contains(t) && !cmdlets.Contains(t))
            .ToList();

        Assert.True(dangling.Count == 0,
            "Set-Alias targets with no psm1 function and no PsBash.Cmdlets cmdlet (dangling aliases): "
            + string.Join(", ", dangling));
    }
}
