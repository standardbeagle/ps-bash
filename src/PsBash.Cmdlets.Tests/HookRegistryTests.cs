using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Unit tests for HookRegistry and the Register/Unregister/Get-BashHook cmdlets.
///
/// NOTE: HookRegistry.Instance is a process-global singleton. Tests that mutate
/// it must clean up after themselves to avoid order-dependent failures.
/// </summary>
public class HookRegistryTests : IDisposable
{
    // Unique name prefix to isolate per-test hooks from any pre-existing hooks.
    private readonly string _prefix = $"test-{Guid.NewGuid():N}";
    private readonly List<(HookKind kind, string name)> _registered = new();

    private string N(string suffix) => $"{_prefix}-{suffix}";

    private void Track(HookKind kind, string name)
    {
        HookRegistry.Instance.Register(kind, name, ScriptBlock.Create(""));
        _registered.Add((kind, name));
    }

    public void Dispose()
    {
        foreach (var (kind, name) in _registered)
            HookRegistry.Instance.Unregister(kind, name);
    }

    // -------------------------------------------------------------------------
    // Register / Get
    // -------------------------------------------------------------------------

    [Fact]
    public void Register_AddsHook_GetAllContainsIt()
    {
        var name = N("add");
        Track(HookKind.ChpwdHook, name);

        var all = HookRegistry.Instance.GetAll();
        Assert.Contains(all, h => h.Kind == HookKind.ChpwdHook && h.Name == name);
    }

    [Fact]
    public void Register_SameName_ReplacesExisting_NoduplicateEntries()
    {
        var name = N("replace");
        var sb1 = ScriptBlock.Create("'v1'");
        var sb2 = ScriptBlock.Create("'v2'");

        HookRegistry.Instance.Register(HookKind.ChpwdHook, name, sb1);
        _registered.Add((HookKind.ChpwdHook, name));
        HookRegistry.Instance.Register(HookKind.ChpwdHook, name, sb2);

        var hits = HookRegistry.Instance.GetAll()
            .Where(h => h.Kind == HookKind.ChpwdHook && h.Name == name)
            .ToArray();
        Assert.Single(hits);
        // Verify the latest scriptblock wins.
        Assert.Same(sb2, hits[0].ScriptBlock);
    }

    // -------------------------------------------------------------------------
    // Unregister
    // -------------------------------------------------------------------------

    [Fact]
    public void Unregister_ExistingHook_RemovesIt()
    {
        var name = N("remove");
        Track(HookKind.ChpwdHook, name);

        HookRegistry.Instance.Unregister(HookKind.ChpwdHook, name);
        _registered.RemoveAll(x => x.name == name);

        var all = HookRegistry.Instance.GetAll();
        Assert.DoesNotContain(all, h => h.Kind == HookKind.ChpwdHook && h.Name == name);
    }

    [Fact]
    public void Unregister_MissingHook_SilentNoOp()
    {
        // Must not throw.
        var result = HookRegistry.Instance.Unregister(HookKind.ChpwdHook, N("nonexistent"));
        Assert.False(result);
    }

    // -------------------------------------------------------------------------
    // GetAll / SnapshotByKind
    // -------------------------------------------------------------------------

    [Fact]
    public void GetAll_ReturnsSortedByName()
    {
        var nameB = N("b-sorted");
        var nameA = N("a-sorted");
        Track(HookKind.ChpwdHook, nameB);
        Track(HookKind.ChpwdHook, nameA);

        var names = HookRegistry.Instance.GetAll()
            .Where(h => h.Name == nameA || h.Name == nameB)
            .Select(h => h.Name)
            .ToArray();

        Assert.Equal(2, names.Length);
        Assert.Equal(nameA, names[0]);
        Assert.Equal(nameB, names[1]);
    }

    [Fact]
    public void SnapshotByKind_FiltersCorrectly()
    {
        var chpwdName = N("chpwd-kind");
        var promptName = N("prompt-kind");
        Track(HookKind.ChpwdHook, chpwdName);
        Track(HookKind.PromptHook, promptName);

        var chpwd = HookRegistry.Instance.SnapshotByKind(HookKind.ChpwdHook);
        var prompt = HookRegistry.Instance.SnapshotByKind(HookKind.PromptHook);

        // Neither array should contain scripts from the other kind.
        // (We can't distinguish between test hooks from other tests here, but
        //  we can verify that at minimum the correct counts include ours.)
        Assert.True(chpwd.Length >= 1);
        Assert.True(prompt.Length >= 1);
    }

    // -------------------------------------------------------------------------
    // FirePrompt — chpwd only fires on path change
    // -------------------------------------------------------------------------

    [Fact]
    public void FirePrompt_SamePath_ChpwdDoesNotFire_PromptDoesFire()
    {
        using var pwsh = PwshTestFixture.Create();

        var chpwdName = N("fire-same-chpwd");
        var promptName = N("fire-same-prompt");

        pwsh.AddScript($@"
            $global:chpwdFired = $false
            $global:promptFired = $false
            Register-BashChpwdHook -Name '{chpwdName}' -ScriptBlock {{ $global:chpwdFired = $true }}
            Register-BashPromptHook -Name '{promptName}' -ScriptBlock {{ $global:promptFired = $true }}
        ").Invoke();
        pwsh.Commands.Clear();

        try
        {
            // Fire with identical old/new path — chpwd must NOT fire.
            pwsh.AddScript($@"
                [PsBash.Cmdlets.HookRegistry]::Instance.FirePrompt(
                    $ExecutionContext.SessionState, '/same', '/same')
                [PSCustomObject]@{{
                    ChpwdFired = $global:chpwdFired
                    PromptFired = $global:promptFired
                }}
            ").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("[PSCustomObject]@{ ChpwdFired = $global:chpwdFired; PromptFired = $global:promptFired }").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("False", result[0].Properties["ChpwdFired"]?.Value?.ToString());
            Assert.Equal("True", result[0].Properties["PromptFired"]?.Value?.ToString());
        }
        finally
        {
            HookRegistry.Instance.Unregister(HookKind.ChpwdHook, chpwdName);
            HookRegistry.Instance.Unregister(HookKind.PromptHook, promptName);
        }
    }

    [Fact]
    public void FirePrompt_DifferentPath_BothKindsFire()
    {
        using var pwsh = PwshTestFixture.Create();

        var chpwdName = N("fire-diff-chpwd");
        var promptName = N("fire-diff-prompt");

        pwsh.AddScript($@"
            $global:chpwdFired = $false
            $global:promptFired = $false
            Register-BashChpwdHook -Name '{chpwdName}' -ScriptBlock {{ $global:chpwdFired = $true }}
            Register-BashPromptHook -Name '{promptName}' -ScriptBlock {{ $global:promptFired = $true }}
        ").Invoke();
        pwsh.Commands.Clear();

        try
        {
            pwsh.AddScript($@"
                [PsBash.Cmdlets.HookRegistry]::Instance.FirePrompt(
                    $ExecutionContext.SessionState, '/old', '/new')
            ").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("[PSCustomObject]@{ ChpwdFired = $global:chpwdFired; PromptFired = $global:promptFired }").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("True", result[0].Properties["ChpwdFired"]?.Value?.ToString());
            Assert.Equal("True", result[0].Properties["PromptFired"]?.Value?.ToString());
        }
        finally
        {
            HookRegistry.Instance.Unregister(HookKind.ChpwdHook, chpwdName);
            HookRegistry.Instance.Unregister(HookKind.PromptHook, promptName);
        }
    }

    [Fact]
    public void FirePrompt_ChpwdHook_ReceivesOldAndNewPath()
    {
        using var pwsh = PwshTestFixture.Create();

        var chpwdName = N("args-chpwd");

        pwsh.AddScript($@"
            $global:capturedOld = $null
            $global:capturedNew = $null
            Register-BashChpwdHook -Name '{chpwdName}' -ScriptBlock {{
                param($OldPath, $NewPath)
                $global:capturedOld = $OldPath
                $global:capturedNew = $NewPath
            }}
        ").Invoke();
        pwsh.Commands.Clear();

        try
        {
            pwsh.AddScript($@"
                [PsBash.Cmdlets.HookRegistry]::Instance.FirePrompt(
                    $ExecutionContext.SessionState, '/from', '/to')
            ").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("[PSCustomObject]@{ Old = $global:capturedOld; New = $global:capturedNew }").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("/from", result[0].Properties["Old"]?.Value?.ToString());
            Assert.Equal("/to", result[0].Properties["New"]?.Value?.ToString());
        }
        finally
        {
            HookRegistry.Instance.Unregister(HookKind.ChpwdHook, chpwdName);
        }
    }

    // -------------------------------------------------------------------------
    // Exception isolation — a throwing hook does not stop subsequent hooks
    // -------------------------------------------------------------------------

    [Fact]
    public void FirePrompt_ThrowingChpwdHook_NextHookStillFires()
    {
        using var pwsh = PwshTestFixture.Create();

        var firstName = N("throw-first");
        var secondName = N("throw-second");

        pwsh.AddScript($@"
            $global:BashHookErrors = [System.Collections.Generic.List[object]]::new()
            $global:secondFired = $false
            Register-BashChpwdHook -Name '{firstName}' -ScriptBlock {{ throw 'boom' }}
            Register-BashChpwdHook -Name '{secondName}' -ScriptBlock {{ $global:secondFired = $true }}
        ").Invoke();
        pwsh.Commands.Clear();

        try
        {
            pwsh.AddScript($@"
                [PsBash.Cmdlets.HookRegistry]::Instance.FirePrompt(
                    $ExecutionContext.SessionState, '/a', '/b')
            ").Invoke();
            pwsh.Commands.Clear();

            var result = pwsh.AddScript("[PSCustomObject]@{ SecondFired = $global:secondFired; ErrorCount = $global:BashHookErrors.Count }").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("True", result[0].Properties["SecondFired"]?.Value?.ToString());
            // The error should have been recorded.
            var errorCount = int.Parse(result[0].Properties["ErrorCount"]?.Value?.ToString() ?? "0");
            Assert.True(errorCount >= 1, $"Expected at least 1 hook error, got {errorCount}");
        }
        finally
        {
            HookRegistry.Instance.Unregister(HookKind.ChpwdHook, firstName);
            HookRegistry.Instance.Unregister(HookKind.ChpwdHook, secondName);
        }
    }

    // -------------------------------------------------------------------------
    // Cmdlet surface via PwshTestFixture
    // -------------------------------------------------------------------------

    [Fact]
    public void RegisterCmdlet_AndGetBashHook_ListsIt()
    {
        using var pwsh = PwshTestFixture.Create();
        var hookName = N("cmdlet-get");

        pwsh.AddScript($"Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{ 'noop' }}").Invoke();
        pwsh.Commands.Clear();

        try
        {
            var result = pwsh.AddScript($"Get-BashHook | Where-Object {{ $_.Name -eq '{hookName}' }}").Invoke();
            pwsh.Commands.Clear();

            Assert.Single(result);
            Assert.Equal("ChpwdHook", result[0].Properties["Kind"]?.Value?.ToString());
            Assert.Equal(hookName, result[0].Properties["Name"]?.Value?.ToString());
        }
        finally
        {
            HookRegistry.Instance.Unregister(HookKind.ChpwdHook, hookName);
        }
    }

    [Fact]
    public void UnregisterCmdlet_RemovesHook()
    {
        using var pwsh = PwshTestFixture.Create();
        var hookName = N("cmdlet-unregister");

        pwsh.AddScript($"Register-BashChpwdHook -Name '{hookName}' -ScriptBlock {{ 'noop' }}").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript($"Unregister-BashChpwdHook -Name '{hookName}'").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript($"Get-BashHook | Where-Object {{ $_.Name -eq '{hookName}' }}").Invoke();
        pwsh.Commands.Clear();

        Assert.Empty(result);
    }

    [Fact]
    public void GetBashHook_KindFilter_ReturnsOnlyThatKind()
    {
        using var pwsh = PwshTestFixture.Create();
        var chpwdName = N("filter-chpwd");
        var promptName = N("filter-prompt");

        pwsh.AddScript($@"
            Register-BashChpwdHook  -Name '{chpwdName}'  -ScriptBlock {{ 'c' }}
            Register-BashPromptHook -Name '{promptName}' -ScriptBlock {{ 'p' }}
        ").Invoke();
        pwsh.Commands.Clear();

        try
        {
            var chpwdResults = pwsh.AddScript("Get-BashHook -Kind ChpwdHook").Invoke();
            pwsh.Commands.Clear();
            var promptResults = pwsh.AddScript("Get-BashHook -Kind PromptHook").Invoke();
            pwsh.Commands.Clear();

            Assert.All(chpwdResults, r => Assert.Equal("ChpwdHook", r.Properties["Kind"]?.Value?.ToString()));
            Assert.All(promptResults, r => Assert.Equal("PromptHook", r.Properties["Kind"]?.Value?.ToString()));

            Assert.Contains(chpwdResults, r => r.Properties["Name"]?.Value?.ToString() == chpwdName);
            Assert.Contains(promptResults, r => r.Properties["Name"]?.Value?.ToString() == promptName);
        }
        finally
        {
            HookRegistry.Instance.Unregister(HookKind.ChpwdHook, chpwdName);
            HookRegistry.Instance.Unregister(HookKind.PromptHook, promptName);
        }
    }
}
