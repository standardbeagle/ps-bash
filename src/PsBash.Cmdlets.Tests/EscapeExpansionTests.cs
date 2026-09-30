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
    // tr SETs: \NNN 1-3 octal digits; no \x (GNU tr has none)
    [InlineData(EscapeDialect.Tr, @"\0", "\0")]
    [InlineData(EscapeDialect.Tr, @"\101", "A")]
    [InlineData(EscapeDialect.Tr, @"\1", "\u0001")]
    [InlineData(EscapeDialect.Tr, @"\18", "\u00018")]
    [InlineData(EscapeDialect.Tr, @"\n", "\n")]
    [InlineData(EscapeDialect.Tr, @"\\", @"\")]
    [InlineData(EscapeDialect.Tr, @"\x41", @"\x41")]
    public void Expand_MatchesBashPerDialect(EscapeDialect dialect, string input, string expected)
        => Assert.Equal(expected, BashEscapes.Expand(input, dialect));

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
        var dir = Path.Combine(Path.GetTempPath(), "psb-esc-" + Guid.NewGuid().ToString("N")[..8]);
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
        var dir = Path.Combine(Path.GetTempPath(), "psb-esc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "f").Replace('\\', '/');
            Stdout($"printf 'x\\0' | tee {f} > /dev/null");
            Assert.Equal(new byte[] { (byte)'x', 0 }, File.ReadAllBytes(f));
        }
        finally { Directory.Delete(dir, true); }
    }
}
