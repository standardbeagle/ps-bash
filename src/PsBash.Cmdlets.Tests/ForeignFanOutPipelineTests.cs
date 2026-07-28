using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The load-bearing constraint on S4's allocation work: typed-object
/// preservation. <c>docs/specs/runtime-functions.md</c> requires consumers to
/// pass ORIGINAL objects through rather than flatten input into text, so
/// <c>ls | grep .txt</c> keeps its <c>PsBash.LsEntry</c> properties and a
/// foreign producer keeps its own (a <c>FileInfo</c>'s <c>FullName</c> /
/// <c>Length</c>). The same spec requires the defensive split for an item whose
/// BashText genuinely carries embedded newlines.
///
/// These are hand-written assertions, not oracle diffs, per Directive 1's
/// exception list: PowerShell pipeline object preservation has no bash
/// equivalent.
/// </summary>
[Collection("PsBashSearchEnv")]
public class ForeignFanOutPipelineTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public ForeignFanOutPipelineTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), $"psb-s4-{Guid.NewGuid():N}".Substring(0, 20));
        Directory.CreateDirectory(_tmpDir);
        File.WriteAllText(Path.Combine(_tmpDir, "alpha.txt"), "alpha\n");
        File.WriteAllText(Path.Combine(_tmpDir, "beta.log"), "beta\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private string Q(string s) => s.Replace("'", "''");

    [Fact]
    public void ForeignProducerIntoGrep_PreservesProducerTypedProperties()
    {
        var result = Run($"Get-ChildItem -File '{Q(_tmpDir)}' | Invoke-BashGrep 'alpha'");

        var row = Assert.Single(result);
        Assert.Equal("alpha.txt", row.Properties["Name"]?.Value);
        Assert.NotNull(row.Properties["FullName"]?.Value);
        Assert.NotNull(row.Properties["Length"]?.Value);
    }

    [Fact]
    public void LsIntoGrep_PreservesLsEntryTypedProperties()
    {
        var result = Run($"Invoke-BashLs '{Q(_tmpDir)}' | Invoke-BashGrep '.txt'");

        var row = Assert.Single(result);
        Assert.Equal("alpha.txt", row.Properties["Name"]?.Value);
        Assert.NotNull(row.Properties["SizeBytes"]);
        Assert.Contains("PsBash.LsEntry", row.TypeNames);
    }

    [Fact]
    public void MultiLineBashTextItem_IsStillDefensivelySplit()
    {
        var result = Run(
            "New-BashObject -BashText \"one`ntwo`nthree`n\" | Invoke-BashGrep 'two'");

        var row = Assert.Single(result);
        Assert.Equal("two", row.Properties["BashText"]?.Value);
    }

    /// <summary>
    /// An instance-level ToString override (<c>Add-Member ToString</c>, what
    /// <c>Set-BashDisplayProperty</c> installs) must still win over the base
    /// object's own ToString. Skipping it to save the ETS call would silently
    /// change output. Exercised through the pipeline, which is the only path
    /// that preserves it: a cmdlet's <c>PSObject</c> parameter keeps the wrapper
    /// (and its instance members), while a plain <c>object</c> argument — the
    /// psm1 <c>Get-BashText</c> shim — is unwrapped by parameter binding and
    /// loses the override, both before and after S4.
    /// </summary>
    [Fact]
    public void InstanceToStringOverride_IsHonored()
    {
        var result = Run(
            $"$o = Get-Item -LiteralPath '{Q(Path.Combine(_tmpDir, "alpha.txt"))}'; " +
            "$o | Add-Member -MemberType ScriptMethod -Name ToString -Value { 'overridden' } -Force; " +
            "$o | Invoke-BashGrep 'overridden'");

        Assert.Single(result);
    }

    [Fact]
    public void SingleLineItem_IsPassedThroughUnwrapped()
    {
        var result = Run($"Get-ChildItem -File '{Q(_tmpDir)}' | Invoke-BashGrep '.'");

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Contains("System.IO.FileInfo", r.TypeNames));
    }
}
