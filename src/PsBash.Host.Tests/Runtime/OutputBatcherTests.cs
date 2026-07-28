using System.Text;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Unit tests for the S1 stdout batcher (fan-out epic 01KYKGFBY4WCHRNZ10R650H57B).
///
/// <para><b>No sleeping anywhere in this file</b> — the QA rubric (Directive 6) rejects
/// <c>Thread.Sleep</c>/<c>Task.Delay</c> in tests, and a batcher with a time-based flush is
/// exactly the component that tempts you into one. The deadline path is therefore driven
/// directly through <see cref="OutputBatcher.FlushDueToDeadline"/> — the same entry point
/// the production timer calls — with the timer itself disabled via
/// <see cref="Timeout.InfiniteTimeSpan"/>. That tests the mechanism deterministically;
/// only the one-line timer wiring is left uncovered.</para>
///
/// <para>The dominant risk this class carries is silent CORRUPTION rather than failure:
/// a batcher that drops, duplicates, reorders, or rewrites a byte would still "work" and
/// would poison every command's output. Most cases below are byte-identity assertions.</para>
/// </summary>
public class OutputBatcherTests
{
    /// <summary>Collects batches and lets a test assert on both the concatenation
    /// (byte identity) and the batch COUNT (that coalescing actually happened).</summary>
    private sealed class Sink
    {
        public List<string> Batches { get; } = new();
        public string All => string.Concat(Batches);
        public Action<string> Write => s => Batches.Add(s);
    }

    private static OutputBatcher NewBatcher(Sink sink, int threshold = 1024) =>
        new(sink.Write, threshold, Timeout.InfiniteTimeSpan);

    // ---------------------------------------------------------------- buffering

    [Fact]
    public void Append_BelowThreshold_EmitsNothingYet()
    {
        var sink = new Sink();
        using var b = NewBatcher(sink);

        b.Append("one\n");
        b.Append("two\n");

        Assert.Empty(sink.Batches);
        Assert.True(b.HasBuffered);
    }

    [Fact]
    public void Append_CrossingThreshold_FlushesOnceWithEverythingBuffered()
    {
        var sink = new Sink();
        using var b = NewBatcher(sink, threshold: 16);

        b.Append("12345678\n");   // 9
        Assert.Empty(sink.Batches);
        b.Append("abcdefgh\n");   // 18 total -> crosses

        Assert.Single(sink.Batches);
        Assert.Equal("12345678\nabcdefgh\n", sink.Batches[0]);
        Assert.False(b.HasBuffered);
    }

    [Fact]
    public void FlushDueToDeadline_EmitsBufferedOutput()
    {
        var sink = new Sink();
        using var b = NewBatcher(sink);

        b.Append("trickle\n");
        Assert.Empty(sink.Batches);

        b.FlushDueToDeadline();

        Assert.Single(sink.Batches);
        Assert.Equal("trickle\n", sink.Batches[0]);
    }

    [Fact]
    public void Flush_WhenEmpty_DoesNotEmitAnEmptyBatch()
    {
        // An empty frame is not harmless: it costs an IPC round trip and, on the
        // interactive path, can render as a stray blank write.
        var sink = new Sink();
        using var b = NewBatcher(sink);

        b.Flush();
        b.FlushDueToDeadline();
        b.Flush();

        Assert.Empty(sink.Batches);
    }

    [Fact]
    public void Append_EmptyString_IsIgnored()
    {
        var sink = new Sink();
        using var b = NewBatcher(sink);

        b.Append("");
        b.Flush();

        Assert.Empty(sink.Batches);
    }

    // ------------------------------------------------------------ byte identity

    [Fact]
    public void Concatenation_IsByteIdenticalToTheUnbatchedStream()
    {
        // The core contract: batching changes FRAMING only. Whatever N separate
        // writes would have produced, the concatenated batches must equal exactly.
        var lines = Enumerable.Range(1, 5000).Select(i => $"line {i}\n").ToArray();
        var expected = string.Concat(lines);

        var sink = new Sink();
        using (var b = NewBatcher(sink))
        {
            foreach (var l in lines) b.Append(l);
        }

        Assert.Equal(expected, sink.All);
        Assert.True(sink.Batches.Count > 1, "expected coalescing into multiple batches, not one per line");
        Assert.True(sink.Batches.Count < lines.Length / 10,
            $"expected far fewer batches than lines; got {sink.Batches.Count} for {lines.Length} lines");
    }

    [Theory]
    // Directive 3 failure axes: unicode (incl. BOM, emoji, combining marks) and CRLF.
    [InlineData("plain ascii\n")]
    [InlineData("crlf line\r\n")]
    [InlineData("no trailing newline")]
    [InlineData("﻿bom-prefixed\n")]
    [InlineData("emoji \U0001F600\U0001F469‍\U0001F4BB\n")]
    [InlineData("combining é́ mark\n")]
    [InlineData("cjk 中文テスト\n")]
    [InlineData("nul\0embedded\n")]
    public void Append_PreservesExactBytes(string text)
    {
        var sink = new Sink();
        using (var b = NewBatcher(sink))
        {
            b.Append(text);
        }

        Assert.Equal(text, sink.All);
    }

    [Fact]
    public void Append_DoesNotSplitOrRewriteAcrossABatchBoundary()
    {
        // A multi-byte sequence spanning the threshold must survive: the batcher
        // works in chars and must never slice a string it was handed.
        var sink = new Sink();
        using var b = NewBatcher(sink, threshold: 8);

        b.Append("1234567");                  // 7, under
        b.Append("\U0001F600 tail\n");        // crosses mid-append

        Assert.Equal("1234567\U0001F600 tail\n", sink.All);
        // The emoji must be intact inside whichever single batch carries it.
        Assert.Contains(sink.Batches, s => s.Contains("\U0001F600"));
    }

    // ----------------------------------------------------------------- lifecycle

    [Fact]
    public void Dispose_FlushesRemainingOutput()
    {
        // RunCommand has many return paths; the finally-block Dispose is what
        // guarantees buffered bytes precede the EXIT sentinel on all of them.
        var sink = new Sink();
        var b = NewBatcher(sink);

        b.Append("pending\n");
        Assert.Empty(sink.Batches);

        b.Dispose();

        Assert.Single(sink.Batches);
        Assert.Equal("pending\n", sink.Batches[0]);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sink = new Sink();
        var b = NewBatcher(sink);
        b.Append("once\n");

        b.Dispose();
        b.Dispose();

        Assert.Single(sink.Batches);
    }

    [Fact]
    public void Append_AfterDispose_WritesThroughRatherThanLosingOutput()
    {
        // A late Out-Default forwarder call shouldn't happen, but dropping output
        // is worse than an unbatched write, so the fallback is pass-through.
        var sink = new Sink();
        var b = NewBatcher(sink);
        b.Dispose();

        b.Append("late\n");

        Assert.Single(sink.Batches);
        Assert.Equal("late\n", sink.Batches[0]);
    }

    // ------------------------------------------------------------------ failures

    [Fact]
    public void SinkThrowing_DoesNotReEmitTheSameBytesLater()
    {
        // Directive 3 axis 5 (broken pipe): the IPC stream closes mid-command.
        // The buffer must already be cleared, or a later flush would duplicate
        // output onto a half-written stream.
        var calls = new List<string>();
        var b = new OutputBatcher(
            s => { calls.Add(s); throw new IOException("pipe closed"); },
            thresholdChars: 8,
            deadline: Timeout.InfiniteTimeSpan);

        Assert.Throws<IOException>(() => b.Append("12345678\n"));

        // Second flush must not re-send the first batch.
        Assert.Throws<IOException>(() => { b.Append("second\n"); b.Flush(); });

        Assert.Equal(2, calls.Count);
        Assert.Equal("12345678\n", calls[0]);
        Assert.Equal("second\n", calls[1]);
    }

    [Fact]
    public void ConcurrentAppends_LoseNoBytesAndNeverInterleaveABatch()
    {
        // Directive 3 axis 6 (slow reader) + the ordering contract: DataAdded fires
        // on pipeline threads while the deadline timer fires on a threadpool thread.
        // Total bytes must be conserved and no batch may contain a torn line.
        const int threads = 8, perThread = 500;
        var sink = new Sink();
        var gate = new object();
        var safeSink = new Action<string>(s => { lock (gate) sink.Batches.Add(s); });
        using var b = new OutputBatcher(safeSink, 256, Timeout.InfiniteTimeSpan);

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++) b.Append($"t{t}-{i}\n");
        });
        b.Flush();

        var all = string.Concat(sink.Batches);
        var lines = all.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(threads * perThread, lines.Length);
        // Every line must be whole — a torn write would produce a fragment.
        Assert.All(lines, l => Assert.Matches(@"^t\d+-\d+$", l));
    }

    [Fact]
    public void LargeInput_IsCoalescedIntoBoundedBatches()
    {
        // Directive 3 axis 2 (large input). Batch count should track total SIZE,
        // not line count — that is the property that removes the per-line cost.
        const int lineCount = 200_000;
        var sink = new Sink();
        using (var b = new OutputBatcher(sink.Write, OutputBatcher.DefaultThresholdChars, Timeout.InfiniteTimeSpan))
        {
            for (int i = 0; i < lineCount; i++) b.Append("0123456789\n");
        }

        var totalChars = lineCount * 11;
        var expectedBatches = totalChars / OutputBatcher.DefaultThresholdChars;
        Assert.Equal(totalChars, sink.All.Length);
        // Allow one extra for the trailing partial batch.
        Assert.InRange(sink.Batches.Count, expectedBatches, expectedBatches + 1);
    }
}
