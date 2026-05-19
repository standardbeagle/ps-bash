using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashPaste</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashPasteCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashPaste</c> function — merges corresponding
/// lines of multiple files using a delimiter (tab by default).
///
/// Failure-surface axes covered (per Directive 3): empty input, unicode,
/// missing file (Directive 14), multi-operand, two-file equal length,
/// two-file unequal length (shorter padded with empty), single-char delim,
/// multi-char delim (oracle bit-for-bit — joined whole as one string), serial
/// mode, pipeline input ignored, <c>--help</c>, alias resolution, and a
/// quoting/injection probe per Directive 12.
/// </summary>
public class InvokeBashPasteCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmpDir;
    private readonly SharedPwshFixture _fixture;

    public InvokeBashPasteCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-paste-{Guid.NewGuid():N}".Substring(0, 24));
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

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_tmpDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Q(string s) => s.Replace("'", "''");

    [Fact]
    public void Paste_NoOperands_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashPaste");
        Assert.Empty(lines);
    }

    [Fact]
    public void Paste_TwoFiles_EqualLength_JoinsWithTab()
    {
        string a = WriteFile("a.txt", "1\n2\n3\n");
        string b = WriteFile("b.txt", "x\ny\nz\n");
        var lines = RunLines($"Invoke-BashPaste '{Q(a)}' '{Q(b)}'");
        Assert.Equal(new[] { "1\tx", "2\ty", "3\tz" }, lines);
    }

    [Fact]
    public void Paste_TwoFiles_DifferentLength_ShorterPaddedEmpty()
    {
        // Shorter file's missing rows are filled with empty strings,
        // preserving the delimiter (tab) — matches psm1 oracle.
        string a = WriteFile("a.txt", "1\n2\n3\n");
        string b = WriteFile("b.txt", "x\n");
        var lines = RunLines($"Invoke-BashPaste '{Q(a)}' '{Q(b)}'");
        Assert.Equal(new[] { "1\tx", "2\t", "3\t" }, lines);
    }

    [Fact]
    public void Paste_SingleCharDelimiter_DashD_Comma()
    {
        string a = WriteFile("a.txt", "1\n2\n");
        string b = WriteFile("b.txt", "x\ny\n");
        var lines = RunLines($"Invoke-BashPaste -d ',' '{Q(a)}' '{Q(b)}'");
        Assert.Equal(new[] { "1,x", "2,y" }, lines);
    }

    [Fact]
    public void Paste_MultiCharDelimiter_OraclePreservesAsLiteral()
    {
        // Oracle behavior: psm1 stored the entire -d value as a single string
        // and -join'd with it. GNU paste cycles per-char ":,"; the oracle does
        // not. This test pins the oracle's behavior bit-for-bit. (See class
        // docstring + cmdlet docstring for rationale.)
        string a = WriteFile("a.txt", "1\n2\n");
        string b = WriteFile("b.txt", "x\ny\n");
        string c = WriteFile("c.txt", "A\nB\n");
        // -d ":,"  → fields separated by literal ":," between each pair.
        var lines = RunLines($"Invoke-BashPaste -d ':,' '{Q(a)}' '{Q(b)}' '{Q(c)}'");
        Assert.Equal(new[] { "1:,x:,A", "2:,y:,B" }, lines);
    }

    [Fact]
    public void Paste_SerialMode_DashS_OneLinePerFile()
    {
        string a = WriteFile("a.txt", "1\n2\n3\n");
        string b = WriteFile("b.txt", "x\ny\n");
        var lines = RunLines($"Invoke-BashPaste -s '{Q(a)}' '{Q(b)}'");
        // File a → "1\t2\t3"; file b → "x\ty".
        Assert.Equal(new[] { "1\t2\t3", "x\ty" }, lines);
    }

    [Fact]
    public void Paste_SerialMode_CustomDelimiter()
    {
        string a = WriteFile("a.txt", "1\n2\n3\n");
        var lines = RunLines($"Invoke-BashPaste -s -d ',' '{Q(a)}'");
        Assert.Equal(new[] { "1,2,3" }, lines);
    }

    [Fact]
    public void Paste_PipelineInputIgnored_NoOperands_NoOutput()
    {
        // The psm1 oracle never consumed pipeline input — paste's domain is
        // file operands. Pipeline input with no operands → nothing emitted.
        var lines = RunLines("'one','two' | Invoke-BashPaste");
        Assert.Empty(lines);
    }

    [Fact]
    public void Paste_MissingFile_NoOutput_NoThrow()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "nope.txt");
        var result = pwsh.AddScript(
            $"Invoke-BashPaste '{Q(missing)}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        // Oracle returns early on null from Read-BashFileLines.
        Assert.Empty(result);
    }

    [Fact]
    public void Paste_UnicodeContent_NonAscii_JoinedCorrectly()
    {
        string a = WriteFile("a.txt", "héllo\nwörld\n");
        string b = WriteFile("b.txt", "café\nüber\n");
        var lines = RunLines($"Invoke-BashPaste '{Q(a)}' '{Q(b)}'");
        Assert.Equal(new[] { "héllo\tcafé", "wörld\tüber" }, lines);
    }

    [Fact]
    public void Paste_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashPaste --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("paste", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Paste_AliasResolution_PasteWorks()
    {
        // psm1 registers `Set-Alias paste Invoke-BashPaste` and the binary
        // cmdlet loads before psm1 runs, so the alias resolves to the cmdlet.
        string a = WriteFile("a.txt", "1\n");
        string b = WriteFile("b.txt", "x\n");
        var lines = RunLines($"paste '{Q(a)}' '{Q(b)}'");
        Assert.Equal(new[] { "1\tx" }, lines);
    }

    [Fact]
    public void Paste_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must be treated as a literal path. Routing through
        // SessionState.Path's resolver means a name like "; $(throw 'pwn')"
        // is just a missing file — no script side effect.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"Invoke-BashPaste '{Q(probe)}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        // If the probe had been re-parsed as script, the test would have
        // thrown or 'pwned' would appear. Empty output proves containment.
        Assert.Empty(result);
    }

    [Fact]
    public void Paste_DashDJoinedForm_DashDComma_Works()
    {
        // Joined form: `-d,` (no space) — oracle supports this via
        // `-cmatch '^-d(.+)$'`. PowerShell would parse a bare `-d,` token as
        // ambiguous (the `,` is the array operator), so the bash transpiler's
        // EmitPassthrough quotes flags containing commas: a bash
        // `paste -d, a b` arrives as `Invoke-BashPaste "-d," a b`. We
        // exercise that exact quoted form here.
        string a = WriteFile("a.txt", "1\n2\n");
        string b = WriteFile("b.txt", "x\ny\n");
        var lines = RunLines($"Invoke-BashPaste '-d,' '{Q(a)}' '{Q(b)}'");
        Assert.Equal(new[] { "1,x", "2,y" }, lines);
    }

    [Fact]
    public void Paste_EmptyFile_ProducesNoOutput()
    {
        // An empty file has zero lines. Two files where one is empty:
        // maxLines comes from the non-empty file; the empty one pads.
        string a = WriteFile("a.txt", "");
        string b = WriteFile("b.txt", "x\ny\n");
        var lines = RunLines($"Invoke-BashPaste '{Q(a)}' '{Q(b)}'");
        // Padding semantics: empty file contributes "" for every row.
        Assert.Equal(new[] { "\tx", "\ty" }, lines);
    }
}
