using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Host.Tests.Shell;

/// <summary>
/// The REPL marks its runspace interactive at startup (<see cref="InteractiveShell.MarkInteractiveCommand"/>).
/// Oracle: `bash -i -c 'echo $-'` has i and `bash -i -c 'set -n; echo still'` prints still (an interactive shell
/// ignores -n); in a script set -n stops the shell. The REPL itself has no bash oracle (ps-bash-specific surface),
/// and a shell started from it must not inherit the mark (an env var such as PSBASH_INTERACTIVE would be inherited).
/// </summary>
[Collection("SdkHost")]
public class InteractiveMarkTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static string Ps(string bash) => PsBash.Core.Transpiler.BashTranspiler.Transpile(bash)!;

    [Fact]
    public async Task MarkedRunspace_HasInteractiveFlag_AndIgnoresNoexec()
    {
        var worker = _fixture.CreateWorker();
        await HostWorkerFixture.CaptureAsync(worker, InteractiveShell.MarkInteractiveCommand);

        var run = await HostWorkerFixture.CaptureAsync(worker, Ps("case $- in *i*) echo interactive ;; *) echo script ;; esac; set -n; echo still"));

        Assert.Equal(["interactive", "still"], run.Lines.Select(l => l.TrimEnd('\r', '\n')).ToArray());
    }

    [Fact]
    public async Task UnmarkedRunspace_IsNotInteractive_AndNoexecStops()
    {
        var worker = _fixture.CreateWorker();

        var run = await HostWorkerFixture.CaptureAsync(worker, Ps("case $- in *i*) echo interactive ;; *) echo script ;; esac; set -n; echo still"));

        Assert.Equal(["script"], run.Lines.Select(l => l.TrimEnd('\r', '\n')).ToArray());
        Assert.Equal(0, run.ExitCode);
    }
}
