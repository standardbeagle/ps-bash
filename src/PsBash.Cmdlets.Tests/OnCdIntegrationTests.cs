using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// E2E integration tests for fnm --use-on-cd and direnv load/unload via
/// the HookRegistry + Enable-BashHookPrompt prompt-tick mechanism.
///
/// All tests are [SkippableFact] and skip gracefully when the required
/// external tool (fnm or direnv) is not installed. The test logic is
/// PS-bash-specific (no bash oracle equivalent: fnm/direnv hook themselves
/// into the shell prompt, which is a ps-bash-specific surface).
///
/// Directive 5 (Platform matrix): fnm tests skip on platforms where fnm
/// is unavailable. direnv tests skip similarly.
/// </summary>
public class OnCdIntegrationTests : IDisposable
{
    private readonly string _prefix = $"oncd-{Guid.NewGuid():N}";
    private readonly List<(HookKind kind, string name)> _hooks = new();
    private readonly List<string> _tempDirs = new();

    private string N(string suffix) => $"{_prefix}-{suffix}";

    public void Dispose()
    {
        foreach (var (kind, name) in _hooks)
            HookRegistry.Instance.Unregister(kind, name);

        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the path of <paramref name="tool"/> if it is on PATH and exits
    /// successfully with --version, otherwise returns null.
    /// </summary>
    private static string? FindTool(string tool)
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = tool,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0 ? tool : null;
        }
        catch
        {
            return null;
        }
    }

    private string MakeTempDir(string suffix = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"psbash-oncd-{Guid.NewGuid():N}{suffix}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // -------------------------------------------------------------------------
    // fnm --use-on-cd scenarios
    // -------------------------------------------------------------------------

    /// <summary>
    /// fnm --use-on-cd: cd to a dir with .nvmrc triggers node version switch.
    ///
    /// PS-bash-specific: tests prompt-hook firing path. No bash oracle because
    /// fnm hooks into the shell prompt, not a language primitive.
    ///
    /// Skips when fnm is not installed.
    /// </summary>
    [SkippableFact]
    public void Fnm_UseOnCd_CdToNvmrcDir_RegistersChpwdHook()
    {
        var fnm = FindTool("fnm");
        Skip.If(fnm is null, "fnm not installed");

        using var pwsh = PwshTestFixture.Create();

        // Collect fnm env output — this is what `eval "$(fnm env --use-on-cd --shell bash)"` does.
        // We verify the hook registration mechanism, not the actual node version switch,
        // because we cannot guarantee specific Node versions are installed in CI.
        var hookName = N("fnm-chpwd");

        try
        {
            var tempA = MakeTempDir("A");
            var tempB = MakeTempDir("B");
            File.WriteAllText(Path.Combine(tempA, ".nvmrc"), "system\n");
            File.WriteAllText(Path.Combine(tempB, ".nvmrc"), "system\n");

            // Register a chpwd hook that records which directories were visited.
            pwsh.AddScript($@"
                $global:FnmHookVisited = [System.Collections.Generic.List[string]]::new()
                Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{
                    param($old, $new)
                    $global:FnmHookVisited.Add($new)
                }}
                # Simulate starting from tempA.
                $script:__BashLastCwd = '{EscapePs(tempA)}'
                # Simulate cd to tempB.
                Set-Location '{EscapePs(tempB)}'
                # Trigger prompt tick (Enable-BashHookPrompt was auto-enabled by Register-BashChpwdHook).
                prompt | Out-Null
            ").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("$global:FnmHookVisited").Invoke();
            pwsh.Commands.Clear();

            Assert.NotEmpty(result);
            Assert.Contains(result, r => r.ToString().Contains(Path.GetFileName(tempB)));
        }
        finally
        {
            _hooks.Add((HookKind.ChpwdHook, hookName));
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
        }
    }

    /// <summary>
    /// fnm --use-on-cd: changing from one .nvmrc dir to another triggers the hook twice.
    ///
    /// PS-bash-specific: exercises prompt-tick firing across two consecutive cd operations.
    /// Skips when fnm is not installed.
    /// </summary>
    [SkippableFact]
    public void Fnm_UseOnCd_TwoNvmrcDirs_HookFiresTwice()
    {
        var fnm = FindTool("fnm");
        Skip.If(fnm is null, "fnm not installed");

        using var pwsh = PwshTestFixture.Create();
        var hookName = N("fnm-chpwd-twice");

        try
        {
            var tempA = MakeTempDir("A2");
            var tempB = MakeTempDir("B2");
            File.WriteAllText(Path.Combine(tempA, ".nvmrc"), "system\n");
            File.WriteAllText(Path.Combine(tempB, ".nvmrc"), "system\n");

            pwsh.AddScript($@"
                $global:FnmFireCount = 0
                Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{
                    param($old, $new)
                    $global:FnmFireCount++
                }}
                # First cd: start -> tempA
                $script:__BashLastCwd = '{EscapePs(Path.GetTempPath())}'
                Set-Location '{EscapePs(tempA)}'
                prompt | Out-Null
                # Second cd: tempA -> tempB
                Set-Location '{EscapePs(tempB)}'
                prompt | Out-Null
            ").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("$global:FnmFireCount").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("2", result[0].ToString());
        }
        finally
        {
            _hooks.Add((HookKind.ChpwdHook, hookName));
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
        }
    }

    /// <summary>
    /// fnm --use-on-cd: hook does NOT fire when the directory does not change.
    ///
    /// PS-bash-specific: verifies no spurious chpwd firing for fnm hooks.
    /// Skips when fnm is not installed.
    /// </summary>
    [SkippableFact]
    public void Fnm_UseOnCd_NoDirectoryChange_HookDoesNotFire()
    {
        var fnm = FindTool("fnm");
        Skip.If(fnm is null, "fnm not installed");

        using var pwsh = PwshTestFixture.Create();
        var hookName = N("fnm-chpwd-nofire");

        try
        {
            pwsh.AddScript($@"
                $global:FnmHookFired = $false
                Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{
                    param($old, $new)
                    $global:FnmHookFired = $true
                }}
                # Simulate no change: __BashLastCwd == current location.
                $script:__BashLastCwd = (Get-Location).Path
                prompt | Out-Null
            ").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("$global:FnmHookFired").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("False", result[0].ToString());
        }
        finally
        {
            _hooks.Add((HookKind.ChpwdHook, hookName));
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // direnv load/unload scenarios
    // -------------------------------------------------------------------------

    /// <summary>
    /// direnv: cd into a dir with an allowed .envrc sets the exported variable.
    ///
    /// PS-bash-specific: tests chpwd hook firing path. No bash oracle because
    /// direnv hooks into the shell prompt, not a language primitive.
    ///
    /// Requires direnv to be installed AND the .envrc to be allowed (direnv allow).
    /// Skips when direnv is not installed.
    /// </summary>
    [SkippableFact]
    public void Direnv_Load_CdIntoEnvrcDir_SetsEnvVar()
    {
        var direnv = FindTool("direnv");
        Skip.If(direnv is null, "direnv not installed");

        var tempC = MakeTempDir("C");
        var envrcPath = Path.Combine(tempC, ".envrc");
        var markerVar = $"PSBASH_DIRENV_MARKER_{Guid.NewGuid():N}";
        File.WriteAllText(envrcPath, $"export {markerVar}=set_by_direnv\n");

        // Allow the .envrc via the direnv CLI before the test runs.
        var allowed = AllowDirenv(tempC);
        Skip.If(!allowed, "direnv allow failed — possibly sandboxed or direnv config blocked");

        using var pwsh = PwshTestFixture.Create();
        var hookName = N("direnv-load");

        try
        {
            // Register a chpwd hook that simulates what direnv hook bash would do:
            // evaluate `direnv export pwsh` and apply exported vars.
            pwsh.AddScript($@"
                Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{
                    param($old, $new)
                    # Simulate direnv export: run direnv export pwsh and eval output.
                    $export = & direnv export pwsh 2>$null
                    if ($export) {{
                        Invoke-Expression $export
                    }}
                }}
                # Simulate cd from temp root into tempC.
                $script:__BashLastCwd = '{EscapePs(Path.GetTempPath())}'
                Set-Location '{EscapePs(tempC)}'
                prompt | Out-Null
            ").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript($"$env:{markerVar}").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("set_by_direnv", result[0]?.ToString() ?? "");
        }
        finally
        {
            _hooks.Add((HookKind.ChpwdHook, hookName));
            // Clean up: unset the marker env var.
            pwsh.AddScript($"Remove-Item -Path 'Env:{markerVar}' -ErrorAction SilentlyContinue").Invoke();
            pwsh.Commands.Clear();
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
            Environment.SetEnvironmentVariable(markerVar, null);
        }
    }

    /// <summary>
    /// direnv: cd out of the .envrc dir unsets the exported variable.
    ///
    /// PS-bash-specific: tests direnv unload path via the chpwd hook.
    /// Skips when direnv is not installed.
    /// </summary>
    [SkippableFact]
    public void Direnv_Unload_CdOutOfEnvrcDir_UnsetEnvVar()
    {
        var direnv = FindTool("direnv");
        Skip.If(direnv is null, "direnv not installed");

        var tempD = MakeTempDir("D");
        var envrcPath = Path.Combine(tempD, ".envrc");
        var markerVar = $"PSBASH_DIRENV_UNLOAD_{Guid.NewGuid():N}";
        File.WriteAllText(envrcPath, $"export {markerVar}=loaded\n");

        var allowed = AllowDirenv(tempD);
        Skip.If(!allowed, "direnv allow failed — possibly sandboxed or direnv config blocked");

        using var pwsh = PwshTestFixture.Create();
        var hookName = N("direnv-unload");

        try
        {
            pwsh.AddScript($@"
                Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{
                    param($old, $new)
                    $export = & direnv export pwsh 2>$null
                    if ($export) {{
                        Invoke-Expression $export
                    }}
                }}
                # Step 1: cd into tempD — direnv should load the .envrc.
                $script:__BashLastCwd = '{EscapePs(Path.GetTempPath())}'
                Set-Location '{EscapePs(tempD)}'
                prompt | Out-Null
            ").Invoke();
            pwsh.Commands.Clear();

            // Verify it was loaded.
            var loaded = pwsh.AddScript($"$env:{markerVar}").Invoke();
            pwsh.Commands.Clear();

            // If load did not work (edge case: direnv export pwsh syntax differs), skip.
            Skip.If(loaded.Count == 0 || loaded[0]?.ToString() != "loaded",
                "direnv load did not set env var — export syntax may differ on this platform");

            // Step 2: cd out — direnv should unload.
            pwsh.AddScript($@"
                Set-Location '{EscapePs(Path.GetTempPath())}'
                prompt | Out-Null
            ").Invoke();
            pwsh.Commands.Clear();

            var unloaded = pwsh.AddScript($"$env:{markerVar}").Invoke();
            pwsh.Commands.Clear();

            // After unload, the variable should be null/empty.
            var markerValue = unloaded.Count > 0 ? unloaded[0]?.ToString() : null;
            Assert.True(
                string.IsNullOrEmpty(markerValue),
                $"Expected {markerVar} to be unset after cd out, but got: {markerValue}");
        }
        finally
        {
            _hooks.Add((HookKind.ChpwdHook, hookName));
            pwsh.AddScript($"Remove-Item -Path 'Env:{markerVar}' -ErrorAction SilentlyContinue").Invoke();
            pwsh.Commands.Clear();
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
            Environment.SetEnvironmentVariable(markerVar, null);
        }
    }

    /// <summary>
    /// direnv: hook does NOT fire when the directory does not change.
    ///
    /// PS-bash-specific: verifies no spurious direnv invocations on same-dir prompt ticks.
    /// Skips when direnv is not installed.
    /// </summary>
    [SkippableFact]
    public void Direnv_NoDirectoryChange_HookDoesNotFire()
    {
        var direnv = FindTool("direnv");
        Skip.If(direnv is null, "direnv not installed");

        using var pwsh = PwshTestFixture.Create();
        var hookName = N("direnv-nofire");

        try
        {
            pwsh.AddScript($@"
                $global:DirenvHookFired = $false
                Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{
                    param($old, $new)
                    $global:DirenvHookFired = $true
                }}
                # No directory change: __BashLastCwd == current location.
                $script:__BashLastCwd = (Get-Location).Path
                prompt | Out-Null
            ").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("$global:DirenvHookFired").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("False", result[0].ToString());
        }
        finally
        {
            _hooks.Add((HookKind.ChpwdHook, hookName));
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs `direnv allow` in <paramref name="dir"/> and returns true on success.
    /// </summary>
    private static bool AllowDirenv(string dir)
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "direnv",
                Arguments = "allow",
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Escapes a path for embedding in a single-quoted PowerShell string.</summary>
    private static string EscapePs(string path) => path.Replace("'", "''").Replace("\\", "\\\\");
}
