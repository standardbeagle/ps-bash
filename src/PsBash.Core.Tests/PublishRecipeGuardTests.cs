using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the shipping recipe against a regression that NO other test can see.
///
/// <para>
/// The failure mode: <c>&lt;PrivateAssets&gt;all&lt;/PrivateAssets&gt;</c> on
/// <c>Microsoft.PowerShell.SDK</c> excludes the SDK's RUNTIME assets from publish, so
/// <c>dotnet publish src/PsBash.Host --self-contained</c> — the exact command
/// <c>publish.yml</c> ships with — emits a host directory containing no
/// <c>System.Management.Automation.dll</c>. That host starts, accepts a connection, and then
/// fails every command with "Could not load file or assembly
/// 'System.Management.Automation'"; from the launcher's side it looks like
/// "ps-bash-host did not accept connections within 20s".
/// </para>
///
/// <para>
/// Why a csproj-text guard rather than a behavioural test: every normal build and test run
/// is blind to this. A dev/CI <c>dotnet build</c> resolves SMA from the NuGet cache via
/// <c>runtimeconfig.dev.json</c> probing paths, and the in-process host tests get SMA from
/// the test project's own SDK reference. ONLY a published or installed layout breaks, so the
/// cheapest honest check is that the attribute has not come back. Catching it for real would
/// mean publishing self-contained in CI and asserting on the output — worth doing if this
/// ever regresses twice.
/// </para>
/// </summary>
public class PublishRecipeGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root (CLAUDE.md).");
    }

    [Fact]
    public void PowerShellSdkReference_DoesNotMarkAssetsPrivate()
    {
        var csprojPath = Path.Combine(RepoRoot(), "src", "PsBash.Host", "PsBash.Host.csproj");
        Assert.True(File.Exists(csprojPath), $"PsBash.Host.csproj not found at {csprojPath}");
        var csproj = File.ReadAllText(csprojPath);

        var sdkIndex = csproj.IndexOf("Microsoft.PowerShell.SDK", StringComparison.Ordinal);
        Assert.True(sdkIndex >= 0, "PsBash.Host must reference Microsoft.PowerShell.SDK.");

        // Inspect only this PackageReference element, not the whole file: PrivateAssets is
        // legitimate on OTHER references (and on the Host ProjectReference in Shell.csproj).
        var elementEnd = csproj.IndexOf("</PackageReference>", sdkIndex, StringComparison.Ordinal);
        var selfClosing = csproj.IndexOf("/>", sdkIndex, StringComparison.Ordinal);
        var end = elementEnd >= 0 && elementEnd < selfClosing ? elementEnd : selfClosing;
        Assert.True(end > sdkIndex, "Could not determine the end of the Microsoft.PowerShell.SDK reference.");

        var element = csproj[sdkIndex..end];
        Assert.DoesNotContain("PrivateAssets", element, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExcludeAssets", element, StringComparison.OrdinalIgnoreCase);
    }
}
