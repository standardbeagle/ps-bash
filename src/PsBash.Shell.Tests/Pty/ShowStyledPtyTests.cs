using System.Runtime.InteropServices;
using Xunit;

namespace PsBash.Shell.Tests.Pty;

/// <summary>
/// End-to-end tests for the interactive <c>Show-Styled</c> viewer driven through <c>ps-bash -i</c>
/// under a real pseudo-terminal (<see cref="PtyHarness"/>). The viewer projects a Strata node tree
/// via Terminal.Gui v2; these prove its console driver initializes over the live PTY slave — the
/// same raw-terminal path <c>browse</c> / <c>vim</c> use (see <see cref="BrowsePtyTests"/>) — and
/// that the full-screen window draws and exits cleanly back to the shell prompt.
///
/// <para>The interactive window title contains the literal "Enter expands", which the headless
/// fallback summary ("Show-Styled (headless): …") does NOT — so a match on it confirms the live TUI
/// ran, not the redirected-I/O fallback.</para>
///
/// <para><b>Determinism (Directive 6).</b> Every wait is <see cref="PtyHarness.WaitForRegexAsync"/>
/// against a bounded deadline — no <c>Thread.Sleep</c> / <c>Task.Delay</c>. Serialized into the
/// <c>PtyHarness</c> collection so PTY allocations do not contend.</para>
/// </summary>
[Collection("PtyHarness")]
public class ShowStyledPtyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Pipe five objects into <c>Show-Styled</c>: it takes over the screen (Terminal.Gui draws a
    /// titled window), Enter expands the focused row's detail, and q returns to a clean prompt.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task ShowStyled_DrawsInteractiveWindowAndExitsClean()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "POSIX-only — Windows ConPTY runtime verification is CI-gated");

        var psBash = PtyHarness.FindPsBashBinary();
        Skip.If(psBash is null, "ps-bash launcher binary not found — build src/PsBash.Shell first");

        await using var harness = await PtyHarness.StartAsync(psBash!);

        // seq emits five BashObjects; Show-Styled styles them via the auto-picked `object` sheet and
        // enters the interactive viewer (the host's stdout is the live PTY slave, so it is not
        // redirected and the headless branch is skipped).
        await harness.WriteKeysAsync("seq 1 5 | Show-Styled\n");

        // The viewer's footer drew — proof the interactive Console.ReadKey + Spectre loop is running.
        // "Enter expand" is in the footer only (never the headless summary "… interactive viewer"),
        // so it distinguishes the live viewer from the redirected-I/O fallback. The "[1/5]" position
        // line confirms all five rows are in the focus ring.
        await harness.WaitForRegexAsync(@"\[1/5\].*Enter expand", Timeout);

        // q quits the viewer; it leaves the alternate screen and the shell prompt returns.
        await harness.WriteKeysAsync("q");
        await harness.WaitForRegexAsync(PtyHarness.PromptPattern, Timeout);

        // The shell is still responsive after the viewer exits — the viewer runs in process via
        // Console.ReadKey (the same terminal path the line editor uses, exactly like `browse`), so it
        // never leaves the host's stdin in a bad state. This is the clean-exit contract; an
        // in-process Terminal.Gui session would leave stdin dead and swallow this echo.
        await harness.WriteKeysAsync("echo show_styled_exited_ok\n");
        await harness.WaitForRegexAsync(@"show_styled_exited_ok", Timeout);
    }

    /// <summary>
    /// With <c>PSBASH_DEFAULT_FORMAT=interactive</c>, native PowerShell object output is routed
    /// straight into the Show-Styled viewer by the SdkWorker — no explicit <c>| Show-Styled</c>. The
    /// viewer takes over, q returns to a responsive shell.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task InteractiveDefault_RoutesObjectOutputIntoViewer()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "POSIX-only — Windows ConPTY runtime verification is CI-gated");

        var psBash = PtyHarness.FindPsBashBinary();
        Skip.If(psBash is null, "ps-bash launcher binary not found — build src/PsBash.Shell first");

        await using var harness = await PtyHarness.StartAsync(psBash!);

        // Opt into the interactive default, then emit two native PSObjects with NO `| Show-Styled`.
        await harness.WriteKeysAsync("export PSBASH_DEFAULT_FORMAT=interactive\n");
        await harness.WaitForRegexAsync(PtyHarness.PromptPattern, Timeout);
        await harness.WriteKeysAsync("Get-Process | Select-Object -First 2 Name,Id\n");

        // The SdkWorker buffered the two objects at end-of-pipeline and handed them to Show-Styled,
        // which drew the viewer — the "[1/2]" position + footer is the proof it routed to the viewer.
        await harness.WaitForRegexAsync(@"\[1/2\].*Enter expand", Timeout);

        await harness.WriteKeysAsync("q");
        await harness.WaitForRegexAsync(PtyHarness.PromptPattern, Timeout);

        // Shell responsive after the viewer exits.
        await harness.WriteKeysAsync("echo interactive_default_ok\n");
        await harness.WaitForRegexAsync(@"interactive_default_ok", Timeout);
    }
}
