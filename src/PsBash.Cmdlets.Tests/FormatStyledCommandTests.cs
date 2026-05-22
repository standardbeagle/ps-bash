using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Smoke tests for the Strata integration cmdlet <c>Format-Styled</c>. Verifies the
/// end-to-end pipeline (PSObject -&gt; Strata adapter -&gt; CSS cascade -&gt; Spectre projection)
/// runs in-process and that selectors/classes drive the output.
/// </summary>
public class FormatStyledCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public FormatStyledCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    // Spectre emits ANSI SGR escapes; strip them to assert on plain content.
    private static string StripAnsi(string s) => Regex.Replace(s, "\\[[0-9;]*m", string.Empty);

    [Fact]
    public void StylesRows_ByPropertyAndKind()
    {
        var pwsh = _fixture.AcquireFresh();
        var script = """
            $rows = @(
              [pscustomobject]@{ PSTypeName='Proc'; Name='chrome'; class='high-cpu' },
              [pscustomobject]@{ PSTypeName='Proc'; Name='vim';    class='' }
            )
            $css = 'Proc { color: grey } .high-cpu { color: red; font-weight: bold }'
            $rows | Format-Styled -Css $css -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var plain = StripAnsi(result[0].ToString() ?? string.Empty);
        Assert.Contains("chrome", plain);
        Assert.Contains("vim", plain);
    }

    [Fact]
    public void EmitsAnsi_ForStyledRow()
    {
        var pwsh = _fixture.AcquireFresh();
        var script = """
            $rows = @([pscustomobject]@{ PSTypeName='Proc'; Name='chrome'; class='high-cpu' })
            $css = '.high-cpu { color: red; font-weight: bold }'
            $rows | Format-Styled -Css $css -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var raw = result[0].ToString() ?? string.Empty;
        // A red+bold style must produce at least one ANSI SGR escape sequence.
        Assert.Matches("\\[[0-9;]*m", raw);
    }

    [Fact]
    public void NoInput_ProducesNoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript("@() | Format-Styled 'Proc { color: red }'").Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Empty(result);
    }
}
