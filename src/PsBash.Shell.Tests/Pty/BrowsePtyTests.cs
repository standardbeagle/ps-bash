using System.Runtime.InteropServices;
using Xunit;

namespace PsBash.Shell.Tests.Pty;

/// <summary>
/// PTY-11 end-to-end tests for the revived <c>Invoke-BrowseInteractive</c>
/// full-screen workbench, driven through <c>ps-bash -i</c> under a real
/// pseudo-terminal (PTY-8 <see cref="PtyHarness"/>).
///
/// <para>PTY-11 rewrote browse to read single keys with
/// <c>[Console]::ReadKey($true)</c> (no Enter, no echo) and to repaint only the
/// cursor / selection-mark cells with ANSI cursor movement instead of a
/// per-keystroke <c>Clear-Host</c> — the per-keystroke full redraw was the
/// original "<c>ll | browse</c> never-ending scroll of text" bug.</para>
///
/// <para><b>Why this is drivable now (vs the PtyTuiParityTests ReadKey skip).</b>
/// <c>PtyTuiParityTests.ConsoleReadKey_RawKeyCodesAreDelivered</c> is skipped
/// because there is no way to run a <i>raw PowerShell</i> <c>ReadKey</c> probe
/// <i>script</i> through the bash front-end. <c>Invoke-BrowseInteractive</c> is
/// different: it is module code reached via the <c>browse</c> alias, so its
/// <c>ReadKey</c> runs inside the host runspace on the live PTY slave — the same
/// raw-input path <c>vim</c> uses, and <c>Vim_EditAndSave</c> proves that path
/// delivers keystrokes.</para>
///
/// <para><b>Determinism (Directive 6).</b> Every wait is
/// <see cref="PtyHarness.WaitForRegexAsync"/> against a bounded deadline — no
/// <c>Thread.Sleep</c>, no <c>Task.Delay</c>. Serialized into the
/// <c>PtyHarness</c> collection so PTY allocations do not contend.</para>
/// </summary>
[Collection("PtyHarness")]
public class BrowsePtyTests
{
    // 10s, matching PtyTuiParityTests: this assembly has a documented heavy
    // parallel-process-spawn baseline. Still a hard bound — WaitForRegexAsync
    // returns the instant the pattern matches.
    private static readonly TimeSpan BrowseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Drive the workbench with the task's <c>n n s i q</c> key sequence:
    /// move the cursor down twice, toggle selection on the third row, inspect
    /// it, dismiss the inspect view, and quit. Asserts the partial-redraw cells
    /// rendered the selection mark at the cursor's row, that the inspect action
    /// ran, and that browse exited cleanly back to the shell prompt (no spin,
    /// no scroll garbage).
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Browse_NavigateSelectInspectQuit_RendersSelectionAndExitsClean()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "POSIX-only — Windows ConPTY runtime verification is CI-gated");

        var psBash = PtyHarness.FindPsBashBinary();
        Skip.If(psBash is null, "ps-bash launcher binary not found — build src/PsBash.Shell first");

        await using var harness = await PtyHarness.StartAsync(psBash!);

        // Pipe five objects into the interactive workbench. browse takes over
        // the screen with a full draw (ESC[2J erase-display) and a header line.
        await harness.WriteKeysAsync("seq 1 5 | browse\n");
        await harness.WaitForRegexAsync(@"browse: 5 object\(s\)", BrowseTimeout);
        // The full draw issued the erase-display escape — proof browse owns the
        // screen and did NOT fall through to the per-line scrolling path.
        await harness.WaitForRegexAsync(@"\x1b\[2J", BrowseTimeout, raw: true);

        // n, n: single-key navigation moves the cursor from row index 0 to
        // index 2. Each move is a partial redraw — browse writes the two
        // leading cells (cursor mark + selection mark) at the affected
        // terminal rows via ESC[<row>;1H, NOT a full repaint.
        await harness.WriteKeysAsync("n");
        await harness.WriteKeysAsync("n");

        // s: toggle selection on the current row (index 2). The selection mark
        // cell for that row becomes '*'. List item index 2 renders on terminal
        // row 4 (header is row 1, item index i is row i+2), so the cell repaint
        // is ESC[4;1H>* — cursor mark '>' plus selection mark '*'.
        await harness.WriteKeysAsync("s");
        await harness.WaitForRegexAsync(@"\x1b\[4;1H>\*", BrowseTimeout, raw: true);

        // i: inspect the current row. The default adapter's inspect action
        // pipes the object through Select-Object *, and browse prints a
        // "press any key to continue" footer before waiting on ReadKey.
        await harness.WriteKeysAsync("i");
        await harness.WaitForRegexAsync(@"press any key to continue", BrowseTimeout);

        // Any key dismisses the inspect view; browse redraws the workbench.
        await harness.WriteKeysAsync(" ");
        await harness.WaitForRegexAsync(@"browse: 5 object\(s\)", BrowseTimeout);

        // q: quit the workbench. browse returns and the shell prompt renders —
        // a clean exit, not a spin.
        await harness.WriteKeysAsync("q");
        await harness.WaitForRegexAsync(PtyHarness.PromptPattern, BrowseTimeout);

        // The shell is still responsive after browse exited.
        await harness.WriteKeysAsync("echo browse_exited_ok\n");
        await harness.WaitForRegexAsync(@"browse_exited_ok", BrowseTimeout);
    }

    // The EOF / non-PTY misroute guard inside Invoke-BrowseInteractive
    // ([Console]::IsInputRedirected -> Write-Error + return) is not driven from
    // here: under the PTY harness the host's process stdin is the live PTY
    // slave, never redirected, so the guard branch is unreachable from an
    // interactive ps-bash session. Invoke-BashBrowse's own IsInputRedirected
    // gate already routes redirected-stdin invocations to the non-interactive
    // list path before Invoke-BrowseInteractive is ever called; the internal
    // guard is defense-in-depth for any other direct caller. Per QA-rubric
    // Directive 5 this is a documented justified omission, not a silent no-op.
}
