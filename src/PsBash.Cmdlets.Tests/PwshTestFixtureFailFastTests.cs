using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The fixture must FAIL IMMEDIATELY, with a message naming the problem, when the module
/// files are missing or a load step fails. It used to skip each missing/failed step silently,
/// leaving a bare runspace that failed (or vacuously passed) tests far from the cause.
/// </summary>
public class PwshTestFixtureFailFastTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"psb-ff-{Guid.NewGuid():N}".Substring(0, 18));

    public PwshTestFixtureFailFastTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void MissingModuleFiles_FailImmediately_NamingEveryMissingFile()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PwshTestFixture.CreateInternal(_dir));

        Assert.Contains("PsBash.psm1", ex.Message);
        Assert.Contains("PsBash.Cmdlets.dll", ex.Message);
        Assert.Contains("PsBash.Format.ps1xml", ex.Message);
        Assert.Contains(_dir, ex.Message);
    }

    [Fact]
    public void OneMissingFile_FailsAndNamesOnlyThatFile()
    {
        var real = AppContext.BaseDirectory;
        File.Copy(Path.Combine(real, "PsBash.Cmdlets.dll"), Path.Combine(_dir, "PsBash.Cmdlets.dll"));
        File.Copy(Path.Combine(real, "PsBash.Format.ps1xml"), Path.Combine(_dir, "PsBash.Format.ps1xml"));

        var ex = Assert.Throws<InvalidOperationException>(() => PwshTestFixture.CreateInternal(_dir));

        Assert.Contains("PsBash.psm1", ex.Message);
        Assert.DoesNotContain("PsBash.Cmdlets.dll,", ex.Message);
    }

    [Fact]
    public void UnimportableCmdletsAssembly_FailsAtTheImportStep()
    {
        var real = AppContext.BaseDirectory;
        File.WriteAllText(Path.Combine(_dir, "PsBash.psm1"), "# stub module");
        File.WriteAllText(Path.Combine(_dir, "PsBash.Format.ps1xml"), "<Configuration/>");
        File.WriteAllText(Path.Combine(_dir, "PsBash.Cmdlets.dll"), "this is not an assembly");

        var ex = Assert.Throws<InvalidOperationException>(() => PwshTestFixture.CreateInternal(_dir));

        Assert.Contains("Import-Module PsBash.Cmdlets.dll", ex.Message);
    }

    [Fact]
    public void BrokenPsm1_FailsAtTheLoadStep()
    {
        var real = AppContext.BaseDirectory;
        File.Copy(Path.Combine(real, "PsBash.Cmdlets.dll"), Path.Combine(_dir, "PsBash.Cmdlets.dll"));
        File.Copy(Path.Combine(real, "PsBash.Format.ps1xml"), Path.Combine(_dir, "PsBash.Format.ps1xml"));
        File.WriteAllText(Path.Combine(_dir, "PsBash.psm1"), "function { this is not powershell");

        var ex = Assert.Throws<InvalidOperationException>(() => PwshTestFixture.CreateInternal(_dir));

        Assert.Contains("PsBash.psm1", ex.Message);
    }

    [Fact]
    public void HealthyModuleDirectory_StillLoads()
    {
        using var pwsh = PwshTestFixture.CreateInternal(AppContext.BaseDirectory);
        var found = pwsh.AddScript("[bool](Get-Command Invoke-BashEcho -ErrorAction SilentlyContinue)").Invoke();
        Assert.True(found[0].BaseObject is true);
    }
}
