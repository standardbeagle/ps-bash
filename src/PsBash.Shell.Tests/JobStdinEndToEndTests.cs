using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// A foreground program keeps its stdin (the launcher's, a compound's) while an in-process background job runs a
/// program. REGRESSION: the job's program took the PROCESS stdin for its lifetime (NativeStdinBridge swapped fd 0 /
/// SetStdHandle from the job's runspace), so <c>printf 'hi\n' | ps-bash -c '/usr/bin/sleep 3 &amp; sleep 1; /usr/bin/cat'</c>
/// printed nothing. Oracle: bash 5.2 prints <c>hi</c> for each case below (the job's program reads nothing; with job
/// control off a top-level job reads /dev/null, one inside a compound shares the compound's stdin). The launcher's
/// stdin is the subject, which the differential harness does not feed, hence hand-written expectations.
/// </summary>
[Trait("Category", "Integration")]
public class JobStdinEndToEndTests
{
    private static readonly string IpcEndpoint = PsBashTestProcess.CreateEndpoint();

    // A program that runs a few seconds without reading stdin, and one that copies its stdin, on each platform.
    private static readonly string Sleeper = OperatingSystem.IsWindows() ? "ping.exe -n 4 127.0.0.1 >/dev/null" : "/bin/sleep 3";
    private static readonly string Reader = OperatingSystem.IsWindows() ? "sort.exe" : "/bin/cat";

    private static async Task<string> RunAsync(string script, string? stdin)
    {
        var psi = PsBashTestProcess.Create(["-c", script], ipcEndpoint: IpcEndpoint);
        var (exit, stdout, stderr) = await ProcessRunHelper.RunAsync(psi, stdinContent: stdin, timeout: TimeSpan.FromSeconds(90));
        Assert.True(exit == 0, $"exit {exit}: {stderr}");
        return stdout.Replace("\r\n", "\n").TrimEnd('\n');
    }

    [SkippableFact]
    public async Task LauncherStdin_ReachesAForegroundProgram_WhileAJobRunsAProgram()
        => Assert.Equal("hi", await RunAsync($"{Sleeper} & sleep 1; {Reader}; wait", "hi\n"));

    [SkippableFact]
    public async Task CompoundStdin_ReachesAForegroundProgram_WhileAJobRunsAProgram()
        => Assert.Equal("hi", await RunAsync($"{Sleeper} & sleep 1; printf 'hi\\n' | {{ {Reader}; }}; wait", null));

    // The job shares the compound's stdin and its program reads none of it: the shell's next reader gets it all.
    [SkippableFact]
    public async Task CompoundStdin_SharedWithAJobsProgram_IsLeftForTheShell()
        => Assert.Equal("hi", await RunAsync($"printf 'hi\\n' | {{ {Sleeper} & sleep 1; {Reader}; wait; }}", null));

    // A job inside a compound reads the compound's stdin.
    [SkippableFact]
    public async Task CompoundStdin_ReachesAJobsProgram()
        => Assert.Equal("hi", await RunAsync($"printf 'hi\\n' | {{ {Reader} & wait; }}", null));
}
