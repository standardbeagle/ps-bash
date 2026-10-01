using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Binary safety of the cmdlets (docs/specs/runtime-functions.md "Raw bytes"): invalid UTF-8 bytes travel
/// the string pipeline as escaped-byte markers (<see cref="PsBash.Core.RawBytes"/>) and every
/// byte-producing boundary writes them back as the single original byte. Oracle: GNU coreutils 9.4 / bash
/// 5.2 (`wsl bash`); the expected byte counts and digests below are its literal output
/// (`printf '\351' | wc -c` = 1, `printf '\xff\xfe' | base64` = `//4=`, md5 of byte E9 =
/// 3406877694691ddd1dfb0aca54681407, `printf 'a\xffb c' | wc -m -w -L -c` = `2 4 5 4` as words/chars/bytes/maxline).
/// </summary>
public class RawBytesCmdletTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    private static readonly byte[] AllBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    private static readonly byte[] RandomBytes = MakeRandom(7, 6000);

    private static byte[] MakeRandom(int seed, int n)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    public RawBytesCmdletTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), $"psb-raw-{Guid.NewGuid():N}".Substring(0, 20));
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "all"), AllBytes);
        File.WriteAllBytes(Path.Combine(_dir, "rnd"), RandomBytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>Runs <paramref name="script"/> from the fixture dir; every record read as its BashText.</summary>
    private CmdResult Run(string script)
    {
        // Invoke-BashRedirect resolves its -Path against the PROCESS directory (the emitted `cd` keeps that
        // in step with the shell); the test must not chdir the process (parallel classes), so anchor it.
        script = script.Replace("-Path '", $"-Path '{_dir}{Path.DirectorySeparatorChar}");
        var pwsh = _fixture.AcquireFresh();
        return CmdResult.Run(pwsh,
            $"$ErrorActionPreference='Continue'; Push-Location '{_dir.Replace("'", "''")}'; " +
            $"try {{ & {{ {script} }} | ForEach-Object {{ Get-BashText $_ }} }} finally {{ Pop-Location }}");
    }

    private byte[] Bytes(string name) => File.ReadAllBytes(P(name));

    // -- acceptance: producers, counters, writers ------------------------------------------------

    [Fact]
    public void Printf_LoneInvalidByte_WcCountsOneByte()
    {
        Assert.Equal(new[] { "1" }, Run(@"Invoke-BashPrintf '\351' | Invoke-BashWc '-c'").AssertSuccess().Lines);
        Assert.Equal(new[] { "1" }, Run(@"Invoke-BashPrintf '\xe9' | Invoke-BashWc '-c'").AssertSuccess().Lines);
    }

    [Fact]
    public void Printf_ValidUtf8ByteRun_IsThatCharacter_ThreeBytes()
    {
        Assert.Equal(new[] { "3" }, Run(@"Invoke-BashPrintf '\xe2\x82\xac' | Invoke-BashWc '-c'").Lines);
        Assert.Equal(new[] { "1" }, Run(@"Invoke-BashPrintf '\xe2\x82\xac' | Invoke-BashWc '-m'").Lines);
    }

    [Fact]
    public void Redirect_PrintfInvalidByte_WritesExactlyOneByte()
    {
        Run(@"Invoke-BashPrintf '\xe9' | Invoke-BashRedirect -Path 'one'").AssertSuccess();
        Assert.Equal(new byte[] { 0xE9 }, Bytes("one"));

        Run(@"Invoke-BashPrintf '\xff\xfe' | Invoke-BashRedirect -Path 'two'").AssertSuccess();
        Assert.Equal(new byte[] { 0xFF, 0xFE }, Bytes("two"));
    }

    [Fact]
    public void Redirect_Append_WritesExactBytes()
    {
        Run(@"Invoke-BashPrintf '\xe9' | Invoke-BashRedirect -Path 'ap'; Invoke-BashPrintf '\xe9\x80' | Invoke-BashRedirect -Path 'ap' -Append")
            .AssertSuccess();
        Assert.Equal(new byte[] { 0xE9, 0xE9, 0x80 }, Bytes("ap"));
    }

    [Fact]
    public void Base64_OfInvalidBytes_MatchesBash()
    {
        Assert.Equal(new[] { "//4=" }, Run(@"Invoke-BashPrintf '\xff\xfe' | Invoke-BashBase64").AssertSuccess().Lines);
    }

    [Fact]
    public void Md5AndSha256_OfInvalidByte_HashTheByte()
    {
        Assert.Equal(new[] { "3406877694691ddd1dfb0aca54681407  -" },
            Run(@"Invoke-BashPrintf '\xe9' | Invoke-BashMd5sum").AssertSuccess().Lines);
        string expected = Convert.ToHexString(SHA256.HashData(new byte[] { 0xE9 })).ToLowerInvariant();
        Assert.Equal(new[] { expected + "  -" },
            Run(@"Invoke-BashPrintf '\xe9' | Invoke-BashSha256sum").Lines);
    }

    // -- acceptance: files round-trip byte-identically -------------------------------------------

    [Theory]
    [InlineData("all")]
    [InlineData("rnd")]
    public void CatToRedirect_RoundTripsEveryByte(string source)
    {
        Run($"Invoke-BashCat '{source}' | Invoke-BashRedirect -Path 'copy-{source}'").AssertSuccess();
        Assert.Equal(Bytes(source), Bytes("copy-" + source));
    }

    [Fact]
    public void CatTwoFiles_Concatenates_Exactly()
    {
        Run("Invoke-BashCat 'all' 'rnd' | Invoke-BashRedirect -Path 'cat2'").AssertSuccess();
        Assert.Equal(AllBytes.Concat(RandomBytes).ToArray(), Bytes("cat2"));
    }

    [Fact]
    public void Tee_OfBinary_WritesTheFileExactly_AndPassesTheStreamOn()
    {
        Run("Invoke-BashCat 'rnd' | Invoke-BashTee 'tee-out' | Invoke-BashRedirect -Path 'tee-pass'").AssertSuccess();
        Assert.Equal(RandomBytes, Bytes("tee-out"));
        Assert.Equal(RandomBytes, Bytes("tee-pass"));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1)]
    [InlineData(255)]
    public void HeadBytes_OfBinaryFile_IsTheExactPrefix(int n)
    {
        Run($"Invoke-BashHead '-c' '{n}' 'rnd' | Invoke-BashRedirect -Path 'head-out'").AssertSuccess();
        Assert.Equal(RandomBytes.Take(n).ToArray(), Bytes("head-out"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1)]
    [InlineData(300)]
    public void TailBytes_OfBinaryFile_IsTheExactSuffix(int n)
    {
        Run($"Invoke-BashTail '-c' '{n}' 'rnd' | Invoke-BashRedirect -Path 'tail-out'").AssertSuccess();
        Assert.Equal(RandomBytes.Skip(RandomBytes.Length - n).ToArray(), Bytes("tail-out"));
    }

    [Fact]
    public void HeadAndTailBytes_OnAPipe_SliceTheExactStream()
    {
        Run("Invoke-BashCat 'rnd' | Invoke-BashHead '-c' '77' | Invoke-BashRedirect -Path 'ph'").AssertSuccess();
        Assert.Equal(RandomBytes.Take(77).ToArray(), Bytes("ph"));
        Run("Invoke-BashCat 'rnd' | Invoke-BashTail '-c' '77' | Invoke-BashRedirect -Path 'pt'").AssertSuccess();
        Assert.Equal(RandomBytes.Skip(RandomBytes.Length - 77).ToArray(), Bytes("pt"));
    }

    [Fact]
    public void Gzip_StdoutThenDecompressStdin_RoundTrips()
    {
        Run("Invoke-BashGzip -c 'rnd' | Invoke-BashGzip -d -c | Invoke-BashRedirect -Path 'gz-out'").AssertSuccess();
        Assert.Equal(RandomBytes, Bytes("gz-out"));
        Assert.True(File.Exists(P("rnd")), "gzip -c must not remove the source");
    }

    [Fact]
    public void Gzip_StdoutRedirect_IsAValidGzipFile()
    {
        Run("Invoke-BashGzip -c 'all' | Invoke-BashRedirect -Path 'all.gz'").AssertSuccess();
        using var gs = new System.IO.Compression.GZipStream(File.OpenRead(P("all.gz")), System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gs.CopyTo(ms);
        Assert.Equal(AllBytes, ms.ToArray());
    }

    [Fact]
    public void Gzip_NoOperand_CompressesThePipeline_AndZcatStyleDecompress()
    {
        Run(@"Invoke-BashPrintf 'a\xe9b' | Invoke-BashGzip | Invoke-BashGzip '-d' | Invoke-BashRedirect -Path 'gz-stdin'").AssertSuccess();
        Assert.Equal(new byte[] { 0x61, 0xE9, 0x62 }, Bytes("gz-stdin"));
    }

    [Fact]
    public void Base64_RoundTripsABinaryFile()
    {
        Run("Invoke-BashBase64 'rnd' | Invoke-BashBase64 '-d' | Invoke-BashRedirect -Path 'b64-out'").AssertSuccess();
        Assert.Equal(RandomBytes, Bytes("b64-out"));
        Assert.Equal(new[] { Convert.ToBase64String(RandomBytes) }, Run("Invoke-BashBase64 '-w' '0' 'rnd'").Lines);
    }

    [Fact]
    public void Checksums_OfBinaryFile_AndOfItsPipeStream_Agree_AndMatchTheTrueDigest()
    {
        string md5 = Convert.ToHexString(MD5.HashData(RandomBytes)).ToLowerInvariant();
        Assert.Equal(new[] { md5 + "  rnd" }, Run("Invoke-BashMd5sum 'rnd'").AssertSuccess().Lines);
        Assert.Equal(new[] { md5 + "  -" }, Run("Invoke-BashCat 'rnd' | Invoke-BashMd5sum").AssertSuccess().Lines);
    }

    [Fact]
    public void WcBytes_OfBinaryFile_IsTheFileLength_IncludingABom()
    {
        File.WriteAllBytes(P("bom"), new byte[] { 0xEF, 0xBB, 0xBF, 0x61, 0x0A });
        Assert.Equal(new[] { "5" }, Run("Invoke-BashWc '-c' 'bom'").Lines.Select(l => l.Split(' ')[0]).ToArray());
        Assert.Equal(new[] { RandomBytes.Length.ToString() }, Run("Invoke-BashWc '-c' 'rnd'").Lines.Select(l => l.Split(' ')[0]).ToArray());
    }

    [Fact]
    public void Split_BytePieces_AreExact()
    {
        Run("Invoke-BashSplit '-b' '1000' 'rnd' 'piece-'").AssertSuccess();
        var joined = Directory.GetFiles(_dir, "piece-*").OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(File.ReadAllBytes).ToArray();
        Assert.Equal(RandomBytes, joined);
    }

    // -- cat copies a binary file byte for byte; a text file keeps the CRLF/BOM-transparent policy ----------

    [Fact]
    public void Cat_BinaryFile_KeepsCrlfAndBom()
    {
        // A NUL in the first 8 KB marks the file binary (the grep/rg heuristic): no BOM sniffing, no CRLF rewrite.
        File.WriteAllBytes(P("crlf-bom"), new byte[] { 0x00, 0xEF, 0xBB, 0xBF, 0x61, 0x0D, 0x0A, 0x62, 0x0D, 0x0A, 0x63 });
        Run("Invoke-BashCat 'crlf-bom' | Invoke-BashRedirect -Path 'crlf-copy'").AssertSuccess();
        Assert.Equal(Bytes("crlf-bom"), Bytes("crlf-copy"));
    }

    [Fact]
    public void Cat_TextFile_KeepsTheTransparentCrlfAndBomPolicy()
    {
        File.WriteAllBytes(P("crlf-text"), new byte[] { 0xEF, 0xBB, 0xBF, 0x61, 0x0D, 0x0A, 0x62, 0x0D, 0x0A });
        Run("Invoke-BashCat 'crlf-text' | Invoke-BashRedirect -Path 'crlf-text-copy'").AssertSuccess();
        Assert.Equal(new byte[] { 0x61, 0x0A, 0x62, 0x0A }, Bytes("crlf-text-copy"));
    }

    // -- wc counts what GNU counts in a UTF-8 locale -------------------------------------------------

    [Fact]
    public void WcChars_DoNotCountInvalidBytes_AndTheyAreNeitherWordNorSeparator()
    {
        // oracle: printf '\xff\xfe' | wc -m  -> 0
        Assert.Equal(new[] { "0" }, Run(@"Invoke-BashPrintf '\xff\xfe' | Invoke-BashWc '-m'").Lines);
        // oracle: printf 'a\xffb c' | wc -w -m -c -L  -> words 2, chars 4, bytes 5, max line 4
        var line = Run(@"Invoke-BashPrintf 'a\xffb c' | Invoke-BashWc '-w' '-m' '-c' '-L'").AssertSuccess().Lines.Single();
        Assert.Equal(new[] { "2", "4", "5", "4" }, line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // oracle: printf '\xe9\n' | wc -mwlLc -> 1 0 1 2 0 (lines words chars bytes maxline): the lone byte is no word
        var one = Run(@"Invoke-BashPrintf '\xe9\n' | Invoke-BashWc '-l' '-w' '-m' '-c' '-L'").Lines.Single();
        Assert.Equal(new[] { "1", "0", "1", "2", "0" }, one.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void WcChars_ValidUtf8_CountsCharactersNotBytes()
    {
        // oracle: printf 'caf\xc3\xa9 \xf0\x9f\x98\x80 \xe6\x97\xa5\n' | wc -m -c -> 9 chars (c a f é sp 😀 sp 日 nl), 15 bytes
        var line = Run(@"Invoke-BashPrintf 'caf\xc3\xa9 \xf0\x9f\x98\x80 \xe6\x97\xa5\n' | Invoke-BashWc '-m' '-c'").Lines.Single();
        Assert.Equal(new[] { "9", "15" }, line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // -- tr / cut work on the bytes ---------------------------------------------------------------

    [Fact]
    public void Tr_OctalSetMatchesTheByte()
    {
        Run(@"Invoke-BashPrintf 'a\351b' | Invoke-BashTr '\351' 'x' | Invoke-BashRedirect -Path 'tr1'").AssertSuccess();
        Assert.Equal(Encoding.ASCII.GetBytes("axb"), Bytes("tr1"));
        Run(@"Invoke-BashPrintf 'a\351b' | Invoke-BashTr '-d' '\351' | Invoke-BashRedirect -Path 'tr2'").AssertSuccess();
        Assert.Equal(Encoding.ASCII.GetBytes("ab"), Bytes("tr2"));
    }

    [Fact]
    public void Tr_OctalRange_CoversTheHighBytes()
    {
        Run(@"Invoke-BashCat 'all' | Invoke-BashTr '\200-\377' 'x' | Invoke-BashRedirect -Path 'tr3'").AssertSuccess();
        var expected = AllBytes.Select(b => b >= 0x80 ? (byte)'x' : b).ToArray();
        Assert.Equal(expected, Bytes("tr3"));
    }

    [Fact]
    public void CutBytes_SelectsTheRawByte()
    {
        Run(@"Invoke-BashPrintf 'a\351b\n' | Invoke-BashCut '-b' '2' | Invoke-BashRedirect -Path 'cut1'").AssertSuccess();
        Assert.Equal(new byte[] { 0xE9, 0x0A }, Bytes("cut1"));
    }

    // -- valid UTF-8 is untouched everywhere -------------------------------------------------------------

    [Fact]
    public void ValidUtf8_AccentsEmojiCjk_RoundTripUnchanged()
    {
        var text = "café \U0001F600 日本語\n";
        File.WriteAllBytes(P("utf8"), Encoding.UTF8.GetBytes(text));
        Run("Invoke-BashCat 'utf8' | Invoke-BashRedirect -Path 'utf8-copy'").AssertSuccess();
        Assert.Equal(Encoding.UTF8.GetBytes(text), Bytes("utf8-copy"));
        Assert.Equal(new[] { text.TrimEnd('\n') }, Run("Invoke-BashCat 'utf8'").Lines);
    }

    // -- the reader keeps invalid bytes as markers, the line tools see ordinary text ---------------------------

    [Fact]
    public void Grep_FindsTextAroundInvalidBytes()
    {
        File.WriteAllBytes(P("mixed"), new byte[] { 0x66, 0x6F, 0x6F, 0xE9, 0x0A, 0x62, 0x61, 0x72, 0x0A });
        var r = Run("Invoke-BashGrep 'foo' 'mixed' | Invoke-BashRedirect -Path 'grep-out'").AssertSuccess();
        Assert.Equal(new byte[] { 0x66, 0x6F, 0x6F, 0xE9, 0x0A }, Bytes("grep-out"));
        Assert.Empty(r.Lines);
    }

    [Fact]
    public void Sort_OfLinesWithInvalidBytes_KeepsEveryByte()
    {
        File.WriteAllBytes(P("srt"), new byte[] { 0x62, 0xE9, 0x0A, 0x61, 0xFF, 0x0A });
        Run("Invoke-BashSort 'srt' | Invoke-BashRedirect -Path 'srt-out'").AssertSuccess();
        var outBytes = Bytes("srt-out");
        Assert.Equal(6, outBytes.Length);
        Assert.Equal(new[] { (byte)0xE9, (byte)0xFF }.OrderBy(x => x), outBytes.Where(b => b >= 0x80).OrderBy(x => x));
    }
}
