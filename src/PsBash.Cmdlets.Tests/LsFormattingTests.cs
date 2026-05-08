using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Shared PowerShell runspace for LsFormattingTests. Loading PsBash.psm1 takes
/// 2-4 seconds; sharing one instance across all tests in the class avoids
/// paying that cost per test.
/// </summary>
public sealed class LsFormattingFixture : IDisposable
{
    public PowerShell Pwsh { get; } = PwshTestFixture.Create();
    public void Dispose() => Pwsh.Dispose();
}

/// <summary>
/// Tests that Invoke-BashLs produces properly formatted output at the module level.
///
/// Root cause context: SdkWorker.RunCommand was calling PSObject.ToString() on output
/// objects, which for PSCustomObject returns "@{Name=...; BashText=...}" rather than
/// invoking the PowerShell Update-TypeData ScriptMethod. The tests here verify the
/// module's objects carry the correct BashText content so SdkWorker (and any consumer
/// that extracts BashText explicitly) receives bash-formatted output.
///
/// Oracle note (Directive 1): behavior is ps-bash-specific (Invoke-Bash* cmdlet
/// surface); hand-written asserts are justified.
/// </summary>
public class LsFormattingTests : IClassFixture<LsFormattingFixture>
{
    private readonly PowerShell _pwsh;

    public LsFormattingTests(LsFormattingFixture fixture)
        => _pwsh = fixture.Pwsh;

    private static string EscapePs(string path) => path.Replace("'", "''");

    private IReadOnlyList<PSObject> RunLs(string args, string dir)
    {
        _pwsh.Commands.Clear();
        return _pwsh.AddScript($"Invoke-BashLs {args} '{EscapePs(dir)}'").Invoke();
    }

    // ── LsEntry shape ─────────────────────────────────────────────────────────

    [Fact]
    public void InvokeBashLs_ReturnsObjectsWithBashTextProperty()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-ls-fmt-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "sample.txt"), "data");
            var result = RunLs("", dir.FullName);

            Assert.NotEmpty(result);
            foreach (var obj in result)
            {
                var bashText = obj.Properties["BashText"]?.Value?.ToString();
                Assert.NotNull(bashText);
                Assert.DoesNotContain("@{", bashText);
            }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void InvokeBashLs_BashTextContainsFilename()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-ls-name-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "uniquefile.txt"), "x");
            var result = RunLs("", dir.FullName);

            var allBashText = result
                .Select(o => o.Properties["BashText"]?.Value?.ToString() ?? "")
                .ToList();

            Assert.Contains(allBashText, bt => bt.Contains("uniquefile.txt"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void InvokeBashLs_TypeNamesContainLsEntry()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-ls-type-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "probe.txt"), "x");
            var result = RunLs("", dir.FullName);

            Assert.NotEmpty(result);
            foreach (var obj in result)
                Assert.Contains("PsBash.LsEntry", obj.TypeNames);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── Long format content ────────────────────────────────────────────────────

    [Fact]
    public void InvokeBashLsLong_BashTextStartsWithPermissionsColumn()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-ls-long-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "readme.txt"), "hello");
            var result = RunLs("-l", dir.FullName);

            Assert.NotEmpty(result);
            var entries = result
                .Select(o => o.Properties["BashText"]?.Value?.ToString() ?? "")
                .ToList();

            Assert.DoesNotContain(entries, bt => bt.StartsWith("@{"));
            Assert.Contains(entries, bt => bt.Length > 0 &&
                (bt[0] == '-' || bt[0] == 'd' || bt[0] == 'l'));
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── Root-cause documentation: C# PSObject.ToString() ─────────────────────

    /// <summary>
    /// Documents the root cause: C# PSObject.ToString() on a PSCustomObject does
    /// NOT invoke the PowerShell Update-TypeData ScriptMethod for ToString.
    /// Instead it returns the "@{prop=val;...}" hash-table representation.
    ///
    /// This is why SdkWorker.RunCommand must extract BashText explicitly with
    ///   pso.Properties["BashText"]?.Value?.ToString()
    /// rather than calling col[e.Index]?.ToString().
    /// </summary>
    [Fact]
    public void InvokeBashLs_CSharpPSObjectToString_DoesNotReturnRawObjectDump()
    {
        var dir = Directory.CreateTempSubdirectory("psbash-ls-cs-ts-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "file.txt"), "content");
            var result = RunLs("", dir.FullName);

            Assert.NotEmpty(result);
            foreach (var obj in result)
            {
                var bashText = obj.Properties["BashText"]?.Value?.ToString() ?? "";
                Assert.Contains("file.txt", bashText);

                // PSObject.ToString() called from C# must not leak the raw object repr.
                // (SdkWorker fix: extract BashText explicitly; do not rely on ToString.)
                var toStr = obj.ToString();
                Assert.DoesNotContain("@{", toStr);
            }
        }
        finally { dir.Delete(recursive: true); }
    }
}
