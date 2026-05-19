using System.Linq;
using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// Invoke-BashBrowse from a PsBash.psm1 script function to a binary
/// cmdlet (PsBash.Cmdlets.dll / InvokeBashBrowseCommand.cs).
///
/// The SDK runspace always reports stdin as redirected, so the cmdlet
/// stays on the non-interactive list-mode path. Row rendering, binding,
/// inspect / action / exec dispatch all delegate to the surviving psm1
/// helpers via parameter-bound InvokeCommand.InvokeScript, so the
/// observable shape matches the psm1 oracle byte for byte.
///
/// Failure-surface axes (per .claude/rules/qa-rubric.md Directive 3):
/// empty pipeline (Directive 7), single-property objects (single-col),
/// multi-property objects (multi-col), alias resolution, --help.
/// Security (Directive 12): a typed-object whose BashText contains
/// $(throw 'pwn') must flow through ConvertTo-BrowseRow as data and
/// never reach a script-block evaluator.
/// </summary>
public class InvokeBashBrowseCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashBrowseCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private System.Collections.ObjectModel.Collection<PSObject> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    [Fact]
    public void Browse_EmptyPipeline_EmitsNothing()
    {
        var rows = Run("@() | Invoke-BashBrowse --list");
        Assert.Empty(rows);
    }

    [Fact]
    public void Browse_SingleProperty_RendersOneBrowseRow()
    {
        // One pipeline object with a single Name property → one BrowseRow.
        var rows = Run("[pscustomobject]@{ Name = 'alpha' } | Invoke-BashBrowse --list");
        Assert.Single(rows);
        var row = rows[0];
        Assert.NotNull(row);
        var display = row!.Properties["Display"]?.Value?.ToString() ?? string.Empty;
        Assert.Contains("alpha", display);
    }

    [Fact]
    public void Browse_MultiPropertyObjects_RenderColumnNames()
    {
        // Two multi-column objects produce two BrowseRow PSObjects with
        // Display strings carrying both property names+values.
        var script = @"
            $a = [pscustomobject]@{ Name = 'one'; Count = 1 }
            $b = [pscustomobject]@{ Name = 'two'; Count = 2 }
            @($a, $b) | Invoke-BashBrowse --list
        ";
        var rows = Run(script);
        Assert.Equal(2, rows.Count);
        var displays = rows
            .Select(r => r?.Properties["Display"]?.Value?.ToString() ?? string.Empty)
            .ToArray();
        Assert.Contains(displays, d => d.Contains("Name=one") && d.Contains("Count=1"));
        Assert.Contains(displays, d => d.Contains("Name=two") && d.Contains("Count=2"));
    }

    [Fact]
    public void Browse_PreservesIndexAndOriginalObject()
    {
        var script = @"
            $a = [pscustomobject]@{ Name = 'alpha' }
            $b = [pscustomobject]@{ Name = 'beta' }
            @($a, $b) | Invoke-BashBrowse --list
        ";
        var rows = Run(script);
        Assert.Equal(2, rows.Count);
        Assert.Equal(0, (int)rows[0]!.Properties["Index"].Value);
        Assert.Equal(1, (int)rows[1]!.Properties["Index"].Value);
        // OriginalObject must round-trip the source PSObject.
        var orig0 = rows[0]!.Properties["OriginalObject"]?.Value as PSObject;
        Assert.NotNull(orig0);
        Assert.Equal("alpha", orig0!.Properties["Name"]?.Value?.ToString());
    }

    [Fact]
    public void Browse_AliasResolvesToCmdlet()
    {
        // The Set-Alias 'browse' line in psm1 must resolve to the binary cmdlet.
        var rows = Run("[pscustomobject]@{ Name = 'aliased' } | browse --list");
        Assert.Single(rows);
        var display = rows[0]!.Properties["Display"]?.Value?.ToString() ?? string.Empty;
        Assert.Contains("aliased", display);
    }

    [Fact]
    public void Browse_HelpFlag_EmitsUsageLine()
    {
        var lines = Run("Invoke-BashBrowse --help")
            .Select(o => o?.ToString() ?? string.Empty)
            .ToArray();
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.StartsWith("Usage: browse"));
    }

    [Fact]
    public void Browse_PassThru_EmitsSelectedItems()
    {
        // -PassThru returns the binding.Items array — for a single object
        // with no -Select that's just the one object.
        var rows = Run("[pscustomobject]@{ Name = 'kept' } | Invoke-BashBrowse -PassThru");
        Assert.Single(rows);
        Assert.Equal("kept", rows[0]!.Properties["Name"]?.Value?.ToString());
    }

    // Directive 12: an injection payload embedded in pipeline-object data
    // must travel through the row renderer as a string and never reach a
    // script-block evaluator. The cmdlet delegates to ConvertTo-BrowseRow
    // via InvokeScript with $args splatting, so the payload only enters
    // the runspace as a property *value*, never as a script body.
    [Fact]
    public void Browse_InjectionProbe_PayloadStaysLiteral()
    {
        var script = @"
            $obj = [pscustomobject]@{ Name = ""$(throw 'pwn')"" }
            $obj | Invoke-BashBrowse --list
        ";
        // Read the script from a here-string so the throw never fires at
        // parse time — when the SCRIPT runs, the literal-string parser
        // sees `$(throw 'pwn')` and evaluates it at object-construction
        // time inside the test runspace. To make the probe meaningful we
        // build the property value via a single-quoted string instead.
        var safeScript = @"
            $payload = '$(throw ''pwn'')'
            [pscustomobject]@{ Name = $payload } | Invoke-BashBrowse --list
        ";
        var rows = Run(safeScript);
        Assert.Single(rows);
        var display = rows[0]!.Properties["Display"]?.Value?.ToString() ?? string.Empty;
        // The literal '$(throw ''pwn'')' substring must appear in the row's
        // Display string — proving the payload was treated as data, not
        // executed.
        Assert.Contains("$(throw", display);
        // And the no-op compile path: ensure the unused declaration still
        // type-checks (suppresses unused-var warning).
        Assert.NotNull(script);
    }
}
