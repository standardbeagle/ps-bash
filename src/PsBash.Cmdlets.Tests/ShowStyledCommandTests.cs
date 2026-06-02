using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Tests for the interactive <c>Show-Styled</c> cmdlet. The xunit host always has redirected I/O,
/// so these exercise the <b>headless</b> branch: the Strata node tree is built and projected through
/// <c>TerminalGuiProjection</c> (proving the cmdlet wires the tree + family stylesheet + projection
/// correctly), and a summary string is emitted instead of entering the interactive loop. The live
/// Terminal.Gui loop needs a real terminal driver and is verified manually (see
/// docs/specs/styled-output.md). Oracle note (Directive 1): ps-bash-specific cmdlet surface.
/// </summary>
public class ShowStyledCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public ShowStyledCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Headless_BuildsViewTreeAndSummarizes()
    {
        var pwsh = _fixture.AcquireFresh();
        var script = """
            $rows = @(
              [pscustomobject]@{ PSTypeName='Process'; Name='chrome'; Id=1; class='busy' },
              [pscustomobject]@{ PSTypeName='Process'; Name='vim';    Id=2; class='idle' }
            )
            $rows | Show-Styled -Property Name,Id
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var text = result[0].ToString() ?? string.Empty;
        // Headless summary: proves the styled tree built and the cascade ran for the given rows.
        Assert.Contains("headless", text);
        Assert.Contains("2 rows", text);
    }

    [Fact]
    public void Headless_AutoPicksFamilySheetByKind()
    {
        var pwsh = _fixture.AcquireFresh();
        // No -Style: a Process row must auto-resolve to the `procsvc` family sheet without error
        // (the auto-pick + cascade + projection path runs clean).
        var script = """
            [pscustomobject]@{ PSTypeName='Process'; Name='nginx'; Id=7 } | Show-Styled
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var text = result[0].ToString() ?? string.Empty;
        Assert.Contains("style 'procsvc'", text);
        Assert.Contains("1 rows", text);
    }

    [Fact]
    public void NoInput_ProducesNoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript("@() | Show-Styled").Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Empty(result);
    }
}
