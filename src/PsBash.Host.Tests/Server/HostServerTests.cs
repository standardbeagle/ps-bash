using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using Xunit;

namespace PsBash.Host.Tests.Server;

/// <summary>
/// Integration tests for HostServer accept loop and Connection dispatcher.
/// The server is backed by a <see cref="WorkerPool{SdkWorker}"/>, so each
/// connection checks out its own isolated runspace — sequential connections do
/// NOT share runspace state (verified below).
/// </summary>
[Collection("SdkHost")]
public sealed class HostServerTests : IAsyncLifetime
{
    private WorkerPool<SdkWorker> _pool = null!;
    private NamedPipeTransport _serverTransport = null!;
    private HostServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private Task _serverTask = null!;
    private string _pipeName = null!;

    public Task InitializeAsync()
    {
        // warmTarget 0: create a runspace on demand (per command) to keep the
        // test's runspace count minimal, matching the old single-worker cost.
        _pool = new WorkerPool<SdkWorker>(warmTarget: 0, max: 2, SdkWorker.Create);
        _pipeName = $"psbash-test-{Guid.NewGuid():N}";
        _serverTransport = new NamedPipeTransport(_pipeName);
        _server = new HostServer(_serverTransport, _pool);
        _cts = new CancellationTokenSource();
        _serverTask = _server.RunAsync(_cts.Token);
        return _server.WhenListening;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        await _server.DisposeAsync();
        await _pool.DisposeAsync();
        _cts.Dispose();
    }

    private async Task<(string output, int exitCode)> SendCommandAsync(string command, CancellationToken ct = default)
    {
        var clientTransport = new NamedPipeTransport(_pipeName);
        await using (clientTransport)
        {
            using var stream = await clientTransport.ConnectAsync(ct);
            await HostProtocol.WriteRequestAsync(stream, new Mode.Command(command), ct);
            var lines = new List<string>();
            var exitCode = await HostProtocol.ReadResponseAsync(stream, line => lines.Add(line), ct);
            return (string.Join("\n", lines), exitCode);
        }
    }

    private async Task<(string output, int exitCode)> SendHealthAsync(string pipeName, CancellationToken ct = default)
    {
        var clientTransport = new NamedPipeTransport(pipeName);
        await using (clientTransport)
        {
            using var stream = await clientTransport.ConnectAsync(ct);
            await HostProtocol.WriteRequestAsync(stream, new Mode.Health(), ct);
            var lines = new List<string>();
            var exitCode = await HostProtocol.ReadResponseAsync(stream, line => lines.Add(line), ct);
            return (string.Join("\n", lines), exitCode);
        }
    }

    [Fact]
    public async Task SingleConnection_InvokeBashEcho_ReturnsOutputAndExit0()
    {
        var (output, exitCode) = await SendCommandAsync("Invoke-BashEcho 'hello'");
        Assert.Equal(0, exitCode);
        Assert.Contains("hello", output);
    }

    [Fact]
    public async Task HealthConnection_DoesNotWaitForWorkerInitialization()
    {
        var pipeName = $"psbash-health-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // A pool whose factory blocks (until the test cancels) models a host whose
        // first runspace is still warming: IsReady stays false, so health must
        // report "starting" without waiting for a runspace. No real SdkWorker is
        // ever built — the blocked factory unwinds via the cts on teardown.
        using var neverReady = new ManualResetEventSlim(false);
        SdkWorker BlockingFactory() { neverReady.Wait(cts.Token); return SdkWorker.Create(); }
        await using var pool = new WorkerPool<SdkWorker>(warmTarget: 1, max: 2, BlockingFactory);
        await using var transport = new NamedPipeTransport(pipeName);
        await using var server = new HostServer(transport, pool);

        var serverTask = server.RunAsync(cts.Token);
        await server.WhenListening.WaitAsync(cts.Token);

        var (output, exitCode) = await SendHealthAsync(pipeName, cts.Token);

        cts.Cancel();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        Assert.Equal(HostProtocol.HealthStartingExitCode, exitCode);
        Assert.Equal(HostProtocol.HealthStartingPayload, output);
    }

    [Fact]
    public async Task TwoSequentialConnections_DoNotShareRunspaceState()
    {
        // Each connection checks out its own isolated runspace from the pool (and
        // the runspace is discarded on release), so a $global set in one -c does
        // NOT leak into the next — matching a fresh bash process per -c. This is
        // the isolation guarantee that lets the daemon be reused safely.
        var (_, setExit) = await SendCommandAsync("$global:PsBashTestShared = 'state-shared-42'");
        Assert.Equal(0, setExit);

        var (output, getExit) = await SendCommandAsync("$global:PsBashTestShared");
        Assert.Equal(0, getExit);
        Assert.DoesNotContain("state-shared-42", output);
    }

    [Fact]
    public async Task GarbledModeHeader_ServerRemainsAlive()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        // Send bytes that don't start with "MODE:"; the server should drop the
        // malformed connection and keep accepting valid clients.
        var garbledTransport = new NamedPipeTransport(_pipeName);
        await using (garbledTransport)
        {
            using var stream = await garbledTransport.ConnectAsync(cts.Token);
            var garbage = System.Text.Encoding.UTF8.GetBytes("NOT_A_VALID_HEADER\n<<<END>>>\n");
            await stream.WriteAsync(garbage, cts.Token);
            // No FlushAsync: on Windows named pipes, FlushFileBuffers blocks until the
            // server reads ALL pending data. The server stops after the first bad line,
            // so flushing here would deadlock. Dispose sends remaining buffer and signals
            // EOF; the server's IOException handler catches it and moves on.
        }

        // Server must still be alive — accept a valid connection.
        var (output, aliveExit) = await SendCommandAsync("Invoke-BashEcho 'alive'", cts.Token);
        Assert.Equal(0, aliveExit);
        Assert.Contains("alive", output);
    }

    // -- PTY-4 ----------------------------------------------------------------

    /// <summary>
    /// PTY-4 end-to-end: a launcher that opts the connection into
    /// <see cref="SessionMode.Interactive"/> MUST observe an IPC response that
    /// carries zero data lines (the host routes command output to
    /// <c>System.Console.Out</c> instead) plus a trailing
    /// <c>PROMPT-READY</c> lifecycle sentinel. The captured Console.Out content
    /// must hold the actual command output bytes — proving the routing switch
    /// is functioning, not just suppressing output.
    /// </summary>
    [Fact]
    public async Task InteractiveSession_OutputRoutesToConsole_NotIpc()
    {
        // Redirect the worker process's Console.Out so we can observe what
        // would land on the PTY slave in a real interactive run. This emulates
        // PtySpawner's stdio inheritance — the slave is just whatever
        // System.Console.Out is wired to at runtime.
        using var captured = new StringWriter();
        var prior = Console.Out;
        Console.SetOut(captured);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var clientTransport = new NamedPipeTransport(_pipeName);
            int exitCode;
            bool promptReady;
            var lines = new List<string>();
            await using (clientTransport)
            {
                using var stream = await clientTransport.ConnectAsync(cts.Token);
                await HostProtocol.WriteRequestAsync(
                    stream,
                    new Mode.Command("Invoke-BashEcho 'pty4-token'", SessionMode.Interactive),
                    cts.Token);
                (exitCode, promptReady) = await HostProtocol.ReadResponseWithLifecycleAsync(
                    stream, line => lines.Add(line), cts.Token);
            }

            // Wire contract: zero IPC data lines, exit 0, PROMPT-READY observed.
            Assert.Equal(0, exitCode);
            Assert.Empty(lines);
            Assert.True(promptReady, "Interactive session must emit PROMPT-READY after EXIT");
        }
        finally
        {
            Console.SetOut(prior);
        }

        // Console.Out must hold the actual command bytes — the routing switch
        // is real, not just a silencer. Use Console-captured output AFTER the
        // restore so the assertion failure (if any) goes to the test runner.
        var consoleBytes = captured.ToString();
        Assert.Contains("pty4-token", consoleBytes);
    }

    /// <summary>
    /// PTY-4 framed-mode regression guard: an explicit
    /// <see cref="SessionMode.Framed"/> request (the default, also covered by
    /// every other test in this class) MUST still stream output through IPC and
    /// MUST NOT emit a PROMPT-READY sentinel. Pins the back-compat contract so
    /// a future "default everything to interactive" refactor breaks loudly.
    /// </summary>
    [Fact]
    public async Task FramedSession_StillStreamsOutputThroughIpc_NoPromptReady()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var clientTransport = new NamedPipeTransport(_pipeName);
        int exitCode;
        bool promptReady;
        var lines = new List<string>();
        await using (clientTransport)
        {
            using var stream = await clientTransport.ConnectAsync(cts.Token);
            await HostProtocol.WriteRequestAsync(
                stream,
                new Mode.Command("Invoke-BashEcho 'framed-token'", SessionMode.Framed),
                cts.Token);
            (exitCode, promptReady) = await HostProtocol.ReadResponseWithLifecycleAsync(
                stream, line => lines.Add(line), cts.Token);
        }

        Assert.Equal(0, exitCode);
        Assert.False(promptReady, "Framed mode MUST NOT emit PROMPT-READY (back-compat)");
        Assert.Contains(lines, l => l.Contains("framed-token"));
    }

    // -- REFACTOR-4 -----------------------------------------------------------

    /// <summary>
    /// Send a framed command and split the response by stream tag.
    /// </summary>
    private async Task<(List<string> Stdout, List<string> Stderr, int ExitCode)> SendCommandWithStreamsAsync(
        string command, CancellationToken ct = default)
    {
        var clientTransport = new NamedPipeTransport(_pipeName);
        await using (clientTransport)
        {
            using var stream = await clientTransport.ConnectAsync(ct);
            await HostProtocol.WriteRequestAsync(stream, new Mode.Command(command), ct);
            var stdout = new List<string>();
            var stderr = new List<string>();
            var exitCode = await HostProtocol.ReadResponseAsync(
                stream,
                (line, tag) =>
                {
                    if (tag == StreamTag.Stderr) stderr.Add(line);
                    else stdout.Add(line);
                },
                ct);
            return (stdout, stderr, exitCode);
        }
    }

    /// <summary>
    /// RC-1 regression — the core of REFACTOR-4. A command whose transpiled
    /// form writes to the host stderr stream (this is exactly what the emitter
    /// produces for <c>echo err &gt;&amp;2</c>) MUST surface on the launcher's
    /// STDERR-tagged IPC frames and MUST NOT leak into the stdout stream.
    /// Before REFACTOR-4 the host wrote to its detached fd 2 and the line was
    /// lost entirely.
    /// </summary>
    [Fact]
    public async Task FramedSession_HostStderr_RoutesToStderrStream_NotStdout()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var (stdout, stderr, exitCode) = await SendCommandWithStreamsAsync(
            "Invoke-BashEcho 'rc1-token' | ForEach-Object { Write-BashHostStderr $_ }",
            cts.Token);

        Assert.Equal(0, exitCode);
        Assert.Contains(stderr, l => l.Contains("rc1-token"));
        Assert.DoesNotContain(stdout, l => l.Contains("rc1-token"));
    }

    /// <summary>
    /// REFACTOR-4: stdout and stderr from the same command both travel the one
    /// IPC channel but stay on their own tagged frames — no interleaving, no
    /// loss, exit code intact.
    /// </summary>
    [Fact]
    public async Task FramedSession_StdoutAndStderr_BothSurviveOnSeparateStreams()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var (stdout, stderr, exitCode) = await SendCommandWithStreamsAsync(
            "Invoke-BashEcho 'on-stdout'; Write-BashHostStderr 'on-stderr'",
            cts.Token);

        Assert.Equal(0, exitCode);
        Assert.Contains(stdout, l => l.Contains("on-stdout"));
        Assert.DoesNotContain(stdout, l => l.Contains("on-stderr"));
        Assert.Contains(stderr, l => l.Contains("on-stderr"));
        Assert.DoesNotContain(stderr, l => l.Contains("on-stdout"));
    }

    /// <summary>
    /// Bound for the 16-connection concurrency probe. Override with
    /// <c>PSBASH_TEST_CONCURRENCY_TIMEOUT_SEC</c>, mirroring the
    /// <c>PSBASH_TEST_PROMPT_TIMEOUT_SEC</c> pattern the interactive harness uses.
    /// </summary>
    private static TimeSpan ConcurrencyTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable("PSBASH_TEST_CONCURRENCY_TIMEOUT_SEC"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(180);

    [Fact]
    public async Task SixteenConcurrentConnections_AllCompleteWithoutInterleaving()
    {
        const int n = 16;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        // 60 s was thin, not generous. Each connection gets an ISOLATED pooled worker that
        // is DISCARDED on release, so this is 16 psm1 imports with in-use concurrency capped
        // at (CPU count clamped to [2,8]) and execution serialized by SdkWorker's exec gate.
        //
        // Measured: 9-11 s standalone, and still only 12 s under six CPU hogs — so raw CPU
        // load is NOT the cause. What is: `dotnet test` on the solution dispatches one vstest
        // worker per project IN PARALLEL (see the comment in scripts/test.sh), so a full-suite
        // run has all seven suites spawning processes and runspaces at once. That is
        // creation/IO contention, not CPU, which is why hogs do not reproduce it. This test
        // was seen once at 1m8 s there, failing on this CTS while every connection was still
        // progressing — no product hang.
        //
        // This bound is not an assertion — Task.WhenAll returns the instant the last
        // connection completes, so raising it costs no coverage and only caps how long a
        // genuine hang takes to REPORT.
        cts.CancelAfter(ConcurrencyTimeout);

        var tasks = Enumerable.Range(0, n)
            .Select(i => SendCommandAsync($"Invoke-BashEcho 'conn-{i}'", cts.Token))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All connections must complete with exit 0.
        for (int i = 0; i < n; i++)
            Assert.Equal(0, results[i].exitCode);

        // Each result must contain exactly its own marker — no cross-stream mixing.
        for (int i = 0; i < n; i++)
            Assert.Contains($"conn-{i}", results[i].output);
    }
}
