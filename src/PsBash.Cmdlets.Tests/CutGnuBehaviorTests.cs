using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// End-to-end cut behavior in the shape the transpiler emits (every dash word single-quoted) plus
/// DIRECT calls (`Invoke-BashCut -d: -f2`, the Pester shape). Oracle: GNU cut 9.4 (`wsl bash`);
/// tests marked FIX assert output that used to be silently wrong.
/// </summary>
public class CutGnuBehaviorTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public CutGnuBehaviorTests(SharedPwshFixture fixture) => _fixture = fixture;

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    [Fact]
    public void Fields_SelectionIsSortedAndDeduplicated()   // FIX (was: spec order, duplicates)
    {
        Assert.Equal(new[] { "a:c" }, Run("'a:b:c' | Invoke-BashCut '-d:' '-f3,1'").AssertSuccess().Lines);
        Assert.Equal(new[] { "a" }, Run("'a:b:c' | Invoke-BashCut '-d:' '-f1,1'").AssertSuccess().Lines);
        Assert.Equal(new[] { "a:b:c" }, Run("'a:b:c' | Invoke-BashCut '-d:' '-f1-2,2-3'").AssertSuccess().Lines);
    }

    [Fact]
    public void Fields_LineWithoutDelimiter_IsPrintedWhole_UnlessS()   // FIX (was: empty for -f2)
    {
        Assert.Equal(new[] { "xyz" }, Run("'xyz' | Invoke-BashCut '-d:' '-f2'").AssertSuccess().Lines);
        Assert.Empty(Run("'xyz' | Invoke-BashCut '-d:' '-s' '-f2'").AssertSuccess().Lines);
        Assert.Equal(new[] { "xyz" }, Run("'xyz' | Invoke-BashCut '-d:' '--complement' '-f1'").AssertSuccess().Lines);
    }

    [Fact]
    public void Complement_FieldsAndChars()   // FIX (was: unsupported)
    {
        Assert.Equal(new[] { "a:c" }, Run("'a:b:c' | Invoke-BashCut '-d:' '-f2' '--complement'").AssertSuccess().Lines);
        Assert.Equal(new[] { "bdef" }, Run("'abcdef' | Invoke-BashCut '-c1,3' '--complement'").AssertSuccess().Lines);
        Assert.Equal(new[] { "a-c-ef" }, Run("'abcdef' | Invoke-BashCut '-c2,4' '--complement' '--output-delimiter=-'").AssertSuccess().Lines);
    }

    [Fact]
    public void OutputDelimiter_FieldsJoinEverything_CharsJoinSeparateRanges()
    {
        Assert.Equal(new[] { "a-b-c" }, Run("'a:b:c' | Invoke-BashCut '-d:' '-f1-3' '--output-delimiter=-'").AssertSuccess().Lines);
        Assert.Equal(new[] { "a-c" }, Run("'abcdef' | Invoke-BashCut '-c1,3' '--output-delimiter=-'").AssertSuccess().Lines);   // FIX
        Assert.Equal(new[] { "ab-cd" }, Run("'abcdef' | Invoke-BashCut '-c1-2,3-4' '--output-delimiter=-'").AssertSuccess().Lines);
        Assert.Equal(new[] { "abcd" }, Run("'abcdef' | Invoke-BashCut '-c1-3,2-4' '--output-delimiter=-'").AssertSuccess().Lines);
        Assert.Equal(new[] { "x" }, Run("'xy' | Invoke-BashCut '-c1,3' '--output-delimiter=-'").AssertSuccess().Lines);   // no trailing delimiter
    }

    [Fact]
    public void Bytes_CountUtf8Bytes_AndSoDoesC()
    {
        // GNU 9.4 (oracle): -c counts BYTES exactly like -b. é is 2 bytes (C3 A9).
        Assert.Equal(new[] { "hé" }, Run("'héllo' | Invoke-BashCut '-b1-3'").AssertSuccess().Lines);
        Assert.Equal(new[] { "hé" }, Run("'héllo' | Invoke-BashCut '-c1-3'").AssertSuccess().Lines);
        Assert.Equal(new[] { "l" }, Run("'héllo' | Invoke-BashCut '-c4'").AssertSuccess().Lines);
        Assert.Equal(new[] { "\U0001F680" }, Run("'\U0001F680abc' | Invoke-BashCut '-c1-4'").AssertSuccess().Lines);
        Assert.Equal(new[] { "héllo" }, Run("'héllo' | Invoke-BashCut '-c1-'").AssertSuccess().Lines);
    }

    [Fact]
    public void C_SplitMultiByteChar_KeepsRawByteMarker()
    {
        // -c1-2 of "héllo" = 68 C3: the lone lead byte round-trips as an escaped-byte marker (U+DC00+0xC3).
        // Byte stream is 68 C3 0A = 3 bytes (oracle: printf 'h\xc3\xa9llo\n' | cut -c1-2 | wc -c = 3).
        Assert.Equal(new[] { "3" }, Run("'héllo' | Invoke-BashCut '-c1-2' | Invoke-BashWc '-c'").AssertSuccess().Lines);
    }

    [Fact]
    public void Validation_Errors_ExitOne()
    {
        Run("'a' | Invoke-BashCut '-c1' '-f1'").AssertFailed(1, "only one list may be specified");   // FIX
        Run("'a' | Invoke-BashCut '-d:' '-c1'").AssertFailed(1, "input delimiter may be specified only when operating on fields");
        Run("'a' | Invoke-BashCut '-dab' '-f1'").AssertFailed(1, "the delimiter must be a single character");
        Run("'a' | Invoke-BashCut").AssertFailed(1, "you must specify a list of bytes, characters, or fields");
        Run("'a' | Invoke-BashCut '-f0'").AssertFailed(1, "fields are numbered from 1");
        Run("'a' | Invoke-BashCut '-z' '-f1'").AssertFailed(2, "not supported");
    }

    [Fact]
    public void DirectCall_DecoysStillBind()
    {
        Assert.Equal(new[] { "b" }, Run("'a:b:c' | Invoke-BashCut -d ':' -f 2").AssertSuccess().Lines);
        Assert.Equal(new[] { "b" }, Run("'a:b:c' | Invoke-BashCut -d: -f2").AssertSuccess().Lines);
        Assert.Equal(new[] { "bcd" }, Run("'abcdef' | Invoke-BashCut -c '2-4'").AssertSuccess().Lines);
        Assert.Equal(new[] { "a:c" }, Run("'a:b:c' | Invoke-BashCut -d ':' -f 2 --complement").AssertSuccess().Lines);
    }
}
