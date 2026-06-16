using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashUnexpand</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashUnexpandCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashUnexpand</c> function — converts runs of
/// spaces to tabs. Default converts only leading runs at multiples of the tab
/// width (default 8); <c>-a</c> converts runs anywhere on the line.
///
/// Failure-surface axes covered (per Directive 3): empty input, unicode
/// content, file mode + pipeline mode + alias resolution, missing-file error
/// continuation, <c>--help</c>, partial-run preservation, and an
/// injection probe per Directive 12.
/// </summary>
public class InvokeBashUnexpandCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashUnexpandCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-unexp-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    [Fact]
    public void Unexpand_EightLeadingSpaces_BecomeOneTab()
    {
        // Default tab width 8 — eight leading spaces collapse to a single tab.
        var lines = RunLines("'        hello' | Invoke-BashUnexpand");
        Assert.Single(lines);
        Assert.Equal("\thello", lines[0]);
    }

    [Fact]
    public void Unexpand_SixteenLeadingSpaces_BecomeTwoTabs()
    {
        var lines = RunLines("'                code' | Invoke-BashUnexpand");
        Assert.Single(lines);
        Assert.Equal("\t\tcode", lines[0]);
    }

    [Fact]
    public void Unexpand_PartialLeadingRun_StaysAsSpaces()
    {
        // Five spaces (< 8) cannot become a tab — preserved as spaces.
        var lines = RunLines("'     half' | Invoke-BashUnexpand");
        Assert.Single(lines);
        Assert.Equal("     half", lines[0]);
    }

    [Fact]
    public void Unexpand_TenLeadingSpaces_BecomeOneTabAndTwoSpaces()
    {
        // 10 leading spaces = 1 tab (8) + 2 remainder spaces.
        var lines = RunLines("'          mix' | Invoke-BashUnexpand");
        Assert.Single(lines);
        Assert.Equal("\t  mix", lines[0]);
    }

    [Fact]
    public void Unexpand_TabFour_FourLeadingSpaces_BecomeOneTab()
    {
        var lines = RunLines("'    four' | Invoke-BashUnexpand -t 4");
        Assert.Single(lines);
        Assert.Equal("\tfour", lines[0]);
    }

    [Fact]
    public void Unexpand_TabFourJoined_FourLeadingSpaces_BecomeOneTab()
    {
        var lines = RunLines("'    four' | Invoke-BashUnexpand -t4");
        Assert.Single(lines);
        Assert.Equal("\tfour", lines[0]);
    }

    [Fact]
    public void Unexpand_LongFormTabs_FourLeadingSpaces_BecomeOneTab()
    {
        var lines = RunLines("'    four' | Invoke-BashUnexpand --tabs=4");
        Assert.Single(lines);
        Assert.Equal("\tfour", lines[0]);
    }

    [Fact]
    public void Unexpand_AllMode_InteriorRunBecomesTabAtBoundary()
    {
        // -a mode: a run of 8 spaces starting at col 0 becomes one tab (col
        // crosses 8). Default mode would also do this since it's the leading
        // run; the all-mode discriminator is the interior run handling below.
        var lines = RunLines("'        foo' | Invoke-BashUnexpand -a");
        Assert.Single(lines);
        Assert.Equal("\tfoo", lines[0]);
    }

    [Fact]
    public void Unexpand_AllMode_InteriorSpaceRun_PreservedWhenNotAtBoundary()
    {
        // Single interior space (run of 1) is never converted to tab in -a
        // mode (oracle requires spaceRun >= 2). Verify it's preserved literally.
        var lines = RunLines("'a b c' | Invoke-BashUnexpand -a");
        Assert.Single(lines);
        Assert.Equal("a b c", lines[0]);
    }

    [Fact]
    public void Unexpand_DefaultMode_InteriorSpaces_Preserved()
    {
        // Default (no -a) only touches leading spaces. Interior runs left
        // alone even if they'd cross a tabstop.
        var lines = RunLines("'        a        b' | Invoke-BashUnexpand");
        Assert.Single(lines);
        Assert.Equal("\ta        b", lines[0]);
    }

    [Fact]
    public void Unexpand_NoSpaces_LineUnchanged()
    {
        var lines = RunLines("'no-spaces-here' | Invoke-BashUnexpand");
        Assert.Single(lines);
        Assert.Equal("no-spaces-here", lines[0]);
    }

    [Fact]
    public void Unexpand_FileMode_MultiLine_EachLineTransformed()
    {
        var file = Path.Combine(_tmpDir, "in.txt");
        File.WriteAllText(file, "        alpha\n    beta\n                gamma\n");
        var lines = RunLines($"Invoke-BashUnexpand '{file.Replace("'", "''")}'");
        Assert.Equal(new[] {
            "\talpha",
            "    beta",
            "\t\tgamma",
        }, lines);
    }

    [Fact]
    public void Unexpand_PipelineMultiItem_EachTransformed()
    {
        var lines = RunLines("'        a','        b' | Invoke-BashUnexpand");
        Assert.Equal(new[] { "\ta", "\tb" }, lines);
    }

    [Fact]
    public void Unexpand_FileMode_MissingFile_NoOutput_NoThrow()
    {
        // Per the psm1 oracle: a missing file emits a bash-style error via
        // Write-BashError and continues. Verify cmdlet doesn't throw and the
        // output stream is empty.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "nope.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashUnexpand '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Unexpand_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashUnexpand --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("unexpand", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unexpand_AliasResolution_UnexpandWorks()
    {
        // The psm1 module registers `Set-Alias unexpand Invoke-BashUnexpand`.
        // Because the binary cmdlet loads before psm1 runs, the alias resolves
        // to the cmdlet.
        var lines = RunLines("'        hi' | unexpand");
        Assert.Single(lines);
        Assert.Equal("\thi", lines[0]);
    }

    [Fact]
    public void Unexpand_Unicode_LeadingSpacesCompressed_RestPreserved()
    {
        // Eight leading spaces collapse to a tab; non-ASCII tail preserved.
        var lines = RunLines("'        héllo' | Invoke-BashUnexpand");
        Assert.Single(lines);
        Assert.Equal("\théllo", lines[0]);
    }

    [Fact]
    public void Unexpand_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. The cmdlet routes
        // file operands through SessionState.Path, never via string-concat
        // into a script body. A path whose NAME contains `; $(throw 'x')`
        // is treated as a literal path → "no such file" → no script side
        // effect.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var lines = RunLines(
            $"Invoke-BashUnexpand '{probe.Replace("'", "''")}' 2>$null");
        Assert.Empty(lines);
    }

    [Fact]
    public void Unexpand_UnrecognizedLongOption_ExitCode2_NoOutput()
    {
        // Completely unknown option → bash-parity "unrecognized option" error,
        // LASTEXITCODE=2, no stdout.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            "Invoke-BashUnexpand --bogus 2>$null; $LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        Assert.Single(result);
        Assert.Equal(2, (int)result[0].BaseObject);
    }

    [Fact]
    public void Unexpand_FirstOnlyFlag_StillProcessesInput()
    {
        // --first-only IS implemented in ps-bash (sets leading-only mode,
        // same as the default). Regression guard: verifying the flag doesn't
        // break normal operation.
        var lines = RunLines("'        hello' | Invoke-BashUnexpand --first-only");
        Assert.Single(lines);
        // Eight leading spaces → one tab (default tab width 8).
        Assert.Equal("\thello", lines[0]);
    }
}
