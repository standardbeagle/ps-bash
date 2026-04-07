using System.Diagnostics;
using System.Linq;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Shell.Tests;

[Trait("Category", "Integration")]
public class AgentPatternEndToEndTests
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunShellAsync(
        params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(ProjectDir);
        psi.ArgumentList.Add("--");
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        psi.Environment["PSBASH_WORKER"] = WorkerScript;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet run");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout, stderr);
    }

    // ── Heredoc ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Heredoc_CatMultipleLines_OutputsAllLines()
    {
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "cat <<'EOF'\n$HOME should be literal\nEOF");

        Assert.Equal(0, exitCode);
        Assert.Contains("$HOME should be literal", stdout);
    }

    // ── Here-string ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task HereString_EchoViaGrepFilter_MatchesLine()
    {
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'hello world' | awk '{print $1}'");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
        Assert.DoesNotContain("world", stdout);
    }

    [SkippableFact]
    public async Task Pipe_AwkWithFieldSep_SplitsOnComma()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'a,b,c' | awk -F, '{print $2}'");

        Assert.Equal(0, exitCode);
        Assert.Contains("b", stdout.Trim().Split('\n').Last().Trim());
    }

    // ── Piped head / tail / wc / cut / tr ────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_HeadLimitsOutput_FirstTwoLines()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'a\\nb\\nc\\nd\\n' | head -n 2");

        Assert.Equal(0, exitCode);
        var lines = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    [SkippableFact]
    public async Task Pipe_WcCountsLines()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'one\\ntwo\\nthree\\n' | wc -l");

        Assert.Equal(0, exitCode);
        Assert.Contains("3", stdout.Trim());
    }

    [SkippableFact]
    public async Task Pipe_CutExtractsField()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'a:b:c' | cut -d: -f2");

        Assert.Equal(0, exitCode);
        Assert.Equal("b", stdout.Trim().Split('\n').Last().Trim());
    }

    [SkippableFact]
    public async Task Pipe_TrTranslatesCharacters()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'hello' | tr 'a-z' 'A-Z'");

        Assert.Equal(0, exitCode);
        Assert.Contains("HELLO", stdout);
    }

    // ── Piped sed ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_SedSubstitution_ReplacesText()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo 'hello world' | sed 's/world/earth/'");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello earth", stdout);
    }

    // ── Piped grep ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Pipe_GrepFiltersLines()
    {
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'cherry\\napple\\nbanana\\napricot\\n' | grep ap | sort | head -n 1");

        Assert.Equal(0, exitCode);
        Assert.Contains("apple", stdout.Trim().Split('\n').Last().Trim());
    }

    // ── Variable expansion in double quotes ──────────────────────────────────

    [SkippableFact]
    public async Task VarExpansion_DoubleQuotedEchoEnvVar()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "X=hello; echo \"value is $X\"");

        Assert.Equal(0, exitCode);
        Assert.Contains("value is hello", stdout);
    }

    // ── Brace expansion ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task BraceExpansion_TupleExpandsToMultiple()
    {
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'one\\ntwo\\nthree\\n' | xargs echo");

        Assert.Equal(0, exitCode);
        Assert.Equal("one two three", stdout.Trim());
    }

    [SkippableFact]
    public async Task Pipe_XargsN1Echo_OutputsSeparateLines()
    {
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, stderr) = await RunShellAsync(
            "-c", "trap 'echo cleanup' EXIT; echo hello");

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout);
    }

    [SkippableFact]
    public async Task Trap_EmptyIntSignal_DoesNotCrash()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "trap '' INT; echo ok");

        Assert.Equal(0, exitCode);
        Assert.Contains("ok", stdout);
    }

    // ── Command substitution ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task CommandSubstitution_InEcho_InlinesResult()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo \"count: $(echo 42)\"");

        Assert.Equal(0, exitCode);
        Assert.Contains("count: 42", stdout);
    }

    // ── Brace range expansion (fix: bare 1..5 → @(1..5)) ────────────────────

    [SkippableFact]
    public async Task BraceRange_DefaultStep_ExpandsSequence()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {1..5}");

        Assert.Equal(0, exitCode);
        Assert.Equal("1 2 3 4 5", stdout.Trim());
    }

    [SkippableFact]
    public async Task BraceRange_ReverseDefaultStep_ExpandsSequence()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {5..1}");

        Assert.Equal(0, exitCode);
        Assert.Equal("5 4 3 2 1", stdout.Trim());
    }

    [SkippableFact]
    public async Task BraceRange_WithStep_ExpandsCorrectly()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {1..10..3}");

        Assert.Equal(0, exitCode);
        Assert.Equal("1 4 7 10", stdout.Trim());
    }

    [SkippableFact]
    public async Task BraceRange_NonDivisibleStep_NoInfiniteLoop()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo {1..10..7}");

        Assert.Equal(0, exitCode);
        Assert.Equal("1 8", stdout.Trim());
    }

    // ── File redirect (fix: Invoke-BashRedirect pipeline binding) ────────────

    [SkippableFact]
    public async Task Redirect_EchoToFile_WritesAndReads()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo hello > /tmp/psbash-redir-test.txt; cat /tmp/psbash-redir-test.txt; rm /tmp/psbash-redir-test.txt");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello", stdout.Trim());
    }

    [SkippableFact]
    public async Task Redirect_AppendToFile_AppendsCorrectly()
    {
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo hidden > /dev/null; echo visible");

        Assert.Equal(0, exitCode);
        Assert.Equal("visible", stdout.Trim());
    }

    // ── Tee /dev/null (fix: $null as file path) ─────────────────────────────

    [SkippableFact]
    public async Task Tee_DevNull_PassesThroughWithoutCrash()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo tee-test | tee /dev/null");

        Assert.Equal(0, exitCode);
        Assert.Equal("tee-test", stdout.Trim());
    }

    [SkippableFact]
    public async Task Tee_ToFile_WritesAndPassesThrough()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "echo tee-content | tee /tmp/psbash-tee-test.txt; echo ---; cat /tmp/psbash-tee-test.txt; rm /tmp/psbash-tee-test.txt");

        Assert.Equal(0, exitCode);
        Assert.Contains("tee-content", stdout);
    }

    // ── Function $1 (fix: $args[0] → $($args[0]) in double quotes) ──────────

    [SkippableFact]
    public async Task Function_PositionalParam_NoIndexSuffix()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "greet() { echo \"hello $1\"; }; greet world");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello world", stdout.Trim());
    }

    [SkippableFact]
    public async Task Function_MultiplePositionalParams_AllResolve()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "f() { echo \"$1 and $2\"; }; f alpha beta");

        Assert.Equal(0, exitCode);
        Assert.Equal("alpha and beta", stdout.Trim());
    }

    // ── While read (fix: trailing newline before split) ──────────────────────

    [SkippableFact]
    public async Task WhileRead_NoExtraBlankLines()
    {
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "paste <(echo hello) <(echo world)");

        Assert.Equal(0, exitCode);
        Assert.Equal("hello\tworld", stdout.Trim());
    }

    [SkippableFact]
    public async Task ProcessSub_PasteMultiLine_CorrectAlignment()
    {
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "if [[ \"apple\" < \"banana\" ]]; then echo correct; else echo wrong; fi");

        Assert.Equal(0, exitCode);
        Assert.Contains("correct", stdout);
    }

    // ── Loop iteration cap ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task WhileTrue_IterCapPreventsInfiniteLoop()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        // Set a very low cap so the test completes quickly
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(ProjectDir);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("i=0; while true; do i=$((i+1)); done; echo $i");
        psi.Environment["PSBASH_WORKER"] = WorkerScript;
        psi.Environment["PSBASH_MAX_ITERATIONS"] = "100";

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet run");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // Should have hit the iteration cap and thrown
        Assert.Contains("loop iteration limit exceeded", stderr);
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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "! true; echo $?");

        Assert.Equal("1", stdout.Trim());
    }

    [SkippableFact]
    public async Task Negation_FalseCommand_ExitCodeIsZero()
    {
        Skip.If(PwshPath is null, "pwsh not available");

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
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "cat nonexistent_file_abc.txt; echo \"exit:$?\"");

        Assert.Contains("exit:1", stdout);
    }

    [SkippableFact]
    public async Task Error_LsNonexistentDir_NonZeroExitCode()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "ls nonexistent_dir_xyz/; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_HeadNonexistentFile_NonZeroExitCode()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "head nonexistent_file_abc.txt; echo \"exit:$?\"");

        Assert.Contains("exit:1", stdout);
    }

    [SkippableFact]
    public async Task Error_SortNonexistentFile_NonZeroExitCode()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "sort nonexistent_file_abc.txt; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_CpNonexistentSource_NonZeroExitCode()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "cp nonexistent_src_abc dest; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    // ── Usage error exit codes ──────────────────────────────────────────────

    [SkippableFact]
    public async Task Error_GrepNoArgs_NonZeroExitCode()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "grep; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_SedNoExpression_NonZeroExitCode()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "sed; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    [SkippableFact]
    public async Task Error_AwkNoProgram_NonZeroExitCode()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "awk; echo \"exit:$?\"");

        Assert.DoesNotContain("exit:0", stdout);
    }

    // ── Exit code propagation in control flow ───────────────────────────────

    [SkippableFact]
    public async Task ControlFlow_FalseAndEcho_OutputsNothing()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "false && echo yes");

        Assert.DoesNotContain("yes", stdout);
    }

    [SkippableFact]
    public async Task ControlFlow_TrueOrEcho_OutputsNothing()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "true || echo no");

        Assert.DoesNotContain("no", stdout);
    }

    [SkippableFact]
    public async Task ControlFlow_TrueAndEcho_OutputsSuccess()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "true && echo success");

        Assert.Contains("success", stdout);
    }

    [SkippableFact]
    public async Task ControlFlow_FalseOrEcho_OutputsFallback()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, stdout, _) = await RunShellAsync(
            "-c", "false || echo fallback");

        Assert.Contains("fallback", stdout);
    }

    // ── Stderr content verification ─────────────────────────────────────────

    [SkippableFact]
    public async Task Error_CatNonexistentFile_StderrHasNoWriteErrorPrefix()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (_, _, stderr) = await RunShellAsync(
            "-c", "cat nonexistent_file_abc.txt");

        Assert.DoesNotContain("Write-Error", stderr);
        Assert.DoesNotContain("FullyQualifiedErrorId", stderr);
    }
}
