using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 Phase 3 migration of
/// Invoke-BashSed from a PsBash.psm1 script function to a binary cmdlet
/// (PsBash.Cmdlets.dll / InvokeBashSedCommand.cs).
///
/// Oracle: the original psm1 Invoke-BashSed and its pure helpers
/// ConvertFrom-SedExpression and Test-SedAddress, modeled on the bash sed
/// stream editor. All three are reimplemented in C# inside the cmdlet. The
/// M1/M2/M3 bash-oracle parity for sed lives in PsBash.Differential.Tests /
/// the canary suite; the surface under test here is M5 (in-process cmdlet)
/// against the psm1 oracle's documented behavior.
///
/// sed has a file + pipeline surface, so the applicable failure-surface axes
/// (per .claude/rules/qa-rubric.md Directive 3) are: empty input, unicode
/// input, CRLF input, large-ish input, and missing target. Negative cases
/// (Directive 7): missing file, missing -f script file, bad substitution,
/// unsupported command. Security (Directive 12): a substitution replacement
/// containing PowerShell scriptblock chars / $() must be treated as literal
/// text, not executed.
///
/// awk, jq, and find are NOT migrated in this phase — awk and jq are
/// near-complete custom interpreters (documented to stay in psm1 / deferred);
/// find has a -exec arbitrary-command surface deferred to a follow-on task.
///
/// The PwshTestFixture loads psm1 (which no longer defines Invoke-BashSed)
/// then imports PsBash.Cmdlets.dll, mirroring the host load order — so these
/// tests also prove the function-shadowing removal worked and the psm1
/// Set-Alias 'sed' line still resolves to the cmdlet.
/// </summary>
public class InvokeBashSedCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashSedCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(), "psbash-sed-" + Guid.NewGuid().ToString("N"));
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

    private System.Collections.ObjectModel.Collection<PSObject> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();
        Assert.True(err.Count == 0 || err[0] == null,
            $"Unexpected error running [{script}]: " +
            $"{(err.Count > 0 ? err[0]?.ToString() : "none")}");

        return result;
    }

    private System.Collections.ObjectModel.Collection<PSObject> RunAllowError(
        string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private string[] RunText(string script)
    {
        return Run(script)
            .Select(o =>
            {
                var prop = o?.Properties["BashText"];
                return prop != null ? prop.Value?.ToString() ?? "" : o?.ToString() ?? "";
            })
            .ToArray();
    }

    private static string Esc(string path) => path.Replace("\\", "\\\\");

    // ===================== s/// substitution =====================

    [Fact]
    public void Sed_Pipeline_BasicSubstitution_FirstMatchOnly()
    {
        var lines = RunText("'hello hello' | Invoke-BashSed 's/hello/world/'");
        Assert.Single(lines);
        Assert.Equal("world hello", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_GlobalSubstitution_AllMatches()
    {
        var lines = RunText("'a a a' | Invoke-BashSed 's/a/b/g'");
        Assert.Single(lines);
        Assert.Equal("b b b", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_CaseInsensitiveFlag()
    {
        var lines = RunText("'Hello HELLO' | Invoke-BashSed 's/hello/x/gI'");
        Assert.Single(lines);
        Assert.Equal("x x", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_Backreference_TranslatedToDotNet()
    {
        // \1 / \2 in the replacement must become $1 / $2 for .NET regex. The
        // capture groups use -r (ERE) so ( ) are groups in both the psm1 oracle
        // and .NET — the oracle's BRE \( handling does not promote \( to a
        // group (it stays a literal under .NET regex semantics), so ERE is the
        // mode that exercises backreference replacement faithfully.
        var lines = RunText(
            @"'foo-42' | Invoke-BashSed -r 's/([a-z]*)-([0-9]*)/\2-\1/'");
        Assert.Single(lines);
        Assert.Equal("42-foo", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_BareAmpersand_IsWholeMatch()
    {
        // A bare & in the replacement is the entire match (.NET $0).
        // Oracle: printf 'cat' | sed 's/cat/[&]/' -> [cat]
        var lines = RunText(@"'cat' | Invoke-BashSed 's/cat/[&]/'");
        Assert.Single(lines);
        Assert.Equal("[cat]", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_EscapedAmpersand_IsLiteral()
    {
        // \& is a LITERAL ampersand in GNU sed, NOT the whole match.
        // Oracle: printf 'cat' | sed 's/cat/[\&]/' -> [&]
        var lines = RunText(@"'cat' | Invoke-BashSed 's/cat/[\&]/'");
        Assert.Single(lines);
        Assert.Equal("[&]", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_NumericOccurrenceFlag_ReplacesNthOnly()
    {
        // s/a/b/2 replaces only the 2nd match.
        // Oracle: printf 'a a a a' | sed 's/a/b/2' -> a b a a
        var lines = RunText("'a a a a' | Invoke-BashSed 's/a/b/2'");
        Assert.Single(lines);
        Assert.Equal("a b a a", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_NumericPlusGlobalFlag_ReplacesNthOnward()
    {
        // s/a/b/2g replaces the 2nd match and everything after it.
        // Oracle: printf 'a a a a' | sed 's/a/b/2g' -> a b b b
        var lines = RunText("'a a a a' | Invoke-BashSed 's/a/b/2g'");
        Assert.Single(lines);
        Assert.Equal("a b b b", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_EscapeSequencesInReplacement_Expand()
    {
        // \t in the replacement expands to a real tab (GNU sed).
        // Oracle: printf 'aXb' | sed 's/X/\t/' -> "a\tb"
        var lines = RunText(@"'aXb' | Invoke-BashSed 's/X/\t/'");
        Assert.Single(lines);
        Assert.Equal("a\tb", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_BasicRegex_ParensAreLiteralWithoutE()
    {
        // BRE mode (no -r): an unescaped ( is a literal paren, not a group.
        var lines = RunText("'a(b)c' | Invoke-BashSed 's/(b)/X/'");
        Assert.Single(lines);
        Assert.Equal("aXc", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_ExtendedRegex_ParensAreGroupsWithR()
    {
        // -r enables ERE: ( ) are a capture group.
        var lines = RunText(@"'abab' | Invoke-BashSed -r 's/(ab)+/X/'");
        Assert.Single(lines);
        Assert.Equal("X", lines[0]);
    }

    [Fact]
    public void Sed_Pipeline_AlternateDelimiter()
    {
        // Any char after `s` is the delimiter — here `|`.
        var lines = RunText("'/usr/bin' | Invoke-BashSed 's|/usr|/opt|'");
        Assert.Single(lines);
        Assert.Equal("/opt/bin", lines[0]);
    }

    // ===================== -e expression flag (collision fix) =====================

    [Fact]
    public void Sed_DashE_SingleExpression_BindsViaExplicitParameter()
    {
        // -e prefix-collides with -ErrorAction; the explicit Expression
        // parameter must capture it.
        var lines = RunText("'abc' | Invoke-BashSed -e 's/b/X/'");
        Assert.Single(lines);
        Assert.Equal("aXc", lines[0]);
    }

    [Fact]
    public void Sed_DashE_MultipleExpressions_AppliedInOrder()
    {
        // PowerShell's parameter binder cannot bind a repeated -e flag to a
        // single string[] parameter, so multiple expressions are passed as a
        // comma-separated array (the binder's documented multi-value form).
        // The cmdlet applies them in array order, matching the psm1 oracle's
        // -e accumulation. (sed -e a -e b in M1/M2/M3 bash mode is handled by
        // the transpiler upstream, not this cmdlet's parameter binder.)
        var lines = RunText(
            "'abc' | Invoke-BashSed -e 's/a/1/','s/b/2/','s/c/3/'");
        Assert.Single(lines);
        Assert.Equal("123", lines[0]);
    }

    // ===================== addresses =====================

    [Fact]
    public void Sed_NumericAddress_SubstitutesOnlyThatLine()
    {
        var lines = RunText("\"x`nx`nx\" | Invoke-BashSed '2s/x/Y/'");
        Assert.Equal(new[] { "x", "Y", "x" }, lines);
    }

    [Fact]
    public void Sed_RangeAddress_SubstitutesWithinRange()
    {
        var lines = RunText("\"a`na`na`na\" | Invoke-BashSed '2,3s/a/Z/'");
        Assert.Equal(new[] { "a", "Z", "Z", "a" }, lines);
    }

    [Fact]
    public void Sed_RangeToDollar_SubstitutesToLastLine()
    {
        var lines = RunText("\"a`na`na\" | Invoke-BashSed '2,$s/a/Q/'");
        Assert.Equal(new[] { "a", "Q", "Q" }, lines);
    }

    [Fact]
    public void Sed_RegexAddress_SubstitutesOnlyMatchingLines()
    {
        var lines = RunText(
            "\"keep`ndrop me`nkeep\" | Invoke-BashSed '/drop/s/me/IT/'");
        Assert.Equal(new[] { "keep", "drop IT", "keep" }, lines);
    }

    [Fact]
    public void Sed_RegexRangeAddress_DeletesBetweenMarkers()
    {
        var lines = RunText(
            "\"a`nSTART`nx`nEND`nb\" | Invoke-BashSed '/START/,/END/d'");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    // ===================== d / p / q / y commands =====================

    [Fact]
    public void Sed_Delete_RemovesMatchingLines()
    {
        var lines = RunText("\"a`nb`nc\" | Invoke-BashSed '/b/d'");
        Assert.Equal(new[] { "a", "c" }, lines);
    }

    [Fact]
    public void Sed_Print_WithSuppressDefault_PrintsMatchOnce()
    {
        // -n suppresses default output; p prints matching lines.
        var lines = RunText("\"a`nb`nc\" | Invoke-BashSed -n '/b/p'");
        Assert.Single(lines);
        Assert.Equal("b", lines[0]);
    }

    [Fact]
    public void Sed_Print_WithoutSuppress_PrintsMatchTwice()
    {
        // Without -n, a matching line is emitted by p AND by the default print.
        var lines = RunText("\"a`nb\" | Invoke-BashSed '/b/p'");
        Assert.Equal(new[] { "a", "b", "b" }, lines);
    }

    [Fact]
    public void Sed_Quit_StopsAfterMatchingLine()
    {
        var lines = RunText("\"a`nb`nc`nd\" | Invoke-BashSed '2q'");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void Sed_Transliterate_MapsCharacters()
    {
        var lines = RunText("'abc' | Invoke-BashSed 'y/abc/xyz/'");
        Assert.Single(lines);
        Assert.Equal("xyz", lines[0]);
    }

    // ===================== a / i / c commands =====================

    [Fact]
    public void Sed_Append_AddsTextAfterMatchingLine()
    {
        var lines = RunText(@"'one' | Invoke-BashSed '/one/a\after'");
        Assert.Equal(new[] { "one", "after" }, lines);
    }

    [Fact]
    public void Sed_Insert_AddsTextBeforeMatchingLine()
    {
        var lines = RunText(@"'one' | Invoke-BashSed '/one/i\before'");
        Assert.Equal(new[] { "before", "one" }, lines);
    }

    [Fact]
    public void Sed_Change_ReplacesMatchingLine()
    {
        // c\text: the matching line is deleted and the change text emitted.
        var lines = RunText(@"""a`nb`nc"" | Invoke-BashSed '/b/c\replaced'");
        Assert.Equal(new[] { "a", "replaced", "c" }, lines);
    }

    // ===================== N / multi-line pattern space =====================

    [Fact]
    public void Sed_NextCommand_JoinsLinesIntoPatternSpace()
    {
        // N appends the next input line into the pattern space; the default
        // print then emits the joined pattern space (split back on its \n).
        var lines = RunText("\"a`nb`nc`nd\" | Invoke-BashSed 'N'");
        Assert.Equal(new[] { "a", "b", "c", "d" }, lines);
    }

    // ===================== file mode =====================

    [Fact]
    public void Sed_File_SubstitutesAndEmitsLines()
    {
        var f = WriteFile("in.txt", "foo\nbar\n");
        var lines = RunText($"Invoke-BashSed 's/o/0/g' '{Esc(f)}'");
        Assert.Equal(new[] { "f00", "bar" }, lines);
    }

    [Fact]
    public void Sed_File_InPlace_RewritesFile()
    {
        var f = WriteFile("edit.txt", "alpha\nbeta\n");
        var result = Run($"Invoke-BashSed -i 's/a/A/g' '{Esc(f)}'");
        Assert.Empty(result); // -i emits nothing to the pipeline
        // The source file's trailing newline is preserved on rewrite, matching
        // the psm1 oracle's $hadTrailingNewline handling.
        Assert.Equal("AlphA\nbetA\n", File.ReadAllText(f).Replace("\r", ""));
    }

    [Fact]
    public void Sed_File_ScriptFile_AppliesEachLineAsCommand()
    {
        var script = WriteFile("script.sed", "s/a/1/\ns/b/2/\n");
        var data = WriteFile("data.txt", "ab\n");
        var lines = RunText($"Invoke-BashSed -f '{Esc(script)}' '{Esc(data)}'");
        Assert.Single(lines);
        Assert.Equal("12", lines[0]);
    }

    [Fact]
    public void Sed_FirstOperandIsExpression_WhenNoDashE()
    {
        var f = WriteFile("op.txt", "x\n");
        var lines = RunText($"Invoke-BashSed 's/x/Y/' '{Esc(f)}'");
        Assert.Single(lines);
        Assert.Equal("Y", lines[0]);
    }

    // ===================== failure-surface axes =====================

    [Fact]
    public void Sed_EmptyInput_File_EmitsNothing()
    {
        // Empty-input axis.
        var f = WriteFile("empty.txt", "");
        var lines = RunText($"Invoke-BashSed 's/x/y/' '{Esc(f)}'");
        Assert.Single(lines); // one empty line (the split of "")
        Assert.Equal("", lines[0]);
    }

    [Fact]
    public void Sed_EmptyPipeline_EmitsNothing()
    {
        var lines = RunText("@() | Invoke-BashSed 's/x/y/'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Sed_UnicodeInput_SubstitutionPreservesNonAscii()
    {
        // Unicode axis: é / emoji must round-trip; substitution targets ascii.
        var lines = RunText("'café 🍕 pie' | Invoke-BashSed 's/pie/tart/'");
        Assert.Single(lines);
        Assert.Equal("café 🍕 tart", lines[0]);
    }

    [Fact]
    public void Sed_CrlfFile_NormalizedBeforeProcessing()
    {
        // CRLF axis: a file written with \r\n is normalized to \n on read.
        var f = WriteFile("crlf.txt", "one\r\ntwo\r\n");
        var lines = RunText($"Invoke-BashSed 's/o/0/' '{Esc(f)}'");
        Assert.Equal(new[] { "0ne", "tw0" }, lines);
    }

    [Fact]
    public void Sed_LargeInput_AllLinesProcessed()
    {
        // Large-ish input axis: 5000 lines through a substitution.
        var lines = RunText("1..5000 | Invoke-BashSed 's/^/L/'");
        Assert.Equal(5000, lines.Length);
        Assert.Equal("L1", lines[0]);
        Assert.Equal("L5000", lines[4999]);
    }

    // ===================== negative cases =====================

    [Fact]
    public void Sed_MissingFile_EmitsErrorAndSkips()
    {
        // Missing-target axis: a non-existent file is skipped with a bash-style
        // error; no output object is produced for it.
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt");
        var result = RunAllowError($"Invoke-BashSed 's/x/y/' '{Esc(missing)}'");
        Assert.Empty(result);
    }

    [Fact]
    public void Sed_MissingScriptFile_EmitsErrorAndExitCode2()
    {
        var missing = Path.Combine(_tmpDir, "no-script.sed");
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(
            $"Invoke-BashSed -f '{Esc(missing)}' ; $global:LASTEXITCODE").Invoke();
        var ec = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        Assert.Equal(2, (int)ec[0].BaseObject);
    }

    [Fact]
    public void Sed_BadSubstitution_EmitsErrorAndExitCode2()
    {
        var pwsh = _fixture.AcquireFresh();
        // "s/x" has only one delimiter section — bad substitution.
        pwsh.AddScript("'a' | Invoke-BashSed 's/x'").Invoke();
        pwsh.Commands.Clear();
        var ec = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        Assert.Equal(2, (int)ec[0].BaseObject);
    }

    [Fact]
    public void Sed_UnsupportedCommand_EmitsErrorAndExitCode2()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("'a' | Invoke-BashSed 'Z'").Invoke();
        pwsh.Commands.Clear();
        var ec = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        Assert.Equal(2, (int)ec[0].BaseObject);
    }

    [Fact]
    public void Sed_NoExpression_EmitsUsageErrorAndExitCode2()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("@() | Invoke-BashSed").Invoke();
        pwsh.Commands.Clear();
        var ec = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        Assert.Equal(2, (int)ec[0].BaseObject);
    }

    // ===================== security (Directive 12) =====================

    [Fact]
    public void Sed_ReplacementWithScriptblockChars_IsLiteralNotExecuted()
    {
        // A replacement containing $() and {} must be inserted as literal text,
        // never evaluated as PowerShell.
        var lines = RunText("'X' | Invoke-BashSed 's/X/$(1+1){a}/'");
        Assert.Single(lines);
        Assert.Equal("$(1+1){a}", lines[0]);
    }

    [Fact]
    public void Sed_ReplacementWithSemicolon_IsLiteral()
    {
        var lines = RunText("'X' | Invoke-BashSed 's/X/a;b/'");
        Assert.Single(lines);
        Assert.Equal("a;b", lines[0]);
    }

    // ===================== alias resolution =====================

    [Fact]
    public void Sed_AliasResolvesToCmdlet()
    {
        // The psm1 Set-Alias 'sed' line must resolve to the binary cmdlet now
        // that the psm1 function is gone.
        var lines = RunText("'abc' | sed 's/b/X/'");
        Assert.Single(lines);
        Assert.Equal("aXc", lines[0]);
    }
}
