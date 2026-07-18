using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

[Collection("SdkHost")]
public sealed class ConnectionWorkerFailureTests
{
    [Fact]
    public async Task AcquireFailure_ReturnsStderrAndNonzeroExit()
    {
        await using var pool = new WorkerPool<SdkWorker>(
            warmTarget: 0,
            max: 1,
            () => throw new FileLoadException("test assembly load failed"));

        var response = await RunConnectionAsync(pool);

        Assert.Equal(1, response.ExitCode);
        var error = Assert.Single(response.Frames);
        Assert.Equal(StreamTag.Stderr, error.Tag);
        Assert.Contains("worker failure", error.Line);
        Assert.Contains("test assembly load failed", error.Line);
    }

    [Fact]
    public async Task ExecuteFailure_ReturnsStderrAndNonzeroExit()
    {
        var disposedWorker = SdkWorker.Create();
        await disposedWorker.DisposeAsync();
        var factoryCalls = 0;
        await using var pool = new WorkerPool<SdkWorker>(
            warmTarget: 0,
            max: 1,
            () => Interlocked.Increment(ref factoryCalls) == 1
                ? disposedWorker
                : SdkWorker.Create());

        var response = await RunConnectionAsync(pool);

        Assert.Equal(1, response.ExitCode);
        var error = Assert.Single(response.Frames);
        Assert.Equal(StreamTag.Stderr, error.Tag);
        Assert.Contains("worker failure", error.Line);

        // A faulted lease must still be released/discarded. With max:1 this
        // second acquisition would hang if Connection failed to release its
        // slot; it must receive a fresh worker and execute normally.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var healthy = await RunConnectionAsync(pool, cts.Token);
        Assert.Equal(0, healthy.ExitCode);
        Assert.Contains(healthy.Frames, f =>
            f.Tag == StreamTag.Stdout && f.Line.Contains("unused"));
        Assert.Equal(2, Volatile.Read(ref factoryCalls));
    }

    private static async Task<(int ExitCode, List<(string Line, StreamTag Tag)> Frames)>
        RunConnectionAsync(WorkerPool<SdkWorker> pool, CancellationToken ct = default)
    {
        await using var stream = new MemoryStream();
        await HostProtocol.WriteRequestAsync(stream, new Mode.Command("Invoke-BashEcho 'unused'"), ct);
        stream.Position = 0;

        var connection = new Connection(stream, pool);
        await connection.HandleAsync(ct);

        stream.Position = 0;
        _ = await HostProtocol.ReadRequestAsync(stream, ct);
        var frames = new List<(string Line, StreamTag Tag)>();
        var exitCode = await HostProtocol.ReadResponseAsync(
            stream,
            (line, tag) => frames.Add((line, tag)), ct);
        return (exitCode, frames);
    }
}
