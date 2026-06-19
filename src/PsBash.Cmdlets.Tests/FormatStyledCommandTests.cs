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
        // -Style ps loads the embedded ps.pcss; a 'busy' process picks up its .busy rule.
        var script = """
            $rows = @([pscustomobject]@{ PSTypeName='Process'; Name='chrome'; class='busy' })
            $rows | Format-Styled -Style ps -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        Assert.Matches("\\[[0-9;]*m", result[0].ToString() ?? string.Empty);
    }

    [Theory]
    [InlineData("fs")]
    [InlineData("procsvc")]
    [InlineData("object")]
    [InlineData("error")]
    public void InteractiveBuiltinSheet_ParsesAndRenders(string style)
    {
        // The button/expansion sheets declare the `command:` interaction property and the
        // :focused / :expanded pseudo-classes. Parsing them through the static grid path proves
        // the InteractionProperties descriptor is registered (else the parser throws
        // "Unknown property 'command'") and that the sheet is a valid cascade input. The bindings
        // are inert here (no input loop) — we only assert the render path stays clean and styled.
        var pwsh = _fixture.AcquireFresh();
        var script = $$"""
            $rows = @(
              [pscustomobject]@{ PSTypeName='Process'; Name='chrome'; class='busy' },
              [pscustomobject]@{ PSTypeName='Process'; Name='vim';    class='idle' }
            )
            $rows | Format-Styled -Style {{style}} -Property Name
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var raw = result[0].ToString() ?? string.Empty;
        Assert.Contains("chrome", StripAnsi(raw));
        Assert.Matches("\\[[0-9;]*m", raw);
    }

    [Fact]
    public void UserOverride_CascadesOverBuiltinDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psbash-styles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prior = Environment.GetEnvironmentVariable("PSBASH_STYLE_PATH");
        try
        {
            // A user `default.pcss` adds a `.zzz` rule the built-in default lacks. The row's
            // only possible style source is that .zzz rule, so it renders with ANSI ONLY if
            // the user override cascaded in (it would be plain text otherwise).
            File.WriteAllText(Path.Combine(dir, "default.pcss"), ".zzz { color: green; font-weight: bold }");
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
    public void UserOverride_LegacyCssExtension_StillLoadsAsFallback()
    {
        // Back-compat: a user override authored as `<name>.css` (pre-`.pcss`-rename) still cascades.
        var dir = Path.Combine(Path.GetTempPath(), "psbash-styles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prior = Environment.GetEnvironmentVariable("PSBASH_STYLE_PATH");
        try
        {
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

    // A bold SGR is the parameter `1` bounded by `[`/`;` on the left and `;`/`m` on the right,
    // so it is not mistaken for the `1` inside a colour code like `31` (red) or `36` (cyan).
    private const string BoldSgr = @"\x1b\[(?:[0-9;]*;)?1(?:;[0-9;]*)?m";
    private const string UnderlineSgr = @"\x1b\[(?:[0-9;]*;)?4(?:;[0-9;]*)?m";

    [Fact]
    public void List_RendersPropertyNamesBoldInTwoColumnGrid()
    {
        var pwsh = _fixture.AcquireFresh();
        // -List with no stylesheet -> built-in `list` sheet: bold property names, plain values.
        var script = """
            $o = [pscustomobject]@{ Name='nginx'; Status='Running' }
            $o | Format-Styled -List
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var raw = result[0].ToString() ?? string.Empty;
        var plain = StripAnsi(raw);
        // Both the property names and their values are present (name/value grid cells).
        Assert.Contains("Name", plain);
        Assert.Contains("nginx", plain);
        Assert.Contains("Status", plain);
        Assert.Contains("Running", plain);
        // The built-in list sheet renders property names bold.
        Assert.Matches(BoldSgr, raw);
    }

    [Fact]
    public void Table_RendersBoldUnderlinedHeaderRow()
    {
        var pwsh = _fixture.AcquireFresh();
        // -Table with no stylesheet -> built-in `table` sheet: bold+underlined header row.
        var script = """
            $rows = @(
              [pscustomobject]@{ Name='nginx'; Id=42 },
              [pscustomobject]@{ Name='redis'; Id=99 }
            )
            $rows | Format-Styled -Table -Property Name,Id
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var raw = result[0].ToString() ?? string.Empty;
        var plain = StripAnsi(raw);
        // Header (the property names) plus every data row are present.
        Assert.Contains("Name", plain);
        Assert.Contains("Id", plain);
        Assert.Contains("nginx", plain);
        Assert.Contains("redis", plain);
        // The built-in table sheet renders the header row bold and underlined.
        Assert.Matches(BoldSgr, raw);
        Assert.Matches(UnderlineSgr, raw);
    }

    [Fact]
    public void Auto_MultipleObjects_RenderAsTable_HeaderAppearsOnce()
    {
        var pwsh = _fixture.AcquireFresh();
        // No -List/-Table: multiple objects auto-select TABLE. A property name then appears once
        // (the header row) rather than once per object (which is how the LIST layout repeats keys).
        var script = """
            $rows = @(
              [pscustomobject]@{ Alpha='a1'; Bravo='b1' },
              [pscustomobject]@{ Alpha='a2'; Bravo='b2' }
            )
            $rows | Format-Styled -Property Alpha,Bravo
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var plain = StripAnsi(result[0].ToString() ?? string.Empty);
        var lines = plain.Split('\n');
        // Header row only — exactly one line mentions the property name "Alpha".
        Assert.Equal(1, lines.Count(l => l.Contains("Alpha")));
        Assert.Contains("a1", plain);
        Assert.Contains("a2", plain);
    }

    [Fact]
    public void Auto_SingleObject_RendersAsList_KeyAndValueShareLine()
    {
        var pwsh = _fixture.AcquireFresh();
        // No -List/-Table: a single object auto-selects LIST, so each property's key and value
        // sit on the same line (the TABLE layout would put the key in a header row above the value).
        var script = """
            $o = [pscustomobject]@{ Alpha='aval'; Bravo='bval' }
            $o | Format-Styled -Property Alpha,Bravo
            """;

        var result = pwsh.AddScript(script).Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        var lines = StripAnsi(result[0].ToString() ?? string.Empty).Split('\n');
        Assert.Contains(lines, l => l.Contains("Alpha") && l.Contains("aval"));
        Assert.Contains(lines, l => l.Contains("Bravo") && l.Contains("bval"));
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
