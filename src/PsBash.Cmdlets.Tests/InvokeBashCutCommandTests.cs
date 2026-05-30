using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashCut</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashCutCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashCut</c> function — extract fields
/// (<c>-f LIST</c> with <c>-d DELIM</c>) or character positions
/// (<c>-c LIST</c>) from each input line.
///
/// Failure-surface axes covered (per Directive 3): empty pipeline,
/// file + pipeline mode, custom delimiter (comma), char-range,
/// field-range, comma-separated lists, missing-delim line edge case,
/// CRLF normalization, unicode content, missing file (axis 14),
/// <c>--help</c>, alias resolution, and an injection probe per
/// Directive 12.
/// </summary>
public class InvokeBashCutCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashCutCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-cut-{Guid.NewGuid():N}".Substring(0, 22));
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
    public void Cut_EmptyPipeline_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashCut -f 1");
        Assert.Empty(lines);
    }

    private (string[] outLines, string[] errors) RunWithErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        var outLines = result.Select(o =>
            o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
        return (outLines, errs);
    }

    [Fact]
    public void Cut_ValidButUnsupportedFlag_S_EmitsSpecificRefusal_NotFileError()
    {
        // -s (--only-delimited) is a real cut flag ps-bash doesn't implement.
        // It must say so specifically, NOT treat -s as a missing file.
        var (_, errs) = RunWithErrors("'a,b' | Invoke-BashCut -s -d ',' -f 1");
        Assert.Contains(errs, m => m.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                                   && m.Contains("-s", StringComparison.Ordinal));
        Assert.DoesNotContain(errs, m => m.Contains("No such file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cut_UnrecognizedLongOption_BashParityMessage()
    {
        var (_, errs) = RunWithErrors("'a,b' | Invoke-BashCut --bogus -d ',' -f 1");
        Assert.Contains(errs, m => m.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
                                   && m.Contains("--bogus", StringComparison.Ordinal));
    }

    [Fact]
    public void Cut_Pipeline_SingleField_TabDelim()
    {
        // Default delimiter is tab — `-f 1` picks the first field.
        var lines = RunLines("\"a`tb`tc\" | Invoke-BashCut -f 1");
        Assert.Single(lines);
        Assert.Equal("a", lines[0]);
    }

    [Fact]
    public void Cut_Pipeline_FieldRange_TabDelim()
    {
        // `-f 2-3` selects fields 2 and 3 joined by the delimiter (tab).
        var lines = RunLines("\"a`tb`tc`td\" | Invoke-BashCut -f 2-3");
        Assert.Single(lines);
        Assert.Equal("b\tc", lines[0]);
    }

    [Fact]
    public void Cut_Pipeline_CommaList_TabDelim()
    {
        // `-f 1,3` picks fields 1 and 3 joined by the delimiter (tab).
        var lines = RunLines("\"a`tb`tc`td\" | Invoke-BashCut -f '1,3'");
        Assert.Single(lines);
        Assert.Equal("a\tc", lines[0]);
    }

    [Fact]
    public void Cut_Pipeline_CustomDelim_Comma()
    {
        // `-d ','` overrides the default tab delimiter.
        var lines = RunLines("'a,b,c,d' | Invoke-BashCut -d ',' -f 2");
        Assert.Single(lines);
        Assert.Equal("b", lines[0]);
    }

    [Fact]
    public void Cut_Pipeline_CharRange()
    {
        // `-c 1-5` picks character positions 1..5 (1-based, inclusive).
        var lines = RunLines("'hello world' | Invoke-BashCut -c 1-5");
        Assert.Single(lines);
        Assert.Equal("hello", lines[0]);
    }

    [Fact]
    public void Cut_Pipeline_CharCommaList()
    {
        // `-c 1,3,5` picks individual char positions.
        var lines = RunLines("'abcdef' | Invoke-BashCut -c '1,3,5'");
        Assert.Single(lines);
        Assert.Equal("ace", lines[0]);
    }

    [Fact]
    public void Cut_Pipeline_EmptyLine_Field_EmitsEmpty()
    {
        // Empty input line: split on tab yields one empty field; `-f 1`
        // emits the empty string.
        var lines = RunLines("'' | Invoke-BashCut -f 1");
        Assert.Single(lines);
        Assert.Equal("", lines[0]);
    }

    [Fact]
    public void Cut_Pipeline_MissingDelim_Field1_ReturnsWholeLine()
    {
        // A line without the delimiter: Split returns the whole line as one
        // field. `-f 1` returns it; `-f 2` returns nothing (no field 2).
        var f1 = RunLines("'no-tab-here' | Invoke-BashCut -d ',' -f 1");
        Assert.Single(f1);
        Assert.Equal("no-tab-here", f1[0]);

        var f2 = RunLines("'no-tab-here' | Invoke-BashCut -d ',' -f 2");
        Assert.Single(f2);
        Assert.Equal("", f2[0]);
    }

    [Fact]
    public void Cut_FileMode_FieldExtract()
    {
        var file = Path.Combine(_tmpDir, "csv.txt");
        File.WriteAllText(file, "a,b,c\nd,e,f\n");
        var lines = RunLines(
            $"Invoke-BashCut -d ',' -f 2 '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "b", "e" }, lines);
    }

    [Fact]
    public void Cut_FileMode_CrlfNormalized()
    {
        var file = Path.Combine(_tmpDir, "crlf.txt");
        File.WriteAllText(file, "a,b\r\nc,d\r\n");
        var lines = RunLines(
            $"Invoke-BashCut -d ',' -f 1 '{file.Replace("'", "''")}'");
        Assert.Equal(new[] { "a", "c" }, lines);
    }

    [Fact]
    public void Cut_FileMode_Unicode_CharPosition()
    {
        var file = Path.Combine(_tmpDir, "uni.txt");
        File.WriteAllText(file, "héllo\n",
            new System.Text.UTF8Encoding(false));
        // Char-position is per-UTF16-code-unit (oracle parity — same as the
        // psm1 oracle's String indexer).
        var lines = RunLines(
            $"Invoke-BashCut -c 1-3 '{file.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.Equal("hél", lines[0]);
    }

    [Fact]
    public void Cut_FileMode_MissingFile_NoOutput()
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "missing.txt").Replace("'", "''");
        var result = pwsh.AddScript(
            $"Invoke-BashCut -f 1 '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Cut_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashCut --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("cut", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cut_AliasResolution_CutWorks()
    {
        // The psm1 module registers `Set-Alias cut Invoke-BashCut`.
        var lines = RunLines("'a,b,c' | cut -d ',' -f 2");
        Assert.Single(lines);
        Assert.Equal("b", lines[0]);
    }

    [Fact]
    public void Cut_InjectionProbe_OperandWithSemicolonsAndDollarParen_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. A file name
        // containing `; $(throw 'INJECTED')` is treated as a literal path
        // → "no such file" → no script side effect.
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var lines = RunLines(
            $"Invoke-BashCut -f 1 '{probe.Replace("'", "''")}' 2>$null");
        Assert.Empty(lines);
    }
}
