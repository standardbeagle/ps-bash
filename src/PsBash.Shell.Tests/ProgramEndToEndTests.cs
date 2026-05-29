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

    // Regression: a host that cannot start (or hangs) must surface a one-line
    // "ps-bash:" diagnostic and a defined exit code — never an unhandled-exception
    // managed stack trace with exit 82, which is what an embedding parent (the
    // Claude Code Bash tool) saw when ps-bash served as its shell and the host
    // wedged. We force the failure deterministically by pointing PSBASH_HOST at a
    // binary that does not exist on a fresh isolated endpoint (no host can serve),
    // so StartAsync throws HostUnavailableException, which Main now maps to a clean
    // message + exit 125.
    [SkippableFact]
    public async Task Command_HostBinaryMissing_FailsCleanlyWithoutStackTrace()
    {
        Skip.If(InteractiveShellHarness.FindPsBashBinary() is null, "ps-bash binary not built");

        var bogusHost = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ps-bash-host-missing-{System.Guid.NewGuid():N}.exe");
        var psi = PsBashTestProcess.Create(
            new[] { "-c", "echo hi" },
            env: new System.Collections.Generic.Dictionary<string, string?> { ["PSBASH_HOST"] = bogusHost },
            ipcEndpoint: PsBashTestProcess.CreateEndpoint());

        var (exitCode, _, stderr) = await ProcessRunHelper.RunAsync(psi, timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(125, exitCode);
        Assert.Contains("ps-bash:", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
        Assert.DoesNotContain("   at PsBash", stderr); // no leaked managed stack trace
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

        // Poll for child reap, bounded by a deadline. Replaces a 2s
        // Task.Delay — usually completes in <100 ms.
        var reapDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        List<int> leaked;
        while (true)
        {
            var postWorkerPids = Process.GetProcessesByName("pwsh")
                .Select(p => p.Id).ToHashSet();
            leaked = postWorkerPids.Except(preWorkerPids).ToList();
            if (leaked.Count == 0 || DateTime.UtcNow >= reapDeadline) break;
            await Task.Delay(50);
        }
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

    // ── General CLI options ──────────────────────────────────────────────────
    // The informational flags (--version / -V / --help) short-circuit before any
    // host/worker spawn, so they run without pwsh and must NEVER hang. Each test
    // carries a timeout: the pre-fix launcher had no case for these flags, so a
    // `-`-prefixed token fell through to the interactive/stdin branch and blocked
    // forever when no tty was attached — exactly the dogfooding probe
    // (`ps-bash --version`) that wedged the caller. A timeout makes a regression
    // back to that behavior fail loudly instead of hanging the suite.

    // The canonical version lives in PsBash.Core.csproj's <Version> — the only
    // project the release process bumps (alongside the module manifest). Reading
    // it independently here is what catches the drift regression: the first
    // --version impl anchored on PsBash.Transpiler's assembly, which sat at 0.9.8
    // while Core (and the real release) was 0.9.10. Returns null if the source
    // tree isn't reachable (e.g. a packaged-only run) so the format assertions
    // still run.
    private static string? ResolveExpectedVersion()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var csproj = Path.Combine(dir, "src", "PsBash.Core", "PsBash.Core.csproj");
            if (File.Exists(csproj))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    File.ReadAllText(csproj), @"<Version>([^<]+)</Version>");
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    [Fact]
    public async Task VersionLongFlag_PrintsCanonicalVersion_ExitsZero_NoHang()
    {
        var (exitCode, stdout, _) = await RunShellAsync(
            new[] { "--version" }, TimeSpan.FromSeconds(15));

        Assert.Equal(0, exitCode);
        Assert.Contains("ps-bash, version", stdout);
        Assert.Contains("Bash-to-PowerShell transpiler", stdout);
        Assert.Matches(@"ps-bash, version \d+\.\d+\.\d+", stdout);

        // Regression: must report the canonical Core/manifest version, not a
        // drifted sibling-project version (was 0.9.8 from PsBash.Transpiler).
        var expected = ResolveExpectedVersion();
        if (expected is not null)
            Assert.Contains($"ps-bash, version {expected}", stdout);
    }

    [Fact]
    public async Task VersionShortFlag_PrintsCanonicalVersion_ExitsZero_NoHang()
    {
        var (exitCode, stdout, _) = await RunShellAsync(
            new[] { "-V" }, TimeSpan.FromSeconds(15));

        Assert.Equal(0, exitCode);
        Assert.Matches(@"ps-bash, version \d+\.\d+\.\d+", stdout);

        var expected = ResolveExpectedVersion();
        if (expected is not null)
            Assert.Contains($"ps-bash, version {expected}", stdout);
    }

    [Fact]
    public async Task HelpLongFlag_PrintsUsage_ExitsZero_NoHang()
    {
        var (exitCode, stdout, _) = await RunShellAsync(
            new[] { "--help" }, TimeSpan.FromSeconds(15));

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: ps-bash", stdout);
        Assert.Contains("-c COMMAND", stdout);
        Assert.Contains("--version", stdout);
    }

    // --version returns before any worker spawn, so a missing/broken host must
    // not matter and the call must not hang. Guards the exact dogfooding probe:
    // Claude Code (which runs ps-bash as its shell) may probe `--version`, and
    // that must succeed even if the host binary is unavailable.
    [Fact]
    public async Task VersionFlag_DoesNotRequireHost_EvenWithBogusHostBinary()
    {
        var bogusHost = Path.Combine(
            Path.GetTempPath(), $"ps-bash-host-missing-{Guid.NewGuid():N}.exe");
        var psi = PsBashTestProcess.Create(
            new[] { "--version" },
            env: new System.Collections.Generic.Dictionary<string, string?> { ["PSBASH_HOST"] = bogusHost },
            ipcEndpoint: PsBashTestProcess.CreateEndpoint());

        var (exitCode, stdout, _) = await ProcessRunHelper.RunAsync(
            psi, timeout: TimeSpan.FromSeconds(15));

        Assert.Equal(0, exitCode);
        Assert.Contains("ps-bash, version", stdout);
    }

    // -l / --login must not swallow the -c command. Both the bundled (-lc) and
    // split (-l -c, --login -c) forms — plus the `-c -l "cmd"` form where flags
    // trail -c — must reach the transpiler with the command intact and still run
    // it. These exact argv shapes were captured from Claude Code via PSBASH_TRACE
    // (see ShellArgsTests); this is the end-to-end counterpart that proves the
    // command actually executes, not just that the parse populates Command.
    [SkippableTheory]
    [InlineData("-lc", "echo login-ok", null)]
    [InlineData("-l", "-c", "echo login-ok")]
    [InlineData("--login", "-c", "echo login-ok")]
    [InlineData("-c", "-l", "echo login-ok")]
    public async Task LoginWithCommand_RunsCommand(string a1, string a2, string? a3 = null)
    {
        var args = new[] { a1, a2, a3 }
            .Where(s => s is not null).Select(s => s!).ToArray();

        var (exitCode, stdout, _) = await RunShellAsync(args, TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);
        Assert.Contains("login-ok", stdout);
    }

    // END-TO-END: the CURRENT Claude Code Bash-tool wrapper must run.
    //
    // Claude Code wraps every Bash-tool command in a shell prelude and invokes
    // ps-bash as `-c -l "<wrapper>"`. The wrapper shape evolved past the one
    // captured in ShellArgsTests.Parse_ClaudeCodeSnapshotPattern: the live tool
    // now injects a TEMP/TMP env-setup as a MULTI-VAR bare assignment before the
    // real command:
    //   shopt ... || true && TEMP=<t> TMP=<t> && eval '<cmd>' < /dev/null && pwd -P >| <out>
    // A multi-pair bare assignment in a && chain emitted
    //   [void]($env:TEMP = ..; $env:TMP = ..)
    // and PowerShell's grouping `(...)` cannot hold a `;`-separated statement
    // list, so the host's PowerShell parser rejected it ("Missing closing ')'")
    // and EVERY Bash-tool command failed with "ps-bash: parse error" before
    // running. Fixed in PsEmitter (multi-statement assignment -> `[void]$(...)`).
    //
    // This is the launcher-level "the Bash tool works" guarantee: it runs the
    // full wrapper through ps-bash.exe and asserts the inner command executed
    // (sentinel on stdout), the process exited 0, and no parse error leaked.
    // The TEMP/TMP value and the `|| true` guard / pwd redirect mirror the live
    // wrapper so the multi-var-assignment-in-chain path is exercised exactly as
    // Claude Code drives it. Captured from the live PSBASH parse error
    // (2026-05-28); see ShellArgsTests for the arg-shape companion.
    [SkippableTheory]
    [InlineData("-lc")]          // bundled login+command (Windows form per ShellArgsTests)
    [InlineData("-c", "-l")]     // split form Claude Code also uses
    public async Task ClaudeCodeBashToolWrapper_RunsAndExitsZero(string a1, string? a2 = null)
    {
        const string sentinel = "PSBASH_WRAPPER_SENTINEL_42";
        var wrapper =
            "shopt -u extglob 2>/dev/null || true && "
          + "TEMP='C:\\Temp' TMP='C:\\Temp' && "
          + $"eval 'echo {sentinel}' < /dev/null && "
          + "pwd -P >| /tmp/psbash-cwd-probe";

        var args = (a2 is null ? new[] { a1, wrapper } : new[] { a1, a2, wrapper });

        var (exitCode, stdout, stderr) = await RunShellAsync(args, TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);
        Assert.Contains(sentinel, stdout);
        // The exact pre-fix failure must never reappear.
        Assert.DoesNotContain("parse error", stderr);
        Assert.DoesNotContain("Missing closing", stderr);
    }

    // --unix-paths / --windows-paths are accepted as leading flags and the -c
    // command still runs. (Path-translation semantics are exercised by the
    // emitter suite; this is the launcher-level smoke that the flag is consumed,
    // not mistaken for the command or a script path.)
    [SkippableTheory]
    [InlineData("--unix-paths")]
    [InlineData("--windows-paths")]
    public async Task PathModeFlag_WithCommand_RunsAndExitsZero(string pathFlag)
    {
        var (exitCode, stdout, _) = await RunShellAsync(
            new[] { pathFlag, "-c", "echo paths-ok" }, TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);
        Assert.Contains("paths-ok", stdout);
    }

    // --noprofile / --norc are consumed as leading flags; the -c command runs.
    // (Profile-skipping only changes interactive startup, which has no observable
    // effect in -c mode — this asserts the flag doesn't break the -c path.)
    [SkippableTheory]
    [InlineData("--noprofile")]
    [InlineData("--norc")]
    public async Task NoProfileFlag_WithCommand_RunsAndExitsZero(string flag)
    {
        var (exitCode, stdout, _) = await RunShellAsync(
            new[] { flag, "-c", "echo noprofile-ok" }, TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);
        Assert.Contains("noprofile-ok", stdout);
    }
}
