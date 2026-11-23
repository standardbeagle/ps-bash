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
    // A program that runs a minute (nohup's own redirect takes its output), and one that exits with status 3.
    private static readonly string Long = OperatingSystem.IsWindows() ? "ping.exe -n 60 127.0.0.1" : "/bin/sleep 60";
    private static readonly string ExitThree = OperatingSystem.IsWindows() ? "cmd.exe /c 'exit 3'" : "/bin/sh -c 'exit 3'";

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

    // REGRESSION (gap 1): a job that runs in a host process of its own (`cd` makes it one) read none of the compound's
    // stdin: b=1 (bash: the job's read takes 1, the shell's 2). The pipe is handed to the running job host.
    [SkippableFact]
    public async Task AJobInItsOwnHost_ReadsOneLine_AndTheShellTheNext()
        => Assert.Equal(Expected("b=2"), await RunAsync(
            Repeat("seq 3 | { cd / && read a & wait; read b; echo \"b=$b\"; }"), null));

    // The job host's program reads the handed pipe as its own stdin (bash: x).
    [SkippableFact]
    public async Task AJobInItsOwnHost_ItsProgramReadsTheCompoundsStdin()
        => Assert.Equal(Expected("x"), await RunAsync(Repeat($"echo x | {{ {{ cd /; {Reader}; }} & wait; }}"), null));

    // REGRESSION (gap 2): `nohup prog &` read /dev/null (bash: in, as GNU nohup leaves a stdin that is not a terminal).
    // `wait` waits for the nohup child: it is a job of the shell, as in bash.
    [SkippableFact]
    public async Task Nohup_InTheBackground_ReadsTheCompoundsStdin()
        => Assert.Equal(Expected("in"), await RunAsync(
            "cd \"$(mktemp -d)\"; " + Repeat($"echo in | {{ nohup {Reader} > o 2>/dev/null & wait; }}; cat o; rm -f o"), null));

    // REGRESSION: `nohup cmd &` set $! but never entered the job table, so `wait` returned at once (status 0), `jobs -p`
    // listed nothing and `kill %1` / `wait %1` said "no such job". bash: same, st=3, st=143, st=3.
    [SkippableFact]
    public async Task NohupChild_IsAJobOfTheShell()
        => Assert.Equal("same\nst=3\nst=143\nst=3", await RunAsync(
            $"nohup {Long} > /dev/null & [ \"$(jobs -p)\" = \"$!\" ] && echo same; kill %1; wait; "
            + $"nohup {ExitThree} > /dev/null & wait %1; echo \"st=$?\"; "
            + $"nohup {Long} > /dev/null & kill %1; wait $!; echo \"st=$?\"; "
            + $"nohup {ExitThree} > /dev/null & wait -n; echo \"st=$?\"", null));

    // The shell neither waits for a nohup child at its end nor ends it: the launcher returns while the child runs on
    // (bash: started, at once). The bound is half the child's life, so a shell that waited for it fails.
    [SkippableFact]
    public async Task NohupChild_StillRunningAtTheEnd_DoesNotHoldTheShell()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("started", await RunAsync($"nohup {Long} > /dev/null & echo started", null));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the shell waited {clock.Elapsed} for its nohup child");
    }

    // A foreground nohup in a compound reads the compound's stdin too, and waits for its program (bash: in).
    [SkippableFact]
    public async Task Nohup_InTheForeground_ReadsTheCompoundsStdin()
        => Assert.Equal(Expected("in"), await RunAsync(
            "cd \"$(mktemp -d)\"; " + Repeat($"echo in | {{ nohup {Reader} > o 2>/dev/null; }}; cat o; rm -f o"), null));
}
