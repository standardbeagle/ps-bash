using System.Diagnostics;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Shell.Tests;

[Trait("Category", "Integration")]
public class ProgramEndToEndTests
{
    private static readonly string? PwshPath = FindPwsh();
    private static readonly string ProjectDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PsBash.Shell"));
    private static readonly string WorkerScript = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "scripts", "ps-bash-worker.ps1"));

    private static string? FindPwsh()
    {
        try { return PwshLocator.Locate(); }
        catch (PwshNotFoundException) { return null; }
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(
        params string[] arguments)
        => RunShellAsync(arguments, timeout: null);

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(
        string[] arguments,
        TimeSpan? timeout)
    {
        var psi = BuildPsi(arguments);
        return ProcessRunHelper.RunAsync(psi, stdinContent: null, timeout: timeout);
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunShellWithStdinAsync(
        string stdinContent, params string[] arguments)
    {
        var psi = BuildPsi(arguments);
        return ProcessRunHelper.RunAsync(psi, stdinContent: stdinContent);
    }

    private static ProcessStartInfo BuildPsi(string[] arguments)
    {
        var psi = new ProcessStartInfo { FileName = "dotnet" };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(ProjectDir);
        psi.ArgumentList.Add("--");
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        psi.Environment["PSBASH_WORKER"] = WorkerScript;
        return psi;
    }

    [SkippableFact]
    public async Task Command_WriteHostHello_OutputsHelloAndExitsZero()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync("-c", "Write-Host hello");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
    }

    [SkippableFact]
    public async Task Command_ThrowError_PropagatesExitCodeAndStderr()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, _, stderr) = await RunShellAsync("-c", "throw 'deliberate failure'");

        Assert.Equal(1, exitCode);
        Assert.Contains("deliberate failure", stderr);
    }

    // Regression: `ps-bash -c "git log --oneline -20"` was reported to fail
    // with "The term '-l' is not recognized". The command string must be
    // handed to the transpiler intact — long flags whose first char collides
    // with a recognized ps-bash short flag (-l / -o / -n / -i / -e) must not
    // be mistaken for shell-host flags.
    [SkippableTheory]
    [InlineData("echo --oneline -20", "--oneline -20")]
    [InlineData("echo --list --long", "--list --long")]
    [InlineData("echo --name --include", "--name --include")]
    public async Task Command_LongFlagStartingWithShortFlagLetter_PassesToTranspilerIntact(
        string command, string expectedOutput)
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, stderr) = await RunShellAsync("-c", command);

        Assert.Equal(0, exitCode);
        Assert.Contains(expectedOutput, stdout);
        Assert.DoesNotContain("is not recognized", stderr);
    }

    [SkippableFact]
    public async Task Stdin_ReadsAndExecutes()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellWithStdinAsync(
            "Write-Host 'from stdin'", "-s");

        Assert.Equal(0, exitCode);
        Assert.Contains("from stdin", stdout);
    }

    [SkippableFact]
    public async Task NoArgs_EntersInteractiveModeAndExitsCleanly()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, _, _) = await RunShellWithStdinAsync("");

        Assert.Equal(0, exitCode);
    }

    [SkippableFact]
    public async Task Debug_WritesToStderr()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var psi = BuildPsi(new[] { "-c", "Write-Host ok" });
        psi.Environment["PSBASH_DEBUG"] = "1";

        var (exitCode, _, stderr) = await ProcessRunHelper.RunAsync(psi);

        Assert.Equal(0, exitCode);
        Assert.Contains("[ps-bash] input:", stderr);
        Assert.Contains("[ps-bash] transpiled:", stderr);
        Assert.Contains("[ps-bash] pwsh:", stderr);
        Assert.Contains("[ps-bash] exit:", stderr);
    }

    // ── Reliability: hung commands time out + kill entire process tree ───────

    [SkippableFact]
    public async Task HangingCommand_TimesOutWithin35Seconds_AndKillsProcessTree()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var preWorkerPids = Process.GetProcessesByName("pwsh")
            .Select(p => p.Id).ToHashSet();

        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(10);
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await RunShellAsync(new[] { "-c", "Start-Sleep 60" }, timeout);
        });
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"Timeout took too long: {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Contains("did not exit within", ex.Message);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var postWorkerPids = Process.GetProcessesByName("pwsh")
            .Select(p => p.Id).ToHashSet();
        var leaked = postWorkerPids.Except(preWorkerPids).ToList();
        Assert.True(leaked.Count == 0,
            $"Leaked pwsh worker PIDs after timeout: {string.Join(",", leaked)}");
    }
}
