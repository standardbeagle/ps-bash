using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashRev</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashRevCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashRev</c> function — reverses each line of
/// input. Only <c>--help</c> is a flag; the function has no other flags.
///
/// Failure-surface axes covered (per Directive 3): empty input,
/// unicode (non-ASCII characters), file mode + pipeline mode + alias
/// resolution, missing-file error continuation, multi-line pipeline split,
/// <c>--help</c>, and a quoting/injection probe per Directive 12.
/// </summary>
public class InvokeBashRevCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashRevCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-rev-{Guid.NewGuid():N}".Substring(0, 22));
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
    public void Rev_EmptyPipeline_EmitsNothing()
    {
        // No operands, no pipeline input -> no output, no error.
        var lines = RunLines("Invoke-BashRev");
        Assert.Empty(lines);
    }

    [Fact]
    public void Rev_SingleLineAscii_Pipeline_IsReversed()
    {
        var lines = RunLines("'hello' | Invoke-BashRev");
        Assert.Single(lines);
        Assert.Equal("olleh", lines[0]);
    }

    [Fact]
    public void Rev_MultiLinePipelineItem_SplitsAndReverses()
    {
        // A single multi-line BashText item is split into its lines and each
        // is reversed individually — matching the psm1 oracle's defensive
        // split path.
        var lines = RunLines("\"abc`ndef`nghi\" | Invoke-BashRev");
        Assert.Equal(new[] { "cba", "fed", "ihg" }, lines);
    }

    [Fact]
    public void Rev_MultiplePipelineItems_EachReversed()
    {
        var lines = RunLines("'one','two','three' | Invoke-BashRev");
        Assert.Equal(new[] { "eno", "owt", "eerht" }, lines);
    }

    [Fact]
    public void Rev_FileMode_AsciiContent_ReversesEachLine()
    {
        var file = Path.Combine(_tmpDir, "ascii.txt");
        File.WriteAllText(file, "alpha\nbeta\ngamma\n");
        var lines = RunLines($"Invoke-BashRev '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "ahpla", "ateb", "ammag" }, lines);
    }

    [Fact]
    public void Rev_FileMode_CrlfNormalized()
    {
        var file = Path.Combine(_tmpDir, "crlf.txt");
        File.WriteAllText(file, "one\r\ntwo\r\n");
        var lines = RunLines($"Invoke-BashRev '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "eno", "owt" }, lines);
    }

    [Fact]
    public void Rev_FileMode_Unicode_NonAscii()
    {
        // Reversal is a char[] reversal — same as the psm1 oracle's
        // [Array]::Reverse on .ToCharArray(). This intentionally treats each
        // UTF-16 code unit as a unit (bash `rev` has a similar caveat in its
        // byte-oriented form). The point of the test is bit-for-bit parity
        // with the oracle, not Unicode grapheme correctness.
        var file = Path.Combine(_tmpDir, "uni.txt");
        File.WriteAllText(file, "héllo\n", new System.Text.UTF8Encoding(false));
        var lines = RunLines($"Invoke-BashRev '{file.Replace("'", "''")}'");
        Assert.Single(lines);
        // "héllo" reversed by char: "olléh"
        Assert.Equal("olléh", lines[0]);
    }

    [Fact]
    public void Rev_FileMode_MissingFile_DoesNotThrow_NoOutput()
    {
        // Per the psm1 oracle: a missing file emits a bash-style error via
        // Write-BashError and continues. We verify the cmdlet does not throw
        // and produces no output for the missing file.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashRev '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Rev_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashRev --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("rev", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rev_AliasResolution_RevWorks()
    {
        // The psm1 module registers `Set-Alias rev Invoke-BashRev`. Because
        // the binary cmdlet loads before psm1 runs, the alias resolves to the
        // cmdlet.
        var lines = RunLines("'foobar' | rev");
        Assert.Single(lines);
        Assert.Equal("raboof", lines[0]);
    }

    [Fact]
    public void Rev_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. The cmdlet routes
        // file operands through SessionState.Path, never via string-concat
        // into a script body. A file whose NAME contains `; $(throw 'x')`
        // is treated as a literal path → "no such file" → no script side
        // effect.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var lines = RunLines(
            $"Invoke-BashRev '{probe.Replace("'", "''")}' 2>$null");
        // No output. If the probe had been evaluated as script, the test
        // would have thrown or 'pwned' would appear in the output.
        Assert.Empty(lines);
    }

    [Fact]
    public void Rev_FileMode_EmptyFile_EmitsNothing()
    {
        var file = Path.Combine(_tmpDir, "empty.txt");
        File.WriteAllText(file, "");
        var lines = RunLines($"Invoke-BashRev '{file.Replace("'", "''")}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Rev_FileMode_SingleNewlineFile_EmitsOneEmptyLine()
    {
        // File of exactly "\n" -> one empty line (reversed empty == empty).
        var file = Path.Combine(_tmpDir, "nl.txt");
        File.WriteAllText(file, "\n");
        var lines = RunLines($"Invoke-BashRev '{file.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.Equal("", lines[0]);
    }
}
