using System.Reflection;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Tests for <see cref="ModuleExtractor"/> — the canonical module-load path
/// (REFACTOR-5). PsBash.Cmdlets.dll is embedded per-TFM in PsBash.Core and
/// extracted alongside PsBash.psm1, so the host runspace imports it from one
/// deterministic path with no probing.
///
/// Oracle note (qa-rubric Directive 1): extraction is ps-bash-specific runtime
/// plumbing with no bash equivalent — hand-written asserts justified per the
/// exception list.
/// </summary>
public class ModuleExtractorTests
{
    [Fact]
    public void ExtractEmbedded_ReturnsExistingPsd1Path()
    {
        var psd1 = ModuleExtractor.ExtractEmbedded();

        Assert.True(File.Exists(psd1),
            $"ExtractEmbedded returned a psd1 path that does not exist: {psd1}");
        Assert.Equal("PsBash.psd1", Path.GetFileName(psd1));
    }

    [Fact]
    public void ExtractEmbedded_AlsoExtractsCmdletsDll()
    {
        // Acceptance criterion: ModuleExtractor extracts the TFM-matching
        // PsBash.Cmdlets.dll into module-{version}/ alongside psd1/psm1.
        ModuleExtractor.ExtractEmbedded();

        var cmdletsDll = ModuleExtractor.GetCmdletsDllPath();
        Assert.True(File.Exists(cmdletsDll),
            $"ExtractEmbedded did not extract PsBash.Cmdlets.dll to {cmdletsDll}. " +
            "Check the EmbedCmdletsDll target in PsBash.Core.csproj and " +
            "ModuleExtractor's cmdlets-resource resolution.");
        Assert.Equal("PsBash.Cmdlets.dll", Path.GetFileName(cmdletsDll));
    }

    [Fact]
    public void GetCmdletsDllPath_IsBesideExtractedPsm1()
    {
        // The setup script imports this exact path; it must sit in the same
        // module-{version}/ directory as the psd1 ModuleExtractor returns.
        var psd1 = ModuleExtractor.ExtractEmbedded();
        var cmdletsDll = ModuleExtractor.GetCmdletsDllPath();

        Assert.Equal(
            Path.GetDirectoryName(psd1),
            Path.GetDirectoryName(cmdletsDll));
    }

    [Fact]
    public void ExtractEmbedded_CmdletsDll_IsAValidManagedAssembly()
    {
        // The extracted DLL must be the real cmdlets assembly, not a truncated
        // or corrupt copy — load just its name to confirm it is a managed PE.
        ModuleExtractor.ExtractEmbedded();
        var cmdletsDll = ModuleExtractor.GetCmdletsDllPath();

        var name = AssemblyName.GetAssemblyName(cmdletsDll);
        Assert.Equal("PsBash.Cmdlets", name.Name);
    }

    [Fact]
    public void CoreAssembly_EmbedsAtLeastOneCmdletsDllResource()
    {
        // Guards the build wiring: if the EmbedCmdletsDll MSBuild target stops
        // running, this fails before the slower extraction tests do.
        var asm = typeof(ModuleExtractor).Assembly;
        var cmdletsResources = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("PsBash.Module/cmdlets/", StringComparison.Ordinal) &&
                        n.EndsWith("/PsBash.Cmdlets.dll", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(cmdletsResources);
    }
}
