using System.Management.Automation.Language;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Build-time syntax check for the embedded Resources/SdkRunspaceSetup.ps1.
///
/// Oracle note (qa-rubric Directive 1): The script is ps-bash-specific runtime
/// setup with no bash oracle — hand-written asserts justified per exception list.
///
/// Purpose: catch PowerShell syntax errors in the embedded setup script at
/// test time rather than at host-startup time. The REFACTOR-1 task description
/// notes four script-string-in-C# escaping bugs in the last 24 hours; moving
/// the script into a .ps1 lets a real PowerShell parser fail the build.
/// </summary>
public class SdkRunspaceSetupScriptTests
{
    [Fact]
    public void EmbeddedResource_IsReachableViaManifestStream()
    {
        var content = RunspaceSetupExtractor.ReadEmbedded();
        Assert.False(string.IsNullOrWhiteSpace(content),
            "Embedded SdkRunspaceSetup.ps1 resource was empty or missing — check " +
            "PsBash.Host.csproj <EmbeddedResource> registration.");
    }

    [Fact]
    public void EmbeddedSetupScript_ParsesWithoutSyntaxErrors()
    {
        var content = RunspaceSetupExtractor.ReadEmbedded();

        Parser.ParseInput(content, out _, out var parseErrors);

        if (parseErrors is { Length: > 0 })
        {
            var msgs = string.Join(Environment.NewLine,
                parseErrors.Select(e => $"  {e.Extent.StartLineNumber}:{e.Extent.StartColumnNumber} {e.Message}"));
            Assert.Fail(
                "Embedded SdkRunspaceSetup.ps1 has PowerShell parse errors:" +
                Environment.NewLine + msgs);
        }
    }

    [Fact]
    public void EmbeddedSetupScript_ReadsExpectedSessionStateVariables()
    {
        // Acceptance criterion: the script consumes parameters via
        // session-state variables, not through C#-side string interpolation.
        // Verify the variable name the C# caller sets is actually referenced
        // by the script — guards against future renames silently breaking the
        // parameter handoff.
        var content = RunspaceSetupExtractor.ReadEmbedded();
        Assert.Contains("$PsBashCmdletsDllPath", content);
    }

    [Fact]
    public void Extract_WritesScriptIntoTargetDirectory()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "ps-bash",
            $"setup-extract-test-{Guid.NewGuid():N}");
        try
        {
            var path = RunspaceSetupExtractor.Extract(dir);
            Assert.True(File.Exists(path),
                $"Extracted setup script was not created at {path}.");
            Assert.Equal("SdkRunspaceSetup.ps1", Path.GetFileName(path));

            var diskContent = File.ReadAllText(path);
            var resourceContent = RunspaceSetupExtractor.ReadEmbedded();
            Assert.Equal(resourceContent, diskContent);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
