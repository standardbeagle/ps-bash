using System.Text;
using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// GNU-vs-ps-bash comparisons for BINARY safety (docs/specs/runtime-functions.md "Raw bytes"): bytes that
/// are not valid UTF-8 survive every boundary. Stdout stays ASCII (counts, base64, digests) so the
/// canonicalizer cannot hide a difference; file CONTENT is compared byte for byte through the FsStateOracle
/// snapshot (base64 of every file in the resulting tree).
/// </summary>
public class RawBytesDifferentialTests
{
    /// <summary>A printf format that writes exactly <paramref name="bytes"/> (every byte as a \NNN octal escape).</summary>
    private static string Oct(IEnumerable<byte> bytes) =>
        string.Concat(bytes.Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));

    private static readonly string AllBytes = Oct(Enumerable.Range(0, 256).Select(i => (byte)i));

    private static readonly string Random1k = Oct(Rand(3, 1000));

    private static byte[] Rand(int seed, int n)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static string MkBin(string name, string octal) => $"printf '{octal}' > {name}";

    // ───────────── the acceptance probes ─────────────

    [SkippableFact] public Task Printf_LoneInvalidByte_WcC_IsOne() =>
        AssertOracle.EqualAsync(@"printf '\351' | wc -c");

    [SkippableFact] public Task Printf_HexInvalidByte_WcC_IsOne() =>
        AssertOracle.EqualAsync(@"printf '\xe9' | wc -c");

    [SkippableFact] public Task Printf_InvalidByte_Redirect_WritesOneByteFile() =>
        EqualAsync("", @"printf '\xe9' > f");

    [SkippableFact] public Task Printf_InvalidBytes_Base64() =>
        AssertOracle.EqualAsync(@"printf '\xff\xfe' | base64");

    [SkippableFact] public Task Printf_InvalidByte_Md5sum() =>
        AssertOracle.EqualAsync(@"printf '\xe9' | md5sum");

    [SkippableFact] public Task Printf_InvalidByte_Sha256sum() =>
        AssertOracle.EqualAsync(@"printf '\xe9' | sha256sum");

    [SkippableFact] public Task Printf_ValidUtf8_Euro_IsThreeBytes() =>
        AssertOracle.EqualAsync(@"printf '\xe2\x82\xac' | wc -c");

    [SkippableFact] public Task Printf_ValidUtf8_Euro_IsOneCharacter() =>
        AssertOracle.EqualAsync(@"printf '\xe2\x82\xac' | wc -m");

    [SkippableFact] public Task AnsiCQuoting_InvalidByte_IsOneByte() =>
        AssertOracle.EqualAsync(@"printf '%s' $'\xe9' | wc -c");

    [SkippableFact] public Task EchoE_InvalidByte_IsOneByteAndNewline() =>
        AssertOracle.EqualAsync(@"echo -e '\xe9' | wc -c");

    [SkippableFact] public Task CommandSubstitution_KeepsTheInvalidByte() =>
        AssertOracle.EqualAsync(@"x=$(printf '\351'); printf '%s' ""$x"" | wc -c");

    [SkippableFact] public Task PrintfAppendRedirect_InvalidBytes() =>
        EqualAsync("", @"printf '\xe9' >> f; printf '\xe9\x80' >> f");

    // ───────────── files round-trip byte-identically ─────────────

    [SkippableFact] public Task Cat_EveryByte_ToRedirect() =>
        EqualAsync(MkBin("bin", AllBytes), "cat bin > copy");

    [SkippableFact] public Task Cat_RandomBinary_ToRedirect() =>
        EqualAsync(MkBin("bin", Random1k), "cat bin > copy");

    [SkippableFact] public Task Cat_TwoBinaryFiles_Concatenate() =>
        EqualAsync(MkBin("a", AllBytes) + "\n" + MkBin("b", Random1k), "cat a b > c");

    [SkippableFact] public Task HeadBytes_OfBinary() =>
        EqualAsync(MkBin("bin", Random1k), "head -c 100 bin > h");

    [SkippableFact] public Task TailBytes_OfBinary() =>
        EqualAsync(MkBin("bin", Random1k), "tail -c 10 bin > t");

    [SkippableFact] public Task HeadAndTailBytes_OnAPipe() =>
        EqualAsync(MkBin("bin", Random1k), "cat bin | head -c 100 > h; cat bin | tail -c 10 > t");

    [SkippableFact] public Task Tee_OfBinary() =>
        EqualAsync(MkBin("bin", Random1k), "cat bin | tee t > /dev/null");

    [SkippableFact] public Task Gzip_StdoutThenDecompress_RoundTrips() =>
        EqualAsync(MkBin("bin", Random1k), "gzip -c bin | gzip -dc > out");

    [SkippableFact] public Task Gzip_File_ThenDecompress_RoundTrips() =>
        EqualAsync(MkBin("bin", Random1k), "gzip -k bin; gzip -dc bin.gz > out; rm bin.gz");

    [SkippableFact] public Task Base64_EncodeDecode_RoundTrips() =>
        EqualAsync(MkBin("bin", Random1k), "base64 bin | base64 -d > out");

    [SkippableFact] public Task Base64_Decode_KeepsTheFinalNewlineByte() =>
        AssertOracle.EqualAsync(@"printf 'a\n' | base64 | base64 -d | wc -c; printf 'a' | base64 | base64 -d | wc -c; printf 'a\n\n' | base64 | base64 -d | wc -c");

    [SkippableFact] public Task Base64_Wrapped_UsesLfBetweenLines() =>
        AssertOracle.EqualAsync(MkBin("bin", Random1k) + "; base64 bin | wc -c; base64 -w 20 bin | wc -c; rm bin");

    [SkippableFact] public Task Base64_OfBinaryFile() =>
        AssertOracle.EqualAsync(MkBin("bin", AllBytes) + "; base64 bin; rm bin");

    [SkippableFact] public Task Checksums_OfBinaryFile() =>
        AssertOracle.EqualAsync(MkBin("bin", Random1k) + "; md5sum bin; sha1sum bin; sha256sum bin; rm bin");

    [SkippableFact] public Task WcC_OfBinaryFile_AndOfItsStream() =>
        AssertOracle.EqualAsync(MkBin("bin", Random1k) + "; wc -c < bin; cat bin | wc -c; rm bin");

    [SkippableFact] public Task StdinRedirect_FeedsExactBytes() =>
        AssertOracle.EqualAsync(MkBin("bin", Random1k) + "; md5sum < bin; base64 < bin | md5sum; rm bin");

    [SkippableFact] public Task Split_BinaryByBytes() =>
        EqualAsync(MkBin("bin", Random1k), "split -b 300 bin p");

    // ───────────── counters, tr, cut ─────────────

    [SkippableFact] public Task Wc_Mwlc_OnInvalidByteWithText() =>
        AssertOracle.EqualAsync(@"printf 'a\xffb c' | wc -w -m -c -L | awk '{print $1, $2, $3, $4}'");

    [SkippableFact] public Task Wc_Mwlc_OnLoneInvalidByteAndNewline() =>
        AssertOracle.EqualAsync(@"printf '\xe9\n' | wc -l -w -m -c -L | awk '{print $1, $2, $3, $4, $5}'");

    [SkippableFact] public Task Wc_M_OnOnlyInvalidBytes() =>
        AssertOracle.EqualAsync(@"printf '\xff\xfe' | wc -m");

    [SkippableFact] public Task Wc_Mc_OnAccentsEmojiCjk() =>
        AssertOracle.EqualAsync(@"printf 'caf\xc3\xa9 \xf0\x9f\x98\x80 \xe6\x97\xa5\n' | wc -m -c | awk '{print $1, $2}'");

    [SkippableFact] public Task Tr_OctalSet_MatchesTheByte() =>
        AssertOracle.EqualAsync(@"printf 'a\351b' | tr '\351' x");

    [SkippableFact] public Task Tr_Delete_InvalidByte() =>
        AssertOracle.EqualAsync(@"printf 'a\351b' | tr -d '\351' | wc -c");

    [SkippableFact] public Task Cut_Bytes_SelectsTheInvalidByte() =>
        AssertOracle.EqualAsync(@"printf 'a\351b\n' | cut -b2 | wc -c");

    [SkippableFact] public Task Cut_Bytes_ExcludesTheInvalidByte() =>
        AssertOracle.EqualAsync(@"printf 'a\351b\n' | cut -b1,3");

    // ───────────── the fused lane and process substitution ─────────────

    [SkippableFact] public Task FusedLane_InvalidBytes_PassThroughCatHeadWc() =>
        AssertOracle.EqualAsync(@"printf 'a\351\nb\377\n' | cat | head -n 1 | wc -c");

    [SkippableFact] public Task FusedLane_Tac_Rev_Keep_TheBytes() =>
        EqualAsync("", @"printf 'x\351\ny\n' | tac | cat > out");

    [SkippableFact] public Task ProcessSubstitution_CarriesInvalidBytes() =>
        AssertOracle.EqualAsync(@"cat <(printf '\351') | wc -c");

    [SkippableFact] public Task Sort_OfInvalidBytes_KeepsEveryByte() =>
        EqualAsync("", @"printf 'b\351\na\377\n' | sort | wc -c > n");
}
