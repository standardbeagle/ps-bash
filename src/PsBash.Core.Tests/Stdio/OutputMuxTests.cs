using PsBash.Core.Stdio;
using Xunit;

namespace PsBash.Core.Tests.Stdio;

/// <summary>
/// The session output mux that carries background-job output (docs/specs/stdio-messaging.md). Hand-written: the mux
/// is ps-bash plumbing with no bash equivalent; the bash-visible behaviour is covered by the Differential tests.
/// Waits are bounded wait-handle / task waits, never sleeps: a "still blocked" check waits a short bounded time for
/// something that must NOT happen.
/// </summary>
public class OutputMuxTests
{
    private const int MustNotHappenMs = 200;
    private const int MustHappenMs = 10_000;

    [Fact]
    public void Attached_WritesReachTheSinkInArrivalOrderAcrossChannels()
    {
        var mux = new OutputMux();
        var seen = new List<(int, string)>();
        mux.Attach((fd, t) => seen.Add((fd, t)));
        var a = mux.Open("a");
        var b = mux.Open("b");

        a.Write(1, "a1\n");
        b.Write(2, "b1\n");
        a.Write(1, "a2\n");

        Assert.Equal(new[] { (1, "a1\n"), (2, "b1\n"), (1, "a2\n") }, seen);
        Assert.Equal(0, mux.PendingChars);
    }

    [Fact]
    public void Detached_WritesQueue_AndAttachDeliversThemFirstInOrder()
    {
        var mux = new OutputMux();
        var a = mux.Open("a");
        var b = mux.Open("b");
        a.Write(1, "a1\n");
        b.Write(2, "b1\n");
        Assert.Equal(6, mux.PendingChars);

        var seen = new List<(int, string)>();
        mux.Attach((fd, t) => seen.Add((fd, t)));
        a.Write(1, "a2\n");

        Assert.Equal(new[] { (1, "a1\n"), (2, "b1\n"), (1, "a2\n") }, seen);
        Assert.Equal(0, mux.PendingChars);
    }

    [Fact]
    public void Detach_StopsDelivery_NothingIsDeliveredTwice()
    {
        var mux = new OutputMux();
        var seen = new List<string>();
        var ch = mux.Open("a");
        mux.Attach((_, t) => seen.Add(t));
        ch.Write(1, "x\n");
        mux.Detach();
        ch.Write(1, "y\n");
        Assert.Equal(new[] { "x\n" }, seen);

        mux.Attach((_, t) => seen.Add(t));
        Assert.Equal(new[] { "x\n", "y\n" }, seen);
    }

    [Fact]
    public async Task ChannelOutOfCredit_BlocksUntilTheQueueIsDrained()
    {
        var mux = new OutputMux(windowChars: 10);
        var ch = mux.Open("a");
        Assert.True(ch.Write(1, "12345678"));

        var blocked = Task.Run(() => ch.Write(1, "abcdefgh"));
        Assert.False(CompletesSoon(blocked), "a write past the window must block, as a full pipe blocks");
        Assert.Equal(8, mux.PendingChars);

        var drained = mux.TakePending();
        Assert.Equal("12345678", Assert.Single(drained).Value);
        Assert.True(await Within(blocked));
        Assert.Equal("abcdefgh", Assert.Single(mux.TakePending()).Value);
    }

    [Fact]
    public async Task ChannelOutOfCredit_AttachUnblocksIt_AndOrderIsKept()
    {
        var mux = new OutputMux(windowChars: 4);
        var ch = mux.Open("a");
        ch.Write(1, "1234");
        var blocked = Task.Run(() => ch.Write(1, "5678"));
        Assert.False(CompletesSoon(blocked));

        var seen = new List<string>();
        var sinkGate = new object();
        mux.Attach((_, t) => { lock (sinkGate) seen.Add(t); });
        Assert.True(await Within(blocked));
        lock (sinkGate) Assert.Equal(new[] { "1234", "5678" }, seen);
    }

    [Fact]
    public async Task BlockedProducer_ThatIsAbandoned_ReturnsFalseAndDropsOnlyThatChunk()
    {
        var mux = new OutputMux(windowChars: 4);
        var ch = mux.Open("a");
        ch.Write(1, "1234");
        var stop = new ManualResetEventSlim();
        var blocked = Task.Run(() => ch.Write(1, "5678", () => stop.IsSet));
        Assert.False(CompletesSoon(blocked));

        stop.Set();
        // An abandoned producer must not stay blocked (Stop() would deadlock against it).
        Assert.False(await Within(blocked));
        Assert.Equal("1234", Assert.Single(mux.TakePending()).Value);
    }

    [Fact]
    public void OneChunkLargerThanTheWindow_IsAcceptedByAnEmptyChannel()
    {
        var mux = new OutputMux(windowChars: 4);
        var ch = mux.Open("a");
        Assert.True(ch.Write(1, "a much longer chunk than four"));
        Assert.Equal(29, mux.PendingChars);
    }

    [Fact]
    public async Task CreditIsPerChannel_OneFullChannelDoesNotBlockAnother()
    {
        var mux = new OutputMux(windowChars: 4);
        var a = mux.Open("a");
        var b = mux.Open("b");
        a.Write(1, "1234");
        var other = Task.Run(() => b.Write(1, "wxyz"));
        Assert.True(await Within(other));
        Assert.Equal(8, mux.PendingChars);
    }

    [Fact]
    public async Task EndlessDetachedProducer_StaysWithinTheWindow()
    {
        const int window = 64 * 1024;
        var mux = new OutputMux(window);
        var ch = mux.Open("flood");
        var stop = new ManualResetEventSlim();
        var line = new string('x', 99) + "\n";
        long written = 0;
        var producer = Task.Run(() =>
        {
            while (ch.Write(1, line, () => stop.IsSet)) Interlocked.Add(ref written, line.Length);
        });

        // The producer fills its window and then blocks; nothing past the window is ever held.
        Assert.False(CompletesSoon(producer));
        Assert.True(mux.PendingChars <= window, $"pending {mux.PendingChars} > window {window}");
        Assert.True(Interlocked.Read(ref written) <= window);

        stop.Set();
        await producer.WaitAsync(TimeSpan.FromMilliseconds(MustHappenMs));
    }

    /// <summary>True when <paramref name="task"/> completes within <see cref="MustNotHappenMs"/>; false while it stays blocked.</summary>
    private static bool CompletesSoon(Task task) => SpinWait.SpinUntil(() => task.IsCompleted, MustNotHappenMs);

    /// <summary>The task's result, failing the test (TimeoutException) when it does not complete in time.</summary>
    private static Task<T> Within<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromMilliseconds(MustHappenMs));

    [Fact]
    public void Close_IsCountedOnce_AndQueuedTextSurvivesIt()
    {
        var mux = new OutputMux();
        var ch = mux.Open("a");
        Assert.Equal(1, mux.OpenChannels);
        ch.Write(1, "tail\n");
        ch.Close();
        ch.Dispose();
        Assert.Equal(0, mux.OpenChannels);
        Assert.True(ch.IsClosed);
        Assert.Equal("tail\n", Assert.Single(mux.TakePending()).Value);
    }

    [Fact]
    public void ThrowingSink_DoesNotKillTheProducer()
    {
        var mux = new OutputMux();
        mux.Attach((_, _) => throw new IOException("launcher went away"));
        var ch = mux.Open("a");
        Assert.True(ch.Write(1, "x\n"));
    }

    [Fact]
    public async Task WaitForPending_WakesOnAWrite()
    {
        var mux = new OutputMux();
        var ch = mux.Open("a");
        Assert.False(mux.WaitForPending(0));
        var waiter = Task.Run(() => mux.WaitForPending(MustHappenMs));
        ch.Write(2, "err\n");
        Assert.True(await Within(waiter));
        Assert.Equal(2, Assert.Single(mux.TakePending()).Key);
    }

    [Fact]
    public void EmptyText_IsANoOp()
    {
        var mux = new OutputMux();
        var ch = mux.Open("a");
        Assert.True(ch.Write(1, ""));
        Assert.Equal(0, mux.PendingChars);
        Assert.False(mux.WaitForPending(0));
    }
}
