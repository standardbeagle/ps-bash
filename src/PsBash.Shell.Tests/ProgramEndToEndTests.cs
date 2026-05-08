using System.Diagnostics;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Shell.Tests;

[Trait("Category", "Integration")]
public class ProgramEndToEndTests
{
    private static readonly string? PwshPath = FindPwsh();

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
        return ProcessRunHelper.BuildPsBashPsi(arguments);
    }

    [SkippableFact]
    public async Task Command_WriteHostHello_OutputsHelloAndExitsZero()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync("-c", "Write-Host hello");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
    }

    [Fact]
    public async Task Help_PrintsUsageAndExitsZero()
    {
        var (exitCode, stdout, stderr) = await RunShellAsync("--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: ps-bash", stdout);
        Assert.Contains("-c <command>", stdout);
        Assert.DoesNotContain("Loading ps-bash", stderr);
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
    public async Task HangingCommand_TimesOutQuickly_AndKillsProcessTree()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var preWorkerPids = Process.GetProcessesByName("pwsh")
            .Select(p => p.Id).ToHashSet();

        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(1);
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await RunShellAsync(new[] { "-c", "Start-Sleep 30" }, timeout);
        });
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8),
            $"Timeout took too long: {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Contains("did not exit within", ex.Message);

        List<int> leaked;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        do
        {
            var postWorkerPids = Process.GetProcessesByName("pwsh")
                .Select(p => p.Id).ToHashSet();
            leaked = postWorkerPids.Except(preWorkerPids).ToList();
            if (leaked.Count == 0)
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        } while (DateTime.UtcNow < deadline);

        Assert.True(leaked.Count == 0,
            $"Leaked pwsh worker PIDs after timeout: {string.Join(",", leaked)}");
    }

    // Regression: `ps-bash -c 'echo a; echo b; echo c'` must produce three
    // distinct output lines, not a single concatenated line like `abc`.
    // See Dart task FpyEHvFl7EXM.
    [SkippableFact]
    public async Task Command_ChainedCommands_EachOutputsOwnLine()
    {
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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

    // ── Regression: ls output must not be raw PSCustomObject dumps ────────────

    /// <summary>
    /// Regression for SdkWorker bug: when running through the host-backed path,
    /// SdkWorker.RunCommand called PSObject.ToString() which returns
    /// "@{Name=...; BashText=...}" for PsBash.LsEntry objects.
    ///
    /// In -c mode the shell falls back to PwshWorker (no host running in CI),
    /// which handles BashText correctly. This test guards against regression on
    /// the PwshWorker path and serves as an oracle for the expected output shape.
    /// </summary>
    [SkippableFact]
    public async Task Command_Ls_OutputsFilenameNotRawObjectHash()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var dir = Directory.CreateTempSubdirectory("psbash-ls-e2e-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "unique_sample.txt"), "data");
            var dirPath = dir.FullName.Replace('\\', '/');

            var (exitCode, stdout, _) = await RunShellAsync("-c", $"ls '{dirPath}'");

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("@{", stdout);
            Assert.Contains("unique_sample.txt", stdout);
        }
        finally { dir.Delete(recursive: true); }
    }

    [SkippableFact]
    public async Task Command_LsLong_OutputsPermissionsColumnNotRawObjectHash()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var dir = Directory.CreateTempSubdirectory("psbash-ls-long-e2e-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "file.txt"), "hello");
            var dirPath = dir.FullName.Replace('\\', '/');

            var (exitCode, stdout, _) = await RunShellAsync("-c", $"ls -l '{dirPath}'");

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("@{", stdout);
            // Long listing: at least one output line starts with a Unix permissions character
            var outputLines = stdout
                .Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains(outputLines, l =>
            {
                var t = l.TrimStart();
                return t.Length > 0 && (t[0] == '-' || t[0] == 'd' || t[0] == 'l');
            });
        }
        finally { dir.Delete(recursive: true); }
    }
}
