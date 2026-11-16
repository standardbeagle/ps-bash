using System.Text;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Deadline logic of <see cref="InteractiveShellHarness.ReadUntilAsync"/>, driven by a fake
/// reader so no shell is spawned. Regression: under full-suite load the prompt arrived inside
/// a starvation extension, and the per-char deadline check failed the wait on its first char
/// ("stdout received (1 chars) --- P").
/// </summary>
public class InteractiveShellHarnessWaitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    private static bool EndsWithPrompt(StringBuilder b) =>
        b.ToString().EndsWith(InteractiveShellHarness.PromptString, StringComparison.Ordinal);

    [Fact]
    public async Task PromptArrivingInsideStarvationExtension_IsReadToTheEnd()
    {
        var reader = new DelayedReader("out\nPSBASH>", TimeSpan.FromMilliseconds(700));

        var (outcome, buf) = await InteractiveShellHarness.ReadUntilAsync(
            reader, EndsWithPrompt, Timeout, isStarved: () => Task.FromResult(true));

        Assert.Equal(InteractiveShellHarness.WaitOutcome.Matched, outcome);
        Assert.Equal("out\nPSBASH>", buf.ToString());
    }

    [Fact]
    public async Task PromptStartingBeforeDeadline_FinishingAfter_IsReadWhenStarved()
    {
        // "P" lands before the deadline, the rest after it: the between-chars deadline
        // check must consult starvation too, not fail outright.
        var reader = new DelayedReader("P", TimeSpan.FromMilliseconds(50),
            then: "SBASH>", thenDelay: TimeSpan.FromMilliseconds(600));

        var (outcome, _) = await InteractiveShellHarness.ReadUntilAsync(
            reader, EndsWithPrompt, Timeout, isStarved: () => Task.FromResult(true));

        Assert.Equal(InteractiveShellHarness.WaitOutcome.Matched, outcome);
    }

    [Fact]
    public async Task QuietMachine_LatePrompt_TimesOut()
    {
        var reader = new DelayedReader("PSBASH>", TimeSpan.FromSeconds(5));

        var (outcome, buf) = await InteractiveShellHarness.ReadUntilAsync(
            reader, EndsWithPrompt, Timeout, isStarved: () => Task.FromResult(false));

        Assert.Equal(InteractiveShellHarness.WaitOutcome.TimedOut, outcome);
        Assert.Equal("", buf.ToString());
    }

    [Fact]
    public async Task StarvedForever_NoOutput_StopsAfterExtensionCap()
    {
        var reader = new DelayedReader("", TimeSpan.FromDays(1));
        var probes = 0;

        var (outcome, _) = await InteractiveShellHarness.ReadUntilAsync(
            reader, EndsWithPrompt, TimeSpan.FromMilliseconds(20),
            isStarved: () => { probes++; return Task.FromResult(true); });

        Assert.Equal(InteractiveShellHarness.WaitOutcome.TimedOut, outcome);
        Assert.Equal(PsBash.Testing.ContentionProbe.MaxExtensions, probes);
    }

    [Fact]
    public async Task Eof_IsReported()
    {
        var reader = new DelayedReader("partial", TimeSpan.Zero, eofAfter: true);

        var (outcome, buf) = await InteractiveShellHarness.ReadUntilAsync(
            reader, EndsWithPrompt, Timeout, isStarved: () => Task.FromResult(false));

        Assert.Equal(InteractiveShellHarness.WaitOutcome.Eof, outcome);
        Assert.Equal("partial", buf.ToString());
    }

    /// <summary>
    /// Yields <c>first</c> after <c>firstDelay</c>, then optionally <c>then</c> after
    /// <c>thenDelay</c>; afterwards blocks forever, or returns EOF when <c>eofAfter</c>.
    /// Delays are measured from construction, like a child process writing on its own clock.
    /// </summary>
    private sealed class DelayedReader : TextReader
    {
        private readonly Queue<(DateTime At, string Text)> _chunks = new();
        private readonly bool _eofAfter;
        private string _current = "";
        private int _pos;

        public DelayedReader(string first, TimeSpan firstDelay,
            string? then = null, TimeSpan? thenDelay = null, bool eofAfter = false)
        {
            var t0 = DateTime.UtcNow;
            _chunks.Enqueue((t0 + firstDelay, first));
            if (then is not null)
                _chunks.Enqueue((t0 + (thenDelay ?? TimeSpan.Zero), then));
            _eofAfter = eofAfter;
        }

        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            while (_pos >= _current.Length)
            {
                if (_chunks.Count == 0)
                {
                    if (_eofAfter) return 0;
                    await Task.Delay(System.Threading.Timeout.Infinite);
                }
                var (at, text) = _chunks.Dequeue();
                var wait = at - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                _current = text;
                _pos = 0;
            }
            buffer[index] = _current[_pos++];
            return 1;
        }
    }
}
