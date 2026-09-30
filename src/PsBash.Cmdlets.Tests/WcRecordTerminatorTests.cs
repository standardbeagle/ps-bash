using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// wc counts the BYTE STREAM the upstream records render to, not one newline per record: a
/// <c>NoTrailingNewline</c> record (printf, echo -n) contributes its exact bytes, a normal record
/// BashText + "\n" (docs/specs/runtime-functions.md "Line terminators must survive the object
/// choice"). Expected values are GNU wc 9.4 (WSL Ubuntu 24.04), one <c>printf INPUT | wc</c> each.
/// </summary>
public class WcRecordTerminatorTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public WcRecordTerminatorTests(SharedPwshFixture fixture) => _fixture = fixture;

    private PSObject Wc(string producer, string flags)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript($"{producer} | Invoke-BashWc {flags}").Invoke();
        pwsh.Commands.Clear();
        return Assert.Single(result);
    }

    private static int Prop(PSObject o, string name) => (int)o.Properties[name]!.Value;

    // (printf format, lines -l, words -w, bytes -c, chars -m, max -L)
    [Theory]
    [InlineData("abc", 0, 1, 3, 3, 3)]
    [InlineData("a\\nb", 1, 2, 3, 3, 1)]
    [InlineData("a\\nb\\n", 2, 2, 4, 4, 1)]
    [InlineData("a b\\n\\nc d", 2, 4, 8, 8, 3)]
    [InlineData("é\\n", 1, 1, 3, 2, 1)]
    [InlineData("x\\n\\ny", 2, 2, 4, 4, 1)]
    public void Printf_CountsTheExactByteStream(string fmt, int l, int w, int c, int m, int bigL)
    {
        var o = Wc($"Invoke-BashPrintf '{fmt}'", "");
        Assert.Equal(l, Prop(o, "Lines"));
        Assert.Equal(w, Prop(o, "Words"));
        Assert.Equal(c, Prop(o, "Bytes"));
        Assert.Equal(m, Prop(o, "Chars"));
        Assert.Equal(bigL, Prop(o, "MaxLineLength"));
    }

    [Theory]
    [InlineData("-c", "3")]
    [InlineData("-m", "3")]
    [InlineData("-l", "0")]
    [InlineData("-w", "1")]
    [InlineData("-L", "3")]
    public void PrintfNoNewline_SingleColumn(string flag, string expected)
    {
        Assert.Equal(expected, BashRuntime.GetBashText(Wc("Invoke-BashPrintf 'abc'", $"'{flag}'")).Trim());
    }

    [Fact]
    public void EchoNDash_EmptyRecordIsZeroBytes()
    {
        Assert.Equal("0", BashRuntime.GetBashText(Wc("Invoke-BashEcho '-n' ''", "'-c'")).Trim());
    }

    [Fact]
    public void TerminatedRecords_StillCountOneNewlineEach()
    {
        // Invoke-BashEcho emits a terminated record: "abc\n" = 4 bytes, 1 line.
        var o = Wc("Invoke-BashEcho abc", "");
        Assert.Equal(1, Prop(o, "Lines"));
        Assert.Equal(4, Prop(o, "Bytes"));
    }

    [Fact]
    public void UnterminatedRecordGluesToTheNextRecord()
    {
        // printf 'ab' ; printf 'c\n' as one stream is "abc\n": one word, one line, 4 bytes.
        var o = Wc("& { Invoke-BashPrintf 'ab'; Invoke-BashPrintf 'c\\n' }", "");
        Assert.Equal(1, Prop(o, "Lines"));
        Assert.Equal(1, Prop(o, "Words"));
        Assert.Equal(4, Prop(o, "Bytes"));
    }
}
