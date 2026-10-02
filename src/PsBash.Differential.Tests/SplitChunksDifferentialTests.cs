using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>split -n CHUNKS</c> against GNU coreutils 9.4: N / K/N (bytes), l/N, l/K/N, r/N, r/K/N.
/// Compares stdout (the K/N forms), exit status and the resulting files byte for byte
/// (<see cref="PsBash.Differential.Tests.Oracle.FsStateOracle"/>). Record with PSBASH_ORACLE_RECORD=1.
/// </summary>
public class SplitChunksDifferentialTests
{
    // 24 bytes: aa\n bbbb\n c\n dd\n eeeeeeee\n f\n
    private static readonly string Lines = "aa\nbbbb\nc\ndd\neeeeeeee\nf";   // Tree adds the final \n
    private const string Digits = "012345678";                            // + \n = 10 bytes

    [SkippableFact] public Task Bytes_RemainderGoesToTheFirstChunks() =>
        EqualAsync(Tree(("f", Digits)), "split -n 3 f");

    [SkippableFact] public Task Bytes_MoreChunksThanBytesPerChunk() =>
        EqualAsync(Tree(("f", Digits)), "split -n 4 f");

    [SkippableFact] public Task Bytes_EveryByteItsOwnChunk_AndAnEmptyExtra() =>
        EqualAsync(Tree(("f", Digits)), "split -n 11 f");

    [SkippableFact] public Task Bytes_KthChunkToStdout_NoFiles() =>
        EqualAsync(Tree(("f", Digits)), "split -n 2/3 f");

    [SkippableFact] public Task Bytes_FromStdin() =>
        EqualAsync(Tree(("f", Digits)), "cat f | split -n 3");

    [SkippableFact] public Task Bytes_EmptyInput_MakesEmptyChunkFiles() =>
        EqualAsync("printf '' > e", "split -n 3 e");

    [SkippableFact] public Task Lines_TwoWay() =>
        EqualAsync(Tree(("f", Lines)), "split -n l/2 f");

    [SkippableFact] public Task Lines_ThreeWay() =>
        EqualAsync(Tree(("f", Lines)), "split -n l/3 f");

    [SkippableFact] public Task Lines_FiveWay_LeavesAnEmptyChunk() =>
        EqualAsync(Tree(("f", Lines)), "split -n l/5 f");

    [SkippableFact] public Task Lines_TenWay_LineBelongsToThePartitionHoldingItsFirstByte() =>
        EqualAsync(Tree(("f", Lines)), "split -n l/10 f");

    [SkippableFact] public Task Lines_KthChunkToStdout() =>
        EqualAsync(Tree(("f", Lines)), "split -n l/2/4 f");

    [SkippableFact] public Task Lines_NoTrailingNewline() =>
        EqualAsync("printf 'a\\nb\\ncc' > f", "split -n l/2 f");

    [SkippableFact] public Task Lines_FromStdin() =>
        EqualAsync(Tree(("f", Lines)), "cat f | split -n l/3");

    [SkippableFact] public Task RoundRobin_TwoWay() =>
        EqualAsync(Tree(("f", Lines)), "split -n r/2 f");

    [SkippableFact] public Task RoundRobin_MoreChunksThanLines() =>
        EqualAsync(Tree(("f", Lines)), "split -n r/7 f");

    [SkippableFact] public Task RoundRobin_KthChunkToStdout() =>
        EqualAsync(Tree(("f", Lines)), "split -n r/2/3 f");

    [SkippableFact] public Task RoundRobin_FromStdin() =>
        EqualAsync(Tree(("f", Lines)), "cat f | split -n r/3");

    [SkippableFact] public Task Suffix_NumericAndPrefixAndAdditional() =>
        EqualAsync(Tree(("f", Digits)), "split -d -n 3 --additional-suffix=.p f pre_");

    [SkippableFact] public Task Suffix_GrowsToThreeLettersFor700Chunks() =>
        EqualAsync(Tree(("f", Digits)), "split -n 700 f");

    [SkippableFact] public Task Suffix_TooShortForTheChunkCount_Fails() =>
        EqualAsync(Tree(("f", Digits)), "split -n 700 -a 2 f", compareStderr: false);

    [SkippableFact] public Task Error_ZeroChunks() => EqualAsync(Tree(("f", Digits)), "split -n 0 f");
    [SkippableFact] public Task Error_ChunkNumberTooLarge() => EqualAsync(Tree(("f", Digits)), "split -n 5/3 f");
    [SkippableFact] public Task Error_ChunkNumberZero() => EqualAsync(Tree(("f", Digits)), "split -n 0/3 f");
    [SkippableFact] public Task Error_NotANumber() => EqualAsync(Tree(("f", Digits)), "split -n x f");
    [SkippableFact] public Task Error_MoreThanOneWay() => EqualAsync(Tree(("f", Digits)), "split -n 2 -l 1 f");
    [SkippableFact] public Task Error_TwoNs() => EqualAsync(Tree(("f", Digits)), "split -n 2 -n 3 f");
}
