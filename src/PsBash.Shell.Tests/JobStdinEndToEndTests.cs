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
    // A program that exits at once without reading stdin, and one that runs about a second without reading it.
    private static readonly string Quick = OperatingSystem.IsWindows() ? "hostname.exe >/dev/null" : "/bin/true";
    private static readonly string Short = OperatingSystem.IsWindows() ? "ping.exe -n 2 127.0.0.1 >/dev/null" : "/bin/sleep 1";

    // These are races (a program's pipe set up and put back beside the shell's reads): each script runs its case this
    // many times in one shell, and every run must be right.
    private const int Runs = 20;

    private static string Repeat(string body) => $"for run in $(seq {Runs}); do {body}; done";

    private static string Expected(string once) => string.Join("\n", Enumerable.Repeat(once, Runs));

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

    // REGRESSION: the foreground program joined the pipe a background program held, the pipe outlived it with the rest
    // of the input inside, and `read` found the stdin done: `a end` in 9 runs of 10 (bash: a b c end).
    [SkippableFact]
    public async Task ReadLoop_ProgramsInTheBackgroundAndForeground_ReadsEveryLine()
        => Assert.Equal(Expected("a b c end"), await RunAsync(
            Repeat($"printf 'a\\nb\\nc\\n' | {{ while read x; do {Quick} & {Quick}; printf '%s ' \"$x\"; done; wait; echo end; }}"), null));

    // REGRESSION: a program that started while the last one's pipe was being put back opened a second pipe and took the
    // records behind it: `a=2 b=1` in 8 runs of 15 (bash: a=1 b=2).
    [SkippableFact]
    public async Task Records_AfterProgramsComeAndGo_KeepTheirOrder()
        => Assert.Equal(Expected("a=1 b=2"), await RunAsync(
            Repeat($"seq 50000 | {{ {Quick} & {Quick}; {Quick}; wait; read a; read b; echo \"a=$a b=$b\"; }}"), null));

    // REGRESSION (also before the pipe was shared): a background program that reads nothing took the input the shell
    // reads next: `a b` for three lines, `a=` (bash: a b c; a=1).
    [SkippableFact]
    public async Task ShellReads_BesideABackgroundProgramThatReadsNothing_GetTheInput()
        => Assert.Equal("a b c\na=1 b=2", await RunAsync(
            $"printf 'a\\nb\\nc\\n' | while read x; do {Short} & printf '%s' \"$x\"; [ $x = c ] && echo || printf ' '; done; wait; "
            + $"seq 5 | {{ {Short} & read a; read b; echo \"a=$a b=$b\"; wait; }}", null));

    // REGRESSION: a program `env` or `command` runs read the process stdin, not the compound's (bash: in1, in2, in3).
    [SkippableFact]
    public async Task EnvsAndCommandsProgram_ReadsTheCompoundsStdin()
        => Assert.Equal("in1\nin2\nin3", await RunAsync(
            $"echo in1 | {{ env X=1 {Reader}; }}; echo in2 | {{ env X=1 {Reader} & wait; }}; echo in3 | {{ command {Reader} & wait; }}", null));
}
