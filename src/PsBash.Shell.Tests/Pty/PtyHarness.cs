using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using PsBash.Shell.Pty;

namespace PsBash.Shell.Tests.Pty;

/// <summary>
/// Cross-platform expect-style test fixture: allocates a real pseudo-terminal
/// (PTY-1 <see cref="PtyAllocator"/>), spawns <c>ps-bash -i</c> against the slave
/// side (PTY-2 <see cref="PtySpawner"/>), and keeps the master end so a test can
/// write keystrokes, read rendered terminal output, and assert byte-accurate TUI
/// behavior.
///
/// <para>This is the PTY counterpart of <see cref="InteractiveShellHarness"/>:
/// same API shape (write keys, wait for prompt), but the pipe transport is
/// replaced by a real PTY master so the raw-passthrough path (vim / less /
/// Read-Host / Clear-Host) is exercised the way it is in a real terminal.</para>
///
/// <para><b>Determinism (QA rubric Directive 6):</b> the spawned shell gets a
/// canonical environment — fixed <c>TERM</c>, <c>LANG</c>, <c>COLUMNS</c>,
/// <c>LINES</c>, an empty <c>PROMPT_COMMAND</c>, a byte-stable <c>PS1</c>/<c>PS2</c>,
/// an isolated short <c>HOME</c>, an isolated history file, and <c>--norc</c>.
/// <see cref="WaitForRegexAsync"/> awaits PTY reads against a deadline — there is
/// no <c>Thread.Sleep</c> / <c>Task.Delay</c> anywhere in the wait path.</para>
///
/// <para><b>Teardown (the POSIX zombie risk):</b> disposal closes the shell's
/// stdin source by disposing the spawner (reaps the child via <c>waitpid</c>),
/// then disposes the <see cref="IPty"/> which closes the slave fd then the master
/// fd. No zombie PTY pairs, no leaked fds.</para>
/// </summary>
internal sealed partial class PtyHarness : IAsyncDisposable
{
    // Continuation prompt injected via PS2 (Directive 6).
    public const string Ps2Value = "> ";

    /// <summary>
    /// Regex matching the shell's interactive prompt as rendered on a real PTY.
    /// Under a PTY the launcher drives the full line editor, which renders its
    /// built-in prompt rather than the redirected-pipe <c>PS1</c> path the pipe
    /// harness sees. The built-in prompt is
    /// <c>user@host:cwd [(git-branch)] $ </c> (<c>#</c> for admin); the user /
    /// host / cwd / branch vary per machine, but the
    /// <c>NAME@HOST:…&#160;$&#160;</c> shape is stable once ANSI escapes are
    /// stripped (which the harness does), so the shape is the deterministic
    /// prompt oracle. <c>[^\n]*?</c> spans the cwd and the optional git segment
    /// without crossing a line boundary.
    /// </summary>
    public const string PromptPattern = @"\S+@\S+:[^\n]*?[$#] ";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(15);

    // Strips ANSI/VT control sequences: CSI (ESC[ … letter), ESC= / ESC>
    // (keypad mode), and lone two-char escapes. The PTY-rendered transcript is
    // full of cursor-movement and color codes; matching prompts / output text
    // requires a clean stream.
    private static readonly Regex AnsiEscape = new(
        @"\x1b\[[0-9;?]*[a-zA-Z]|\x1b[=>]|\x1b[()][AB0-2]", RegexOptions.Compiled);

    private readonly IPty _pty;
    private readonly PtySpawner _spawner;
    private readonly string _tempHome;
    private readonly Task _readerLoop;
    private readonly CancellationTokenSource _readerCts = new();

    // A dedicated write-only stream over the SAME PTY master fd as the one the
    // background reader reads from. IPty exposes Input and Output as the SAME
    // FileStream instance, and a single sync FileStream is NOT safe for
    // concurrent read + write — the background read and a WriteKeysAsync call
    // would corrupt its shared buffer/position state and crash the process.
    // Two independent FileStreams over one fd is fine for a PTY (no seekable
    // position; read and write are independent kernel directions). The handle
    // is non-owning — UnixPtyAdapter.DisposeAsync still owns closing the fd.
    private readonly FileStream _writeStream;

    // PID of the ps-bash-host process the launcher spawns. The launcher spawns
    // the host in its OWN new session (PtySpawner uses POSIX_SPAWN_SETSID), so
    // the host is NOT in the launcher's process group — killing the launcher
    // group alone orphans the host. Captured at start (while the launcher is
    // alive and the host exists) so teardown can kill the host's group too.
    // -1 when not found (host not yet spawned, or non-POSIX).
    private int _hostPid = -1;

    // Raw decoded transcript accumulated by the background reader. CR bytes are
    // stripped (PTY ONLCR translation), but ANSI escapes are kept here and
    // stripped on read — an escape sequence split across two reads is only
    // complete once both halves are in the buffer.
    private readonly StringBuilder _transcript = new();
    private readonly object _transcriptLock = new();

    private int _disposed;

    private PtyHarness(IPty pty, PtySpawner spawner, string tempHome)
    {
        _pty = pty;
        _spawner = spawner;
        _tempHome = tempHome;

        // Build a separate write-only FileStream over the master fd so writes
        // never share FileStream state with the background reader. IPty.Input
        // is a FileStream on POSIX; reuse its fd via a non-owning SafeFileHandle.
        var masterHandle = ((FileStream)pty.Input).SafeFileHandle;
        var writeHandle = new SafeFileHandle(masterHandle.DangerousGetHandle(), ownsHandle: false);
        _writeStream = new FileStream(writeHandle, FileAccess.Write);

        _readerLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>PID of the spawned <c>ps-bash</c> process.</summary>
    public int Pid => _spawner.Pid;

    /// <summary>The isolated temp HOME directory created for this harness.</summary>
    public string TempHome => _tempHome;

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Locates the built <c>ps-bash</c> launcher binary. Returns <c>null</c> when
    /// not found so callers can <c>Skip</c>. Reuses
    /// <see cref="InteractiveShellHarness.FindPsBashBinary"/> — the binary
    /// location logic is identical for the pipe and PTY harnesses.
    /// </summary>
    public static string? FindPsBashBinary() => InteractiveShellHarness.FindPsBashBinary();

    /// <summary>
    /// Allocates a PTY, spawns <c>ps-bash -i --norc</c> against its slave with a
    /// canonical environment, and waits for the first prompt before returning.
    /// </summary>
    /// <param name="psBashPath">Path to the launcher binary (from <see cref="FindPsBashBinary"/>).</param>
    /// <param name="cols">Initial terminal width.</param>
    /// <param name="rows">Initial terminal height.</param>
    /// <param name="startTimeout">How long to wait for the initial prompt.</param>
    public static async Task<PtyHarness> StartAsync(
        string psBashPath,
        short cols = 120,
        short rows = 40,
        TimeSpan? startTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(psBashPath);

        // Keep the temp dir name SHORT — the ps-bash host derives its Unix domain
        // socket path under {TMPDIR}/ps-bash/, and a UDS path is capped at 108
        // chars on Linux (RC-6 lesson). A short suffix leaves headroom.
        var tempHome = Path.Combine(Path.GetTempPath(), "psh-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(tempHome);
        var tempHistoryFile = Path.Combine(tempHome, "h.db");

        // Canonical environment (Directive 6): fixed terminal type / locale /
        // dimensions, empty PROMPT_COMMAND, isolated HOME and history, no profile
        // sourcing. Merged onto the launcher's environment by PtySpawner. Note:
        // under a real PTY the launcher drives the built-in line editor, which
        // renders its own prompt (user@host:cwd $) — PS1 is honored only on the
        // redirected-pipe path, so it is not set here. The prompt oracle is
        // PromptPattern.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TERM"] = "xterm-256color",
            ["LANG"] = "C.UTF-8",
            ["COLUMNS"] = cols.ToString(),
            ["LINES"] = rows.ToString(),
            ["PROMPT_COMMAND"] = "",
            ["PS2"] = Ps2Value,
            ["HOME"] = tempHome,
            ["PSBASH_HOME"] = tempHome,
            ["PSBASH_HISTORY_PATH"] = tempHistoryFile,
        };

        IPty? pty = null;
        PtySpawner? spawner = null;
        try
        {
            pty = await PtyAllocator.AllocateAsync(cols, rows).ConfigureAwait(false);

            // -i forces the REPL even though the launcher's own stdio is a PTY
            // slave; --norc keeps the shell hermetic (Directive 6).
            spawner = PtySpawner.Spawn(
                executablePath: psBashPath,
                arguments: new[] { "-i", "--norc" },
                pty: pty,
                environment: env);

            var harness = new PtyHarness(pty, spawner, tempHome);
            try
            {
                await harness.WaitForRegexAsync(
                    PromptPattern,
                    startTimeout ?? DefaultStartTimeout).ConfigureAwait(false);
                // The prompt is up, so the launcher has spawned the host by now.
                // Capture the host PID for deterministic teardown — the host
                // lives in its own session, outside the launcher's group.
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    harness._hostPid = FindHostPid(spawner.Pid);
            }
            catch
            {
                await harness.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return harness;
        }
        catch
        {
            // StartAsync failed before the harness took ownership — clean up the
            // raw resources here so nothing leaks.
            if (spawner is not null) await spawner.DisposeAsync().ConfigureAwait(false);
            if (pty is not null) await pty.DisposeAsync().ConfigureAwait(false);
            try { Directory.Delete(tempHome, recursive: true); } catch { /* best effort */ }
            throw;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes raw keystrokes to the PTY master (toward the shell's stdin).
    /// Include <c>"\n"</c> to submit a line, <c>"\x03"</c> for Ctrl-C, <c>"\t"</c>
    /// for Tab, etc. UTF-8 encoded.
    /// </summary>
    public async Task WriteKeysAsync(string keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var bytes = Encoding.UTF8.GetBytes(keys);
        // Writes go through the dedicated write stream — never _pty.Input, which
        // shares FileStream state with the background reader.
        await _writeStream.WriteAsync(bytes).ConfigureAwait(false);
        await _writeStream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the decoded transcript captured so far, with CR bytes and ANSI
    /// escape sequences stripped. Snapshots the buffer under lock; safe to call
    /// while the reader runs.
    /// </summary>
    public string ReadOutput()
    {
        lock (_transcriptLock)
            return AnsiEscape.Replace(_transcript.ToString(), "");
    }

    /// <summary>
    /// Reads PTY output until the transcript matches <paramref name="pattern"/> or
    /// the <paramref name="timeout"/> deadline fires. Returns the transcript up to
    /// and including the match. On timeout throws with a full diagnostic dump
    /// (transcript, env, cwd, exit code) per Directive 6 + Directive 9.
    /// </summary>
    /// <remarks>No <c>Thread.Sleep</c> / <c>Task.Delay</c>: the wait awaits the
    /// background reader signalling new data, bounded by the deadline.</remarks>
    public async Task<string> WaitForRegexAsync(string pattern, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        var effective = timeout ?? DefaultTimeout;
        var regex = new Regex(pattern, RegexOptions.Compiled);
        using var cts = new CancellationTokenSource(effective);

        while (true)
        {
            string snapshot;
            TaskCompletionSource dataSignal;
            lock (_transcriptLock)
            {
                snapshot = AnsiEscape.Replace(_transcript.ToString(), "");
                if (regex.IsMatch(snapshot))
                    return snapshot;
                // Register for the next reader notification while holding the
                // lock so we cannot miss data appended between the match check
                // and the await.
                dataSignal = _dataSignal;
            }

            try
            {
                await dataSignal.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ThrowTimeout(pattern, effective);
            }
        }
    }

    /// <summary>Resizes the PTY window (drives <c>TIOCSWINSZ</c> on the master).</summary>
    public void Resize(short cols, short rows) => _pty.Resize(cols, rows);

    /// <summary>
    /// Sends a real signal to the spawned <c>ps-bash</c> process. POSIX only —
    /// on Windows this throws <see cref="PlatformNotSupportedException"/> because
    /// ConPTY signal delivery uses a different mechanism (write Ctrl-C to the
    /// master, or <c>GenerateConsoleCtrlEvent</c>) that PTY-9 will exercise.
    /// </summary>
    /// <param name="signal">POSIX signal number (e.g. 2 = SIGINT, 15 = SIGTERM).</param>
    public void SendSignal(int signal)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException(
                "SendSignal is POSIX-only; Windows ConPTY uses Ctrl-C bytes / GenerateConsoleCtrlEvent");

        if (NativeMethods.kill(_spawner.Pid, signal) != 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"kill(pid={_spawner.Pid}, sig={signal}) failed (errno={err}): " +
                Marshal.GetPInvokeErrorMessage(err));
        }
    }

    /// <summary>Waits for the spawned shell to exit and returns its exit code.</summary>
    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _spawner.WaitForExitAsync(cts.Token).ConfigureAwait(false);
    }

    // ── Background reader ─────────────────────────────────────────────────────

    // Reset every time the reader appends data. WaitForRegexAsync captures the
    // current instance under lock, then awaits it — the reader completes it to
    // wake all waiters.
    private TaskCompletionSource _dataSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ReadLoopAsync()
    {
        var buf = new byte[4096];
        try
        {
            while (!_readerCts.IsCancellationRequested)
            {
                int n = await _pty.Output.ReadAsync(buf.AsMemory(), _readerCts.Token)
                    .ConfigureAwait(false);
                if (n <= 0) break; // EOF — shell exited and slave closed.

                lock (_transcriptLock)
                {
                    for (int i = 0; i < n; i++)
                    {
                        // Strip CR so ONLCR translation does not break matching.
                        if (buf[i] != (byte)'\r')
                            _transcript.Append((char)buf[i]);
                    }
                    // Signal current waiters and arm a fresh signal for the next.
                    var prev = _dataSignal;
                    _dataSignal = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    prev.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
        catch (IOException) { /* master closed */ }
        catch (ObjectDisposedException) { /* master stream disposed */ }
        finally
        {
            // Wake any waiter blocked on a signal so it sees EOF and times out
            // cleanly instead of hanging until its own deadline.
            lock (_transcriptLock)
                _dataSignal.TrySetResult();
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    private void ThrowTimeout(string pattern, TimeSpan timeout)
    {
        string transcript = ReadOutput();
        // Probe exit state without blocking: a 1ms budget either reaps a
        // finished child or cancels cleanly if it is still running.
        string exitInfo;
        try
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
            int code = _spawner.WaitForExitAsync(probeCts.Token).GetAwaiter().GetResult();
            exitInfo = $"exit={code}";
        }
        catch (OperationCanceledException)
        {
            exitInfo = "still running";
        }
        catch (Exception ex)
        {
            exitInfo = $"exit-state-unknown ({ex.GetType().Name})";
        }

        throw new TimeoutException(
            $"WaitForRegexAsync('{pattern}') timed out after {timeout.TotalSeconds:F1}s.\n" +
            $"Process: pid={_spawner.Pid} {exitInfo}\n" +
            $"CWD: {Environment.CurrentDirectory}\n" +
            $"HOME: {_tempHome}\n" +
            $"--- PTY transcript ({transcript.Length} chars) ---\n{transcript}\n" +
            $"--- end transcript ---");
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    /// <summary>
    /// Deterministic teardown — no zombie PTY pairs, no orphaned host process,
    /// no leaked fds:
    /// <list type="number">
    ///   <item><description>SIGKILL <b>two</b> process groups: the launcher's
    ///     and the host's. <see cref="PtySpawner"/> deliberately does not kill
    ///     on dispose, and the launcher spawns the host in its OWN new session
    ///     (POSIX_SPAWN_SETSID), so the host is not in the launcher's group —
    ///     killing the launcher alone orphans the host (which then keeps
    ///     <c>dotnet test</c>'s testhost from exiting). Both PIDs are
    ///     process-group leaders, so <c>kill(-pid)</c> reaps each group and any
    ///     foreground job under it.</description></item>
    ///   <item><description>Reap the launcher via the spawner
    ///     (<c>waitpid</c>) — no zombie.</description></item>
    ///   <item><description>Close the PTY slave then master fds via
    ///     <see cref="IPty"/>. The master close unblocks the background
    ///     reader's <c>read()</c> with EOF.</description></item>
    ///   <item><description>Delete the isolated HOME.</description></item>
    /// </list>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 1) Tear the process tree down. On Windows PtySpawner.DisposeAsync
        //    handles process teardown; on POSIX we must do it explicitly,
        //    killing BOTH the launcher's group and the host's group. SIGKILL
        //    (not SIGTERM) because this is test teardown — there is no state to
        //    flush, and SIGKILL cannot be caught/ignored, so teardown is
        //    bounded with no grace-period wait.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { NativeMethods.kill(-_spawner.Pid, 9 /* SIGKILL */); } catch { }
            if (_hostPid > 0)
                try { NativeMethods.kill(-_hostPid, 9 /* SIGKILL */); } catch { }
        }

        // 2) Reap the launcher (waitpid) — no zombie. Bounded so a wedged
        //    waitpid cannot hang teardown.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _spawner.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch { /* timed out or already reaped */ }
        await _spawner.DisposeAsync().ConfigureAwait(false);

        // 3) Stop the reader, drop the write-stream wrapper (non-owning — does
        //    not close the fd), then close the PTY fds (slave then master —
        //    IPty.DisposeAsync owns that order and the fd). Closing the master
        //    makes the reader's in-flight read() return EOF.
        _readerCts.Cancel();
        await _writeStream.DisposeAsync().ConfigureAwait(false);
        await _pty.DisposeAsync().ConfigureAwait(false);
        try { await _readerLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* reader already gone, or stuck on a closed fd — let it fall off */ }

        _readerCts.Dispose();

        // 4) Drop the isolated HOME.
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Finds the <c>ps-bash-host</c> process the launcher spawned, by scanning
    /// <c>/proc/&lt;pid&gt;/cmdline</c> for the <c>--launcher-pid=&lt;launcherPid&gt;</c>
    /// argument the launcher passes to its host. Returns the host PID, or -1 if
    /// no match is found (host not up yet, or <c>/proc</c> unavailable).
    /// </summary>
    private static int FindHostPid(int launcherPid)
    {
        string marker = $"--launcher-pid={launcherPid}";
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                string name = Path.GetFileName(dir);
                if (!int.TryParse(name, out int pid)) continue;
                string cmdlinePath = Path.Combine(dir, "cmdline");
                string cmdline;
                try { cmdline = File.ReadAllText(cmdlinePath); }
                catch { continue; } // process exited mid-scan, or no permission
                // /proc cmdline is NUL-separated; a substring match on the
                // marker arg is sufficient and avoids splitting.
                if (cmdline.Contains(marker, StringComparison.Ordinal))
                    return pid;
            }
        }
        catch { /* /proc not present (non-Linux POSIX) — caller falls back */ }
        return -1;
    }

    private static partial class NativeMethods
    {
        // kill(2): a positive pid targets one process; a negative pid targets
        // the process group |pid|. Both the launcher and the host are session /
        // process group leaders (PtySpawner uses POSIX_SPAWN_SETSID), so
        // kill(-pid) reaches each one and any foreground job under it.
        [LibraryImport("libc", SetLastError = true)]
        public static partial int kill(int pid, int sig);
    }
}
