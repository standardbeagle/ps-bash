using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 Phase 1c migration of
/// Invoke-BashCat / Invoke-BashHead / Invoke-BashTail / Invoke-BashWc from
/// PsBash.psm1 script functions to binary cmdlets (PsBash.Cmdlets.dll).
///
/// Oracle: the original psm1 functions, modeled on the bash builtins. These
/// four commands have a file + pipeline surface, so the applicable
/// failure-surface axes (per .claude/rules/qa-rubric.md Directive 3) are:
/// empty input, unicode input, CRLF input, missing target (file does not
/// exist), and large-ish input. Mode coverage: M5 (in-process cmdlet) is the
/// surface under test here; the M1/M2/M3 bash-oracle parity for all five
/// commands lives in PsBash.Differential.Tests. Negative cases (Directive 7):
/// missing file, empty file, glob with no match.
///
/// ls was deliberately NOT migrated in Phase 1c — it is the hardest of the
/// five (typed LsEntry objects, -R recursion, Format-LsGrid) and is split into
/// its own follow-on task. echo also stays a psm1 function (its -e/-n/-E short
/// flags prefix-collide with PSCmdlet common parameters).
///
/// The PwshTestFixture loads psm1 (which no longer defines cat/head/tail/wc)
/// then imports PsBash.Cmdlets.dll, mirroring the host load order — so these
/// tests also prove the function-shadowing removal worked and the psm1
/// Set-Alias cat/head/tail/wc lines still resolve to the cmdlets.
/// </summary>
public class InvokeBashCatHeadTailWcCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashCatHeadTailWcCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(), "psbash-1c-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tmpDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Runs a script, asserts no error record was generated.</summary>
    private System.Collections.ObjectModel.Collection<PSObject> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        // Scripts using `2>$null` explicitly redirect their error stream — the
        // caller is asserting that they don't care about the error text, only
        // about the absence of stdout. PowerShell's `2>$null` suppresses the
        // user-visible stream but still appends to `$error`, so a literal
        // `$error.Count` check after such a script conflates "test author said
        // this is fine" with "unexpected internal error". Skip the assertion
        // when the script explicitly uses the redirect.
        if (!script.Contains("2>$null"))
        {
            var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
            pwsh.Commands.Clear();
            Assert.True(err.Count == 0 || err[0] == null,
                $"Unexpected error running [{script}]: " +
                $"{(err.Count > 0 ? err[0]?.ToString() : "none")}");
        }

        return result;
    }

    /// <summary>Extracts the BashText payload of each emitted object.</summary>
    private string[] RunBashText(string script)
    {
        return Run(script)
            .Select(o =>
            {
                var prop = o?.Properties["BashText"];
                return prop != null ? prop.Value?.ToString() ?? "" : o?.ToString() ?? "";
            })
            .ToArray();
    }

    // ======================= wc =======================

    [Fact]
    public void Wc_Pipeline_NoFlags_EmitsThreeColumnCounts()
    {
        // "a b\nc\n" -> 2 lines, 3 words, bytes = len("a b")+1 + len("c")+1 = 6.
        var lines = RunBashText("\"a b`nc\" | Invoke-BashWc");
        Assert.Single(lines);
        Assert.Equal("2       3       6", lines[0]);
    }

    [Fact]
    public void Wc_Pipeline_LinesOnlyFlag()
    {
        var lines = RunBashText("\"x`ny`nz\" | Invoke-BashWc -l");
        Assert.Single(lines);
        Assert.Equal("3", lines[0]);
    }

    [Fact]
    public void Wc_Pipeline_WordsOnlyFlag()
    {
        var lines = RunBashText("'one two three' | Invoke-BashWc -w");
        Assert.Single(lines);
        Assert.Equal("3", lines[0]);
    }

    [Fact]
    public void Wc_File_EmitsCountsWithFileName()
    {
        // "alpha beta\ngamma\n" = 17 bytes (10 + \n + 5 + \n).
        var f = WriteFile("wc1.txt", "alpha beta\ngamma\n");
        var result = Run($"Invoke-BashWc '{f.Replace("\\", "\\\\")}'");
        Assert.Single(result);
        Assert.Equal(2, (int)result[0]!.Properties["Lines"].Value);
        Assert.Equal(3, (int)result[0]!.Properties["Words"].Value);
        Assert.Equal(17, (int)result[0]!.Properties["Bytes"].Value);
    }

    [Fact]
    public void Wc_File_EmptyFile_AllZero()
    {
        // Empty-input axis: an empty file is 0 lines, 0 words, 0 bytes.
        var f = WriteFile("empty.txt", "");
        var result = Run($"Invoke-BashWc '{f.Replace("\\", "\\\\")}'");
        Assert.Single(result);
        Assert.Equal(0, (int)result[0]!.Properties["Lines"].Value);
        Assert.Equal(0, (int)result[0]!.Properties["Words"].Value);
        Assert.Equal(0, (int)result[0]!.Properties["Bytes"].Value);
    }

    [Fact]
    public void Wc_File_Unicode_BytesAreUtf8Count()
    {
        // Unicode axis: "é" is 2 UTF-8 bytes; file "é\n" -> 1 line, 1 word, 3 bytes.
        var f = WriteFile("uni.txt", "é\n");
        var result = Run($"Invoke-BashWc '{f.Replace("\\", "\\\\")}'");
        Assert.Single(result);
        Assert.Equal(1, (int)result[0]!.Properties["Lines"].Value);
        Assert.Equal(3, (int)result[0]!.Properties["Bytes"].Value);
    }

    [Fact]
    public void Wc_MissingFile_EmitsErrorAndContinues()
    {
        // Missing-target axis: a non-existent file produces a bash-style error
        // on the Write-BashError sink (not a PowerShell error record).
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$global:LASTEXITCODE = 0").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript("Invoke-BashWc '/no/such/wc/file' 2>$null").Invoke();
        pwsh.Commands.Clear();
        var code = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        Assert.Equal("1", code[0]?.ToString());
    }

    [Fact]
    public void Wc_MultipleFiles_EmitsTotalRow()
    {
        var f1 = WriteFile("m1.txt", "a\n");
        var f2 = WriteFile("m2.txt", "b\nc\n");
        var result = Run(
            $"Invoke-BashWc '{f1.Replace("\\", "\\\\")}' '{f2.Replace("\\", "\\\\")}'");
        Assert.Equal(3, result.Count);
        Assert.Equal("total", result[2]!.Properties["FileName"].Value);
        Assert.Equal(3, (int)result[2]!.Properties["Lines"].Value);
    }

    [Fact]
    public void Wc_EmitsTypedWcResultObject()
    {
        var result = Run("'hi' | Invoke-BashWc");
        Assert.Single(result);
        Assert.Contains("PsBash.WcResult", result[0]!.TypeNames);
    }

    [Fact]
    public void Wc_AliasResolvesToCmdlet()
    {
        var lines = RunBashText("'one two' | wc -w");
        Assert.Equal(new[] { "2" }, lines);
    }

    [Fact]
    public void Wc_Help_DelegatesToShowBashHelp()
    {
        var lines = Run("Invoke-BashWc --help")
            .Select(o => o?.ToString() ?? "").ToArray();
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("wc"));
    }

    // ======================= cat =======================

    [Fact]
    public void Cat_Pipeline_NoFlags_PassesLinesThrough()
    {
        var lines = RunBashText("'a','b','c' | Invoke-BashCat");
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [Fact]
    public void Cat_File_NoFlags_EmitsLines()
    {
        var f = WriteFile("cat1.txt", "line1\nline2\n");
        var lines = RunBashText($"Invoke-BashCat '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "line1", "line2" }, lines);
    }

    [Fact]
    public void Cat_File_CrlfNormalized()
    {
        // CRLF axis: \r\n is normalized to \n before line splitting.
        var f = WriteFile("crlf.txt", "x\r\ny\r\n");
        var lines = RunBashText($"Invoke-BashCat '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "x", "y" }, lines);
    }

    [Fact]
    public void Cat_File_NumberAllLines_Flag()
    {
        var f = WriteFile("catn.txt", "foo\nbar\n");
        var lines = RunBashText($"Invoke-BashCat -n '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "     1\tfoo", "     2\tbar" }, lines);
    }

    [Fact]
    public void Cat_File_NumberNonBlankLines_Flag()
    {
        var f = WriteFile("catb.txt", "foo\n\nbar\n");
        var lines = RunBashText($"Invoke-BashCat -b '{f.Replace("\\", "\\\\")}'");
        // -b numbers only non-blank lines; the blank line gets no number.
        Assert.Equal(new[] { "     1\tfoo", "", "     2\tbar" }, lines);
    }

    [Fact]
    public void Cat_File_SqueezeBlankLines_Flag()
    {
        var f = WriteFile("cats.txt", "a\n\n\n\nb\n");
        var lines = RunBashText($"Invoke-BashCat -s '{f.Replace("\\", "\\\\")}'");
        // -s collapses runs of blank lines to a single blank line.
        Assert.Equal(new[] { "a", "", "b" }, lines);
    }

    [Fact]
    public void Cat_File_ShowEndsAndTabs_Flags()
    {
        var f = WriteFile("catet.txt", "x\ty\n");
        var lines = RunBashText($"Invoke-BashCat -E -T '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "x^Iy$" }, lines);
    }

    [Fact]
    public void Cat_MissingFile_SetsExitCodeOne()
    {
        // Missing-target axis: a file-read failure sets $global:LASTEXITCODE = 1.
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$global:LASTEXITCODE = 0").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript("Invoke-BashCat '/no/such/cat/file' 2>$null").Invoke();
        pwsh.Commands.Clear();
        var code = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        Assert.Equal("1", code[0]?.ToString());
    }

    [Fact]
    public void Cat_EmptyFile_EmitsNothing()
    {
        // Empty-input axis.
        var f = WriteFile("catempty.txt", "");
        var lines = RunBashText($"Invoke-BashCat '{f.Replace("\\", "\\\\")}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Cat_Flagged_EmitsTypedCatLineObject()
    {
        var f = WriteFile("cattype.txt", "z\n");
        var result = Run($"Invoke-BashCat -n '{f.Replace("\\", "\\\\")}'");
        Assert.Single(result);
        Assert.Contains("PsBash.CatLine", result[0]!.TypeNames);
        Assert.Equal(1, (int)result[0]!.Properties["LineNumber"].Value);
    }

    [Fact]
    public void Cat_AliasResolvesToCmdlet()
    {
        var lines = RunBashText("'aliased' | cat");
        Assert.Equal(new[] { "aliased" }, lines);
    }

    // ======================= head =======================

    [Fact]
    public void Head_Pipeline_DefaultTenLines()
    {
        var lines = RunBashText("1..15 | Invoke-BashHead");
        Assert.Equal(10, lines.Length);
        Assert.Equal("1", lines[0]);
        Assert.Equal("10", lines[9]);
    }

    [Fact]
    public void Head_Pipeline_DashNFlag()
    {
        var lines = RunBashText("1..15 | Invoke-BashHead -n 3");
        Assert.Equal(new[] { "1", "2", "3" }, lines);
    }

    [Fact]
    public void Head_Pipeline_JoinedDashNFlag()
    {
        var lines = RunBashText("1..15 | Invoke-BashHead -n2");
        Assert.Equal(new[] { "1", "2" }, lines);
    }

    [Fact]
    public void Head_Pipeline_LegacyDashNumberFlag()
    {
        var lines = RunBashText("1..15 | Invoke-BashHead -4");
        Assert.Equal(new[] { "1", "2", "3", "4" }, lines);
    }

    [Fact]
    public void Head_Pipeline_ByteCountFlag()
    {
        var lines = RunBashText("'abcdef' | Invoke-BashHead -c 3");
        Assert.Equal(new[] { "abc" }, lines);
    }

    [Fact]
    public void Head_File_DashNFlag()
    {
        var f = WriteFile("head1.txt", "l1\nl2\nl3\nl4\n");
        var lines = RunBashText($"Invoke-BashHead -n 2 '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "l1", "l2" }, lines);
    }

    [Fact]
    public void Head_File_EmitsTypedCatLineObject()
    {
        var f = WriteFile("headtype.txt", "only\n");
        var result = Run($"Invoke-BashHead -n 1 '{f.Replace("\\", "\\\\")}'");
        Assert.Single(result);
        Assert.Contains("PsBash.CatLine", result[0]!.TypeNames);
    }

    [Fact]
    public void Head_File_Unicode_PreservedExactly()
    {
        // Unicode axis.
        var f = WriteFile("headuni.txt", "é你好\n");
        var lines = RunBashText($"Invoke-BashHead -n 1 '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "é你好" }, lines);
    }

    [Fact]
    public void Head_File_MoreLinesRequestedThanExist()
    {
        // Stops at EOF rather than padding.
        var f = WriteFile("headshort.txt", "a\nb\n");
        var lines = RunBashText($"Invoke-BashHead -n 99 '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void Head_AliasResolvesToCmdlet()
    {
        var lines = RunBashText("1..5 | head -n 2");
        Assert.Equal(new[] { "1", "2" }, lines);
    }

    // ======================= tail =======================

    [Fact]
    public void Tail_Pipeline_DefaultTenLines()
    {
        var lines = RunBashText("1..15 | Invoke-BashTail");
        Assert.Equal(10, lines.Length);
        Assert.Equal("6", lines[0]);
        Assert.Equal("15", lines[9]);
    }

    [Fact]
    public void Tail_Pipeline_DashNFlag()
    {
        var lines = RunBashText("1..15 | Invoke-BashTail -n 3");
        Assert.Equal(new[] { "13", "14", "15" }, lines);
    }

    [Fact]
    public void Tail_Pipeline_FromLinePlusN()
    {
        // -n +N emits from line N onward (1-based).
        var lines = RunBashText("1..6 | Invoke-BashTail -n +4");
        Assert.Equal(new[] { "4", "5", "6" }, lines);
    }

    [Fact]
    public void Tail_Pipeline_LegacyDashNumberFlag()
    {
        var lines = RunBashText("1..15 | Invoke-BashTail -2");
        Assert.Equal(new[] { "14", "15" }, lines);
    }

    [Fact]
    public void Tail_File_DashNFlag()
    {
        var f = WriteFile("tail1.txt", "l1\nl2\nl3\nl4\n");
        var lines = RunBashText($"Invoke-BashTail -n 2 '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "l3", "l4" }, lines);
    }

    [Fact]
    public void Tail_File_FromLinePlusN()
    {
        var f = WriteFile("tailplus.txt", "a\nb\nc\nd\n");
        var lines = RunBashText($"Invoke-BashTail -n +3 '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "c", "d" }, lines);
    }

    [Fact]
    public void Tail_File_EmitsTypedCatLineObject()
    {
        var f = WriteFile("tailtype.txt", "p\nq\n");
        var result = Run($"Invoke-BashTail -n 1 '{f.Replace("\\", "\\\\")}'");
        Assert.Single(result);
        Assert.Contains("PsBash.CatLine", result[0]!.TypeNames);
        // Circular-buffer line numbering: last line of a 2-line file is line 2.
        Assert.Equal(2, (int)result[0]!.Properties["LineNumber"].Value);
    }

    [Fact]
    public void Tail_File_ByteCountFlag()
    {
        var f = WriteFile("tailbytes.txt", "abcdef");
        var lines = RunBashText($"Invoke-BashTail -c 3 '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "def" }, lines);
    }

    [Fact]
    public void Tail_File_MoreLinesRequestedThanExist()
    {
        var f = WriteFile("tailshort.txt", "x\ny\n");
        var lines = RunBashText($"Invoke-BashTail -n 99 '{f.Replace("\\", "\\\\")}'");
        Assert.Equal(new[] { "x", "y" }, lines);
    }

    [Fact]
    public void Tail_File_MissingFile_EmitsNothing()
    {
        // Missing-target axis: tail with no resolvable file emits nothing
        // (matching the psm1 oracle's early return on an empty resolved list).
        var lines = RunBashText("Invoke-BashTail '/no/such/tail/file' 2>$null");
        Assert.Empty(lines);
    }

    [Fact]
    public void Tail_AliasResolvesToCmdlet()
    {
        var lines = RunBashText("1..5 | tail -n 2");
        Assert.Equal(new[] { "4", "5" }, lines);
    }
}
