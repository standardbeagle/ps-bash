using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Byte-parity tests for the S3 <c>tail</c> streaming core (<c>TailStage</c>) against the
/// real <c>Invoke-BashTail</c> (the parity oracle).
///
/// <para><b>The follow guard is the safety-critical case.</b> The fused executor runs the
/// chain to COMPLETION before it returns, so a followed stage would buffer forever — a
/// silent hang, with no error message, where the unfused lane streams live. The emitter's
/// <c>PsEmitter.StageIsUnbounded</c> name check is one barrier; these tests pin the
/// SECOND, independent one in the core itself, so the guarantee does not rest on a single
/// string comparison in another project.</para>
/// </summary>
public class LineStreamTailParityTests : LineStreamParityHarness
{
    public LineStreamTailParityTests(SharedPwshFixture fixture) : base(fixture) { }

    private void AssertTail(string[] argv, IEnumerable<string> lines)
        => AssertCoreMatchesCmdlet("tail", "Invoke-BashTail", argv, lines);

    private static string[] Corpus(int n)
        => Enumerable.Range(1, n).Select(i => "line" + i).ToArray();

    // ── certified argv × content ─────────────────────────────────────────────

    [Theory]
    [InlineData("")]             // default: last 10
    [InlineData("-n 5")]
    [InlineData("-n5")]
    [InlineData("-5")]           // legacy shorthand
    [InlineData("5")]            // bare positional number
    [InlineData("-n 0")]         // oracle keeps ONE line here (cap = max(count,1))
    [InlineData("-n 100")]       // more than the input
    [InlineData("--lines=3")]
    [InlineData("--lines 3")]
    [InlineData("-q -n 3")]      // -q is an accepted no-op
    public void TailCore_FlagForms_MatchCmdlet(string flags)
        => AssertTail(Split(flags), Corpus(20));

    [Theory]
    [InlineData("-n +5")]
    [InlineData("-n+5")]
    [InlineData("--lines=+5")]
    [InlineData("-n +1")]
    [InlineData("-n +100")]      // past the end → nothing
    public void TailCore_FromLineForms_MatchCmdlet(string flags)
        => AssertTail(Split(flags), Corpus(20));

    [Fact]
    public void TailCore_EmptyInput_MatchesCmdlet()
        => AssertTail(new[] { "-n", "3" }, Array.Empty<string>());

    [Fact]
    public void TailCore_FewerLinesThanCount_MatchesCmdlet()
        => AssertTail(new[] { "-n", "10" }, Corpus(3));

    [Fact]
    public void TailCore_Unicode_MatchesCmdlet()
        => AssertTail(new[] { "-n", "2" }, new[] { "café", "naïve", "🚀 rocket" });

    [Fact]
    public void TailCore_BlankLines_MatchCmdlet()
        => AssertTail(new[] { "-n", "3" }, new[] { "a", "", "", "b", "" });

    [Fact]
    public void TailCore_FromLineIsLazy_DoesNotDrainInfiniteProducer()
    {
        // `-n +N` is a skip-then-stream form: it must NOT buffer.
        Assert.True(LineStreamRegistry.TryCreate("tail", new[] { "-n", "+2" }, out var stage));
        Assert.Equal(3, stage.Run(Endless()).Take(3).Count());

        static IEnumerable<string> Endless()
        {
            long i = 0;
            while (true) yield return "line" + (i++);
        }
    }

    // ── the follow guard: decline, never hang ────────────────────────────────

    [Theory]
    [InlineData("-f")]
    [InlineData("-F")]
    [InlineData("--follow")]
    [InlineData("--follow=name")]
    [InlineData("-f -n 5")]
    [InlineData("-n 5 -f")]
    [InlineData("-qf")]                   // follow hidden in a short-flag bundle
    [InlineData("-fn")]
    [InlineData("--retry")]
    public void TailCore_FollowForms_Decline(string flags)
        => Assert.False(LineStreamRegistry.TryCreate("tail", Split(flags), out _),
            $"tail core must DECLINE follow form '{flags}' — a fused follow hangs silently");

    // ── decline matrix ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("-c 20")]                 // byte mode: file-only in the cmdlet
    [InlineData("-c20")]
    [InlineData("-s 2")]                  // sleep interval only means anything with -f
    [InlineData("--sleep-interval 2")]
    [InlineData("-v")]
    [InlineData("--")]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("-z")]
    [InlineData("file.txt")]              // file operand → file mode (typed CatLine objects)
    [InlineData("-n x")]                  // non-numeric value
    [InlineData("-n -3")]                 // negative count
    [InlineData("-n")]                    // dangling value flag
    public void TailCore_UncertifiedArgv_Declines(string flags)
        => Assert.False(LineStreamRegistry.TryCreate("tail", Split(flags), out _),
            $"tail core must DECLINE '{flags}' rather than guess");
}
