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

    [Fact]
    public void Default_AppliesWhenNoStylesheetArgument()
    {
        var pwsh = _fixture.AcquireFresh();
        // No stylesheet arg -> the built-in `default` sheet, which colors a Process kind.
        var script = """
            $rows = @([pscustomobject]@{ PSTypeName='Process'; Name='chrome' })
            $rows | Format-Styled -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var raw = result[0].ToString() ?? string.Empty;
        Assert.Contains("chrome", StripAnsi(raw));
        Assert.Matches("\\[[0-9;]*m", raw); // the default sheet styled the row
    }

    [Fact]
    public void NamedBuiltin_ResolvesViaStyleAlias()
    {
        var pwsh = _fixture.AcquireFresh();
        // -Style ps loads the embedded ps.css; a 'busy' process picks up its .busy rule.
        var script = """
            $rows = @([pscustomobject]@{ PSTypeName='Process'; Name='chrome'; class='busy' })
            $rows | Format-Styled -Style ps -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        Assert.Matches("\\[[0-9;]*m", result[0].ToString() ?? string.Empty);
    }

    [Fact]
    public void UserOverride_CascadesOverBuiltinDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psbash-styles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prior = Environment.GetEnvironmentVariable("PSBASH_STYLE_PATH");
        try
        {
            // A user `default.css` adds a `.zzz` rule the built-in default lacks. The row's
            // only possible style source is that .zzz rule, so it renders with ANSI ONLY if
            // the user override cascaded in (it would be plain text otherwise).
            File.WriteAllText(Path.Combine(dir, "default.css"), ".zzz { color: green; font-weight: bold }");
            Environment.SetEnvironmentVariable("PSBASH_STYLE_PATH", dir);

            var pwsh = _fixture.AcquireFresh();
            var script = """
                $rows = @([pscustomobject]@{ PSTypeName='Plain'; Name='x'; class='zzz' })
                $rows | Format-Styled -Property Name
                """;

            var result = pwsh.AddScript(script).Invoke();

            Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
            Assert.Single(result);
            Assert.Matches("\\[[0-9;]*m", result[0].ToString() ?? string.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_STYLE_PATH", prior);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void UnknownStylesheetName_SurfacesError()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(
            "@([pscustomobject]@{ Name='x' }) | Format-Styled -Style does-not-exist-12345 -Property Name").Invoke();

        Assert.True(pwsh.HadErrors);
        Assert.Empty(result);
    }
}
