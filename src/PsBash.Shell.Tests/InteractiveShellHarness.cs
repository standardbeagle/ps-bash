using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// Spawns ps-bash in interactive mode with stdin/stdout pipes, providing a
/// synchronous send/receive API for integration tests.
///
/// Process spawn contract: every wait uses a configurable timeout, and a
/// finally block kills the entire process tree to prevent leaks. No Sleep().
///
/// Env isolation (Directive 6):
///   TERM=xterm-256color, LANG=C.UTF-8, COLUMNS=120, LINES=40
///   PROMPT_COMMAND=  (empty), PS1=PSBASH$ , PS2=>
///   HOME -> temp dir, PSBASH_HISTORY_PATH -> temp file
///   No .psbashrc unless test opts in via NoProfile=false
/// </summary>
internal sealed class InteractiveShellHarness : IAsyncDisposable
{
    // PS1 injected into the child process. GetPS1Async trims the value before
    // using it with ExpandPS1, so trailing spaces are lost. Use a prompt that
    // survives trim and is still unique enough to detect reliably.
    public static readonly string Ps1Value = "PSBASH> ";
    public static readonly string PromptString = "PSBASH>";
    private static readonly Regex PromptRegex = new(Regex.Escape(PromptString), RegexOptions.Compiled);

    // Per-command prompt wait. A command's round-trip goes through the IPC socket to
    // the SDK host, so on a machine running several shell-spawning suites at once it
    // can exceed a tight bound — 8 s produced a recurring family of one-off
    // "WaitForPromptAsync timed out after 8.0s / stdout received (0 chars)" failures
    // across DIFFERENT interactive tests, every one of which passed when re-run alone.
    //
    // A timeout only bounds how long a genuine failure takes to REPORT; it is not an
    // assertion, and the harness dumps a full transcript when it fires. So the bound is
    // generous, with the same env override the cold-start timeout below already uses.
    public static readonly TimeSpan DefaultPromptTimeout = TimeSpan.FromSeconds(
        int.TryParse(
            Environment.GetEnvironmentVariable("PSBASH_TEST_PROMPT_TIMEOUT_SEC"),
            out var promptTimeoutSec) && promptTimeoutSec > 0
            ? promptTimeoutSec
            : 25);
    // Cold-start: spawn ps-bash, locate + spawn the host child, open the IPC
    // socket, extract the embedded PowerShell module, import it, and emit the
    // first PS1. Under parallel test load on slow Linux CI runners (multiple
    // shell-spawning tests racing for CPU + disk) this can exceed 15 s. Allow
    // an env override (PSBASH_TEST_START_TIMEOUT_SEC) so a slow CI lane can
    // dial it up without recompiling.
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(
        int.TryParse(
            Environment.GetEnvironmentVariable("PSBASH_TEST_START_TIMEOUT_SEC"),
            out var startTimeoutSec) && startTimeoutSec > 0
            ? startTimeoutSec
            : 30);

    // A caller may RAISE the cold-start bound, never shorten it: PromptRenderingIntegrationTests passed 15 s, under the
    // 30 s default and past the PSBASH_TEST_START_TIMEOUT_SEC knob, and a loaded fleet host (s22a) timed out the start
    // of a correct shell. One bound for the whole suite (memory: flake family = one harness bound).
    internal static TimeSpan StartBound(TimeSpan? requested)
        => requested is { } t && t > DefaultStartTimeout ? t : DefaultStartTimeout;

    private readonly Process _process;
    private readonly StringBuilder _sinceLastPrompt = new();
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrReader;
    private readonly string _tempHome;
    private readonly string _tempHistoryFile;
    // When false the caller owns the temp home directory and DisposeAsync must
    // not delete it.
    private readonly bool _ownsTempHome;

    // Prevents multiple disposes racing each other.
    private int _disposed;

    private InteractiveShellHarness(
        Process process,
        Task stderrReader,
        string tempHome,
        string tempHistoryFile,
        bool ownsTempHome = true)
    {
        _process = process;
        _stderrReader = stderrReader;
        _tempHome = tempHome;
        _tempHistoryFile = tempHistoryFile;
        _ownsTempHome = ownsTempHome;
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Locates the ps-bash binary the same way <see cref="ProcessLifecycleTests"/> does:
    /// looks for ps-bash.exe / ps-bash in a bin/ directory under src/PsBash.Shell.
    /// Returns null when the binary is not found (so callers can use SkippableFact).
    /// </summary>
    public static string? FindPsBashBinary()
    {
        // Walk up from the test assembly base dir to find the repo root, then
        // locate the built binary under src/PsBash.Shell/bin/.
        // On Windows prefer ps-bash.exe; on other platforms prefer the ELF binary.
        var preferExe = OperatingSystem.IsWindows();

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var binDir = Path.Combine(dir.FullName, "src", "PsBash.Shell", "bin");
            if (Directory.Exists(binDir))
            {
                string? fallback = null;
                string? configFallback = null;
                var currentConfig = AppContext.BaseDirectory.Contains(
                    $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Release"
                    : AppContext.BaseDirectory.Contains(
                        $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase)
                        ? "Debug"
                        : null;

                foreach (var exe in Directory.EnumerateFiles(binDir, "ps-bash*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(exe);
                    var isExe = string.Equals(name, "ps-bash.exe", StringComparison.OrdinalIgnoreCase);
                    var isNoExt = string.Equals(name, "ps-bash", StringComparison.OrdinalIgnoreCase);

                    if (!isExe && !isNoExt)
                        continue;

                    var matchesConfig = currentConfig is not null
                        && exe.Contains(
                            $"{Path.DirectorySeparatorChar}{currentConfig}{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase);

                    if (matchesConfig && preferExe && isExe)
                        return exe;
                    if (matchesConfig && !preferExe && isNoExt)
                        return exe;

                    if (matchesConfig)
                        configFallback ??= exe;

                    // Keep as fallback if we don't find the preferred form.
                    fallback ??= exe;
                }
                if (configFallback is not null)
                    return configFallback;
                if (fallback is not null)
                    return fallback;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Creates and starts a harness. Waits for the first prompt before returning.
    /// </summary>
    /// <param name="psBashPath">Path to the ps-bash binary (from <see cref="FindPsBashBinary"/>).</param>
    /// <param name="noProfile">When true, passes --norc so .psbashrc is not sourced.</param>
    /// <param name="startTimeout">How long to wait for the initial prompt.</param>
    /// <param name="psBashHome">
    /// Optional pre-created home directory.  When supplied, the harness uses this
    /// directory as HOME and PSBASH_HOME instead of creating a new temp directory.
    /// The caller is responsible for creating the directory and populating any rc
    /// files before calling StartAsync.  The harness will NOT delete this directory
    /// on dispose — the caller owns the lifecycle.  Use this for profile-loading
    /// tests that need .psbashrc to exist before the shell starts.
    /// </param>
    public static async Task<InteractiveShellHarness> StartAsync(
        string psBashPath,
        bool noProfile = true,
        TimeSpan? startTimeout = null,
        string? psBashHome = null)
    {
        var ownsTempHome = psBashHome is null;
        var tempHome = psBashHome
            ?? Path.Combine(Path.GetTempPath(), "ps-bash-harness-" + Guid.NewGuid().ToString("N"));
        if (ownsTempHome)
            Directory.CreateDirectory(tempHome);
        var tempHistoryFile = Path.Combine(tempHome, "history.db");

        var psi = new ProcessStartInfo
        {
            FileName = psBashPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
            RedirectStandardError = true, StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Directive 6 env vars — byte-stable prompt, no timing dependency.
        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment["LANG"] = "C.UTF-8";
        psi.Environment["COLUMNS"] = "120";
        psi.Environment["LINES"] = "40";
        // Drive the REPL (line editor) through the pipe: piped `-i` otherwise runs as PipedInteractive.
        psi.Environment["PSBASH_INTERACTIVE_REPL"] = "1";
        psi.Environment["PROMPT_COMMAND"] = "";
        psi.Environment["PS1"] = Ps1Value;
        psi.Environment["PS2"] = "> ";
        psi.Environment["HOME"] = tempHome;
        // On Windows bash `~` / $HOME follow USERPROFILE (SdkWorker.SyncHomeToEnvironment, PowerShell's own source),
        // not HOME: with HOME alone `cd ~/work/x` went to the developer's real profile, so a test passed only on a
        // machine where that directory happened to exist (fleet g13-g17: Interactive_CdHomeRelativeThenPwd).
        if (OperatingSystem.IsWindows())
            psi.Environment["USERPROFILE"] = tempHome;
        // PSBASH_HOME tells InteractiveShell.SourceRcFileAsync to look for
        // .psbashrc in the isolated temp directory rather than the real user
        // profile.  This keeps profile-loading tests hermetic.
        psi.Environment["PSBASH_HOME"] = tempHome;
        psi.Environment["PSBASH_HISTORY_PATH"] = tempHistoryFile;
        // Pin unix-paths off — when PSBASH_UNIX_PATHS=1 is inherited from the
        // test runner's shell (Git Bash on Windows commonly sets it), ps-bash
        // translates the Windows-style HOME we just set into a POSIX path
        // (`C:\Users\...\Temp\foo` -> `/tmp/foo`), breaking the
        // `reportedHome.StartsWith(Path.GetTempPath())` assertion in
        // Harness_IsolatesHome. Directive 6 requires deterministic env;
        // tests that specifically want unix-paths translation can override
        // this var after StartAsync.
        psi.Environment["PSBASH_UNIX_PATHS"] = "0";

        // Pass through PATH for commands launched by the shell.
        if (Environment.GetEnvironmentVariable("PATH") is { } path)
            psi.Environment["PATH"] = path;

        // Always pass -i (interactive) so the REPL starts even with stdin redirected.
        psi.ArgumentList.Add("-i");
        if (noProfile)
            psi.ArgumentList.Add("--norc");

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start ps-bash: {psBashPath}");

        // Drain stderr asynchronously so it never blocks the process.
        var stderrBuf = new StringBuilder();
        var stderrReader = Task.Run(async () =>
        {
            var buf = new char[256];
            while (true)
            {
                var n = await process.StandardError.ReadAsync(buf, 0, buf.Length);
                if (n == 0) break;
                lock (stderrBuf)
                    stderrBuf.Append(buf, 0, n);
            }
        });

        var harness = new InteractiveShellHarness(process, stderrReader, tempHome, tempHistoryFile, ownsTempHome);
        // Inject the shared stderr buffer reference.
        // We share the lock object — reassign here via reflection would be complex;
        // instead transfer the reference by reading during DrainStderr.
        harness._stderrSb = stderrBuf;

        // Wait for the initial prompt so callers know the shell is ready.
        try
        {
            await harness.WaitForPromptAsync(StartBound(startTimeout));

            // Startup type-ahead: the first prompt now appears BEFORE the runspace is executable
            // (the slow init runs in the background while the prompt accepts type-ahead). Warm it with
            // a no-op `true` and wait within the generous start timeout, so the runspace-init latency
            // is absorbed here — not by the tight per-command WaitForPromptAsync timeouts the tests use.
            await harness.SendLineAsync("true");
            await harness.WaitForPromptAsync(StartBound(startTimeout));
        }
        catch
        {
            // Start failed — kill and rethrow.
            await harness.DisposeAsync();
            throw;
        }

        return harness;
    }

    // The stderr buffer lives on the Task closure but we expose it here so
    // Stderr property can read it.
    private StringBuilder? _stderrSb;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all stderr output captured so far.
    /// </summary>
    public string Stderr
    {
        get
        {
            var sb = _stderrSb;
            if (sb is null) return "";
            lock (sb) return sb.ToString();
        }
    }

    /// <summary>
    /// Returns the temp HOME directory created for this harness instance.
    /// </summary>
    public string TempHome => _tempHome;

    /// <summary>
    /// Sends text followed by a newline to the shell's stdin.
    /// </summary>
    public async Task SendLineAsync(string text)
    {
        await _process.StandardInput.WriteLineAsync(text);
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>
    /// Sends raw bytes to stdin (for escape sequences, Ctrl-C, Tab, etc.).
    /// Example: <c>"\x03"</c> = Ctrl-C, <c>"\t"</c> = Tab.
    /// </summary>
    public async Task SendKeysAsync(string rawKeys)
    {
        await _process.StandardInput.WriteAsync(rawKeys);
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>
    /// Closes stdin, signalling EOF to the shell (equivalent to Ctrl-D on empty line).
    /// </summary>
    public void CloseStdin()
    {
        try { _process.StandardInput.Close(); } catch { }
    }

    /// <summary>
    /// Reads stdout until the prompt regex matches or the timeout fires.
    /// On success, <see cref="ReadSinceLastPrompt"/> returns the output before the prompt.
    /// On timeout, throws with a transcript dump.
    /// </summary>
    public async Task WaitForPromptAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultPromptTimeout;
        var (outcome, buf) = await ReadUntilAsync(
            _process.StandardOutput, EndsWithPs1, effectiveTimeout);
        if (outcome != WaitOutcome.Matched)
            ThrowTimeout(effectiveTimeout, buf.ToString(),
                outcome == WaitOutcome.Eof ? "stdout EOF (process may have exited)" : null);
        SetSinceLastPrompt(buf, PromptString.Length);
    }

    /// <summary>
    /// PS2 continuation prompt string set in Directive 6 env vars.
    /// </summary>
    public static readonly string Ps2Value = "> ";

    /// <summary>
    /// Waits for either the PS1 prompt (<c>PSBASH&gt;</c>) or the PS2 continuation
    /// prompt (<c>&gt; </c>) to appear on stdout.  Returns <c>true</c> if PS2 was
    /// seen, <c>false</c> if PS1 was seen.
    ///
    /// Used by multi-line continuation tests: send an incomplete line, call
    /// <see cref="WaitForPS2Async"/> to confirm the shell is buffering, then send the
    /// rest of the command and call <see cref="WaitForPromptAsync"/> for the final PS1.
    /// </summary>
    public async Task<bool> WaitForAnyPromptAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultPromptTimeout;
        var (outcome, buf) = await ReadUntilAsync(
            _process.StandardOutput, b => EndsWithPs1(b) || EndsWith(b, Ps2Value),
            effectiveTimeout);
        if (outcome != WaitOutcome.Matched)
            ThrowTimeout(effectiveTimeout, buf.ToString(),
                outcome == WaitOutcome.Eof ? "stdout EOF (process may have exited)" : null);

        // PS1 first: it is the longer, more specific marker.
        if (EndsWithPs1(buf))
        {
            SetSinceLastPrompt(buf, PromptString.Length);
            return false; // PS1 seen
        }
        SetSinceLastPrompt(buf, Ps2Value.Length);
        return true; // PS2 seen
    }

    internal enum WaitOutcome { Matched, TimedOut, Eof }

    /// <summary>
    /// Reads <paramref name="reader"/> one char at a time until <paramref name="isMatch"/>
    /// accepts the accumulated text, the reader hits EOF, or the deadline passes.
    ///
    /// The wait has ONE clock, a <see cref="PsBash.Testing.CheckpointDeadline"/>: when it passes,
    /// <paramref name="isStarved"/> decides whether it moves out another full window. The check is
    /// the same whether the deadline passes while a read is pending or between two chars, so a
    /// prompt that starts arriving inside an extension window is read to the end instead of
    /// failing on its first char (the "stdout received (1 chars) --- P" bug).
    /// </summary>
    internal static async Task<(WaitOutcome Outcome, StringBuilder Buffer)> ReadUntilAsync(
        TextReader reader,
        Func<StringBuilder, bool> isMatch,
        TimeSpan timeout,
        Func<Task<bool>>? isStarved = null)
    {
        var buf = new StringBuilder();
        var charBuf = new char[1];
        var deadline = new PsBash.Testing.CheckpointDeadline(timeout, isStarved);
        Task<int>? pending = null;

        while (true)
        {
            if (!await deadline.ContinueAsync())
                return (WaitOutcome.TimedOut, buf);

            // A read abandoned at a checkpoint stays pending and is awaited next round:
            // a reader must never have two reads in flight.
            pending ??= reader.ReadAsync(charBuf, 0, 1);
            if (!pending.IsCompleted)
            {
                using var delayCts = new CancellationTokenSource();
                var winner = await Task.WhenAny(pending, Task.Delay(deadline.Remaining, delayCts.Token));
                delayCts.Cancel();
                if (winner != pending)
                    continue; // deadline checkpoint
            }

            var n = await pending;
            pending = null;
            if (n == 0)
                return (WaitOutcome.Eof, buf);

            buf.Append(charBuf, 0, n);
            if (isMatch(buf))
                return (WaitOutcome.Matched, buf);
        }
    }

    private static bool EndsWithPs1(StringBuilder buf) => EndsWith(buf, PromptString);

    private static bool EndsWith(StringBuilder buf, string marker) =>
        buf.Length >= marker.Length
        && buf.ToString(buf.Length - marker.Length, marker.Length) == marker;

    // Stores the output before the prompt marker for ReadSinceLastPrompt.
    private void SetSinceLastPrompt(StringBuilder buf, int markerLength)
    {
        var content = buf.ToString(0, buf.Length - markerLength);
        lock (_sinceLastPrompt)
        {
            _sinceLastPrompt.Clear();
            _sinceLastPrompt.Append(content);
        }
    }

    /// <summary>
    /// Returns stdout output captured since the last <see cref="WaitForPromptAsync"/> call.
    /// The prompt itself is not included.
    /// </summary>
    public string ReadSinceLastPrompt()
    {
        lock (_sinceLastPrompt)
            return _sinceLastPrompt.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ThrowTimeout(TimeSpan timeout, string received, string? extra = null)
    {
        var exitInfo = _process.HasExited ? $"exit={_process.ExitCode}" : "still running";
        var stderrText = Stderr;
        throw new TimeoutException(
            $"WaitForPromptAsync timed out after {timeout.TotalSeconds:F1}s " +
            (extra is not null ? $"({extra})" : "") + "\n" +
            $"Process: {exitInfo}\n" +
            $"--- stdout received ({received.Length} chars) ---\n{received}\n" +
            $"--- stderr ---\n{stderrText}");
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Close stdin first so the shell sees EOF and can exit gracefully.
        try { _process.StandardInput.Close(); } catch { }

        // Give the process up to 5s to exit on its own.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) { }

        // Kill the entire tree if still running.
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { }

        // Drain the stderr reader task.
        try { await _stderrReader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

        _process.Dispose();

        // Clean up the temp directory only when this harness created it.
        // When psBashHome was supplied by the caller, the caller owns lifecycle.
        if (_ownsTempHome)
            try { Directory.Delete(_tempHome, recursive: true); } catch { }
    }
}
