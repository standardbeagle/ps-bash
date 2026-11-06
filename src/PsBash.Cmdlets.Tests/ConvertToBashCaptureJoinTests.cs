using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// `ConvertTo-BashCapture -Join` is the quoted / assignment command-substitution value. It must equal what the emitter
/// used to build with `@(… | ConvertTo-BashCapture) -join "`n"` then `-replace '(\r?\n)+$',''` (bash `$(cmd)` strips ALL
/// trailing newlines, keeps inner ones), but in one pass so `x=$(cat big)` no longer holds line array + joined text +
/// replaced copy at once. Hand-written: the cmdlet switch has no bash analogue; the end-to-end text is covered by the
/// differential command-substitution tests.
/// </summary>
public class ConvertToBashCaptureJoinTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public ConvertToBashCaptureJoinTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private string Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var r = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        Assert.Single(r);
        return (string)r[0].BaseObject;
    }

    [Theory]
    [InlineData("'a','b','c'", "a\nb\nc")]
    [InlineData("'a','','b'", "a\n\nb")]            // inner blank line kept
    [InlineData("'a','',''", "a")]                    // every trailing newline stripped
    [InlineData("'a'", "a")]
    [InlineData("'',''", "")]
    public void Join_MatchesTheOldJoinAndTrimOfTheLines(string lines, string expected)
    {
        Assert.Equal(expected, Run($"{lines} | ConvertTo-BashCapture -Join"));
        // and the old two-step form agrees (the contract being preserved)
        var old = Run($"(@({lines} | ConvertTo-BashCapture) -join [string][char]10) -replace '(\\r?\\n)+$',''");
        Assert.Equal(old, Run($"{lines} | ConvertTo-BashCapture -Join"));
    }

    [Fact]
    public void Join_NoInput_IsTheEmptyStringNotNothing()
    {
        Assert.Equal("", Run("@() | ConvertTo-BashCapture -Join"));
    }

    [Fact]
    public void Join_StripsCarriageReturnBeforeEachTrailingNewline()
    {
        Assert.Equal("a", Run("\"a`r\",\"\" | ConvertTo-BashCapture -Join"));
    }

    [Fact]
    public void Join_ExactBytesRecordsGlueLikeBash()
    {
        // printf a; printf b  ->  "ab" (NoTrailingNewline records concatenate)
        Assert.Equal("ab", Run("& { Invoke-BashPrintf a; Invoke-BashPrintf b } | ConvertTo-BashCapture -Join"));
    }
}
