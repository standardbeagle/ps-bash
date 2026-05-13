using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;
using PsBash.Host.Server;
using PsBash.Host.Shell;

namespace PsBash.Host;

internal sealed class Program
{
    // POSIX dup2 / open imports for detaching inherited stdio. When the host
    // is spawned as a daemon by the launcher (PSBASH_HOST_DETACH=1), the
    // launcher's stdout/stderr file descriptors are inherited by this
    // process. The launcher then exits but the daemon keeps the inherited
    // write ends open, so any caller of the launcher that read its output
    // via a pipe never sees EOF — the read hangs forever. Replace fd 0/1/2
    // with /dev/null before any code path can write to them.
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true, EntryPoint = "readlink")]
    private static extern long readlink(string path, byte[] buf, ulong bufsiz);
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int fcntl(int fd, int cmd);
    private const int O_RDWR = 2;
    private const int F_GETFD = 1;

    private static string? ReadFdTarget(int fd)
    {
        var buf = new byte[256];
        var n = readlink($"/proc/self/fd/{fd}", buf, (ulong)buf.Length);
        if (n <= 0) return null;
        return System.Text.Encoding.UTF8.GetString(buf, 0, (int)n);
    }

    private static bool IsFdPipe(int fd)
    {
        // /proc/self/fd readlink returns "pipe:[N]" for pipes (Linux). macOS
        // /dev/fd readlink returns the actual path; pipes appear as
        // "/dev/fd/N" or similar. fstat would be cleanest but our libc
        // P/Invoke surface is already wide enough — match on the Linux
        // form and fall back to "any non-path target" on macOS via the
        // anon_inode hint that .NET runtime fds tend to expose.
        var target = ReadFdTarget(fd);
        if (target is null) return false;
        if (target.StartsWith("pipe:", StringComparison.Ordinal)) return true;
        if (target.StartsWith("anon_inode:[eventfd]", StringComparison.Ordinal)) return false;
        if (target.StartsWith("anon_inode:[eventpoll]", StringComparison.Ordinal)) return false;
        if (target.StartsWith("anon_inode:", StringComparison.Ordinal)) return false;
        if (target.StartsWith("socket:", StringComparison.Ordinal)) return false;
        if (target.StartsWith("/", StringComparison.Ordinal)) return false;
        return false;
    }

    private static void DetachInheritedStdioIfRequested()
    {
        if (OperatingSystem.IsWindows()) return;
        if (Environment.GetEnvironmentVariable("PSBASH_HOST_DETACH") != "1") return;
        try
        {
            var nullFd = open("/dev/null", O_RDWR);
            if (nullFd < 0) return;
            dup2(nullFd, 0);
            dup2(nullFd, 1);
            dup2(nullFd, 2);
            if (nullFd > 2) close(nullFd);

            // Close inherited pipe fds at 3+. .NET's Process.Start +
            // Console subsystem in the launcher leaks duplicates of the
            // launcher's stdout/stderr pipes into fds 3+ of this host
            // process (verified via /proc/<host>/fd while a test hung).
            // Without closing them, the daemon keeps the test runner's
            // pipe write ends open after the launcher exits and the
            // test's ReadToEndAsync never sees EOF.
            //
            // Only close fds that resolve to "pipe:[...]" so we don't
            // touch .NET runtime fds that resolve to /memfd:, regular
            // files, or sockets (event pipe, debugger pipe, telemetry).
            try
            {
                if (System.IO.Directory.Exists("/proc/self/fd"))
                {
                    foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries("/proc/self/fd"))
                    {
                        var name = System.IO.Path.GetFileName(entry);
                        if (!int.TryParse(name, out var fd)) continue;
                        if (fd < 3) continue;
                        if (IsFdPipe(fd))
                            close(fd);
                    }
                }
                else
                {
                    // macOS / FreeBSD have no /proc; close pipe-like fds in
                    // [3, 256) by querying each one. F_GETFD on an unopen
                    // fd returns -1 with errno=EBADF so we skip closed slots.
                    for (int fd = 3; fd < 256; fd++)
                    {
                        if (fcntl(fd, F_GETFD) < 0) continue;
                        close(fd);
                    }
                }
            }
            catch { /* best effort */ }
        }
        catch { /* best effort — daemon stdio detach */ }
    }

    static async Task<int> Main(string[] args)
    {
        DetachInheritedStdioIfRequested();
        // PTY-2 probe mode: spawned by PtySpawnTests to verify the host's
        // System.Console is wired to a real terminal when invoked through
        // PtySpawner. Writes a single marker line to stdout and exits 0.
        // No runspace, no IPC — just enough to assert IsInputRedirected /
        // IsOutputRedirected and observe the PSBASH_PTY_ATTACHED hand-off
        // env var. Intentionally runs BEFORE --interactive handling so a
        // misconfigured CI environment cannot accidentally enter the REPL.
        if (args.Contains("--pty-probe"))
        {
            var inRedir = Console.IsInputRedirected;
            var outRedir = Console.IsOutputRedirected;
            var ptyEnv = Environment.GetEnvironmentVariable("PSBASH_PTY_ATTACHED") ?? "<unset>";
            Console.Out.WriteLine(
                $"PSBASH-PTY-PROBE: IsInputRedirected={inRedir} IsOutputRedirected={outRedir} PSBASH_PTY_ATTACHED={ptyEnv}");
            Console.Out.Flush();
            return 0;
        }

        // Interactive mode: host owns the tty; run the REPL directly. The
        // launcher (ps-bash with no -c) spawns us in this mode and waits.
        if (args.Contains("--interactive"))
        {
            var noProfile = args.Contains("--no-profile");
            int? iLauncherPid = GetArgInt(args, "--launcher-pid");

            var priorInteractive = Environment.GetEnvironmentVariable("PSBASH_INTERACTIVE");
            Environment.SetEnvironmentVariable("PSBASH_INTERACTIVE", "1");
            using var iCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; iCts.Cancel(); };
            using var iDeathWatcher = ParentDeathWatcher.TryCreate(iLauncherPid, iCts);

            try
            {
                await using var interactiveWorker = SdkWorker.Create();
                return await InteractiveShell.RunAsync(interactiveWorker, noProfile);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PSBASH_INTERACTIVE", priorInteractive);
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Start runspace init on a background thread — this is the slow part (~2-8 s).
        // The transport starts listening while the runspace warms up so clients
        // can connect immediately and queue work.
        var workerTask = Task.Run(SdkWorker.Create);

        var (transport, scheme, endpoint) = CreateTransport(args);

        var idleTimeout = IdleShutdown.DefaultTimeout;
        using var idle = new IdleShutdown(cts, idleTimeout);

        int? launcherPid = GetNonInteractiveLauncherPid(args);
        using var deathWatcher = ParentDeathWatcher.TryCreate(launcherPid, cts);

        await using var server = new HostServer(transport, workerTask, idle);

        try
        {
            var serverTask = server.RunAsync(cts.Token);
            await server.WhenListening;
            // Sidecar must be written AFTER bind succeeds — otherwise a launcher
            // racing with us would read metadata for a process that hasn't yet
            // claimed the endpoint. Per docs/specs/host-lifecycle-contract.md.
            WriteHostMetadata(scheme, endpoint);
            try { await serverTask; }
            finally { HostMetadata.Remove(scheme, endpoint); }
        }
        finally
        {
            try { var w = await workerTask; await w.DisposeAsync(); } catch { }
        }

        return 0;
    }

    private static void WriteHostMetadata(string scheme, string endpoint)
    {
        var meta = new HostMetadata(
            Pid: Environment.ProcessId,
            ExecutablePath: Environment.ProcessPath ?? "<unknown>",
            ProtocolVersion: HostProtocol.ProtocolVersion,
            BuildIdentity: HostProtocol.BuildIdentity,
            TransportScheme: scheme,
            Endpoint: endpoint,
            StartedAt: DateTimeOffset.UtcNow,
            Owner: Environment.UserName);
        meta.Write(scheme, endpoint);
    }

    private static (IIpcTransport Transport, string Scheme, string Endpoint) CreateTransport(string[] args)
    {
        // Scheme-specific overrides (used by tests) take precedence and bypass
        // the factory entirely so they cannot be shadowed by env vars.
        var socketPath = GetArg(args, "--socket");
        if (socketPath != null) return (new UnixSocketTransport(socketPath), "unix", socketPath);

        var pipeName = GetArg(args, "--pipe");
        if (pipeName != null) return (new NamedPipeTransport(pipeName), "pipe", pipeName);

        // --ipc-endpoint <scheme:endpoint> is the public/agent-facing override:
        // tests, WSL bash drivers, or anyone who needs an isolated host can
        // pass it on both the host and the launcher (via PSBASH_IPC_ENDPOINT)
        // to bind the same address. Falls through to the canonical endpoint
        // when absent.
        var ipcEndpoint = GetArg(args, "--ipc-endpoint");
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint(ipcEndpoint);
        return (IpcTransportFactory.CreateDefault(ipcEndpoint), scheme, endpoint);
    }

    // Accepts both `--name value` (two-token) and `--name=value` (single-token,
    // equals form). The actual launcher (src/PsBash.Shell/Program.cs) emits the
    // equals form for --launcher-pid, so the two-token-only variant silently
    // disarmed the parent-death watcher in production. See task Zjvk1UwhQiHG.
    private static string? GetArg(string[] args, string name)
    {
        var prefix = name + "=";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(prefix, StringComparison.Ordinal))
                return args[i].Substring(prefix.Length);
        }
        return null;
    }

    private static int? GetArgInt(string[] args, string name)
    {
        var val = GetArg(args, name);
        return int.TryParse(val, out var n) ? n : null;
    }

    internal static int? GetNonInteractiveLauncherPid(string[] args)
    {
        var launcherPid = GetArgInt(args, "--launcher-pid");
        if (launcherPid is not null) return launcherPid;

        var envValue = Environment.GetEnvironmentVariable("PSBASH_HOST_PARENT_PID");
        return int.TryParse(envValue, out var envPid) ? envPid : null;
    }
}
