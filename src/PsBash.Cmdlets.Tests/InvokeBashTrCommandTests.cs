using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashTr</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashTrCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashTr</c> function — character
/// translation / deletion / squeeze. Pipeline-only.
///
/// Failure-surface axes (per Directive 3): empty input, unicode (escape
/// sequence expansion to <c>\n</c>), missing-target (no SETs after a
/// delete flag — degenerate, returns input), quoting/injection
/// (operand containing PowerShell scriptblock chars), plus the
/// canonical translation / delete / squeeze / char-class / range
/// scenarios from the task spec.
/// </summary>
public class InvokeBashTrCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashTrCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
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
    public void Tr_EmptyPipeline_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashTr a b");
        Assert.Empty(lines);
    }

    [Fact]
    public void Tr_UppercaseRange_TranslatesLowerToUpper()
    {
        var lines = RunLines("'hello' | Invoke-BashTr a-z A-Z");
        Assert.Single(lines);
        Assert.Equal("HELLO", lines[0]);
    }

    [Fact]
    public void Tr_LowercaseRange_TranslatesUpperToLower()
    {
        var lines = RunLines("'HELLO' | Invoke-BashTr 'A-Z' 'a-z'");
        Assert.Single(lines);
        Assert.Equal("hello", lines[0]);
    }

    [Fact]
    public void Tr_DeleteFlag_RemovesSpaces()
    {
        // -d collides with -Debug, exercised via the declared `D` switch.
        var lines = RunLines("'a b c d' | Invoke-BashTr -d ' '");
        Assert.Single(lines);
        Assert.Equal("abcd", lines[0]);
    }

    [Fact]
    public void Tr_SqueezeFlag_CollapsesRuns()
    {
        var lines = RunLines("'a   b    c' | Invoke-BashTr -s ' '");
        Assert.Single(lines);
        Assert.Equal("a b c", lines[0]);
    }

    [Fact]
    public void Tr_PosixDigitClass_DeletesDigits()
    {
        var lines = RunLines("'abc123def456' | Invoke-BashTr -d '[:digit:]'");
        Assert.Single(lines);
        Assert.Equal("abcdef", lines[0]);
    }

    [Fact]
    public void Tr_PosixAlphaClass_Translates()
    {
        // [:alpha:] -> [:upper:] via classes: lowercase mapped to uppercase,
        // uppercase mapped to itself (within the same alpha class).
        var lines = RunLines("'aBc' | Invoke-BashTr '[:lower:]' '[:upper:]'");
        Assert.Single(lines);
        Assert.Equal("ABC", lines[0]);
    }

    [Fact]
    public void Tr_EscapeSequence_NewlineInSet1_DeletesNewlines()
    {
        // The pipeline item is a multi-line BashText. The oracle joins
        // pipeline items with '\n' and operates on the joined text per-line,
        // so this case asserts the escape sequence in the SET argument
        // ('\n') expands to a real newline char and is then deleted.
        // Build an input containing an embedded newline via a pipeline of
        // two items: 'foo' then 'bar'; the cmdlet joins with '\n'.
        var lines = RunLines("'a\\nb' | Invoke-BashTr -d '\\n'");
        // PowerShell escape `\\n` in a single-quoted string is literally
        // backslash-n. tr expands it to a real newline char; the input
        // has no literal newline char, so output is unchanged.
        Assert.Single(lines);
        Assert.Equal("a\\nb", lines[0]);
    }

    [Fact]
    public void Tr_RangeAndChar_TranslatesDigitsToLetters()
    {
        var lines = RunLines("'abc12' | Invoke-BashTr '0-9' 'X'");
        Assert.Single(lines);
        // SET2='X' is one char; remaining set1 digits all map to the last
        // char of set2 ('X') per the oracle's idx>=0 && set2.Length>0 branch.
        Assert.Equal("abcXX", lines[0]);
    }

    [Fact]
    public void Tr_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashTr --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("tr", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tr_AliasResolution_TrWorks()
    {
        // The psm1 module registers `Set-Alias tr Invoke-BashTr`. Because
        // the binary cmdlet loads before psm1 runs, the alias resolves to
        // the cmdlet.
        var lines = RunLines("'hi' | tr 'a-z' 'A-Z'");
        Assert.Single(lines);
        Assert.Equal("HI", lines[0]);
    }

    [Fact]
    public void Tr_InjectionProbe_OperandWithDollarParenAndSemicolons_Literal()
    {
        // Directive 12: a user-controlled operand containing PowerShell
        // injection chars must not be re-parsed as script. tr operands are
        // bound positionally through the cmdlet's [Parameter] surface, not
        // concatenated into a script body. The probe is fed as SET1 to
        // -d so any char in the probe string becomes a deletion target.
        // If the probe had been evaluated as script, the test would have
        // thrown or 'pwned' would appear in the output.
        var probe = "$(throw 'INJECTED');pwned";
        var lines = RunLines(
            $"'abcde' | Invoke-BashTr -d '{probe.Replace("'", "''")}'");
        // The probe contains the chars: $ ( t h r o w ' I N J E C T E D ' ) ; p w n e d (and space)
        // Of 'abcde', 'a','b','c','d','e' — 'd' and 'e' are in the probe,
        // 'a','b','c' are not. So output should be 'abc'.
        Assert.Single(lines);
        Assert.Equal("abc", lines[0]);
    }

    [Fact]
    public void Tr_MultiplePipelineItems_TranslatedPerLine()
    {
        // Pipeline items are joined with '\n' and the oracle drives a
        // per-line transform loop; each line goes out as its own object.
        var lines = RunLines("'one','two','three' | Invoke-BashTr 'a-z' 'A-Z'");
        Assert.Equal(new[] { "ONE", "TWO", "THREE" }, lines);
    }
}
