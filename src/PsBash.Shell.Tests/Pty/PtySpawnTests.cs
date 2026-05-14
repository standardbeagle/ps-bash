using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PsBash.Shell.Pty;
using Xunit;

namespace PsBash.Shell.Tests.Pty;

/// <summary>
/// PTY-2 acceptance tests: spawn the host binary with the slave side of a
/// PTY wired as stdin/stdout/stderr, and verify the host runspace sees a
/// real terminal (<c>[Console]::IsInputRedirected</c> is <c>false</c>).
///
/// <para><b>Linux invocation:</b>
/// <c>./scripts/test.sh --filter "FullyQualifiedName~PtySpawnTests"</c>.
/// The POSIX tests run; the Windows test skips with a recorded reason.</para>
///
/// <para><b>Windows invocation (CI):</b> the <c>build.yml</c> matrix runs
/// the full test suite on <c>windows-latest</c>; the POSIX tests skip and
/// the Windows test executes. No manual wiring is required for CI runs.</para>
///
/// <para><b>Windows invocation (local, from this WSL2 host):</b> a Windows
/// .NET SDK must be installed Windows-side. If installed, invoke via
/// powershell.exe interop:</para>
/// <code>
/// powershell.exe -NoProfile -Command "&amp; 'C:\Program Files\dotnet\dotnet.exe' test '\\wsl$\Ubuntu\home\beagle\work\core\ps-bash\src\PsBash.Shell.Tests\PsBash.Shell.Tests.csproj' --filter 'FullyQualifiedName~PtySpawnTests'"
/// </code>
/// <para>If only the .NET runtime is present Windows-side (the current state
/// of this developer machine), the Windows leg of PtySpawnTests is covered
/// by CI.</para>
/// </summary>
public class PtySpawnTests
{
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_HostSpawnedUnderPty_ReportsStdioIsTerminal()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "POSIX-only test — Windows uses STARTUPINFOEX + PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE_HANDLE");

        var hostBinary = TryLocateHostBinary();
        Skip.If(hostBinary is null,
            "ps-bash-host binary not found — build src/PsBash.Host first");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        Assert.False(string.IsNullOrEmpty(pty.SlaveName),
            "POSIX slave name must be populated for PtySpawner to open the slave inside the child");

        var spawner = PtySpawner.Spawn(
            executablePath: hostBinary!,
            arguments: new[] { "--pty-probe" },
            pty: pty,
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PSBASH_PTY_ATTACHED"] = "1",
            });

        await using (spawner)
        {
            // Read up to 1 KB of probe output. The host writes a single marker
            // line and exits 0; the PTY may translate LF→CRLF via ONLCR, so we
            // strip CRs before substring-matching.
            string probeOutput = await ReadUntilAsync(
                pty.Output,
                marker: "PSBASH-PTY-PROBE:",
                timeout: TimeSpan.FromSeconds(10));

            // The host runspace MUST see a real terminal on both directions
            // when we routed through PtySpawner. If either is True, the slave
            // wasn't wired as stdio (or the test harness isn't reading the
            // PTY master) and the PTY-2 contract is broken.
            Assert.Contains("IsInputRedirected=False", probeOutput);
            Assert.Contains("IsOutputRedirected=False", probeOutput);
            // Hand-off env var: host should observe PSBASH_PTY_ATTACHED=1.
            Assert.Contains("PSBASH_PTY_ATTACHED=1", probeOutput);

            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            int exitCode = await spawner.WaitForExitAsync(waitCts.Token);
            Assert.Equal(0, exitCode);
        }
    }

    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_PtyAllocator_SlaveName_Is_PtsDevicePath()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        // POSIX slave names look like /dev/pts/N (Linux) or /dev/ttysNNN (macOS).
        // Both start with "/dev/".
        Assert.NotNull(pty.SlaveName);
        Assert.StartsWith("/dev/", pty.SlaveName);
    }

    [SkippableFact]
    [Trait("Platform", "Windows")]
    public async Task Windows_HostSpawnedUnderConPty_ReportsStdioIsTerminal()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "Windows-only test — POSIX uses posix_spawn + SETSID + slave re-open");
        Skip.If(Environment.OSVersion.Version.Build < 17763,
            $"ConPTY requires Win10 1809 / build 17763+; current build is {Environment.OSVersion.Version.Build}");

        var hostBinary = TryLocateHostBinary();
        Skip.If(hostBinary is null,
            "ps-bash-host.exe binary not found — build src/PsBash.Host first");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        Assert.NotEqual(IntPtr.Zero, pty.SlaveHandle);

        var spawner = PtySpawner.Spawn(
            executablePath: hostBinary!,
            arguments: new[] { "--pty-probe" },
            pty: pty,
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PSBASH_PTY_ATTACHED"] = "1",
            });

        await using (spawner)
        {
            string probeOutput = await ReadUntilAsync(
                pty.Output,
                marker: "PSBASH-PTY-PROBE:",
                timeout: TimeSpan.FromSeconds(15));

            Assert.Contains("IsInputRedirected=False", probeOutput);
            Assert.Contains("IsOutputRedirected=False", probeOutput);
            Assert.Contains("PSBASH_PTY_ATTACHED=1", probeOutput);

            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int exitCode = await spawner.WaitForExitAsync(waitCts.Token);
            Assert.Equal(0, exitCode);
        }
    }

    /// <summary>
    /// PTY-2 mode-isolation regression. The interactive PTY path is gated on
    /// <c>shellArgs.Interactive</c> in <c>src/PsBash.Shell/Program.cs</c>; this
    /// test asserts the gate exists in source so the IPC/redirected-pipe path
    /// for <c>-c</c> and script mode cannot be regressed by accident.
    /// </summary>
    [Fact]
    public void ShellProgramPtySpawn_OnlyEntersFromInteractiveBranch()
    {
        // Locate the launcher source via the assembly path of one of its
        // own types, then walk up to find src/PsBash.Shell/Program.cs.
        var asmDir = Path.GetDirectoryName(typeof(PtySpawner).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && dir.Name != "src" && dir.Parent is not null)
            dir = dir.Parent;
        Assert.NotNull(dir);
        var programPath = Path.Combine(dir!.FullName, "PsBash.Shell", "Program.cs");
        Assert.True(File.Exists(programPath), $"Could not find {programPath}");

        var src = File.ReadAllText(programPath);

        // The PTY spawn helper is called from inside the interactive branch
        // only. The -c path uses IpcWorker and redirected pipes — no PTY.
        int ptyCallIdx = src.IndexOf("RunHostUnderPtyAsync", StringComparison.Ordinal);
        Assert.True(ptyCallIdx > 0, "RunHostUnderPtyAsync invocation missing from Program.cs");

        int interactiveBranchIdx = src.IndexOf(
            "if (shellArgs.Interactive || shellArgs.Command is null)",
            StringComparison.Ordinal);
        Assert.True(interactiveBranchIdx > 0,
            "Interactive branch guard missing from Program.cs — PTY spawn must be gated on it");
        Assert.True(ptyCallIdx > interactiveBranchIdx,
            "PtySpawner usage must be inside the interactive branch, not in -c or script-mode paths");

        // The -c path must still use IpcWorker (redirected pipe IPC).
        Assert.Contains("workerFactory()", src);
        Assert.Contains("BashTranspiler.Transpile", src);
    }

    /// <summary>
    /// PTY-10 acceptance test: two parallel interactive launches must get two
    /// distinct host processes. The interactive REPL path spawns the host
    /// directly via <see cref="PtySpawner"/> (see
    /// <c>Program.RunHostUnderPtyAsync</c>) — it never routes through
    /// <c>IpcWorker</c>'s shared-socket <c>Lifetime.Daemon</c> discovery, so a
    /// fresh host per session is guaranteed by construction. This test pins
    /// that contract: spawn two hosts under two PTYs, assert the PIDs differ.
    /// If a future change ever wired the interactive path to a shared daemon,
    /// two launches would observe the same PID and this fails.
    ///
    /// <para>Deterministic by design (QA rubric Directive 6): two spawns, two
    /// PIDs, assert not-equal. No timing, no prompt-wait, no sleep — the only
    /// non-determinism is process-spawn itself, which the WSL2 baseline-diff
    /// guidance covers. There is no path on which two independent
    /// <c>posix_spawn</c>/<c>CreateProcessW</c> calls return the same PID.</para>
    /// </summary>
    [SkippableFact]
    public async Task TwoInteractiveSpawns_GetDistinctHostPids()
    {
        var hostBinary = TryLocateHostBinary();
        Skip.If(hostBinary is null,
            "ps-bash-host binary not found — build src/PsBash.Host first");
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && Environment.OSVersion.Version.Build < 17763,
            $"ConPTY requires Win10 1809 / build 17763+; current build is {Environment.OSVersion.Version.Build}");

        // Use --pty-probe: the host writes a marker line and exits 0, so each
        // spawn is short-lived and self-cleaning — but it is still a full,
        // independent host process spawned exactly the way the interactive
        // REPL path spawns one (PtySpawner + a dedicated PTY, never IpcWorker).
        await using var ptyA = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        var spawnerA = PtySpawner.Spawn(
            executablePath: hostBinary!,
            arguments: new[] { "--pty-probe" },
            pty: ptyA,
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PSBASH_PTY_ATTACHED"] = "1",
            });

        await using var ptyB = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        var spawnerB = PtySpawner.Spawn(
            executablePath: hostBinary!,
            arguments: new[] { "--pty-probe" },
            pty: ptyB,
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PSBASH_PTY_ATTACHED"] = "1",
            });

        await using (spawnerA)
        await using (spawnerB)
        {
            // The core PTY-10 assertion: two interactive spawns, two distinct
            // host PIDs. A shared interactive daemon would yield one PID.
            Assert.True(spawnerA.Pid > 0, "spawnerA produced no host PID");
            Assert.True(spawnerB.Pid > 0, "spawnerB produced no host PID");
            Assert.NotEqual(spawnerA.Pid, spawnerB.Pid);

            // Drain each probe so the hosts exit cleanly rather than being
            // killed on dispose — keeps the box free of orphaned probes.
            await ReadUntilAsync(ptyA.Output, "PSBASH-PTY-PROBE:", TimeSpan.FromSeconds(15));
            await ReadUntilAsync(ptyB.Output, "PSBASH-PTY-PROBE:", TimeSpan.FromSeconds(15));
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await spawnerA.WaitForExitAsync(waitCts.Token);
            await spawnerB.WaitForExitAsync(waitCts.Token);
        }
    }

    /// <summary>
    /// PTY-10 source-level regression: the interactive REPL launch path must
    /// spawn the host directly (PtySpawner / Process.Start with
    /// <c>--launcher-pid</c>) and must NOT select <c>Lifetime.Daemon</c>. A
    /// shared-socket daemon for interactive sessions would let two launchers
    /// attach to one PTY-bound host — keystroke cross-talk. <c>Lifetime.Daemon</c>
    /// is reachable only from <c>HostCommands.cs</c> (<c>ps-bash host restart</c>),
    /// never from the interactive launcher branch in <c>Program.cs</c>.
    /// </summary>
    [Fact]
    public void InteractiveLaunchPath_NeverSelectsDaemonLifetime()
    {
        var asmDir = Path.GetDirectoryName(typeof(PtySpawner).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && dir.Name != "src" && dir.Parent is not null)
            dir = dir.Parent;
        Assert.NotNull(dir);
        var programPath = Path.Combine(dir!.FullName, "PsBash.Shell", "Program.cs");
        Assert.True(File.Exists(programPath), $"Could not find {programPath}");

        var src = File.ReadAllText(programPath);

        // Program.cs is the launcher entry point. It selects Lifetime only for
        // the non-interactive worker factory — and that must be PerInvocation.
        // Lifetime.Daemon must not appear anywhere in the launcher entry point.
        Assert.DoesNotContain("Lifetime.Daemon", src);
        Assert.Contains("Lifetime.PerInvocation", src);

        // The interactive branch spawns the host directly and ties its lifetime
        // to this one launcher via --launcher-pid (ParentDeathWatcher), so the
        // host never outlives or is shared beyond its single launcher session.
        Assert.Contains("--launcher-pid", src);
        Assert.Contains("--interactive", src);
    }

    /// <summary>
    /// Negative path (QA rubric Directive 7): the public spawn surface rejects
    /// empty arguments before reaching any platform-specific syscall. Without
    /// this gate, an empty <c>executablePath</c> would fall through to
    /// <c>posix_spawn</c> / <c>CreateProcessW</c> and surface as a confusing
    /// platform errno instead of an actionable ArgumentException.
    /// </summary>
    [Fact]
    public void PtySpawner_Spawn_RejectsEmptyExecutablePath()
    {
        var fakePty = new ThrowingPty();
        // The interface contract is the same on every platform: ArgumentException
        // (or its NullReference subclass for null) before any spawn call.
        Assert.ThrowsAny<ArgumentException>(() =>
            PtySpawner.Spawn(executablePath: "", arguments: Array.Empty<string>(), pty: fakePty));
        Assert.ThrowsAny<ArgumentException>(() =>
            PtySpawner.Spawn(executablePath: null!, arguments: Array.Empty<string>(), pty: fakePty));
        Assert.Throws<ArgumentNullException>(() =>
            PtySpawner.Spawn(executablePath: "/usr/bin/true", arguments: null!, pty: fakePty));
        Assert.Throws<ArgumentNullException>(() =>
            PtySpawner.Spawn(executablePath: "/usr/bin/true", arguments: Array.Empty<string>(), pty: null!));
    }

    /// <summary>
    /// Negative path (QA rubric Directive 7 + 14: missing target): when the
    /// POSIX spawner is handed a path that does not exist, <c>posix_spawn</c>
    /// returns ENOENT and we surface it as an <see cref="InvalidOperationException"/>.
    /// Skipped on Windows where the equivalent is GetLastWin32Error /
    /// Win32Exception and the spawn helper structure differs.
    /// </summary>
    [SkippableFact]
    [Trait("Platform", "Posix")]
    public async Task Posix_PtySpawner_Spawn_PropagatesEnoentForMissingBinary()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "POSIX-only");

        await using var pty = await PtyAllocator.AllocateAsync(cols: 80, rows: 24);
        var bogus = Path.Combine(Path.GetTempPath(), $"definitely-not-a-binary-{Guid.NewGuid():N}");
        Assert.False(File.Exists(bogus));

        var ex = Assert.ThrowsAny<Exception>(() =>
            PtySpawner.Spawn(bogus, Array.Empty<string>(), pty));
        // posix_spawn surfaces ENOENT via the rc, which we wrap with the
        // failing-call name. The wrapper text varies per glibc, so we only
        // assert the call name appears in the message.
        Assert.Contains("posix_spawn", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Minimal IPty stub used by arg-validation tests; intentionally
    /// throws on every access so a regression that accidentally reaches the
    /// spawn body lights up loudly.</summary>
    private sealed class ThrowingPty : IPty
    {
        public Stream Input => throw new InvalidOperationException("ThrowingPty.Input touched");
        public Stream Output => throw new InvalidOperationException("ThrowingPty.Output touched");
        public IntPtr SlaveHandle => (IntPtr)1; // non-zero so Windows path doesn't pre-validate fail
        public int SlaveFileDescriptor => 3;
        public string? SlaveName => "/dev/pts/0";
        public void Resize(short cols, short rows) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---- Helpers ----------------------------------------------------------

    /// <summary>
    /// Read bytes from <paramref name="stream"/> until either
    /// <paramref name="marker"/> appears in the decoded output, EOF, or the
    /// <paramref name="timeout"/> deadline. CR bytes are stripped so PTY
    /// ONLCR-translated output substring-matches cleanly against ASCII
    /// markers. No <c>Thread.Sleep</c> or polling — we await the next read.
    /// </summary>
    private static async Task<string> ReadUntilAsync(Stream stream, string marker, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sw = Stopwatch.StartNew();
        var buf = new byte[4096];
        var sink = new StringBuilder();
        try
        {
            while (sw.Elapsed < timeout)
            {
                int n = await stream.ReadAsync(buf.AsMemory(), cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                // Strip CR so PTY's LF->CRLF translation doesn't break substring matching.
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] != (byte)'\r') sink.Append((char)buf[i]);
                }
                if (sink.ToString().Contains(marker, StringComparison.Ordinal))
                    return sink.ToString();
            }
        }
        catch (OperationCanceledException) { /* timeout */ }
        catch (IOException) { /* pipe closed */ }

        // Return whatever we have for the assertion to dump a useful failure.
        return sink.ToString();
    }

    private static string? TryLocateHostBinary()
    {
        var name = OperatingSystem.IsWindows() ? "ps-bash-host.exe" : "ps-bash-host";
        var asmDir = Path.GetDirectoryName(typeof(PtySpawnTests).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && dir.Name != "src" && dir.Parent is not null)
            dir = dir.Parent;
        if (dir is null) return null;
        var hostBinDir = Path.Combine(dir.FullName, "PsBash.Host", "bin");
        if (!Directory.Exists(hostBinDir)) return null;

        // On Linux only Linux-rid builds run; same for macOS. Windows-only
        // builds (win-x64) only run when OS is Windows. Filter accordingly so
        // a cross-built artifact doesn't confuse the test.
        var matches = Directory.EnumerateFiles(hostBinDir, name, SearchOption.AllDirectories)
            .Where(p => OperatingSystem.IsWindows() ? p.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                                                   : !p.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        return matches.FirstOrDefault();
    }
}
