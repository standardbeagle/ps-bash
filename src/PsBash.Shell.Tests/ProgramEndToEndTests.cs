using System.Diagnostics;
using Xunit;

namespace PsBash.Shell.Tests;

[Trait("Category", "Integration")]
public class ProgramEndToEndTests
{
    private static readonly string IpcEndpoint = PsBashTestProcess.CreateEndpoint();

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
        => PsBashTestProcess.Create(arguments, ipcEndpoint: IpcEndpoint);

    [SkippableFact]
    public async Task Command_WriteHostHello_OutputsHelloAndExitsZero()
    {

        var (exitCode, stdout, _) = await RunShellAsync("-c", "Write-Host hello");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
    }

    [SkippableFact]
    public async Task Command_ThrowError_PropagatesExitCodeAndStderr()
    {

        var (exitCode, stdout, stderr) = await RunShellAsync("-c", "throw 'deliberate failure'");

        Assert.Equal(1, exitCode);
        Assert.Contains("deliberate failure", stdout + stderr);
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

        var (exitCode, stdout, stderr) = await RunShellAsync("-c", command);

        Assert.Equal(0, exitCode);
        Assert.Contains(expectedOutput, stdout);
        Assert.DoesNotContain("is not recognized", stderr);
    }

    [SkippableFact]
    public async Task Stdin_ReadsAndExecutes()
    {

        var (exitCode, stdout, _) = await RunShellWithStdinAsync(
            "Write-Host 'from stdin'", "-s");

        Assert.Equal(0, exitCode);
        Assert.Contains("from stdin", stdout);
    }

    [SkippableFact]
    public async Task NoArgs_EntersInteractiveModeAndExitsCleanly()
    {

        var (exitCode, _, _) = await RunShellWithStdinAsync("");

        Assert.Equal(0, exitCode);
    }

    [SkippableFact]
    public async Task Debug_WritesToStderr()
    {

        var psi = BuildPsi(new[] { "-c", "Write-Host ok" });
        psi.Environment["PSBASH_DEBUG"] = "1";

        var (exitCode, _, stderr) = await ProcessRunHelper.RunAsync(psi);

        Assert.Equal(0, exitCode);
        Assert.Contains("[ps-bash] input:", stderr);
        Assert.Contains("[ps-bash] transpiled:", stderr);
        Assert.Contains("[ps-bash] exit:", stderr);
    }

    // ── Reliability: hung commands time out + kill entire process tree ───────

    [SkippableFact]
    public async Task HangingCommand_TimesOutWithin35Seconds_AndKillsProcessTree()
    {

        var preWorkerPids = Process.GetProcessesByName("pwsh")
            .Select(p => p.Id).ToHashSet();

        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(10);
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            var psi = PsBashTestProcess.Create(["-c", "Start-Sleep 60"]);
            await ProcessRunHelper.RunAsync(psi, stdinContent: null, timeout: timeout);
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
            $"Leaked SDK host PIDs after timeout: {string.Join(",", leaked)}");
    }

    // Regression: `ps-bash -c 'echo a; echo b; echo c'` must produce three
    // distinct output lines, not a single concatenated line like `abc`.
    // See Dart task FpyEHvFl7EXM.
    [SkippableFact]
    public async Task Command_ChainedCommands_EachOutputsOwnLine()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo alpha; echo beta; echo gamma");

        Assert.Equal(0, exitCode);

        var lines = stdout
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        Assert.Contains("alpha", lines);
        Assert.Contains("beta", lines);
        Assert.Contains("gamma", lines);
        Assert.True(lines.Count >= 3,
            $"Expected >=3 output lines, got {lines.Count}: [{string.Join("|", lines)}]");
    }

    [SkippableFact]
    public async Task Command_PipeToLess_NonInteractivePassesThroughWithoutHanging()
    {

        var (exitCode, stdout, stderr) = await RunShellAsync(
            new[] { "-c", "printf 'x\\n' | less" },
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        Assert.Contains("x", stdout);
        Assert.DoesNotContain("native less executable not found", stderr);
    }

    [SkippableFact]
    public async Task Command_LessFile_NonInteractivePrintsFileWithoutHanging()
    {

        var path = Path.Combine(Path.GetTempPath(), $"ps-bash-less-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "from file\n");
        try
        {
            var escaped = path.Replace("\\", "/");
            var (exitCode, stdout, _) = await RunShellAsync(
                new[] { "-c", $"less '{escaped}'" },
                TimeSpan.FromSeconds(10));

            Assert.Equal(0, exitCode);
            Assert.Contains("from file", stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public async Task Command_PipeToMore_NonInteractivePassesThroughWithoutHanging()
    {
        var (exitCode, stdout, _) = await RunShellAsync(
            new[] { "-c", "printf 'one\\ntwo\\n' | more" },
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        Assert.Contains("one", stdout);
        Assert.Contains("two", stdout);
    }

    [SkippableFact]
    public async Task Command_MoreFile_NonInteractivePrintsFileWithoutHanging()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ps-bash-more-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "from more file\n");
        try
        {
            var escaped = path.Replace("\\", "/");
            var (exitCode, stdout, _) = await RunShellAsync(
                new[] { "-c", $"more '{escaped}'" },
                TimeSpan.FromSeconds(10));

            Assert.Equal(0, exitCode);
            Assert.Contains("from more file", stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // M3: file-arg mode — nonexistent script exits 2 with specific stderr message.
    // Does not require pwsh because the missing-file check runs before worker spawn.
    [Fact]
    public async Task ScriptFile_DoesNotExist_Exits2WithStderrMessage()
    {
        var (exitCode, _, stderr) = await RunShellAsync("nonexistent.sh");

        Assert.Equal(2, exitCode);
        Assert.Contains("ps-bash: nonexistent.sh: No such file or directory", stderr);
    }

    // M3: .sh file execution ─────────────────────────────────────────────────

    private static string WriteTempScript(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ps-bash-test-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, content);
        return path;
    }

    private static string WriteTempPs1(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ps-bash-test-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, content);
        return path;
    }

    // M3: .ps1 file execution ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task ScriptFile_Ps1_DotSourced()
    {

        var script = WriteTempPs1("Write-Host \"hello from ps1\"");
        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(script);

            Assert.Equal(0, exitCode);
            Assert.Contains("hello from ps1", stdout);
        }
        finally { File.Delete(script); }
    }

    [SkippableFact]
    public async Task ScriptFile_Ps1_ExitCodePropagates()
    {

        var script = WriteTempPs1("exit 42");
        try
        {
            var (exitCode, _, _) = await RunShellAsync(script);

            Assert.Equal(42, exitCode);
        }
        finally { File.Delete(script); }
    }

    [SkippableFact]
    public async Task ScriptFile_Sh_ExecutesTranspiled()
    {

        var script = WriteTempScript("echo hello");
        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(script);

            Assert.Equal(0, exitCode);
            Assert.Contains("hello", stdout);
        }
        finally { File.Delete(script); }
    }

    [SkippableFact]
    public async Task ScriptFile_Sh_PositionalArgs()
    {

        var script = WriteTempScript("echo $1 $2");
        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(script, "foo", "bar");

            Assert.Equal(0, exitCode);
            Assert.Contains("foo", stdout);
            Assert.Contains("bar", stdout);
        }
        finally { File.Delete(script); }
    }

    [SkippableFact]
    public async Task ScriptFile_Sh_Shebang_Ignored()
    {

        var script = WriteTempScript("#!/bin/bash\necho ok");
        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(script);

            Assert.Equal(0, exitCode);
            Assert.Contains("ok", stdout);
        }
        finally { File.Delete(script); }
    }

    [SkippableFact]
    public async Task ScriptFile_Sh_SetE_PropagatesExit()
    {

        var script = WriteTempScript("set -e\nfalse");
        try
        {
            var (exitCode, _, _) = await RunShellAsync(script);

            Assert.NotEqual(0, exitCode);
        }
        finally { File.Delete(script); }
    }

    // Regression: mixed chained commands (echo + pwd + piped ls) must each
    // produce their own line(s). Original repro from FpyEHvFl7EXM.
    [SkippableFact]
    public async Task Command_EchoPwdLsPipeHead_OutputsDistinctLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo \"bash tool works\"; pwd; echo FINAL_MARKER_XYZ");

        Assert.Equal(0, exitCode);

        var lines = stdout
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        Assert.Contains(lines, l => l.Contains("bash tool works"));
        Assert.Contains(lines, l => l.Contains("FINAL_MARKER_XYZ"));
        // The "bash tool works" line and "FINAL_MARKER_XYZ" line must be on
        // different lines — that's the core regression. Also expect at least
        // one line between them for pwd output.
        var worksIdx = lines.FindIndex(l => l.Contains("bash tool works"));
        var doneIdx = lines.FindIndex(l => l.Contains("FINAL_MARKER_XYZ"));
        Assert.True(worksIdx >= 0 && doneIdx > worksIdx,
            $"'bash tool works' and 'done' must be on separate lines. Got: [{string.Join("|", lines)}]");
        Assert.True(lines.Count >= 3,
            $"Expected >=3 output lines, got {lines.Count}: [{string.Join("|", lines)}]");
    }
}
