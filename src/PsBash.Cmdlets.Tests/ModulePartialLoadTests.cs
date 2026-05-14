using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Partial-load CI guard for PsBash.psm1.
///
/// Background (REFACTOR-6):
///   PsBash.psm1 historically set `Set-StrictMode -Version Latest` at file
///   scope. A StrictMode trip during a function-body parse could silently abort
///   the rest of the parse pass, leaving later functions unregistered — the
///   leading hypothesis for the RC-3a partial-load gap (Invoke-BashEcho loads,
///   Invoke-BashLs does not).
///
/// These tests load the psm1 fresh via PwshTestFixture, read the advertised
/// surface from PsBash.psd1 (FunctionsToExport / AliasesToExport), and assert
/// every advertised name is Get-Command-resolvable. If the psm1 parse aborts
/// partway, the missing names fail here at CI time instead of in production.
/// </summary>
public class ModulePartialLoadTests
{
    /// <summary>
    /// Reads PsBash.psd1 from the test output directory and returns the value
    /// of a list-valued manifest key (FunctionsToExport / AliasesToExport).
    /// </summary>
    private static IReadOnlyList<string> ReadManifestList(
        System.Management.Automation.PowerShell pwsh, string key)
    {
        var psd1Path = Path.Combine(AppContext.BaseDirectory, "PsBash.psd1");
        Assert.True(File.Exists(psd1Path), $"manifest not found: {psd1Path}");

        var script =
            "param($p,$k) (Import-PowerShellDataFile -Path $p).$k";
        var result = pwsh
            .AddScript(script)
            .AddArgument(psd1Path)
            .AddArgument(key)
            .Invoke();
        pwsh.Commands.Clear();

        var names = result
            .Select(o => o?.BaseObject as string ?? o?.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();

        Assert.NotEmpty(names);
        return names;
    }

    [Fact]
    public void EveryExportedFunction_IsGetCommandResolvable()
    {
        using var pwsh = PwshTestFixture.Create();

        var functions = ReadManifestList(pwsh, "FunctionsToExport");

        var missing = new List<string>();
        foreach (var name in functions)
        {
            pwsh.AddScript("param($n) Get-Command -Name $n -ErrorAction SilentlyContinue")
                .AddArgument(name);
            var found = pwsh.Invoke();
            pwsh.Commands.Clear();

            if (found.Count == 0)
                missing.Add(name);
        }

        Assert.True(missing.Count == 0,
            $"psm1 partial-load: {missing.Count} advertised function(s) did not " +
            $"register: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryExportedAlias_IsGetCommandResolvable()
    {
        using var pwsh = PwshTestFixture.Create();

        var aliases = ReadManifestList(pwsh, "AliasesToExport");

        var missing = new List<string>();
        foreach (var name in aliases)
        {
            pwsh.AddScript(
                "param($n) Get-Command -Name $n -CommandType Alias -ErrorAction SilentlyContinue")
                .AddArgument(name);
            var found = pwsh.Invoke();
            pwsh.Commands.Clear();

            if (found.Count == 0)
                missing.Add(name);
        }

        Assert.True(missing.Count == 0,
            $"psm1 partial-load: {missing.Count} advertised alias(es) did not " +
            $"register: {string.Join(", ", missing)}");
    }
}
