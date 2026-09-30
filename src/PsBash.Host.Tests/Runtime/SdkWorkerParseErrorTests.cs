using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// A transpiled script that PowerShell cannot PARSE runs nothing at all — not even the
/// statements before the bad one — so the host must say why on stderr and exit non-zero, never
/// finish quietly. bash's own syntax-error status is 2, and the launcher already uses 2 for a
/// bash parse error, so a PowerShell-level parse failure of the emitted script reports the same.
///
/// <para>Oracle note (qa-rubric Directive 1): SdkWorker parse handling is ps-bash-specific —
/// bash has no "emitted PowerShell" to fail parsing. The exit status follows bash's
/// syntax-error convention (`bash -c 'if'` exits 2).</para>
/// </summary>
[Collection("SdkHost")]
public class SdkWorkerParseErrorTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private async Task<(List<string> Stdout, List<string> Stderr, int Exit)> RunAsync(string script)
    {
        var worker = _fixture.CreateWorker();
        var stdout = new List<string>();
        var stderr = new List<string>();
        var gate = new object();
        var exit = await worker.ExecuteWithOutputAsync(
            script,
            s => { lock (gate) { stdout.Add(s); } },
            s => { lock (gate) { stderr.Add(s); } });
        return (stdout, stderr, exit);
    }

    [Theory]
    [InlineData("Invoke-BashEcho before; Invoke-BashEcho \"unterminated")]   // missing string terminator
    [InlineData("Invoke-BashEcho before; if ($true) { Invoke-BashEcho x")]    // missing closing brace
    [InlineData("Invoke-BashEcho before; Invoke-BashEcho x }")]               // unexpected token
    public async Task UnparseableScript_ReportsOnStderr_ExitsTwo_RunsNothing(string script)
    {
        var r = await RunAsync(script);

        Assert.Equal(2, r.Exit);
        Assert.Contains(r.Stderr, line => line.Contains("parse error", StringComparison.Ordinal));
        // PowerShell parses the whole script before running any of it.
        Assert.Empty(r.Stdout);
    }

    [Fact]
    public async Task RuntimeError_StillReportsOnStderr_ExitsOne()
    {
        var r = await RunAsync("Invoke-BashEcho before; throw 'boom'");

        Assert.Equal(1, r.Exit);
        Assert.Contains(r.Stderr, line => line.Contains("boom", StringComparison.Ordinal));
        Assert.Contains(r.Stdout, line => line.Contains("before", StringComparison.Ordinal));
    }
}
