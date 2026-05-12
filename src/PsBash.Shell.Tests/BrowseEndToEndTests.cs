using System.Diagnostics;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// End-to-end tests for the <c>browse</c> object workbench.
///
/// These spawn a real <c>ps-bash</c> process via <see cref="PsBashTestProcess"/> +
/// <see cref="ProcessRunHelper"/> so they exercise the launcher, IPC, host
/// runspace, and PsBash.psm1 in the same configuration a user sees. Asserts
/// observable output, not implementation details.
///
/// Each test has a short hard timeout. The <c>NoArgs_TerminatesWithBoundedOutput</c>
/// case is the regression for the user-reported "ll | browse was a never-ending
/// scroll of text" bug: <c>Invoke-BrowseInteractive</c> calls <c>Read-Host</c> in a
/// loop, and when stdin is non-interactive (host runspace, redirected stdin)
/// <c>Read-Host</c> returns immediately, so the loop re-renders the header forever.
/// </summary>
[Trait("Category", "Integration")]
public class BrowseEndToEndTests
{
    private static readonly string IpcEndpoint = PsBashTestProcess.CreateEndpoint();
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(15);

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string command, TimeSpan? timeout = null)
    {
        var psi = PsBashTestProcess.Create(["-c", command], ipcEndpoint: IpcEndpoint);
        return ProcessRunHelper.RunAsync(psi, stdinContent: null, timeout: timeout ?? ShortTimeout);
    }

    // ── Regression: bare `browse` must terminate ─────────────────────────────

    // User-reported: `ll | browse` produced a never-ending scroll of text. Root
    // cause: bare `browse` falls into Invoke-BrowseInteractive when
    // [Console]::IsInputRedirected is false, and Read-Host on a non-tty stdin
    // returns immediately with empty input — so the header loop runs without
    // bound. The shell host has no tty, so this is the standard case.
    //
    // Contract: `browse` with no flags and a finite input pipeline must finish
    // within a short bound and must NOT emit more output than is justifiable
    // for the input size.
    [SkippableFact]
    public async Task NoArgs_OnFiniteInput_TerminatesWithBoundedOutput()
    {
        var (exitCode, stdout, stderr) = await RunAsync("1..3 | browse", TimeSpan.FromSeconds(8));

        Assert.Equal(0, exitCode);

        // Generous upper bound: a single rendered table with a header row, 3
        // data rows, and some formatter padding fits well inside a few KB.
        // The bug produced ~880 KB for 3 items.
        Assert.True(
            stdout.Length < 16 * 1024,
            $"`1..3 | browse` produced {stdout.Length} bytes of stdout; expected < 16 KB. " +
            $"Likely an unbounded Invoke-BrowseInteractive loop. First 400 bytes:\n" +
            stdout[..Math.Min(400, stdout.Length)]);

        // Stderr should be quiet on the happy path.
        Assert.True(
            stderr.Length < 4 * 1024,
            $"Unexpectedly large stderr ({stderr.Length} bytes): {stderr[..Math.Min(400, stderr.Length)]}");
    }

    // ── -List: emit row objects ─────────────────────────────────────────────

    [SkippableFact]
    public async Task List_OnIntegers_EmitsOneRowPerItemWithIndices()
    {
        var (exitCode, stdout, stderr) = await RunAsync("1..3 | browse -List");

        Assert.Equal(0, exitCode);

        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        // Each row contains the integer and an Index column. Verify presence of
        // the three indices and the three values in the output.
        var joined = string.Join("\n", lines);
        Assert.Contains("0", joined);
        Assert.Contains("1", joined);
        Assert.Contains("2", joined);
        Assert.DoesNotContain("error", stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ── -PassThru: returns original items ───────────────────────────────────

    [SkippableFact]
    public async Task PassThru_ReturnsOriginalObjectsInOrder()
    {
        var (exitCode, stdout, _) = await RunAsync("'a','b','c' | browse -PassThru");

        Assert.Equal(0, exitCode);
        var lines = stdout.Replace("\r\n", "\n").Trim().Split('\n');
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [SkippableFact]
    public async Task PassThru_OnEmptyPipeline_EmitsNothingAndExitsZero()
    {
        var (exitCode, stdout, _) = await RunAsync("@() | browse -PassThru");

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrWhiteSpace(stdout), $"Expected empty output, got: {stdout}");
    }

    // ── -Inspect: runs the inspect action against the chosen item ───────────

    [SkippableFact]
    public async Task Inspect_ResolvesIndexAndRunsInspectAction()
    {
        // Use a typed object whose properties are unambiguous in the output.
        var (exitCode, stdout, _) = await RunAsync(
            "[pscustomobject]@{ Name='alpha'; Value=42 }, [pscustomobject]@{ Name='beta'; Value=99 } | browse -Inspect 1");

        Assert.Equal(0, exitCode);
        Assert.Contains("beta", stdout);
        Assert.Contains("99", stdout);
        Assert.DoesNotContain("alpha", stdout);
    }

    // ── -Exec: $1 / $items bindings ─────────────────────────────────────────

    [SkippableFact]
    public async Task Exec_DollarOneBindsCurrentObject()
    {
        var (exitCode, stdout, _) = await RunAsync(
            "'a','bb','ccc' | browse -Select 1 -Exec '$1.Length'");

        Assert.Equal(0, exitCode);
        Assert.Equal("2", stdout.Trim());
    }

    [SkippableFact]
    public async Task Exec_DollarItemsBindsSelectedSet()
    {
        var (exitCode, stdout, _) = await RunAsync(
            "1..5 | browse -Select 0,2,4 -Exec '($items | Measure-Object -Sum).Sum'");

        Assert.Equal(0, exitCode);
        Assert.Equal("9", stdout.Trim());
    }

    // ── -Action: unknown name + destructive gate ────────────────────────────

    [SkippableFact]
    public async Task Action_UnknownName_ErrorsCleanly()
    {
        var (exitCode, stdout, stderr) = await RunAsync(
            "1..3 | browse -Select 0 -Action does-not-exist");

        Assert.NotEqual(0, exitCode);
        var combined = stdout + stderr;
        Assert.Contains("does-not-exist", combined);
        Assert.Contains("not available", combined);
    }

    [SkippableFact]
    public async Task DestructiveAction_WithoutForce_ReturnsSafetyGatePreview()
    {
        // Use a Process object so the 'process' adapter (with destructive
        // 'stop' action) is selected. We don't want to actually kill anything;
        // the gate must intercept.
        var (exitCode, stdout, _) = await RunAsync(
            "Get-Process -Id $PID | browse -Select 0 -Action stop");

        Assert.Equal(0, exitCode);
        // The gate object includes the message string ("browse: action:stop requires -Force"
        // per New-BrowseSafetyPreview) and the resolved adapter target text.
        Assert.Contains("requires -Force", stdout);
        Assert.Contains("stop", stdout);
    }

    // ── Mode resolution: redirected stdin still emits rows in -List ─────────

    [SkippableFact]
    public async Task List_FromMultilinePipeline_PreservesOrderAndCount()
    {
        var (exitCode, stdout, _) = await RunAsync("1..10 | browse -List");

        Assert.Equal(0, exitCode);
        var nonEmpty = stdout
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Header(s) + 10 data rows + possible separator line; at minimum we
        // expect 10 lines that look like data.
        Assert.True(nonEmpty.Length >= 10,
            $"Expected >=10 output lines, got {nonEmpty.Length}:\n{stdout}");
    }

    // ── Interactive REPL: ll | browse must not run an unbounded loop ────────

    // Pairs with NoArgs_OnFiniteInput_TerminatesWithBoundedOutput, but exercises
    // the actual interactive shell path that the user reported the bug from.
    // Sends `1..3 | browse` then `exit` and verifies the shell reaches the next
    // prompt within a short bound and that the captured output between prompts
    // is also bounded.
    [SkippableFact]
    public async Task InteractiveRepl_PipeIntoBrowse_DoesNotRunUnboundedLoop()
    {
        var psBashPath = InteractiveShellHarness.FindPsBashBinary();
        Skip.IfNot(psBashPath is not null, "ps-bash binary not found");

        await using var harness = await InteractiveShellHarness.StartAsync(psBashPath!, noProfile: true);

        await harness.SendLineAsync("1..3 | browse");

        // If the bug is present, the next prompt never returns because
        // Invoke-BrowseInteractive loops forever calling Read-Host. We cap
        // the wait at 8s — well below the bug's effectively-infinite duration.
        await harness.WaitForPromptAsync(TimeSpan.FromSeconds(8));

        var output = harness.ReadSinceLastPrompt();
        Assert.True(
            output.Length < 32 * 1024,
            $"Interactive `1..3 | browse` produced {output.Length} bytes of output before " +
            $"returning to the prompt; expected < 32 KB. Likely an unbounded loop.");

        await harness.SendLineAsync("exit 0");
    }
}
