using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle tests for redirection features (DART-2hcBeDLXOvxH).
///
/// Each test runs the script in real bash AND ps-bash and diffs bytes.
/// Tests skip when no bash oracle is available (e.g. Windows without WSL).
///
/// Failure-surface axes targeted (Directive 3):
///   Axis  1: empty input (>/dev/null discard)
///   Axis  8: exit-code propagation (stderr redirect + $?)
///   Axis  9: stderr interleave (2>&amp;1, |&amp;)
///   Axis 12: quoting / injection (redirect target with variable expansion)
///   Axis 14: missing target (command not found -> stderr redirect)
/// </summary>
public class RedirectionDifferentialTests
{
    // -----------------------------------------------------------------------
    // Test 1: stdout to file (>) and cat back
    //
    // Failure surface: Axis 12 (quoting), EmitRedirect > path.
    // Verifies: Invoke-BashRedirect writes file; cat reads it back.
    // Uses bash PID ($$) to make temp file unique within the script.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stdout redirect (>) writes content to a temp file; cat reads it back.
    /// Covers: EmitRedirect ">" path -> Invoke-BashRedirect, TransformRedirectTarget.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_StdoutToFile_EchoCatRoundtrip()
    {
        await AssertOracle.EqualAsync(
            "tmpf=\"/tmp/psbash_redir_$$\"; echo hello > \"$tmpf\"; cat \"$tmpf\"; rm -f \"$tmpf\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 2: append redirect (>>)
    //
    // Failure surface: Axis 8 (exit code), EmitRedirect ">>" path.
    // Verifies two appends produce two lines in correct order.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Append redirect (>>) accumulates lines; both lines must appear in order.
    /// Covers: EmitRedirect ">>" path -> Invoke-BashRedirect -Append.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_AppendToFile_TwoLineOutput()
    {
        await AssertOracle.EqualAsync(
            "tmpf=\"/tmp/psbash_append_$$\"; echo a > \"$tmpf\"; echo b >> \"$tmpf\"; cat \"$tmpf\"; rm -f \"$tmpf\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 3: stdout to /dev/null discards output
    //
    // Failure surface: Axis 1 (empty output), TransformRedirectTarget /dev/null -> $null.
    // Verifies: redirecting to /dev/null produces no stdout; following echo still appears.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Redirecting stdout to /dev/null discards the output entirely.
    /// Covers: TransformRedirectTarget /dev/null -> $null path.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_DevNull_DiscardsOutput()
    {
        await AssertOracle.EqualAsync(
            "echo suppressed > /dev/null; echo done",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 4: stderr redirect (2>/dev/null) — command not found, exit code 127
    //
    // Failure surface: Axis 14 (missing target), Axis 8 (exit code).
    // Verifies: stderr from unknown command is suppressed; $? is non-zero.
    // Note: bash exits 127 for command not found; ps-bash should match.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stderr redirect suppresses the "command not found" error message.
    /// Exit code must be non-zero (127 in bash; ps-bash should match or be non-zero).
    /// Covers: 2>/dev/null IoNumber reclassification + EmitRedirect fd=2.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_Stderr_DevNull_SuppressesError()
    {
        await AssertOracle.EqualAsync(
            "__psbash_no_such_cmd_xyz__ 2>/dev/null; echo $?",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 5: here-string (<<<) fed to cat
    //
    // Failure surface: Axis 12 (quoting), TLess token, HereDoc with Expand=true.
    // Verifies: cat <<< "text" emits the text with a trailing newline.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Here-string feeds a quoted string directly to cat's stdin.
    /// Covers: TLess token -> HereDoc path in parser + emitter heredoc pipe.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_HereString_CatHello()
    {
        await AssertOracle.EqualAsync(
            "cat <<< \"hello\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 6: heredoc expanding (<<EOF) with $USER variable
    //
    // Failure surface: Axis 12 (variable expansion inside heredoc body).
    // TranslateHereDocVars must convert $USER -> $env:USER.
    // Use GoldenAsync because $USER is platform-specific.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Expanding heredoc substitutes $USER in the body.
    /// Uses golden mode because $USER value is machine-specific.
    /// Record with: UPDATE_GOLDENS=1 ./scripts/test.sh --filter Differential_Redirect_Heredoc_Expanding
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_Heredoc_Expanding_VarSubstitution()
    {
        await AssertOracle.GoldenAsync(
            "cat <<EOF\nhello $USER\nEOF",
            "Redirect_Heredoc_Expanding_VarSubstitution",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 7: heredoc literal (<<'EOF') — no variable expansion
    //
    // Failure surface: Axis 12 (quoting — literal $USER must not be expanded).
    // Verifies: quoted delimiter suppresses variable expansion in body.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Literal heredoc (single-quoted delimiter) does not expand $USER.
    /// Covers: Expand=false HereDoc path -> @' ... '@ PS here-string.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_Heredoc_Literal_NoVarExpansion()
    {
        await AssertOracle.EqualAsync(
            "cat <<'EOF'\n$USER\nEOF",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 8: 2>&1 — stderr merged into stdout, captured by pipeline
    //
    // Failure surface: Axis 9 (stderr interleave), EmitPipeline |& path.
    // Verifies: echo err >&2 2>&1 | cat delivers "err" on stdout of cat.
    // -----------------------------------------------------------------------

    /// <summary>
    /// stderr (>&2) merged with stdout (2>&amp;1) and piped to cat.
    /// The string "err" must appear on cat's stdout.
    /// Covers: EmitSimple stderrRedirect + 2>&amp;1 pass-through.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_StderrToStdout_PipedToCat()
    {
        await AssertOracle.EqualAsync(
            "echo err >&2 2>&1 | cat",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 9: redirect target with variable expansion (>$logfile)
    //
    // Failure surface: Axis 12 (variable as redirect target).
    // Verifies: EmitWord on redirect target expands the variable correctly.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Redirect target is a variable; the file must be created at the expanded path.
    /// Covers: EmitWord on redirect target CompoundWord with SimpleVarSub.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_VariableTarget_WritesCorrectFile()
    {
        await AssertOracle.EqualAsync(
            "logfile=\"/tmp/psbash_var_$$\"; echo content > \"$logfile\"; cat \"$logfile\"; rm -f \"$logfile\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 10: stdin redirect (< file)
    //
    // Failure surface: Axis 12 (file path quoting), EmitSimple inputRedirect path.
    // Verifies: Get-Content file | cmd pattern works for simple stdin redirect.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stdin redirect (< file) feeds file content to cat via Get-Content | cmd.
    /// Covers: EmitSimple inputRedirect -> "Get-Content path | cmd".
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_StdinFromFile_CatReadsIt()
    {
        await AssertOracle.EqualAsync(
            "tmpf=\"/tmp/psbash_stdin_$$\"; echo world > \"$tmpf\"; cat < \"$tmpf\"; rm -f \"$tmpf\"",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 11: IoNumber adjacency — 2>file vs 2 >file
    //
    // Failure surface: Axis 8 (exit code), IoNumber reclassification edge case.
    // "2>file" — 2 is IoNumber (fd 2 redirect); no space.
    // "2 >file" — 2 is a Word arg to the command; > is stdout redirect.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adjacent 2> (no space) redirects fd 2 (stderr); output to /dev/null suppresses it.
    /// Covers: TryReclassifyIoNumber adjacency check in BashLexer.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_IoNumber_Adjacent_IsStderrRedirect()
    {
        await AssertOracle.EqualAsync(
            "__psbash_no_such_xyz__ 2>/dev/null; echo after",
            timeout: TimeSpan.FromSeconds(15));
    }

    // -----------------------------------------------------------------------
    // Test 12: redirect on brace group ({ ...; } > file)
    //
    // Failure surface: Axis 12 (compound command redirect), EmitSubshell redirect path.
    // Verifies: stdout redirect on a brace group captures all inner output.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Redirect on a brace group captures all lines from the group body.
    /// Covers: EmitSubshell redirect -> Invoke-BashRedirect pipe.
    /// Note: bash uses brace group { }; ps-bash wraps in try { Push-Location; ... }.
    /// </summary>
    [SkippableFact]
    public async Task Differential_Redirect_BraceGroup_StdoutToFile()
    {
        await AssertOracle.EqualAsync(
            "tmpf=\"/tmp/psbash_brace_$$\"; { echo line1; echo line2; } > \"$tmpf\"; cat \"$tmpf\"; rm -f \"$tmpf\"",
            timeout: TimeSpan.FromSeconds(15));
    }
}
