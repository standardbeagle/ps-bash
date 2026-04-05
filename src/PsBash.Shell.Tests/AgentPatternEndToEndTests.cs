using System.Diagnostics;
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
    public async Task Pipe_XargsEcho_ConcatenatesInput()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, _) = await RunShellAsync(
            "-c", "printf 'one\\ntwo\\nthree\\n' | xargs echo");

        Assert.Equal(0, exitCode);
        Assert.Contains("one", stdout);
        Assert.Contains("two", stdout);
        Assert.Contains("three", stdout);
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
}
