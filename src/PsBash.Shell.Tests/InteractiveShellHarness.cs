using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core.Runtime;
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

    public static readonly TimeSpan DefaultPromptTimeout = TimeSpan.FromSeconds(5);

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
                foreach (var exe in Directory.EnumerateFiles(binDir, "ps-bash*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(exe);
                    var isExe = string.Equals(name, "ps-bash.exe", StringComparison.OrdinalIgnoreCase);
                    var isNoExt = string.Equals(name, "ps-bash", StringComparison.OrdinalIgnoreCase);

                    if (!isExe && !isNoExt)
                        continue;

                    if (preferExe && isExe)
                        return exe;
                    if (!preferExe && isNoExt)
                        return exe;

                    // Keep as fallback if we don't find the preferred form.
                    fallback ??= exe;
                }
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
    /// <param name="workerScript">Optional PSBASH_WORKER override (for dev builds that need the script).</param>
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
        string? workerScript = null,
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
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Directive 6 env vars — byte-stable prompt, no timing dependency.
        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment["LANG"] = "C.UTF-8";
        psi.Environment["COLUMNS"] = "120";
        psi.Environment["LINES"] = "40";
        psi.Environment["PROMPT_COMMAND"] = "";
        psi.Environment["PS1"] = Ps1Value;
        psi.Environment["PS2"] = "> ";
        psi.Environment["HOME"] = tempHome;
        // PSBASH_HOME tells InteractiveShell.SourceRcFileAsync to look for
        // .psbashrc in the isolated temp directory rather than the real user
        // profile.  This keeps profile-loading tests hermetic.
        psi.Environment["PSBASH_HOME"] = tempHome;
        psi.Environment["PSBASH_HISTORY_PATH"] = tempHistoryFile;

        // Pass through PATH so pwsh can be found.
        if (Environment.GetEnvironmentVariable("PATH") is { } path)
            psi.Environment["PATH"] = path;

        // Allow the test to point at the dev worker script.
        if (workerScript is not null)
            psi.Environment["PSBASH_WORKER"] = workerScript;

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
            await harness.WaitForPromptAsync(startTimeout ?? DefaultPromptTimeout);
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
        var deadline = DateTime.UtcNow + effectiveTimeout;

        var localBuf = new StringBuilder();

        // Read one character at a time so we can detect the prompt as soon as
        // it appears without any Sleep().
        var charBuf = new char[1];
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (true)
        {
            int n;
            try
            {
                // ReadAsync with cancellation so we get a clean timeout path.
                var readTask = _process.StandardOutput.ReadAsync(charBuf, 0, 1);
                var completed = await Task.WhenAny(readTask, Task.Delay(effectiveTimeout, cts.Token));

                if (completed != readTask)
                {
                    // Timeout branch.
                    ThrowTimeout(effectiveTimeout, localBuf.ToString());
                    return; // unreachable
                }

                n = await readTask;
            }
            catch (OperationCanceledException)
            {
                ThrowTimeout(effectiveTimeout, localBuf.ToString());
                return;
            }

            if (n == 0)
            {
                // EOF — process exited.
                ThrowTimeout(effectiveTimeout, localBuf.ToString(),
                    "stdout EOF (process may have exited)");
                return;
            }

            localBuf.Append(charBuf, 0, n);

            // Check if the accumulated buffer ends with the prompt string.
            if (localBuf.Length >= PromptString.Length)
            {
                var tail = localBuf.ToString(localBuf.Length - PromptString.Length, PromptString.Length);
                if (tail == PromptString)
                {
                    // Consume the output before the prompt into the since-last-prompt buffer.
                    var content = localBuf.ToString(0, localBuf.Length - PromptString.Length);
                    lock (_sinceLastPrompt)
                    {
                        _sinceLastPrompt.Clear();
                        _sinceLastPrompt.Append(content);
                    }
                    return;
                }
            }

            // Refresh deadline check — if total wall-clock exceeds timeout, abort.
            if (DateTime.UtcNow > deadline)
            {
                ThrowTimeout(effectiveTimeout, localBuf.ToString());
                return;
            }
        }
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
        var deadline = DateTime.UtcNow + effectiveTimeout;

        var localBuf = new StringBuilder();
        var charBuf = new char[1];
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (true)
        {
            int n;
            try
            {
                var readTask = _process.StandardOutput.ReadAsync(charBuf, 0, 1);
                var completed = await Task.WhenAny(readTask, Task.Delay(effectiveTimeout, cts.Token));
                if (completed != readTask)
                {
                    ThrowTimeout(effectiveTimeout, localBuf.ToString());
                    return false;
                }
                n = await readTask;
            }
            catch (OperationCanceledException)
            {
                ThrowTimeout(effectiveTimeout, localBuf.ToString());
                return false;
            }

            if (n == 0)
            {
                ThrowTimeout(effectiveTimeout, localBuf.ToString(), "stdout EOF (process may have exited)");
                return false;
            }

            localBuf.Append(charBuf, 0, n);

            // Check for PS1 prompt first (longer, more specific).
            if (localBuf.Length >= PromptString.Length)
            {
                var tail = localBuf.ToString(localBuf.Length - PromptString.Length, PromptString.Length);
                if (tail == PromptString)
                {
                    var content = localBuf.ToString(0, localBuf.Length - PromptString.Length);
                    lock (_sinceLastPrompt)
                    {
                        _sinceLastPrompt.Clear();
                        _sinceLastPrompt.Append(content);
                    }
                    return false; // PS1 seen
                }
            }

            // Check for PS2 prompt ("> ").
            if (localBuf.Length >= Ps2Value.Length)
            {
                var tail = localBuf.ToString(localBuf.Length - Ps2Value.Length, Ps2Value.Length);
                if (tail == Ps2Value)
                {
                    // Store content before PS2 marker.
                    var content = localBuf.ToString(0, localBuf.Length - Ps2Value.Length);
                    lock (_sinceLastPrompt)
                    {
                        _sinceLastPrompt.Clear();
                        _sinceLastPrompt.Append(content);
                    }
                    return true; // PS2 seen
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                ThrowTimeout(effectiveTimeout, localBuf.ToString());
                return false;
            }
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
