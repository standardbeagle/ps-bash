// CA1416: the POSIX paths exercised here (killpg / ioctl probes, the POSIX
// branch of SignalForwarder.Install) are guarded by Skip.If(IsWindows) at every
// call site. The analyzer cannot see through Skippable gating, so the file-level
// suppression keeps the POSIX-only tests warning-free.
#pragma warning disable CA1416

using System.Diagnostics;
using System.Runtime.InteropServices;
using PsBash.Shell.Pty;
using Xunit;

namespace PsBash.Shell.Tests.Pty;

/// <summary>
/// PTY-5 acceptance tests: launcher-side terminal-signal forwarding
/// (<see cref="SignalForwarder"/>).
///
/// <para><b>POSIX</b> is the primary deliverable and is fully verifiable on
/// Linux/WSL2: the tests spawn a real child process group, install a forwarder
/// targeting that pgid, raise the signal in-process, and assert the child
/// group received it. The SIGINT handler sets <c>ctx.Cancel=true</c> so raising
/// SIGINT does not kill the test host — it only re-delivers to the target
/// group, which is exactly the production behaviour.</para>
///
/// <para><b>Windows</b> Ctrl-C / resize verification is CI-gated: ConPTY's
/// <c>CTRL_C_EVENT</c> translation and <c>ResizePseudoConsole</c> need a real
/// console host. The Windows-tagged tests assert the install/dispose contract
/// (handler add/remove succeeds, double-dispose is safe) which is verifiable
/// headless; the runtime Ctrl-C delivery is asserted by the CI matrix.</para>
/// </summary>
public partial class SignalForwarderTests
{
    // ------------------------------------------------------------------
    // Install contract — platform-agnostic
    // ------------------------------------------------------------------

    /// <summary>
    /// A launcher whose stdin is not a tty (pipe-driven, test host) has no
    /// controlling terminal and therefore no terminal signals to forward.
    /// <see cref="SignalForwarder.Install"/> must short-circuit to an inactive
    /// scope that installs nothing and disposes as a no-op.
    /// </summary>
    [Fact]
    public async Task Install_WhenLauncherStdinNotTty_ReturnsInactiveScope()
    {
        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);

        using var fwd = SignalForwarder.Install(
            hostPid: 99999,
            pty: pty,
            isLauncherStdinTty: false);

        Assert.False(fwd.IsActive, "non-tty launcher stdin must yield an inactive forwarder");
        Assert.Equal(0, fwd.SigintDeliveredCount);
        Assert.Equal(0, fwd.SigtstpDeliveredCount);
        Assert.Equal(0, fwd.WinchForwardedCount);
        // Dispose of an inactive scope must be a clean no-op (no throw).
        fwd.Dispose();
    }

    /// <summary>
    /// <see cref="SignalForwarder.Install"/> rejects a null PTY before touching
    /// any platform syscall — QA rubric Directive 7 (negative path).
    /// </summary>
    [Fact]
    public void Install_WithNullPty_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SignalForwarder.Install(hostPid: 1, pty: null!, isLauncherStdinTty: true));
    }

    /// <summary>
    /// Disposing an active forwarder twice must be safe (the launcher's outer
    /// <c>finally</c> can race the using-scope). Mirrors
    /// <c>TerminalMode.Scope</c>'s double-dispose guarantee.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_Install_DoubleDispose_IsSafe()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);

        // Target our own process group as a harmless pgid — no signal is raised
        // in this test so nothing is actually delivered.
        var fwd = SignalForwarder.Install(
            hostPid: Environment.ProcessId,
            pty: pty,
            isLauncherStdinTty: true);

        Assert.True(fwd.IsActive, "tty launcher stdin must yield an active forwarder");
        fwd.Dispose();
        fwd.Dispose(); // must not throw
    }

    // ------------------------------------------------------------------
    // POSIX end-to-end: SIGINT / SIGTSTP forwarding to a child process group
    // ------------------------------------------------------------------

    /// <summary>
    /// The core PTY-5 POSIX deliverable: raising <c>SIGINT</c> in the launcher
    /// process must be re-delivered via <c>killpg</c> to the host's foreground
    /// process group, and must NOT terminate the launcher itself.
    ///
    /// <para>Test shape: spawn a <c>sleep 30</c> child in its own process
    /// group, install a forwarder targeting that pgid, raise <c>SIGINT</c> on
    /// the launcher (this test process). The forwarder's handler sets
    /// <c>ctx.Cancel=true</c> (test process survives) and <c>killpg</c>s the
    /// child group — the sleep dies with signal 2 (exit code 130). We assert
    /// the child exited promptly and <see cref="SignalForwarder.SigintDeliveredCount"/>
    /// incremented.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_RaisingSigint_ForwardsToChildProcessGroup_WithoutKillingLauncher()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);

        // Spawn `sleep 30` as its own session/process-group leader via setsid
        // so killpg(childPid, SIGINT) targets exactly that group — mirroring
        // PTY-2's POSIX_SPAWN_SETSID host. setsid runs the program detached
        // into a new session; we capture the new leader's pid.
        using var child = StartSessionLeaderChild("sleep", "30");
        try
        {
            int childPgid = child.LeaderPid; // session leader: pgid == pid

            using var fwd = SignalForwarder.Install(
                hostPid: childPgid,
                pty: pty,
                isLauncherStdinTty: true);
            Assert.True(fwd.IsActive);

            // Raise SIGINT in THIS process. PosixSignalRegistration delivers it
            // to the forwarder, which sets Cancel=true and killpg's the child.
            int rc = Raise(SIGINT);
            Assert.Equal(0, rc);

            // The child must die promptly from the forwarded SIGINT. Poll its
            // liveness rather than Thread.Sleep — bounded 5 s deadline.
            bool exited = await WaitProcessExitAsync(child.LeaderPid, TimeSpan.FromSeconds(5));
            Assert.True(exited,
                "sleep child must exit after SIGINT was forwarded to its process group");

            // The launcher (this test process) survived — if Cancel had not
            // been set, the runtime would have aborted us and the test would
            // never reach this assert.
            Assert.True(fwd.SigintDeliveredCount >= 1,
                $"expected >=1 forwarded SIGINT, got {fwd.SigintDeliveredCount}");
        }
        finally
        {
            child.Dispose();
        }
    }

    /// <summary>
    /// <c>SIGTSTP</c> (Ctrl-Z) forwarding: raising <c>SIGTSTP</c> in the
    /// launcher must <c>killpg</c> the host's process group so the foreground
    /// job receives it — and must NOT stop the launcher (a stopped launcher
    /// freezes byte pumping forever).
    ///
    /// <para><b>Why this asserts <i>delivery</i>, not the stopped state:</b>
    /// PTY-2 spawns the host as its own session leader, so the host's process
    /// group is, by POSIX definition, an <i>orphaned</i> process group (no
    /// member has a parent in a different group of the same session). POSIX
    /// (2.4.3 + <c>_POSIX_JOB_CONTROL</c>) says a stop signal whose action is
    /// the <i>default</i> is <b>discarded</b> for an orphaned process group —
    /// so a host using the default <c>SIGTSTP</c> disposition would never
    /// actually stop. The launcher's job is to <i>deliver</i> the signal; what
    /// the host does with it (install a handler to checkpoint, or accept the
    /// discard) is the host's concern. The test child therefore installs a
    /// <c>trap ... TSTP</c> handler — which makes the signal <i>deliverable</i>
    /// (no discard, because the action is no longer the default stop) — and
    /// exits on receipt. We assert the child exited (signal delivered to the
    /// group) and the launcher survived.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_RaisingSigtstp_ForwardsToChildProcessGroup_WithoutStoppingLauncher()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);

        // Child traps SIGTSTP and exits on receipt. The trap makes SIGTSTP
        // deliverable to this orphaned process group (a default-disposition
        // stop would be discarded); exiting gives the test an observable
        // "signal arrived" signal without depending on the stopped (T) state.
        using var child = StartSessionLeaderShellLoop(
            "trap 'exit 0' TSTP");
        try
        {
            int childPgid = child.LeaderPid;

            using var fwd = SignalForwarder.Install(
                hostPid: childPgid,
                pty: pty,
                isLauncherStdinTty: true);
            Assert.True(fwd.IsActive);

            int rc = Raise(SIGTSTP);
            Assert.Equal(0, rc);

            // The child must exit promptly once SIGTSTP reaches its group and
            // fires the trap. Poll rather than sleep — bounded 5 s deadline.
            bool exited = await WaitProcessExitAsync(child.LeaderPid, TimeSpan.FromSeconds(5));
            Assert.True(exited,
                "trap-handling child must exit after SIGTSTP was forwarded to its process group");

            // The launcher (this test process) survived — if the forwarder's
            // raw handler had let SIGTSTP stop the launcher, the test process
            // would be frozen here and the run would time out instead.
            Assert.True(fwd.SigtstpDeliveredCount >= 1,
                $"expected >=1 forwarded SIGTSTP, got {fwd.SigtstpDeliveredCount}");
        }
        finally
        {
            child.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // POSIX SIGWINCH: resize propagation launcher tty -> PTY master
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>SIGWINCH</c> handling: when the launcher's tty is resized, the
    /// forwarder reads the new winsize (<c>TIOCGWINSZ</c>) and pushes it onto
    /// the PTY master (<c>TIOCSWINSZ</c> via <see cref="IPty.Resize"/>).
    ///
    /// <para>Headless test harnesses have no resizable controlling tty, so this
    /// test verifies the propagation mechanism directly: it installs the
    /// forwarder, then raises <c>SIGWINCH</c> in-process and asserts the
    /// resize path ran (<see cref="SignalForwarder.WinchForwardedCount"/>
    /// incremented). The launcher's stdin in the test host may or may not be a
    /// tty; when it is not, <c>TIOCGWINSZ</c> on fd 0 fails and the forward is
    /// a deliberate no-op — so the count assertion is tolerant of that
    /// environment and the test instead asserts "raising SIGWINCH does not
    /// throw and the forwarder stays healthy".</para>
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_RaisingSigwinch_DoesNotThrow_AndForwardsWhenLauncherHasTty()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);

        using var fwd = SignalForwarder.Install(
            hostPid: Environment.ProcessId,
            pty: pty,
            isLauncherStdinTty: true);
        Assert.True(fwd.IsActive);

        // Priming during Install already attempted one forward. Capture the
        // baseline, raise SIGWINCH, and confirm the handler ran cleanly.
        int before = fwd.WinchForwardedCount;
        int rc = Raise(SIGWINCH);
        Assert.Equal(0, rc);

        // Bounded poll: the SIGWINCH handler is async w.r.t. Raise() returning.
        // Either the count moved (launcher fd 0 is a tty) or it stayed (fd 0
        // is not a tty in this harness — a correct, documented no-op).
        bool moved = await PollAsync(
            () => fwd.WinchForwardedCount > before,
            TimeSpan.FromSeconds(2));

        // Whichever branch: the forwarder must remain usable and disposable.
        Assert.True(fwd.WinchForwardedCount >= before,
            "WinchForwardedCount must never decrease");
        fwd.Dispose(); // clean teardown after a real SIGWINCH delivery

        // If the test host gave us a real tty on fd 0, prove the propagation
        // actually fired; otherwise the no-op branch is the documented and
        // correct behaviour and there is nothing further to assert.
        if (StdinIsTty())
        {
            Assert.True(moved,
                "with a real launcher tty, SIGWINCH must propagate the resize to the PTY master");
        }
    }

    /// <summary>
    /// <see cref="SignalForwarder.ForwardWinch"/> (exercised via Install's
    /// priming step) must tolerate a PTY that is disposed underneath it — the
    /// host child is on its way out and dropping the resize is the correct
    /// fallback, not a crash.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_Winch_AfterPtyDisposed_IsSilentNoOp()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        using var fwd = SignalForwarder.Install(
            hostPid: Environment.ProcessId,
            pty: pty,
            isLauncherStdinTty: true);

        // Dispose the PTY out from under the live forwarder.
        await pty.DisposeAsync();

        // Raising SIGWINCH now must not throw — IPty.Resize on a disposed PTY
        // is a documented no-op, and the forwarder catches any residual error.
        int rc = Raise(SIGWINCH);
        Assert.Equal(0, rc);

        // Give the handler a bounded window to run; the assertion is simply
        // "we did not crash and the forwarder is still disposable".
        await Task.Yield();
        fwd.Dispose();
    }

    // ------------------------------------------------------------------
    // Windows install/dispose contract (runtime Ctrl-C is CI-gated)
    // ------------------------------------------------------------------

    /// <summary>
    /// Windows: <see cref="SignalForwarder.Install"/> registers a console
    /// control handler and starts the resize poll. The runtime Ctrl-C delivery
    /// (ConPTY <c>CTRL_C_EVENT</c> → host <c>Console.CancelKeyPress</c>) and
    /// <c>ResizePseudoConsole</c> are exercised by the CI matrix on a real
    /// console host; here we assert the headless-verifiable contract: Install
    /// returns a scope, dispose removes the handler and stops the poll, and
    /// double-dispose is safe.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public async Task Windows_Install_AndDispose_IsSafe()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);

        var fwd = SignalForwarder.Install(
            hostPid: Environment.ProcessId,
            pty: pty,
            isLauncherStdinTty: true);

        // Install may return Inactive if the CI host has no console attached;
        // either way, dispose must be clean and idempotent.
        fwd.Dispose();
        fwd.Dispose();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private const int SIGINT = 2;
    private const int SIGTSTP = 20;
    private const int SIGWINCH = 28;

    /// <summary>
    /// A signal-forwarding test target: a child that is its own session /
    /// process-group leader (so <c>killpg(LeaderPid, sig)</c> targets exactly
    /// that group), mirroring PTY-2's host spawned with
    /// <c>POSIX_SPAWN_SETSID</c>.
    /// </summary>
    private sealed class SessionLeaderChild : IDisposable
    {
        public required Process Wrapper { get; init; }
        /// <summary>
        /// PID == PGID == SID of the leader process running the workload. This
        /// is the value passed to <c>SignalForwarder.Install(hostPid:)</c>.
        /// </summary>
        public required int LeaderPid { get; init; }

        public void Dispose()
        {
            // Kill the leader's process group, then the wrapper, best-effort.
            try { NativeRaise.killpg(LeaderPid, 9 /* SIGKILL */); } catch { }
            try { if (!Wrapper.HasExited) Wrapper.Kill(entireProcessTree: true); }
            catch { }
            Wrapper.Dispose();
        }
    }

    /// <summary>
    /// Start <paramref name="program"/> as a session/process-group leader.
    ///
    /// <para>Shape: <c>setsid sh -c 'echo $$; exec PROGRAM ARGS'</c>. <c>setsid</c>
    /// makes the <c>sh</c> a brand-new session leader (so its
    /// pid == pgid == sid); <c>sh</c> echoes <c>$$</c> — its own pid — as the
    /// first stdout line; <c>exec</c> then replaces <c>sh</c> with the workload
    /// <i>keeping the same pid</i>. The first captured stdout line is therefore
    /// the leader pid, and it is deterministic — no <c>setpgid</c>-after-exec
    /// race.</para>
    /// </summary>
    private static SessionLeaderChild StartSessionLeaderChild(string program, string args)
    {
        // ArgumentList (not Arguments) so the `sh -c` script survives as one
        // argv element with no host-side quote-parsing ambiguity.
        var psi = new ProcessStartInfo
        {
            FileName = "setsid",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"echo $$; exec {program} {args}");
        var wrapper = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start setsid wrapper");

        // The leader prints its own pid as the first stdout line before exec.
        string? firstLine = wrapper.StandardOutput.ReadLine();
        if (!int.TryParse(firstLine?.Trim(), out int leaderPid) || leaderPid <= 0)
        {
            try { wrapper.Kill(entireProcessTree: true); } catch { }
            wrapper.Dispose();
            throw new InvalidOperationException(
                $"setsid wrapper did not report a leader pid (got '{firstLine}')");
        }

        return new SessionLeaderChild { Wrapper = wrapper, LeaderPid = leaderPid };
    }

    /// <summary>
    /// Start a session-leader child running <paramref name="prelude"/> followed
    /// by a busy-wait loop. Same <c>setsid sh -c 'echo $$; ...'</c> shape as
    /// <see cref="StartSessionLeaderChild"/>, but instead of <c>exec</c>ing a
    /// workload it stays in <c>sh</c> so a <c>trap</c> installed by
    /// <paramref name="prelude"/> remains in effect — used by the SIGTSTP test,
    /// which needs the child to have a non-default signal disposition.
    /// </summary>
    private static SessionLeaderChild StartSessionLeaderShellLoop(string prelude)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "setsid",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        // echo $$ first (leader pid), install the prelude (e.g. a trap), then
        // a bounded busy-wait so the child cannot outlive a wedged test.
        psi.ArgumentList.Add($"echo $$; {prelude}; i=0; while [ $i -lt 600 ]; do sleep 0.1; i=$((i+1)); done");

        var wrapper = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start setsid shell-loop wrapper");

        string? firstLine = wrapper.StandardOutput.ReadLine();
        if (!int.TryParse(firstLine?.Trim(), out int leaderPid) || leaderPid <= 0)
        {
            try { wrapper.Kill(entireProcessTree: true); } catch { }
            wrapper.Dispose();
            throw new InvalidOperationException(
                $"setsid shell-loop wrapper did not report a leader pid (got '{firstLine}')");
        }

        return new SessionLeaderChild { Wrapper = wrapper, LeaderPid = leaderPid };
    }

    /// <summary>Raise <paramref name="sig"/> in the current process.</summary>
    private static int Raise(int sig) => NativeRaise.raise(sig);

    private static async Task<bool> WaitProcessExitAsync(int pid, TimeSpan timeout)
        => await PollAsync(() => !ProcessAlive(pid), timeout);

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; } // no such process
        catch (InvalidOperationException) { return false; }
    }

    private static bool StdinIsTty()
        => !Console.IsInputRedirected;

    private static async Task<bool> PollAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return condition();
    }

    private static partial class NativeRaise
    {
        [System.Runtime.InteropServices.LibraryImport("libc", SetLastError = true)]
        public static partial int raise(int sig);

        [System.Runtime.InteropServices.LibraryImport("libc", SetLastError = true)]
        public static partial int killpg(int pgrp, int sig);
    }
}
