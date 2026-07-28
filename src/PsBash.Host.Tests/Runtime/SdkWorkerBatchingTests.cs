using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// End-to-end tests for the S1 stdout batching wired into <see cref="SdkWorker"/>
/// (fan-out epic 01KYKGFBY4WCHRNZ10R650H57B).
///
/// <para><see cref="OutputBatcherTests"/> covers the batcher in isolation. These probe the
/// two integration properties that unit tests structurally cannot: that stdout and stderr
/// keep their relative ORDER once only one of them is buffered, and that output survives
/// every <c>RunCommand</c> return path (normal, error, command-not-found).</para>
///
/// <para>Oracle note (qa-rubric Directive 1): SdkWorker is ps-bash-specific with no
/// in-process bash oracle, so hand-written asserts are justified per the exception list.
/// The byte-identity property IS oracle-checked, against the unbatched lane itself:
/// every test asserts batched output equals what <c>PSBASH_NO_BATCH=1</c> produces.</para>
/// </summary>
[Collection("SdkHost")]
public class SdkWorkerBatchingTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private sealed record Streams(string Stdout, string Stderr, List<string> Interleaved, int ExitCode);

    /// <summary>Run a command capturing stdout and stderr with their arrival order, with
    /// batching either on or off.</summary>
    private async Task<Streams> RunAsync(string command, bool noBatch)
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_NO_BATCH");
        Environment.SetEnvironmentVariable("PSBASH_NO_BATCH", noBatch ? "1" : null);
        try
        {
            var worker = _fixture.CreateWorker();
            var outSb = new System.Text.StringBuilder();
            var errSb = new System.Text.StringBuilder();
            var order = new List<string>();
            var gate = new object();

            var exit = await worker.ExecuteWithOutputAsync(
                command,
                s => { lock (gate) { outSb.Append(s); order.Add("O:" + s.TrimEnd('\r', '\n')); } },
                s => { lock (gate) { errSb.Append(s); order.Add("E:" + s.TrimEnd('\r', '\n')); } });

            return new Streams(outSb.ToString(), errSb.ToString(), order, exit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_NO_BATCH", prior);
        }
    }

    // ------------------------------------------------------------ byte identity

    [Theory]
    [InlineData("Invoke-BashEcho 'hello'")]
    [InlineData("1..500 | ForEach-Object { Invoke-BashEcho \"line $_\" }")]
    [InlineData("Invoke-BashEcho ''")]
    [InlineData("Invoke-BashEcho 'ünïcødé \U0001F600'")]
    public async Task BatchedStdout_IsByteIdenticalToUnbatched(string command)
    {
        var batched = await RunAsync(command, noBatch: false);
        var unbatched = await RunAsync(command, noBatch: true);

        Assert.Equal(unbatched.Stdout, batched.Stdout);
        Assert.Equal(unbatched.ExitCode, batched.ExitCode);
    }

    [Fact]
    public async Task BatchedStdout_CoalescesManyLinesIntoFewerCallbackInvocations()
    {
        // The point of the slice: callback invocations (= IPC frames) must drop far
        // below the line count. Without this assertion the whole change is a no-op
        // that still passes every correctness test.
        var cmd = "1..2000 | ForEach-Object { Invoke-BashEcho \"line $_\" }";

        var batched = await RunAsync(cmd, noBatch: false);
        var unbatched = await RunAsync(cmd, noBatch: true);

        var batchedWrites = batched.Interleaved.Count;
        var unbatchedWrites = unbatched.Interleaved.Count;

        Assert.True(unbatchedWrites >= 2000,
            $"expected ~one write per line unbatched; got {unbatchedWrites}");
        Assert.True(batchedWrites < unbatchedWrites / 10,
            $"expected >10x fewer writes when batched; got {batchedWrites} vs {unbatchedWrites}");
        Assert.Equal(unbatched.Stdout, batched.Stdout);
    }

    // --------------------------------------------------------- stream interleave

    [Fact]
    public async Task StderrWrite_FlushesPendingStdoutFirst_PreservingRelativeOrder()
    {
        // Directive 3 axis 9 (stderr interleave). This is the sharpest regression risk
        // in the slice: stdout is buffered and stderr is not, so without an explicit
        // flush on the stderr path, error text would overtake earlier stdout.
        var cmd = "Invoke-BashEcho 'before'; $Host.UI.WriteErrorLine('boom'); Invoke-BashEcho 'after'";

        var batched = await RunAsync(cmd, noBatch: false);
        var unbatched = await RunAsync(cmd, noBatch: true);

        // Same content on both streams...
        Assert.Equal(unbatched.Stdout, batched.Stdout);
        Assert.Equal(unbatched.Stderr, batched.Stderr);

        // ...and crucially the same ORDER: 'before' must precede the error, which
        // must precede 'after'.
        var idxBefore = batched.Interleaved.FindIndex(s => s.Contains("before"));
        var idxErr = batched.Interleaved.FindIndex(s => s.StartsWith("E:"));
        var idxAfter = batched.Interleaved.FindIndex(s => s.Contains("after"));

        Assert.True(idxBefore >= 0 && idxErr >= 0 && idxAfter >= 0,
            $"missing an expected record in: {string.Join(" | ", batched.Interleaved)}");
        Assert.True(idxBefore < idxErr, "buffered stdout overtook stderr — the flush-before-stderr guard is broken");
        Assert.True(idxErr < idxAfter, "stderr appeared after later stdout");
    }

    // ------------------------------------------------------------- return paths

    [Fact]
    public async Task OutputBeforeAFailingCommand_IsNotLost()
    {
        // RunCommand returns through several paths (127, runtime error, $LASTEXITCODE).
        // Buffered bytes must be flushed in the finally on every one of them, or output
        // silently disappears whenever a command happens to fail.
        var cmd = "Invoke-BashEcho 'kept'; this_command_does_not_exist_psbash";

        var batched = await RunAsync(cmd, noBatch: false);
        var unbatched = await RunAsync(cmd, noBatch: true);

        Assert.Contains("kept", batched.Stdout);
        Assert.Equal(unbatched.Stdout, batched.Stdout);
        Assert.Equal(unbatched.ExitCode, batched.ExitCode);
    }

    [Fact]
    public async Task SingleLineCommand_StillDelivered()
    {
        // The degenerate case the deadline exists for: one short line, well under the
        // size threshold, must still arrive (here via the end-of-command flush).
        var batched = await RunAsync("Invoke-BashEcho 'solo'", noBatch: false);

        Assert.Contains("solo", batched.Stdout);
    }
}
