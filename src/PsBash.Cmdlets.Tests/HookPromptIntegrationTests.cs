using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Tests for Enable-BashHookPrompt / Disable-BashHookPrompt prompt-wrapper functions.
///
/// Design notes:
/// - The psm1 is loaded as a script (not as a module) in the test fixture, so
///   $script: variables resolve to the global scope in this context.
/// - Each test uses a unique hook-name prefix to avoid collisions with the
///   process-global HookRegistry singleton.
/// - Tests clean up registered hooks in finally blocks.
/// - The 'prompt' function is restored (or removed) after each test to avoid
///   cross-test contamination.
/// </summary>
public class HookPromptIntegrationTests : IDisposable
{
    private readonly string _prefix = $"hpi-{Guid.NewGuid():N}";
    private readonly List<(HookKind kind, string name)> _hooks = new();

    private string N(string suffix) => $"{_prefix}-{suffix}";

    public void Dispose()
    {
        foreach (var (kind, name) in _hooks)
            HookRegistry.Instance.Unregister(kind, name);
    }

    // -------------------------------------------------------------------------
    // Enable / Disable idempotency
    // -------------------------------------------------------------------------

    [Fact]
    public void EnableBashHookPrompt_Idempotent_DoesNotDoubleWrap()
    {
        // PS-bash-specific test: no bash oracle equivalent for prompt-function wrapping.
        using var pwsh = PwshTestFixture.Create();
        try
        {
            // Capture pre-enable prompt text as baseline.
            pwsh.AddScript("Enable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();

            var before = pwsh.AddScript("(Get-Item Function:\\prompt).ScriptBlock.ToString()").Invoke();
            pwsh.Commands.Clear();

            // Call again — must be a no-op.
            pwsh.AddScript("Enable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();

            var after = pwsh.AddScript("(Get-Item Function:\\prompt).ScriptBlock.ToString()").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(before);
            Assert.Single(after);
            // Same wrapper installed — function body unchanged.
            Assert.Equal(before[0].ToString(), after[0].ToString());
        }
        finally
        {
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
        }
    }

    [Fact]
    public void DisableBashHookPrompt_WhenNotEnabled_IsNoOp()
    {
        // PS-bash-specific: tests module state, no bash oracle.
        using var pwsh = PwshTestFixture.Create();

        // Ensure it's not enabled first.
        pwsh.AddScript("$script:__BashHookPromptEnabled = $false").Invoke();
        pwsh.Commands.Clear();

        // Must not throw a terminating exception. Non-terminating errors from
        // strict-mode variable lookups in the psm1 loader are outside scope here.
        var ex = Record.Exception(() =>
        {
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
        });
        Assert.Null(ex);
    }

    // -------------------------------------------------------------------------
    // Original prompt preserved
    // -------------------------------------------------------------------------

    [Fact]
    public void EnableBashHookPrompt_OriginalPromptOutput_IsPreserved()
    {
        // PS-bash-specific: verifies prompt delegation (no bash equivalent).
        using var pwsh = PwshTestFixture.Create();
        try
        {
            // Install a known original prompt.
            pwsh.AddScript("function global:prompt { 'ORIGINAL> ' }").Invoke();
            pwsh.Commands.Clear();

            pwsh.AddScript("Enable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();

            // Invoking the wrapper should return the original's output.
            var result = pwsh.AddScript("prompt").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("ORIGINAL> ", result[0].ToString());
        }
        finally
        {
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
        }
    }

    [Fact]
    public void DisableBashHookPrompt_RestoresOriginalPrompt()
    {
        // PS-bash-specific: verifies prompt restoration.
        using var pwsh = PwshTestFixture.Create();

        // Install a known original prompt.
        pwsh.AddScript("function global:prompt { 'BEFORE> ' }").Invoke();
        pwsh.Commands.Clear();

        pwsh.AddScript("Enable-BashHookPrompt").Invoke();
        pwsh.Commands.Clear();

        pwsh.AddScript("Disable-BashHookPrompt").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript("prompt").Invoke();
        pwsh.Commands.Clear();

        Assert.Single(result);
        Assert.Equal("BEFORE> ", result[0].ToString());
    }

    // -------------------------------------------------------------------------
    // ChpwdHook fires on directory change
    // -------------------------------------------------------------------------

    [Fact]
    public void ChpwdHook_FiresOnDirectoryChange_ViaPromptWrapper()
    {
        // PS-bash-specific: tests prompt-hook firing (no bash oracle; bash chpwd fires immediately on cd).
        using var pwsh = PwshTestFixture.Create();
        var hookName = N("chpwd-fires");

        try
        {
            var tempDir1 = Path.Combine(Path.GetTempPath(), $"psbash-test-{Guid.NewGuid():N}");
            var tempDir2 = Path.Combine(Path.GetTempPath(), $"psbash-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir1);
            Directory.CreateDirectory(tempDir2);

            try
            {
                pwsh.AddScript($@"
                    $global:BashHookFired = $null
                    Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{
                        param($old, $new)
                        $global:BashHookFired = ""$old->$new""
                    }}
                    # Simulate being in tempDir1 as starting point.
                    $script:__BashLastCwd = '{tempDir1.Replace("\\", "\\\\")}'
                    # Simulate a directory change.
                    Set-Location '{tempDir2.Replace("\\", "\\\\")}'
                    # Invoke the prompt wrapper to trigger hook firing.
                    prompt | Out-Null
                ").Invoke();
                pwsh.Commands.Clear();

                var result = pwsh.AddScript("$global:BashHookFired").Invoke();
                pwsh.Commands.Clear();

                Assert.Single(result);
                var fired = result[0]?.ToString() ?? "";
                Assert.Contains("->", fired);
                Assert.EndsWith(tempDir2, fired);
            }
            finally
            {
                Directory.Delete(tempDir1, recursive: true);
                Directory.Delete(tempDir2, recursive: true);
            }
        }
        finally
        {
            _hooks.Add((HookKind.ChpwdHook, hookName));
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
        }
    }

    [Fact]
    public void ChpwdHook_DoesNotFire_WhenPathUnchanged()
    {
        // PS-bash-specific: verifies no spurious chpwd firing.
        using var pwsh = PwshTestFixture.Create();
        var hookName = N("chpwd-nofire");

        try
        {
            pwsh.AddScript($@"
                $global:BashHookFired = $false
                Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{
                    param($old, $new)
                    $global:BashHookFired = $true
                }}
                # Set last CWD to current directory so no change is detected.
                $script:__BashLastCwd = (Get-Location).Path
                # Invoke prompt wrapper — chpwd must NOT fire.
                prompt | Out-Null
            ").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("$global:BashHookFired").Invoke();
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
    // Disable stops hook firing
    // -------------------------------------------------------------------------

    [Fact]
    public void Disable_SubsequentDirectoryChange_DoesNotFireHook()
    {
        // PS-bash-specific: verifies disable semantics.
        using var pwsh = PwshTestFixture.Create();
        var hookName = N("chpwd-after-disable");

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"psbash-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                pwsh.AddScript($@"
                    $global:BashHookFired = $false
                    Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{
                        param($old, $new)
                        $global:BashHookFired = $true
                    }}
                    Disable-BashHookPrompt
                    # After disable, the original (or default) prompt is active.
                    # Manually simulate what FirePrompt would do — but it should NOT be called.
                    # Instead just verify the hook does NOT fire through the prompt wrapper.
                    $script:__BashLastCwd = 'C:\doesnotexist'
                    Set-Location '{tempDir.Replace("\\", "\\\\")}'
                    # Invoke prompt — this is now the original prompt, not the wrapper.
                    prompt | Out-Null
                ").Invoke();
                pwsh.Commands.Clear();

                var result = pwsh.AddScript("$global:BashHookFired").Invoke();
                pwsh.Commands.Clear();

                Assert.Single(result);
                // Hook should NOT have fired — the wrapper was disabled.
                Assert.Equal("False", result[0].ToString());
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        finally
        {
            _hooks.Add((HookKind.ChpwdHook, hookName));
        }
    }

    // -------------------------------------------------------------------------
    // Auto-enable via Register-BashChpwdHook
    // -------------------------------------------------------------------------

    [Fact]
    public void RegisterBashChpwdHook_AutoEnablesPromptWrapper()
    {
        // PS-bash-specific: verifies auto-enable side effect.
        using var pwsh = PwshTestFixture.Create();
        var hookName = N("auto-enable");

        try
        {
            // Ensure disabled to start.
            pwsh.AddScript("$script:__BashHookPromptEnabled = $false").Invoke();
            pwsh.Commands.Clear();

            pwsh.AddScript($"Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{ }}").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("$script:__BashHookPromptEnabled").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("True", result[0].ToString());
        }
        finally
        {
            _hooks.Add((HookKind.ChpwdHook, hookName));
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
        }
    }

    [Fact]
    public void RegisterBashPromptHook_AutoEnablesPromptWrapper()
    {
        // PS-bash-specific: verifies auto-enable side effect for prompt hooks.
        using var pwsh = PwshTestFixture.Create();
        var hookName = N("auto-enable-prompt");

        try
        {
            // Ensure disabled to start.
            pwsh.AddScript("$script:__BashHookPromptEnabled = $false").Invoke();
            pwsh.Commands.Clear();

            pwsh.AddScript($"Register-BashPromptHook -Name '{hookName}' -ScriptBlock {{ }}").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("$script:__BashHookPromptEnabled").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("True", result[0].ToString());
        }
        finally
        {
            _hooks.Add((HookKind.PromptHook, hookName));
            pwsh.AddScript("Disable-BashHookPrompt").Invoke();
            pwsh.Commands.Clear();
        }
    }
}
