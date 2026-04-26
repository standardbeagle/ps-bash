using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Tests that the interactive shell shows a PS2 continuation prompt when a
/// bash statement is incomplete, and buffers input until the statement is
/// complete before executing.
///
/// Hand-written asserts justified: behaviour is ps-bash-specific (REPL
/// buffering, PS2 prompt detection); no bash oracle available for interactive
/// TTY prompts.
///
/// Directive 6 env isolation is provided by <see cref="InteractiveShellHarness.StartAsync"/>.
/// </summary>
[Trait("Category", "Integration")]
public class MultiLineContinuationTests
{
    private static readonly string? PsBashPath = InteractiveShellHarness.FindPsBashBinary();

    private static string? FindPwsh()
    {
        try { return PwshLocator.Locate(); }
        catch (PwshNotFoundException) { return null; }
    }

    private static readonly string? PwshPath = FindPwsh();

    private static string? FindWorkerScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "ps-bash-worker.ps1");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static readonly string? WorkerScript = FindWorkerScript();

    private bool CanRun => PsBashPath is not null && PwshPath is not null;

    private async Task<InteractiveShellHarness> StartAsync()
    {
        return await InteractiveShellHarness.StartAsync(
            PsBashPath!,
            workerScript: WorkerScript,
            noProfile: true);
    }

    // ── Test 1: Pipe at end of line ──────────────────────────────────────────

    /// <summary>
    /// <c>echo hello |</c> is incomplete: shell should show PS2.
    /// After <c>cat</c> is sent on the next line, the full pipeline executes
    /// and outputs "hello".
    /// </summary>
    [SkippableFact]
    public async Task MultiLine_TrailingPipe_ShowsPS2ThenExecutes()
    {
        Skip.IfNot(CanRun, "ps-bash binary or pwsh not found");

        await using var harness = await StartAsync();

        // Send incomplete first line.
        await harness.SendLineAsync("echo hello |");

        // Shell should show PS2 ("> "), not execute yet.
        var isPs2 = await harness.WaitForAnyPromptAsync();
        Assert.True(isPs2, "Expected PS2 prompt after trailing pipe");

        // Complete the pipeline.
        await harness.SendLineAsync("cat");
        // Use a longer timeout for the final prompt so the command has time to execute.
        await harness.WaitForPromptAsync(TimeSpan.FromSeconds(10));

        var output = harness.ReadSinceLastPrompt()
            .Replace("\r\n", "\n");
        var stderr = harness.Stderr;

        // Diagnostic: include output details in assertion failure message.
        Assert.True(output.Contains("hello"),
            $"Expected 'hello' in output. Got: '{output.Trim()}'. Stderr: '{stderr}'");
    }

    // ── Test 2: && at end of line ────────────────────────────────────────────

    /// <summary>
    /// <c>true &amp;&amp;</c> at end of line is incomplete: shell should show PS2.
    /// After <c>echo yes</c> is sent, the and-or executes and outputs "yes".
    /// </summary>
    [SkippableFact]
    public async Task MultiLine_TrailingAndAnd_ShowsPS2ThenExecutes()
    {
        Skip.IfNot(CanRun, "ps-bash binary or pwsh not found");

        await using var harness = await StartAsync();

        await harness.SendLineAsync("true &&");

        var isPs2 = await harness.WaitForAnyPromptAsync();
        Assert.True(isPs2, "Expected PS2 prompt after trailing &&");

        await harness.SendLineAsync("echo yes");
        await harness.WaitForPromptAsync();

        var output = harness.ReadSinceLastPrompt()
            .Replace("\r\n", "\n")
            .Trim();

        Assert.Contains("yes", output);
    }

    // ── Test 3: Unclosed if block ────────────────────────────────────────────

    /// <summary>
    /// Sending <c>if true</c> then <c>then</c> then <c>echo ok</c> then <c>fi</c>
    /// across multiple lines buffers and executes once complete, outputting "ok".
    /// </summary>
    [SkippableFact]
    public async Task MultiLine_UnclosedIf_BuffersUntilFi()
    {
        Skip.IfNot(CanRun, "ps-bash binary or pwsh not found");

        await using var harness = await StartAsync();

        await harness.SendLineAsync("if true");
        var isPs2 = await harness.WaitForAnyPromptAsync();
        Assert.True(isPs2, "Expected PS2 after 'if true'");

        await harness.SendLineAsync("then");
        isPs2 = await harness.WaitForAnyPromptAsync();
        Assert.True(isPs2, "Expected PS2 after 'then'");

        await harness.SendLineAsync("echo ok");
        isPs2 = await harness.WaitForAnyPromptAsync();
        Assert.True(isPs2, "Expected PS2 after 'echo ok' inside if body");

        await harness.SendLineAsync("fi");
        await harness.WaitForPromptAsync();

        var output = harness.ReadSinceLastPrompt()
            .Replace("\r\n", "\n")
            .Trim();

        Assert.Contains("ok", output);
    }

    // ── Test 4: Unclosed brace group ────────────────────────────────────────

    /// <summary>
    /// <c>{ echo a;</c> is incomplete: shell shows PS2.
    /// After <c>echo b; }</c> is sent, the brace group executes and outputs both "a" and "b".
    /// </summary>
    [SkippableFact]
    public async Task MultiLine_UnclosedBraceGroup_BuffersUntilClose()
    {
        Skip.IfNot(CanRun, "ps-bash binary or pwsh not found");

        await using var harness = await StartAsync();

        await harness.SendLineAsync("{ echo a;");
        var isPs2 = await harness.WaitForAnyPromptAsync();
        Assert.True(isPs2, "Expected PS2 after unclosed brace group");

        await harness.SendLineAsync("echo b; }");
        await harness.WaitForPromptAsync();

        var output = harness.ReadSinceLastPrompt()
            .Replace("\r\n", "\n")
            .Trim();

        Assert.Contains("a", output);
        Assert.Contains("b", output);
    }

    // ── Test 5: PS2 prompt is visible ───────────────────────────────────────

    /// <summary>
    /// After an incomplete line, WaitForAnyPromptAsync returns true (PS2 seen).
    /// This verifies the harness correctly detects the "> " PS2 string set by
    /// Directive 6 env vars.
    /// </summary>
    [SkippableFact]
    public async Task MultiLine_PS2PromptVisible_AfterIncompleteInput()
    {
        Skip.IfNot(CanRun, "ps-bash binary or pwsh not found");

        await using var harness = await StartAsync();

        // An unclosed single quote makes the input incomplete.
        await harness.SendLineAsync("echo 'hello");
        var isPs2 = await harness.WaitForAnyPromptAsync();

        Assert.True(isPs2,
            $"Expected PS2 prompt ('{InteractiveShellHarness.Ps2Value}') " +
            "but saw PS1 or timeout after incomplete input");
    }
}
