using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Text;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// NUL / octal / hex / unicode escape handling for printf (format and %b), echo -e, tr SETs
/// and $'...' quoting. Before the shared <see cref="BashEscapes"/> expander, none of
/// <c>\0</c> <c>\NNN</c> <c>\0NNN</c> <c>\xHH</c> <c>\uHHHH</c> <c>\e</c> <c>\c</c> existed:
/// <c>printf 'a\0b\n' | wc -c</c> answered 5 (bash: 4) and <c>tr '\0' X</c> matched both
/// <c>\</c> and <c>0</c>.
///
/// Oracle (qa-rubric Directive 1): every expected value below was taken from bash 5.2 via
/// <c>wsl.exe -d Ubuntu-24.04 -- bash -c '… | od -c'</c>. ps-bash has no <c>od</c>, so the tests
/// assert the resulting characters directly (a NUL shows up as <c>\0</c> in the expected strings).
/// </summary>
public class EscapeExpansionTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public EscapeExpansionTests(SharedPwshFixture fixture) => _fixture = fixture;

    /// <summary>Transpile real bash text, run it, and return stdout as the exact byte-text a file would hold.</summary>
    private string Stdout(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        var pwsh = _fixture.AcquireFresh();
        Collection<PSObject> result = pwsh.AddScript(ps).Invoke();
        pwsh.Commands.Clear();
        var errs = string.Join(" | ", pwsh.Streams.Error.Select(e => e.ToString()));
        Assert.True(errs.Length == 0, $"errors while running: {ps} => {errs}");
        var sb = new StringBuilder();
        foreach (var o in result)
        {
            if (o is null) continue;
            if (o.BaseObject is string s) sb.Append(s).Append('\n');
            else sb.Append(BashRuntime.RecordFilePayload(o));
        }
        return sb.ToString();
    }

    // ---- the shared expander, per dialect (one row = one oracle-checked case) ----

    [Theory]
    // printf FORMAT: \NNN is 1-3 octal digits including the first; \c is literal
    [InlineData(EscapeDialect.PrintfFormat, @"a\0b", "a\0b")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\0101b", "a\b" + "1b")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\101b", "aAb")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\1b", "a\u0001b")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\18b", "a\u00018b")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\1234", "aS4")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\x41b", "aAb")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\x4b", "aK")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\xZb", @"a\xZb")]
    [InlineData(EscapeDialect.PrintfFormat, @"aéb", "aéb")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\U0001F600b", "a\U0001F600b")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\eb", "a\u001bb")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\Eb", "a\u001bb")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\cb", @"a\cb")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\\b", @"a\b")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\qb", @"a\qb")]
    [InlineData(EscapeDialect.PrintfFormat, "a\\\"b", "a\"b")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\?b", "a?b")]
    [InlineData(EscapeDialect.PrintfFormat, @"a\", @"a\")]
    // printf %b ARGUMENT: \0NNN (0 + up to 3 digits) and \NNN both octal; \c stops
    [InlineData(EscapeDialect.PrintfB, @"a\0101b", "aAb")]
    [InlineData(EscapeDialect.PrintfB, @"a\101b", "aAb")]
    [InlineData(EscapeDialect.PrintfB, @"a\0b", "a\0b")]
    [InlineData(EscapeDialect.PrintfB, @"a\00b", "a\0b")]
    [InlineData(EscapeDialect.PrintfB, @"a\x41b", "aAb")]
    [InlineData(EscapeDialect.PrintfB, @"a\eb", "a\u001bb")]
    [InlineData(EscapeDialect.PrintfB, @"aéb", "aéb")]
    [InlineData(EscapeDialect.PrintfB, @"a\\b", @"a\b")]
    [InlineData(EscapeDialect.PrintfB, @"a\qb", @"a\qb")]
    // echo -e: ONLY \0NNN is octal; \NNN stays literal; \" stays literal
    [InlineData(EscapeDialect.Echo, @"a\0b", "a\0b")]
    [InlineData(EscapeDialect.Echo, @"a\0101b", "aAb")]
    [InlineData(EscapeDialect.Echo, @"a\101b", @"a\101b")]
    [InlineData(EscapeDialect.Echo, @"a\01b", "a\u0001b")]
    [InlineData(EscapeDialect.Echo, @"a\0777b", "aÿb")]
    [InlineData(EscapeDialect.Echo, @"a\x41b", "aAb")]
    [InlineData(EscapeDialect.Echo, @"a\eb", "a\u001bb")]
    [InlineData(EscapeDialect.Echo, @"a\Eb", "a\u001bb")]
    [InlineData(EscapeDialect.Echo, @"aéb", "aéb")]
    [InlineData(EscapeDialect.Echo, @"a\\b", @"a\b")]
    [InlineData(EscapeDialect.Echo, @"a\qb", @"a\qb")]
    [InlineData(EscapeDialect.Echo, "a\\\"b", "a\\\"b")]
    public void Expand_MatchesBashPerDialect(EscapeDialect dialect, string input, string expected)
        => Assert.Equal(expected, BashEscapes.Expand(input, dialect));

    // ---- \xHH / \NNN name BYTES: a run that is valid UTF-8 is that character (bash writes those bytes) ----
    // Oracle (bash 5.2, `| od -An -tx1`): printf '\xe2\x82\xac' = e2 82 ac, printf 'caf\xc3\xa9' = 63 61 66 c3 a9,
    // printf '\xf0\x9f\x98\x80' = f0 9f 98 80. Before, each byte became its own Latin-1 char (â<0x82>¬) and
    // went out as SIX bytes.

    [Theory]
    [InlineData(EscapeDialect.PrintfFormat, @"\xe2\x82\xac", "€")]
    [InlineData(EscapeDialect.PrintfFormat, @"\342\202\254", "€")]
    [InlineData(EscapeDialect.PrintfFormat, @"caf\xc3\xa9", "café")]
    [InlineData(EscapeDialect.PrintfFormat, @"\xf0\x9f\x98\x80", "\U0001F600")]
    [InlineData(EscapeDialect.PrintfFormat, @"\xc3\xa9\xc3\xa9", "éé")]
    [InlineData(EscapeDialect.PrintfFormat, @"\xc3\xa9\x41", "éA")]
    [InlineData(EscapeDialect.PrintfB, @"\xe2\x82\xac", "€")]
    [InlineData(EscapeDialect.PrintfB, @"\0342\0202\0254", "€")]
    [InlineData(EscapeDialect.Echo, @"\xe2\x82\xac", "€")]
    [InlineData(EscapeDialect.Echo, @"\0342\0202\0254", "€")]
    // Not valid UTF-8 (overlong, surrogate, truncated, lone, interrupted): one Latin-1 char per byte.
    // KNOWN GAP — bash writes the raw bytes, ps-bash writes the UTF-8 of those chars (see the design note).
    [InlineData(EscapeDialect.PrintfFormat, @"\xe9", "é")]
    [InlineData(EscapeDialect.PrintfFormat, @"\351", "é")]
    [InlineData(EscapeDialect.PrintfFormat, @"\xc3A", "ÃA")]
    [InlineData(EscapeDialect.PrintfFormat, @"\xc0\x80", "À\u0080")]
    [InlineData(EscapeDialect.PrintfFormat, @"\xed\xa0\x80", "í \u0080")]
    [InlineData(EscapeDialect.PrintfFormat, @"\xe2\x82", "â\u0082")]
    [InlineData(EscapeDialect.PrintfFormat, @"\xe2x\x82\xac", "âx\u0082¬")]
    public void Expand_ByteRuns_AreUtf8Decoded(EscapeDialect dialect, string input, string expected)
        => Assert.Equal(expected, BashEscapes.Expand(input, dialect));

    [Fact]
    public void Redirect_PrintfUtf8ByteEscapes_WritesTheExactBytes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psb-esc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "f").Replace('\\', '/');
            Stdout($"printf '\\xe2\\x82\\xac' > {f}");
            // bash: e2 82 ac — three bytes, not the six of "â<0x82>¬" re-encoded
            Assert.Equal(new byte[] { 0xE2, 0x82, 0xAC }, File.ReadAllBytes(f));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnsiC_ByteEscapes_AreUtf8Decoded()
    {
        Assert.Equal("€\n", Stdout("echo $'\\xe2\\x82\\xac'"));
        Assert.Equal("€\n", Stdout("echo $'\\342\\202\\254'"));
        Assert.Equal("café\n", Stdout("echo $'caf\\xc3\\xa9'"));
    }

    // ---- tr SETs: ExpandTrSet (escapes + classes + ranges in ONE pass, every row oracle-checked) ----
    // \NNN is 1-3 octal digits; GNU tr has no \x \e \c \u: an unknown escape DROPS the backslash and
    // keeps the character; an escaped '-' / '[' is a literal, never a range / class operator.

    [Theory]
    [InlineData(@"\0", "\0")]
    [InlineData(@"\101", "A")]
    [InlineData(@"\1", "\u0001")]
    [InlineData(@"\18", "\u00018")]
    [InlineData(@"\n", "\n")]
    [InlineData(@"\\", @"\")]
    [InlineData(@"\q", "q")]  // FIX (was: \q)
    [InlineData(@"\x41", "x41")]  // FIX (was: \x41) GNU tr has no \x: backslash dropped
    [InlineData(@"\e", "e")]
    [InlineData(@"\c", "c")]
    [InlineData(@"\8", "8")]
    [InlineData(@"a\qb", "aqb")]
    [InlineData(@"a\-c", "a-c")]  // FIX: literal '-', not the range a..c
    [InlineData(@"a-c", "abc")]
    [InlineData(@"a-c\-", "abc-")]
    [InlineData(@"\[:digit:]", "[:digit:]")]  // escaped '[' is not a class opener
    [InlineData(@"[:digit:]", "0123456789")]
    [InlineData(@"[:upper:]x", "ABCDEFGHIJKLMNOPQRSTUVWXYZx")]
    [InlineData(@"[:punct:]", "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~")]  // its '-' is not a range operator
    [InlineData(@"[:bogus:]", "[:bogus:]")]
    [InlineData(@"\011-\012", "\t\n")]  // an escaped char can END or START a range
    [InlineData(@"a-\n", null)]  // reverse range (a > \n): an error, see ReverseRange test
    [InlineData(@"a\", @"a\")]  // lone trailing backslash stays literal (GNU also warns)
    [InlineData(@"\", @"\")]
    public void ExpandTrSet_MatchesGnuTr(string input, string? expected)
    {
        if (expected is null)
        {
            Assert.Throws<TrSetException>(() => BashEscapes.ExpandTrSet(input, out _));
            return;
        }
        Assert.Equal(expected, BashEscapes.ExpandTrSet(input, out _));
    }

    [Theory]
    [InlineData(@"a\", true)]
    [InlineData(@"\", true)]
    [InlineData(@"\\", false)]
    [InlineData(@"a", false)]
    public void ExpandTrSet_ReportsTrailingBackslash(string input, bool warn)
    {
        BashEscapes.ExpandTrSet(input, out bool trailing);
        Assert.Equal(warn, trailing);
    }

    [Fact]
    public void ExpandTrSet_ReverseRange_MessageIsGnus()
    {
        var ex = Assert.Throws<TrSetException>(() => BashEscapes.ExpandTrSet("c-a", out _));
        Assert.Equal("range-endpoints of 'c-a' are in reverse collating sequence order", ex.Message);
    }

    [Fact]
    public void Tr_UnknownEscape_DropsTheBackslash()
        // bash: aXb — `\q` is just `q`
        => Assert.Equal("aXb\n", Stdout(@"printf 'aqb\n' | tr '\q' X"));

    [Fact]
    public void Tr_EscapedDash_IsLiteral_NotARange()
        // bash: XXXb — a, '-' and c only
        => Assert.Equal("XXXb\n", Stdout(@"printf 'a-cb\n' | tr 'a\-c' X"));

    [Fact]
    public void Tr_EscapedDash_TranslatesPositionally()
        // bash: XbYZ
        => Assert.Equal("XbYZ\n", Stdout(@"printf 'ab-c\n' | tr 'a\-c' XYZ"));

    // ---- tr and the record terminator: seq/echo/cat emit bare lines, printf emits exact bytes ----
    // Every expected value was taken from bash 5.2 (`… | od -c`). Before, `seq 1 3 | tr -d '\n'`
    // printed 1 2 3 on separate lines because the newline was never part of the record tr saw.

    [Theory]
    [InlineData("seq 1 3 | tr -d '\\n'", "123")]
    [InlineData("seq 1 3 | tr '\\n' ,", "1,2,3,")]
    [InlineData("seq 1 3 | tr '\\n' '\\t'", "1\t2\t3\t")]
    [InlineData("printf 'a\\nb\\n' | tr -d '\\n'", "ab")]
    [InlineData("echo hi | tr -d '\\n'", "hi")]
    [InlineData("echo -n hi | tr h X", "Xi")]  // was: a newline the producer never wrote
    [InlineData("echo -e 'a\\nb' | tr '\\n' ,", "a,b,")]
    [InlineData("seq 1 3 | tr 1 x", "x\n2\n3\n")]  // a newline-free table streams as before
    [InlineData("printf 'a\\nb' | tr x y", "a\nb")]  // no final newline in, none out (was: one added)
    [InlineData("seq 1 3 | tr '\\n' ' ' | tr ' ' X", "1X2X3X")]  // exact bytes feed the next tr
    [InlineData("x=$(seq 1 3 | tr -d \"\\n\"); echo \"$x\"", "123\n")]
    [InlineData("printf 'a\\n\\n\\nb\\n' | tr -s '\\n'", "a\nb\n")]  // squeeze runs across records
    [InlineData("(echo a; echo; echo; echo b) | tr -s '\\n'", "a\nb\n")]
    [InlineData("seq 1 3 | tr -c 0-9 X", "1X2X3X")]  // the complement includes the newline
    [InlineData("seq 1 3 | tr -cd '0-9\\n'", "1\n2\n3\n")]  // ... unless SET1 names it
    [InlineData("seq 1 3 | tr -cd 0-9", "123")]
    [InlineData("echo 'a b c' | tr ' ' '\\n'", "a\nb\nc\n")]  // INTO a newline: unchanged shape
    [InlineData("echo 'a b c' | tr ' ' '\\n' | wc -l", "3\n")]
    [InlineData("printf '' | tr -d '\\n'", "")]
    [InlineData("echo | tr -d '\\n'", "")]
    public void Tr_TreatsTheRecordTerminatorAsARealNewline(string bash, string expected)
        => Assert.Equal(expected, Stdout(bash).Replace("\r\n", "\n"));

    [Fact]
    public void Tr_TrailingBackslash_IsLiteralAndWarns()
    {
        // bash: aXb plus "tr: warning: an unescaped backslash at end of string is not portable" on stderr
        var ps = PsEmitter.Transpile(@"printf 'a\\b\n' | tr '\' X")!;
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(ps).Invoke();
        pwsh.Commands.Clear();
        Assert.Equal("aXb", string.Concat(result.Select(o => BashRuntime.GetBashText(o).TrimEnd('\n'))));
        Assert.Contains(pwsh.Streams.Error, e => e.ToString() ==
            "tr: warning: an unescaped backslash at end of string is not portable");
    }

    [Theory]
    [InlineData(EscapeDialect.Echo)]
    [InlineData(EscapeDialect.PrintfB)]
    public void Expand_BackslashC_StopsOutput(EscapeDialect dialect)
    {
        Assert.Equal("a", BashEscapes.Expand(@"a\cb", dialect, out bool stopped));
        Assert.True(stopped);
    }

    // ---- through the real transpile + cmdlets ----

    [Fact]
    public void Printf_NulEscape_KeepsNulByte()
        => Assert.Equal("a\0b\n", Stdout(@"printf 'a\0b\n'"));

    [Fact]
    public void Printf_NulThenWcC_Counts4NotBackslashZero()
        => Assert.Equal("4", Stdout(@"printf 'a\0b\n' | wc -c").Trim());

    [Fact]
    public void Printf_NulThenTr_TranslatesOnlyTheNul()
        // bash: aXb (tr's \0 is the NUL, not '\' and '0')
        => Assert.Equal("aXb\n", Stdout(@"printf 'a\0b\n' | tr '\0' X"));

    [Fact]
    public void Tr_BackslashZeroSet_DoesNotMatchLiteralZeroOrBackslash()
        => Assert.Equal("a0b\\c\n", Stdout(@"printf 'a0b\\c\n' | tr '\0' X"));

    [Fact]
    public void Tr_OctalSet_MatchesTheCharacter()
        => Assert.Equal("aXb\n", Stdout(@"printf 'aAb\n' | tr '\101' X"));

    [Fact]
    public void Tr_DeleteNul()
        => Assert.Equal("ab\n", Stdout(@"printf 'a\0b\n' | tr -d '\0'"));

    [Fact]
    public void Printf_PercentB_OctalNulAndStop()
    {
        Assert.Equal("aAb", Stdout(@"printf '%b' 'a\0101b'"));
        Assert.Equal("a\0b", Stdout(@"printf '%b' 'a\0b'"));
        // \c in %b suppresses everything after it, including the rest of the format.
        Assert.Equal("a", Stdout(@"printf '%b|' 'a\cb'"));
    }

    [Fact]
    public void Printf_PercentB_KeepsLeadingZerosOfNumericLookingArg()
        => Assert.Equal("A\n", Stdout(@"printf '%b\n' '\0101'"));

    [Fact]
    public void Printf_Format_OctalHexUnicodeEscape()
    {
        Assert.Equal("aAb", Stdout(@"printf 'a\101b'"));
        Assert.Equal("aAb", Stdout(@"printf 'a\x41b'"));
        Assert.Equal("aéb", Stdout(@"printf 'aéb'"));
        Assert.Equal("a\u001bb", Stdout(@"printf 'a\eb'"));
    }

    [Fact]
    public void EchoE_NulOctalStop()
    {
        Assert.Equal("a\0b\n", Stdout(@"echo -e 'a\0b'"));
        Assert.Equal("aAb\n", Stdout(@"echo -e 'a\0101b'"));
        Assert.Equal("a\\101b\n", Stdout(@"echo -e 'a\101b'"));
        // \c also swallows the trailing newline.
        Assert.Equal("x", Stdout(@"echo -e 'x\cy'"));
        Assert.Equal("x", Stdout(@"echo -en 'x\cy'"));
    }

    [Fact]
    public void AnsiC_NulTruncatesWord_LikeBashCStrings()
    {
        Assert.Equal("a\n", Stdout("echo $'a\\0b'"));
        Assert.Equal("aAb\n", Stdout("echo $'a\\101b'"));
        Assert.Equal("aAb\n", Stdout("echo $'a\\x41b'"));
    }

    // ---- NUL must survive to pipeline consumers ----

    [Fact]
    public void Xargs_NullDelim_FedByPrintf()
        // bash: a b
        => Assert.Equal("a b\n", Stdout(@"printf 'a\0b\0' | xargs -0 echo"));

    [Fact]
    public void Xargs_NullDelim_OneItemPerLineFedByPrintf()
        => Assert.Equal("a\nb\n", Stdout(@"printf 'a\0b\0' | xargs -0 -n 1 echo"));

    [Fact]
    public void Redirect_PrintfNul_WritesExactlyTwoBytes()
    {
        // NOT Path.GetTempPath(): on Linux that is /tmp, which the emitter rewrites to $env:TEMP (unset there), so the path would not resolve.
        var dir = Path.Combine(AppContext.BaseDirectory, "psb-esc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "f").Replace('\\', '/');
            Stdout($"printf 'x\\0' > {f}");
            Assert.Equal(new byte[] { (byte)'x', 0 }, File.ReadAllBytes(f));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Tee_PrintfNul_WritesExactlyTwoBytes()
    {
        // NOT Path.GetTempPath(): on Linux that is /tmp, which the emitter rewrites to $env:TEMP (unset there), so the path would not resolve.
        var dir = Path.Combine(AppContext.BaseDirectory, "psb-esc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "f").Replace('\\', '/');
            Stdout($"printf 'x\\0' | tee {f} > /dev/null");
            Assert.Equal(new byte[] { (byte)'x', 0 }, File.ReadAllBytes(f));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TmpPath_WriteThenRead_RoundTripsOnThisOs()
    {
        // /tmp must resolve on whichever OS runs the transpiled text: Windows maps it to the
        // temp dir, Linux/macOS keep the literal /tmp ($env:TEMP is unset there, which used
        // to turn /tmp/x into \x at the filesystem root).
        var name = "psbash-x-" + Guid.NewGuid().ToString("N")[..8];
        var p = "/tmp/" + name;
        try
        {
            Assert.Equal("hi\n", Stdout($"echo hi > {p}; cat {p}"));
            var real = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, name)
                : p;
            Assert.True(File.Exists(real), real);
        }
        finally
        {
            var real = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, name) : p;
            if (File.Exists(real)) File.Delete(real);
        }
    }}
