using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PsBash.Core.Platform;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Core.Runtime.Processes;

public sealed partial class JobProcess
{
    // POSIX state. _reaped flips (under _gate) the moment waitpid returns: before that the pid cannot have
    // been recycled, so signalling the group is safe; after it a group kill is only allowed shortly afterwards
    // and only while the group still exists (see SignalPosix).
    private bool _reaped;
    private long _reapedAtTicks;
    private static readonly TimeSpan PostExitGroupKillWindow = TimeSpan.FromSeconds(30);
    // False when the child stayed in this process's group (JobProcessStart.OwnProcessGroup): signals then go to the
    // leader, and a kill also takes what is still linked to it by parent pid.
    private bool _ownGroup = true;

    /// <summary>The shell wrapper used where libc has no <c>posix_spawn_file_actions_addchdir_np</c>.</summary>
    internal const string ChdirExecScript = "cd -- \"$1\" || exit 126; shift; exec \"$@\"";

    private static readonly bool HasAddChdir = ProbeAddChdir();

    private static bool ProbeAddChdir()
    {
        try
        {
            return NativeLibrary.TryLoad("libc", typeof(JobProcess).Assembly, null, out var libc)
                && NativeLibrary.TryGetExport(libc, "posix_spawn_file_actions_addchdir_np", out _);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// True when this process is the foreground job of its controlling terminal. A child put in its own process
    /// group there would be a BACKGROUND job of that terminal: reading it stops the child (SIGTTIN). Such a child
    /// must stay in this group, where the terminal's own Ctrl+C already reaches the whole tree.
    /// </summary>
    public static bool IsTerminalForegroundJob()
    {
        if (OperatingSystem.IsWindows()) return false;
        int fd = -1;
        try
        {
            fd = Posix.open(PsBash.Core.DevicePath.Tty, 2 /* O_RDWR */);
            if (fd < 0) return false; // no controlling terminal
            int foreground = Posix.tcgetpgrp(fd);
            return foreground > 0 && foreground == Posix.getpgrp();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        finally { if (fd >= 0) Posix.close(fd); }
    }

    private static JobProcess StartPosix(JobProcessStart spec)
    {
        bool chdir = !string.IsNullOrEmpty(spec.WorkingDirectory);
        bool chdirBySh = chdir && !HasAddChdir;
        string program = chdirBySh ? "/bin/sh" : spec.FileName;
        var argv = new List<IntPtr> { Utf8(program) };
        if (chdirBySh)
        {
            argv.Add(Utf8("-c")); argv.Add(Utf8(ChdirExecScript)); argv.Add(Utf8("sh"));
            argv.Add(Utf8(spec.WorkingDirectory!)); argv.Add(Utf8(spec.FileName));
        }
        foreach (var a in spec.Arguments) argv.Add(Utf8(a));
        argv.Add(IntPtr.Zero);

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!spec.EnvironmentIsComplete)
            foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
                if (e.Key is string k && e.Value is string v) merged[k] = v;
        if (spec.Environment is not null)
            foreach (var (k, v) in spec.Environment)
            {
                if (v is null) merged.Remove(k);
                else merged[k] = v;
            }
        var envp = new List<IntPtr>(merged.Count + 1);
        foreach (var (k, v) in merged) envp.Add(Utf8(k + "=" + v));
        envp.Add(IntPtr.Zero);

        var path = Utf8(program);
        var nullPath = Utf8(PsBash.Core.DevicePath.Null);
        var cwdPath = chdir && !chdirBySh ? Utf8(spec.WorkingDirectory!) : IntPtr.Zero;
        var argvBlock = PointerArray(argv);
        var envpBlock = PointerArray(envp);
        var actions = Marshal.AllocHGlobal(Posix.SpawnStructSize);
        var attr = Marshal.AllocHGlobal(Posix.SpawnStructSize);
        var sigDefault = Marshal.AllocHGlobal(Posix.SpawnStructSize);
        var sigMask = Marshal.AllocHGlobal(Posix.SpawnStructSize);
        bool actionsInit = false, attrInit = false;
        try
        {
            Check("posix_spawn_file_actions_init", Posix.posix_spawn_file_actions_init(actions)); actionsInit = true;
            // fd 0/1/2 are the caller's handles (dup2: the copies are not close-on-exec) or /dev/null (O_RDWR);
            // every other descriptor is CLOEXEC in .NET. An inherited stdin gets no action at all.
            var stdio = spec.Stdio ?? default;
            IntPtr?[] given = [stdio.Stdin, stdio.Stdout, stdio.Stderr];
            for (int fd = 0; fd <= 2; fd++)
            {
                if (given[fd] is { } handle)
                    Check("adddup2", Posix.posix_spawn_file_actions_adddup2(actions, (int)handle, fd));
                else if (fd != 0 || !stdio.InheritStdin)
                    Check("addopen", Posix.posix_spawn_file_actions_addopen(actions, fd, nullPath, 2 /* O_RDWR */, 0));
            }
            if (cwdPath != IntPtr.Zero)
                Check("addchdir", Posix.posix_spawn_file_actions_addchdir_np(actions, cwdPath));

            Check("posix_spawnattr_init", Posix.posix_spawnattr_init(attr)); attrInit = true;
            // Own process group (pgid = pid) so signals reach the whole tree; default dispositions (the runtime
            // may have SIGPIPE ignored, which exec would otherwise pass on); empty signal mask.
            Check("sigfillset", Posix.sigfillset(sigDefault));
            Check("setsigdefault", Posix.posix_spawnattr_setsigdefault(attr, sigDefault));
            Check("sigemptyset", Posix.sigemptyset(sigMask));
            Check("setsigmask", Posix.posix_spawnattr_setsigmask(attr, sigMask));
            if (spec.OwnProcessGroup) Check("setpgroup", Posix.posix_spawnattr_setpgroup(attr, 0));
            Check("setflags", Posix.posix_spawnattr_setflags(attr,
                (short)((spec.OwnProcessGroup ? Posix.SetPGroup : 0) | Posix.SetSigDef | Posix.SetSigMask)));

            int rc = Posix.posix_spawnp(out int pid, path, actions, attr, argvBlock, envpBlock);
            if (rc != 0) throw Failure($"posix_spawnp('{spec.FileName}')", rc);

            // Never throws (a setuid child such as macOS /bin/ps refuses the read): the waiter below still reaps it.
            var identity = ProcessIdentity.Of(pid);

            var job = new JobProcess(pid, identity) { _ownGroup = spec.OwnProcessGroup };
            var waiter = new Thread(() => job.ReapLoop()) { IsBackground = true, Name = $"job-wait-{pid}" };
            waiter.Start();
            return job;
        }
        finally
        {
            if (actionsInit) Posix.posix_spawn_file_actions_destroy(actions);
            if (attrInit) Posix.posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(actions); Marshal.FreeHGlobal(attr);
            Marshal.FreeHGlobal(sigDefault); Marshal.FreeHGlobal(sigMask);
            Marshal.FreeHGlobal(argvBlock); Marshal.FreeHGlobal(envpBlock);
            Marshal.FreeHGlobal(path); Marshal.FreeHGlobal(nullPath);
            if (cwdPath != IntPtr.Zero) Marshal.FreeHGlobal(cwdPath);
            foreach (var p in argv) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            foreach (var p in envp) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>Blocks in waitpid so the child is always reaped (no zombies), then publishes the status.</summary>
    private void ReapLoop()
    {
        int status = 0;
        while (true)
        {
            int rc = Posix.waitpid(Pid, out status, 0);
            if (rc == Pid) break;
            int err = Marshal.GetLastPInvokeError();
            if (rc < 0 && err == Posix.EINTR) continue;
            status = unchecked((int)0xFF00); // ECHILD: someone else reaped it; report 255 rather than a false success
            break;
        }
        lock (_gate)
        {
            _reaped = true;
            _reapedAtTicks = Stopwatch.GetTimestamp();
        }
        // WIFEXITED -> WEXITSTATUS; WIFSIGNALED -> 128 + the signal's bash number (the shell convention).
        int code = OsSignals.ExitStatusOf(status);
        PublishExit(code);
    }

    private bool SignalPosix(JobSignal signal)
    {
        int sig = signal == JobSignal.Kill ? Posix.SIGKILL : Posix.SIGTERM;
        return SendToGroup(sig);
    }

    private bool SuspendPosix(bool suspend) => SendToGroup(suspend ? Posix.SIGSTOP : Posix.SIGCONT);

    // Caller holds _gate.
    private bool SendToGroup(int sig)
    {
        if (!_ownGroup)
        {
            if (_reaped) return false; // the pid may be someone else's now, and there is no group to find the rest by
            if (sig != Posix.SIGKILL) return Posix.kill(Pid, sig) == 0;
            try
            {
                // No group of its own (this process is its terminal's foreground job): the leader and whatever is
                // still linked to it by parent pid. The terminal's Ctrl+C is what reaches the whole group there.
                using var leader = Process.GetProcessById(Pid);
                leader.Kill(entireProcessTree: true);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
            {
                return Posix.kill(Pid, sig) == 0;
            }
        }
        if (_reaped)
        {
            // The leader is gone and its pid may have been recycled. The group (its descendants) can still exist;
            // only touch it for a short while after exit and only if it demonstrably still has members.
            if (Stopwatch.GetElapsedTime(_reapedAtTicks) > PostExitGroupKillWindow) return false;
            if (Posix.kill(-Pid, 0) != 0) return false;
        }
        return Posix.kill(-Pid, sig) == 0;
    }

    /// <summary>Live members of the process group (<see cref="PsBash.Core.Platform.ProcessInfo.GroupMembers"/>: Linux /proc,
    /// macOS libproc; zombies excluded). Caller holds <c>_gate</c>.</summary>
    private IReadOnlyList<int> TreePidsPosix()
    {
        if (!_ownGroup)
        {
            var pids = new List<int>();
            if (!_reaped) pids.Add(Pid);
            return pids;
        }
        return PsBash.Core.Platform.ProcessInfo.GroupMembers(Pid);
    }

    private static IntPtr Utf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte(p, bytes.Length, 0);
        return p;
    }

    private static IntPtr PointerArray(List<IntPtr> pointers)
    {
        var block = Marshal.AllocHGlobal(IntPtr.Size * pointers.Count);
        Marshal.Copy(pointers.ToArray(), 0, block, pointers.Count);
        return block;
    }

    private static void Check(string call, int rc)
    {
        if (rc != 0) throw Failure(call, rc);
    }

    private static class Posix
    {
        // Sizes are upper bounds for glibc/musl/macOS opaque types (posix_spawn_file_actions_t, posix_spawnattr_t, sigset_t).
        public const int SpawnStructSize = 1024;
        // Values identical on Linux (glibc, musl) and macOS.
        public const int SetPGroup = 0x02, SetSigDef = 0x04, SetSigMask = 0x08;
        // OS numbers for kill(): the one signal table (Platform/OsSignals) knows where macOS differs.
        public static readonly int SIGTERM = OsSignals.ToNative(OsSignals.SIGTERM), SIGKILL = OsSignals.ToNative(OsSignals.SIGKILL);
        public static readonly int SIGSTOP = OsSignals.ToNative(OsSignals.SIGSTOP);
        public static readonly int SIGCONT = OsSignals.ToNative(OsSignals.SIGCONT);
        public const int EINTR = 4;

        [DllImport("libc", SetLastError = true)] public static extern int kill(int pid, int sig);
        [DllImport("libc", SetLastError = true)] public static extern int waitpid(int pid, out int status, int options);
        [DllImport("libc")] public static extern int posix_spawnp(out int pid, IntPtr file, IntPtr actions, IntPtr attr, IntPtr argv, IntPtr envp);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_init(IntPtr a);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_destroy(IntPtr a);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_addopen(IntPtr a, int fd, IntPtr path, int oflag, int mode);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_adddup2(IntPtr a, int fd, int newfd);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_addchdir_np(IntPtr a, IntPtr path);
        [DllImport("libc", SetLastError = true)] public static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] public static extern int tcgetpgrp(int fd);
        [DllImport("libc")] public static extern int getpgrp();
        [DllImport("libc")] public static extern int posix_spawnattr_init(IntPtr a);
        [DllImport("libc")] public static extern int posix_spawnattr_destroy(IntPtr a);
        [DllImport("libc")] public static extern int posix_spawnattr_setflags(IntPtr a, short flags);
        [DllImport("libc")] public static extern int posix_spawnattr_setpgroup(IntPtr a, int pgroup);
        [DllImport("libc")] public static extern int posix_spawnattr_setsigdefault(IntPtr a, IntPtr set);
        [DllImport("libc")] public static extern int posix_spawnattr_setsigmask(IntPtr a, IntPtr set);
        [DllImport("libc")] public static extern int sigfillset(IntPtr set);
        [DllImport("libc")] public static extern int sigemptyset(IntPtr set);
    }
}
