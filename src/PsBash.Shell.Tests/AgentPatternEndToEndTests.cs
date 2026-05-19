using System.Diagnostics;
using System.Linq;
using Xunit;

namespace PsBash.Shell.Tests;

[Trait("Category", "Integration")]
public class AgentPatternEndToEndTests
{
    private static readonly string IpcEndpoint = PsBashTestProcess.CreateEndpoint();

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(
        params string[] arguments)
        => RunShellAsync(arguments, timeout: null, env: null, workingDirectory: null);

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(
        string[] arguments,
        TimeSpan? timeout)
        => RunShellAsync(arguments, timeout, env: null, workingDirectory: null);

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(
        string[] arguments,
        TimeSpan? timeout,
        IReadOnlyDictionary<string, string?>? env,
        string? workingDirectory = null)
    {
        var psi = env is null
            ? PsBashTestProcess.Create(arguments, workingDirectory, env, ipcEndpoint: IpcEndpoint)
            : PsBashTestProcess.Create(arguments, workingDirectory, env);
        return ProcessRunHelper.RunAsync(psi, stdinContent: null, timeout: timeout);
    }

    [SkippableFact]
    public async Task Pwd_AfterHomeRelativeCd_PrintsDirectory()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "ps-bash-home-" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(tempHome, "work", "beagle-term");
        Directory.CreateDirectory(targetDir);

        try
        {
            var (exitCode, stdout, stderr) = await RunShellAsync(
                ["-c", "cd ~/work/beagle-term; pwd"],
                timeout: null,
                env: new Dictionary<string, string?>
                {
                    ["HOME"] = tempHome,
                    ["USERPROFILE"] = tempHome,
                });

            Assert.Equal(0, exitCode);
            Assert.Contains("work/beagle-term", stdout.Replace('\\', '/'));
            Assert.DoesNotContain("work/beagle-term", stderr.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Pwd_AfterRelativeCd_PrintsDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-cd-relative-" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(tempDir, "child");
        Directory.CreateDirectory(targetDir);

        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(
                ["-c", "cd child; pwd"],
                timeout: null,
                env: null,
                workingDirectory: tempDir);

            Assert.Equal(0, exitCode);
            Assert.Contains("child", stdout.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Pwd_AfterQuotedCdWithSpaces_PrintsDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-cd-spaces-" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(tempDir, "child dir");
        Directory.CreateDirectory(targetDir);

        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(
                ["-c", "cd 'child dir'; pwd"],
                timeout: null,
                env: null,
                workingDirectory: tempDir);

            Assert.Equal(0, exitCode);
            Assert.Contains("child dir", stdout.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Pwd_AfterParentDirectoryCd_PrintsParent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ps-bash-cd-parent-" + Guid.NewGuid().ToString("N"));
        var childDir = Path.Combine(tempDir, "child");
        Directory.CreateDirectory(childDir);

        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(
                ["-c", "cd ..; pwd"],
                timeout: null,
                env: null,
                workingDirectory: childDir);

            Assert.Equal(0, exitCode);
            Assert.Contains(tempDir.Replace('\\', '/'), stdout.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task CdWithoutArgs_GoesHome()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "ps-bash-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);

        try
        {
            var (exitCode, stdout, _) = await RunShellAsync(
                ["-c", "cd; pwd"],
                timeout: null,
                env: new Dictionary<string, string?>
                {
                    ["HOME"] = tempHome,
                    ["USERPROFILE"] = tempHome,
                });

            Assert.Equal(0, exitCode);
            Assert.Contains(tempHome.Replace('\\', '/'), stdout.Replace('\\', '/'));
        }
        finally
        {
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task CdMissingDirectory_ReturnsNonZeroAndKeepsCwd()
    {
        var (exitCode, stdout, _) = await RunShellAsync(
            ["-c", "pwd; cd /definitely/not/ps-bash-here; pwd"]);

        Assert.NotEqual(0, exitCode);
        var lines = stdout.Replace('\\', '/')
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2);
        Assert.Equal(lines[0], lines[^1]);
    }

    // ── Heredoc ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Heredoc_CatMultipleLines_OutputsAllLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "cat <<EOF\nline one\nline two\nline three\nEOF");

        Assert.Equal(0, exitCode);
        Assert.Contains("line one", stdout);
        Assert.Contains("line two", stdout);
        Assert.Contains("line three", stdout);
    }

    [SkippableFact]
    public async Task Heredoc_QuotedDelimiter_NoVariableExpansion()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "cat <<'EOF'\n$HOME should be literal\nEOF");

        Assert.Equal(0, exitCode);
        Assert.Contains("$HOME should be literal", stdout);
    }

    // ── Here-string ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task HereString_EchoViaGrepFilter_MatchesLine()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "grep foo <<EOF\nfoo bar\nbaz qux\nEOF");

        Assert.Equal(0, exitCode);
        Assert.Contains("foo bar", stdout);
        Assert.DoesNotContain("baz qux", stdout);
    }

    // ── Piped awk ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_AwkPrintField_ExtractsColumn()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'hello world' | awk '{print $1}'");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
        Assert.DoesNotContain("world", stdout);
    }

    [SkippableFact]
    public async Task Pipe_AwkWithFieldSep_SplitsOnComma()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'a,b,c' | awk -F, '{print $2}'");

        Assert.Equal(0, exitCode);
        Assert.Contains("b", stdout.Trim().Split('\n').Last().Trim());
    }

    // ── Piped head / tail / wc / cut / tr ────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_HeadLimitsOutput_FirstTwoLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'a\\nb\\nc\\nd\\n' | head -n 2");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    [SkippableFact]
    public async Task Pipe_WcCountsLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'one\\ntwo\\nthree\\n' | wc -l");

        Assert.Equal(0, exitCode);
        Assert.Contains("3", stdout.Trim());
    }

    [SkippableFact]
    public async Task Pipe_CutExtractsField()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'a:b:c' | cut -d: -f2");

        Assert.Equal(0, exitCode);
        Assert.Equal("b", stdout.Trim().Split('\n').Last().Trim());
    }

    [SkippableFact]
    public async Task Pipe_TrTranslatesCharacters()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'hello' | tr 'a-z' 'A-Z'");

        Assert.Equal(0, exitCode);
        Assert.Contains("HELLO", stdout);
    }

    // ── Piped sed ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_SedSubstitution_ReplacesText()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'hello world' | sed 's/world/earth/'");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello earth", stdout);
    }

    // ── Piped grep ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_GrepFiltersLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'apple\\nbanana\\napricot\\n' | grep ap");

        Assert.Equal(0, exitCode);
        Assert.Contains("apple", stdout);
        Assert.Contains("apricot", stdout);
        Assert.DoesNotContain("banana", stdout);
    }

    // ── Multi-stage pipeline ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipeline_MultiStage_GrepSortHead()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'cherry\\napple\\nbanana\\napricot\\n' | grep ap | sort | head -n 1");

        Assert.Equal(0, exitCode);
        Assert.Contains("apple", stdout.Trim().Split('\n').Last().Trim());
    }

    // ── Variable expansion in double quotes ──────────────────────────────────

    [SkippableFact]
    public async Task VarExpansion_DoubleQuotedEchoEnvVar()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "X=hello; echo \"value is $X\"");

        Assert.Equal(0, exitCode);
        Assert.Contains("value is hello", stdout);
    }

    // ── Brace expansion ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task BraceExpansion_TupleExpandsToMultiple()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {a,b,c}");

        Assert.Equal(0, exitCode);
        Assert.Contains("a", stdout);
        Assert.Contains("b", stdout);
        Assert.Contains("c", stdout);
    }

    // ── For loop ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ForLoop_IteratesOverWords()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "for x in alpha beta gamma; do echo $x; done");

        Assert.Equal(0, exitCode);
        Assert.Contains("alpha", stdout);
        Assert.Contains("beta", stdout);
        Assert.Contains("gamma", stdout);
    }

    // ── C-style for loop (while-like counting) ─────────────────────────────

    [SkippableFact]
    public async Task ForArith_CountsToThree()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "for ((i=1; i<=3; i++)); do echo $i; done");

        Assert.Equal(0, exitCode);
        Assert.Contains("1", stdout);
        Assert.Contains("2", stdout);
        Assert.Contains("3", stdout);
    }

    [SkippableFact]
    public async Task ForArith_PrintfNoNewline_AccumulatesOnOneLine()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "for ((i=0; i<5; i++)); do printf \"%d \" $i; done; echo");

        Assert.Equal(0, exitCode);
        var trimmed = stdout.TrimEnd('\n', '\r');
        Assert.Equal("0 1 2 3 4", trimmed.TrimEnd());
    }

    // ── If/else ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task IfElse_TrueBranch_OutputsYes()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "if [[ 1 -eq 1 ]]; then echo yes; else echo no; fi");

        Assert.Equal(0, exitCode);
        Assert.Contains("yes", stdout);
        Assert.DoesNotContain("no", stdout);
    }

    // ── Case statement ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Case_MatchesPattern_OutputsCorrectBranch()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "X=banana; case $X in apple) echo fruit1;; banana) echo fruit2;; *) echo other;; esac");

        Assert.Equal(0, exitCode);
        Assert.Contains("fruit2", stdout);
        Assert.DoesNotContain("fruit1", stdout);
        Assert.DoesNotContain("other", stdout);
    }

    // ── Xargs ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_XargsEcho_ConcatenatesInputOnOneLine()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'one\\ntwo\\nthree\\n' | xargs echo");

        Assert.Equal(0, exitCode);
        Assert.Equal("one two three", stdout.Trim());
    }

    [SkippableFact]
    public async Task Pipe_XargsN1Echo_OutputsSeparateLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'a\\nb\\nc\\n' | xargs -n 1 echo");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("a", lines[0]);
        Assert.Equal("b", lines[1]);
        Assert.Equal("c", lines[2]);
    }

    // ── Trap ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Trap_ExitHandler_DoesNotCrash()
    {

        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", "trap 'echo cleanup' EXIT; echo hello");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
    }

    [SkippableFact]
    public async Task Trap_EmptyIntSignal_DoesNotCrash()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "trap '' INT; echo ok");

        Assert.Equal(0, exitCode);
        Assert.Contains("ok", stdout);
    }

    // ── Command substitution ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task CommandSubstitution_InEcho_InlinesResult()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo \"count: $(echo 42)\"");

        Assert.Equal(0, exitCode);
        Assert.Contains("count: 42", stdout);
    }

    // ── Brace range expansion (fix: bare 1..5 → @(1..5)) ────────────────────

    [SkippableFact]
    public async Task BraceRange_DefaultStep_ExpandsSequence()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {1..5}");

        Assert.Equal(0, exitCode);
        Assert.Equal("1 2 3 4 5", stdout.Trim());
    }

    [SkippableFact]
    public async Task BraceRange_ReverseDefaultStep_ExpandsSequence()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {5..1}");

        Assert.Equal(0, exitCode);
        Assert.Equal("5 4 3 2 1", stdout.Trim());
    }

    [SkippableFact]
    public async Task BraceRange_WithStep_ExpandsCorrectly()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {1..10..3}");

        Assert.Equal(0, exitCode);
        Assert.Equal("1 4 7 10", stdout.Trim());
    }

    [SkippableFact]
    public async Task BraceRange_NonDivisibleStep_NoInfiniteLoop()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {1..10..7}");

        Assert.Equal(0, exitCode);
        Assert.Equal("1 8", stdout.Trim());
    }

    // ── File redirect (fix: Invoke-BashRedirect pipeline binding) ────────────

    [SkippableFact]
    public async Task Redirect_EchoToFile_WritesAndReads()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo hello > /tmp/psbash-redir-test.txt; cat /tmp/psbash-redir-test.txt; rm /tmp/psbash-redir-test.txt");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello", stdout.Trim());
    }

    [SkippableFact]
    public async Task Redirect_AppendToFile_AppendsCorrectly()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo line1 > /tmp/psbash-append-test.txt; echo line2 >> /tmp/psbash-append-test.txt; cat /tmp/psbash-append-test.txt; rm /tmp/psbash-append-test.txt");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal("line1", lines[0]);
        Assert.Equal("line2", lines[1]);
    }

    [SkippableFact]
    public async Task Redirect_ToDevNull_NoOutput()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo hidden > /dev/null; echo visible");

        Assert.Equal(0, exitCode);
        Assert.Equal("visible", stdout.Trim());
    }

    [SkippableFact]
    public async Task Redirect_InputRedirect_CatReadsFile()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo hello > /tmp/psbash-input-redir-test.txt; cat < /tmp/psbash-input-redir-test.txt; rm /tmp/psbash-input-redir-test.txt");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello", stdout.Trim());
    }

    [SkippableFact]
    public async Task Array_LengthExpansion_ReturnsCount()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "array=(one two three); echo ${#array[@]}");

        Assert.Equal(0, exitCode);
        Assert.Equal("3", stdout.Trim());
    }

    [SkippableFact]
    public async Task Array_LengthExpansion_InDoubleQuotes()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", @"array=(one two three); echo ""count: ${#array[@]}""");

        Assert.Equal(0, exitCode);
        Assert.Equal("count: 3", stdout.Trim());
    }

    // ── Tee /dev/null (fix: $null as file path) ─────────────────────────────

    [SkippableFact]
    public async Task Tee_DevNull_PassesThroughWithoutCrash()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo tee-test | tee /dev/null");

        Assert.Equal(0, exitCode);
        Assert.Equal("tee-test", stdout.Trim());
    }

    [SkippableFact]
    public async Task Tee_ToFile_WritesAndPassesThrough()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo tee-content | tee /tmp/psbash-tee-test.txt; echo ---; cat /tmp/psbash-tee-test.txt; rm /tmp/psbash-tee-test.txt");

        Assert.Equal(0, exitCode);
        Assert.Contains("tee-content", stdout);
    }

    // ── Function $1 (fix: $args[0] → $($args[0]) in double quotes) ──────────

    [SkippableFact]
    public async Task Function_PositionalParam_NoIndexSuffix()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "greet() { echo \"hello $1\"; }; greet world");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello world", stdout.Trim());
    }

    [SkippableFact]
    public async Task Function_MultiplePositionalParams_AllResolve()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "f() { echo \"$1 and $2\"; }; f alpha beta");

        Assert.Equal(0, exitCode);
        Assert.Equal("alpha and beta", stdout.Trim());
    }

    // ── While read (fix: trailing newline before split) ──────────────────────

    [SkippableFact]
    public async Task WhileRead_NoExtraBlankLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo -e \"a\\nb\\nc\" | while read x; do echo \"[$x]\"; done");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("[a]", lines[0]);
        Assert.Equal("[b]", lines[1]);
        Assert.Equal("[c]", lines[2]);
    }

    // ── Process substitution (fix: Out-File double newlines) ─────────────────

    [SkippableFact]
    public async Task ProcessSub_PasteNoExtraBlankLines()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "paste <(echo hello) <(echo world)");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello\tworld", stdout.Trim());
    }

    [SkippableFact]
    public async Task ProcessSub_PasteMultiLine_CorrectAlignment()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "paste <(echo -e \"a\\nb\") <(echo -e \"1\\n2\")");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal("a\t1", lines[0]);
        Assert.Equal("b\t2", lines[1]);
    }

    // ── [[ ]] string comparison (fix: lexicographic vs numeric) ──────────────

    [SkippableFact]
    public async Task ExtendedTest_StringLessThan_LexicographicOrder()
    {

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "if [[ \"apple\" < \"banana\" ]]; then echo correct; else echo wrong; fi");

        Assert.Equal(0, exitCode);
        Assert.Contains("correct", stdout);
    }

    // ── Loop iteration cap ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task WhileTrue_IterCapPreventsInfiniteLoop()
    {

        // Set a very low cap so the test completes quickly
        var psi = PsBashTestProcess.Create(["-c", "i=0; while true; do i=$((i+1)); done; echo $i"]);
        psi.Environment["PSBASH_MAX_ITERATIONS"] = "100";

        var (_, stdout, stderr) = await ProcessRunHelper.RunAsync(psi);

        // Should have hit the iteration cap and thrown
        Assert.Contains("loop iteration limit exceeded", stdout + stderr);
    }

    // ── Reliability: hung commands time out + kill entire process tree ───────

    [SkippableFact]
    public async Task HangingCommand_TimesOutWithin35Seconds_AndKillsProcessTree()
    {

        // Capture pre-existing worker PIDs so we can prove none leak after timeout.
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
            $"Timeout took too long: {sw.Elapsed.TotalSeconds:F1}s (expected <20s)");
        Assert.Contains("did not exit within", ex.Message);

        // Poll until killed children are reaped, bounded by a deadline.
        // Replaces a 2s Task.Delay — usually completes in <100 ms on
        // Linux, occasionally up to 500 ms on Windows under load.
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

    // ═══════════════════════════════════════════════════════════════════════════
    // FILE LOCKING STRESS TESTS
    //
    // These tests target the Invoke-BashRedirect file I/O path which uses
    // File.WriteAllText/AppendAllText — atomic operations that replaced PS
    // native > operator to avoid file handle leaks in chained commands.
    // ═══════════════════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task FileLocking_SequentialWritesThenRead_NoCorruption()
    {

        // Rapid sequential writes to same file — tests that handles are released between commands
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo line1 > /tmp/psbash-lock1.txt",
                "echo line2 > /tmp/psbash-lock1.txt",
                "echo line3 > /tmp/psbash-lock1.txt",
                "cat /tmp/psbash-lock1.txt",
                "rm /tmp/psbash-lock1.txt"));

        Assert.Equal(0, exitCode);
        // Last write wins — file should only contain line3
        Assert.Equal("line3", stdout.Trim());
    }

    [SkippableFact]
    public async Task FileLocking_RapidAppendsThenRead_AllLinesPresent()
    {

        // Rapid sequential appends — tests that each >> releases the handle
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo line1 > /tmp/psbash-lock2.txt",
                "echo line2 >> /tmp/psbash-lock2.txt",
                "echo line3 >> /tmp/psbash-lock2.txt",
                "echo line4 >> /tmp/psbash-lock2.txt",
                "echo line5 >> /tmp/psbash-lock2.txt",
                "wc -l /tmp/psbash-lock2.txt",
                "rm /tmp/psbash-lock2.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("5", stdout.Trim());
    }

    [SkippableFact]
    public async Task FileLocking_WriteThenAppendThenCat_NoHandleLeak()
    {

        // Interleave write, append, and read — all three file modes in sequence
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo first > /tmp/psbash-lock3.txt",
                "echo second >> /tmp/psbash-lock3.txt",
                "cat /tmp/psbash-lock3.txt",
                "echo third > /tmp/psbash-lock3.txt",
                "echo ---",
                "cat /tmp/psbash-lock3.txt",
                "rm /tmp/psbash-lock3.txt"));

        Assert.Equal(0, exitCode);
        var parts = stdout.Split("---", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parts.Length);
        // First cat: first + second
        Assert.Contains("first", parts[0]);
        Assert.Contains("second", parts[0]);
        // Second cat: third (overwrite)
        Assert.Contains("third", parts[1]);
    }

    [SkippableFact]
    public async Task FileLocking_PipelineRedirectChain_EachCommandReleasesHandle()
    {

        // Pipeline output redirected to file, then another command reads it
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo -e \"cherry\\napple\\nbanana\" | sort > /tmp/psbash-lock4.txt",
                "cat /tmp/psbash-lock4.txt",
                "rm /tmp/psbash-lock4.txt"));

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("apple", lines[0]);
        Assert.Equal("banana", lines[1]);
        Assert.Equal("cherry", lines[2]);
    }

    [SkippableFact]
    public async Task FileLocking_LoopWritesToFile_NoAccumulation()
    {

        // For loop writing to file each iteration — tests handle release between loop bodies
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "for i in 1 2 3 4 5; do echo $i >> /tmp/psbash-lock5.txt; done",
                "wc -l /tmp/psbash-lock5.txt",
                "rm /tmp/psbash-lock5.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("5", stdout.Trim());
    }

    [SkippableFact]
    public async Task FileLocking_TeeAndRedirect_BothFilesWritten()
    {

        // Tee writes to one file, redirect writes to another — both must complete
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo -e \"a\\nb\\nc\" | tee /tmp/psbash-lock6a.txt > /tmp/psbash-lock6b.txt",
                "echo \"tee:\"; cat /tmp/psbash-lock6a.txt",
                "echo \"redir:\"; cat /tmp/psbash-lock6b.txt",
                "rm /tmp/psbash-lock6a.txt /tmp/psbash-lock6b.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("tee:", stdout);
        Assert.Contains("redir:", stdout);
    }

    [SkippableFact]
    public async Task FileLocking_WriteReadWriteRead_RapidAlternation()
    {

        // Rapid write-read alternation on same file — classic file locking trigger
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo alpha > /tmp/psbash-lock7.txt",
                "cat /tmp/psbash-lock7.txt",
                "echo beta > /tmp/psbash-lock7.txt",
                "cat /tmp/psbash-lock7.txt",
                "echo gamma > /tmp/psbash-lock7.txt",
                "cat /tmp/psbash-lock7.txt",
                "rm /tmp/psbash-lock7.txt"));

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("alpha", lines[0]);
        Assert.Equal("beta", lines[1]);
        Assert.Equal("gamma", lines[2]);
    }

    [SkippableFact]
    public async Task FileLocking_MultipleFilesInOneCommand_AllWritten()
    {

        // Write to multiple different files in quick succession
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo f1 > /tmp/psbash-mf1.txt",
                "echo f2 > /tmp/psbash-mf2.txt",
                "echo f3 > /tmp/psbash-mf3.txt",
                "cat /tmp/psbash-mf1.txt /tmp/psbash-mf2.txt /tmp/psbash-mf3.txt",
                "rm /tmp/psbash-mf1.txt /tmp/psbash-mf2.txt /tmp/psbash-mf3.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("f1", stdout);
        Assert.Contains("f2", stdout);
        Assert.Contains("f3", stdout);
    }

    [SkippableFact]
    public async Task FileLocking_ProcessSubWithRedirect_NoTempFileConflict()
    {

        // Process substitution creates temp files — verify no conflicts with redirects
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "paste <(echo col1) <(echo col2) > /tmp/psbash-psub-redir.txt",
                "cat /tmp/psbash-psub-redir.txt",
                "rm /tmp/psbash-psub-redir.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("col1", stdout);
        Assert.Contains("col2", stdout);
    }

    [SkippableFact]
    public async Task FileLocking_AppendInWhileLoop_AllIterationsWritten()
    {

        // For loop appending to file each iteration — tests handle release between loop bodies
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "for i in 1 2 3 4 5 6 7 8 9 10; do echo \"line $i\" >> /tmp/psbash-wloop.txt; done",
                "wc -l /tmp/psbash-wloop.txt",
                "head -n 1 /tmp/psbash-wloop.txt",
                "tail -n 1 /tmp/psbash-wloop.txt",
                "rm /tmp/psbash-wloop.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("10", stdout); // 10 lines
        Assert.Contains("line 1", stdout); // first line
        Assert.Contains("line 10", stdout); // last line
    }

    [SkippableFact]
    public async Task FileLocking_SedInPlace_FileUpdatedCorrectly()
    {

        // sed -i modifies file in place — tests that file handle is properly released
        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ",
                "echo -e \"hello world\\nfoo bar\" > /tmp/psbash-sed.txt",
                "sed -i 's/world/earth/' /tmp/psbash-sed.txt",
                "cat /tmp/psbash-sed.txt",
                "rm /tmp/psbash-sed.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("hello earth", stdout);
        Assert.Contains("foo bar", stdout);
    }

    [SkippableFact]
    public async Task FileLocking_RedirectOverwriteChainOf10_LastValueOnly()
    {

        // 10 rapid overwrites to same file — stress test handle release
        var commands = new List<string>();
        for (int i = 0; i < 10; i++)
            commands.Add($"echo {i} > /tmp/psbash-chain.txt");
        commands.Add("cat /tmp/psbash-chain.txt");
        commands.Add("rm /tmp/psbash-chain.txt");

        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", string.Join("; ", commands));

        Assert.Equal(0, exitCode);
        Assert.Equal("9", stdout.Trim());
    }

    // ── Pipeline negation ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task Negation_TrueCommand_ExitCodeIsOne()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "! true; echo $?");

        Assert.Equal("1", stdout.Trim());
    }

    [SkippableFact]
    public async Task Negation_FalseCommand_ExitCodeIsZero()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "! false; echo $?");

        Assert.Equal("0", stdout.Trim());
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ERROR SCENARIO TESTS
    //
    // Verify that commands set correct exit codes on failure and that
    // control flow operators propagate exit codes correctly.
    // ═══════════════════════════════════════════════════════════════════════════

    // ── File error exit codes ───────────────────────────────────────────────

    [SkippableFact]
    public async Task Error_CatNonexistentFile_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "cat nonexistent_file_abc.txt; echo \"exit:$?\"");

        Assert.Contains("exit:1", stdout);
    }

    [SkippableFact]
    public async Task Error_LsNonexistentDir_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "ls nonexistent_dir_xyz/; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_HeadNonexistentFile_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "head nonexistent_file_abc.txt; echo \"exit:$?\"");

        Assert.Contains("exit:1", stdout);
    }

    [SkippableFact]
    public async Task Error_SortNonexistentFile_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "sort nonexistent_file_abc.txt; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_CpNonexistentSource_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "cp nonexistent_src_abc dest; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    // ── Usage error exit codes ──────────────────────────────────────────────

    [SkippableFact]
    public async Task Error_GrepNoArgs_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "grep; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_SedNoExpression_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "sed; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_AwkNoProgram_NonZeroExitCode()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "awk; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    // ── Exit code propagation in control flow ───────────────────────────────

    [SkippableFact]
    public async Task ControlFlow_FalseAndEcho_OutputsNothing()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "false && echo yes");

        Assert.DoesNotContain("yes", stdout);
    }

    [SkippableFact]
    public async Task ControlFlow_TrueOrEcho_OutputsNothing()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "true || echo no");

        Assert.DoesNotContain("no", stdout);
    }

    [SkippableFact]
    public async Task ControlFlow_TrueAndEcho_OutputsSuccess()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "true && echo success");

        Assert.Contains("success", stdout);
    }

    [SkippableFact]
    public async Task ControlFlow_FalseOrEcho_OutputsFallback()
    {

        var (_, stdout, _) = await RunShellAsync(
            "-c", "false || echo fallback");

        Assert.Contains("fallback", stdout);
    }

    // ── Stderr content verification ─────────────────────────────────────────

    [SkippableFact]
    public async Task Error_CatNonexistentFile_StderrHasNoWriteErrorPrefix()
    {

        var (_, _, stderr) = await RunShellAsync(
            "-c", "cat nonexistent_file_abc.txt");

        Assert.DoesNotContain("Write-Error", stderr);
        Assert.DoesNotContain("FullyQualifiedErrorId", stderr);
    }
}
