using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Cmdlet error diagnostics (<c>FileSystemHelpers.WriteBashError</c>) through the host's
/// stderr sink. bash writes a diagnostic to fd 2 exactly once, inline, and
/// <c>2&gt;/dev/null</c> (emitted as <c>2&gt;$null</c>) discards it.
///
/// <para>Regression (task 01M3NPZZYRC5FHC0HWF7RTGYNK): every message went out on two
/// channels — an ErrorRecord (which honours PowerShell redirection, but SdkWorker only
/// delivered <c>Streams.Error</c> after the run) AND <c>$Host.UI.WriteErrorLine</c> via
/// the psm1 <c>Write-BashError</c> (inline, but invisible to redirection). So
/// <c>cat missing 2&gt;/dev/null</c> still printed, and an unredirected error printed
/// twice, the second copy after all later stdout.</para>
///
/// <para>Oracle note (qa-rubric Directive 1): SdkWorker is ps-bash-specific; the bash
/// shapes asserted here were confirmed with <c>wsl bash</c> (`cat nz 2&gt;/dev/null`
/// prints nothing; `{ cat nz; echo out; }` prints the diagnostic before `out`).</para>
/// </summary>
[Collection("SdkHost")]
public class SdkWorkerCmdletErrorTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string MissingPath()
        => Path.Combine(Path.GetTempPath(), $"psbash-missing-{Guid.NewGuid():N}.txt").Replace('\\', '/');

    private async Task<(List<string> Order, string Stderr, int Exit)> RunAsync(string command)
    {
        var worker = _fixture.CreateWorker();
        var errSb = new System.Text.StringBuilder();
        var order = new List<string>();
        var gate = new object();
        var exit = await worker.ExecuteWithOutputAsync(
            command,
            s => { lock (gate) { order.Add("O:" + s.TrimEnd('\r', '\n')); } },
            s => { lock (gate) { errSb.AppendLine(s); order.Add("E:" + s.TrimEnd('\r', '\n')); } });
        return (order, errSb.ToString(), exit);
    }

    [Theory]
    [InlineData("Invoke-BashCat")]
    [InlineData("Invoke-BashGrep x")]
    public async Task CmdletError_StderrRedirectedToNull_WritesNothingToStderr(string cmd)
    {
        var missing = MissingPath();

        var r = await RunAsync($"{cmd} '{missing}' 2>$null; Invoke-BashEcho after");

        Assert.Equal(string.Empty, r.Stderr);
        Assert.Equal(new[] { "O:after" }, r.Order);
    }

    [Fact]
    public async Task CmdletError_NotRedirected_WrittenOnceBeforeLaterStdout()
    {
        var missing = MissingPath();

        var r = await RunAsync($"Invoke-BashCat '{missing}'; Invoke-BashEcho after");

        Assert.Equal(
            new[] { $"E:cat: {missing}: No such file or directory", "O:after" },
            r.Order);
    }

    [Fact]
    public async Task CmdletError_MergedIntoStdout_RendersAsOneTextLine()
    {
        // bash: `cat nz 2>&1 | grep nz` prints the diagnostic line. The merged
        // ErrorRecord used to reach the table formatter (PSMessageDetails ... header).
        var missing = MissingPath();

        var r = await RunAsync($"Invoke-BashCat '{missing}' 2>&1 | Invoke-BashGrep missing");

        Assert.Equal(new[] { $"O:cat: {missing}: No such file or directory" }, r.Order);
    }
}
