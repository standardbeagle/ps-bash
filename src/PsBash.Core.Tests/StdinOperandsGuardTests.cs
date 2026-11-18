using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Inside a compound command's stdin scope the emitter feeds a command the scope's stdin when it has no file operand
/// (StdinReaders). Whether a word is a flag or an operand is decided on its value AFTER quote removal, as bash sees
/// it, for every quoting of the same word. REGRESSION: only an unquoted literal was read, so <c>-d' '</c> /
/// <c>'-d:'</c> / <c>'cat'</c> counted as an unknown operand and <c>x | { cut -d' ' -f1; }</c> printed nothing
/// (end-to-end: LineAssemblyDifferentialTests). Oracle note (qa-rubric Directive 1): emitter-shape invariant —
/// every spelling must emit what the plain spelling emits.
/// </summary>
public class StdinOperandsGuardTests
{
    private const string Feed = PsBuild.StdinFeed;

    [Theory]
    [InlineData("cut -d: -f1", "cut -d':' -f1", true)]
    [InlineData("cut -d: -f1", "cut '-d:' -f1", true)]
    [InlineData("cut -d: -f1", "cut \"-d:\" -f1", true)]
    [InlineData("cut -d: -f1", "cut -d\\: -f1", true)]
    [InlineData("cut -d: -f1", "'cut' -d: -f1", true)]
    [InlineData("cut -d, -f1", "cut -d' ' -f1", true)]
    [InlineData("sort -t: -k2", "sort -t':' '-k2'", true)]
    [InlineData("cat -", "cat '-'", true)]
    [InlineData("head -n 2", "head '-n' \"2\"", true)]
    [InlineData("cut -f1 f", "cut -f1 'f'", false)]           // a quoted FILE operand is still an operand: not fed
    public void QuotedSpelling_FeedsExactlyLikeThePlainOne(string plain, string quoted, bool fed)
    {
        bool PlainFeeds(string body) => PsEmitter.Transpile("echo x | { true; " + body + "; }")!.Contains(Feed, StringComparison.Ordinal);
        Assert.Equal(fed, PlainFeeds(plain));
        Assert.Equal(fed, PlainFeeds(quoted));
    }

    [Fact]
    public void StaticEval_OfAQuotedFlag_IsFed()
        => Assert.Contains(Feed, PsEmitter.Transpile("echo a:b | eval \"cut '-d:' -f1\"")!, StringComparison.Ordinal);
}
