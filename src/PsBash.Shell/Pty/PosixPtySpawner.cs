using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PsBash.Shell.Pty;

/// <summary>
/// POSIX <see cref="PtySpawner"/> implementation using
/// <c>posix_spawn</c> + file actions + <c>POSIX_SPAWN_SETSID</c>.
///
/// <para><b>Why posix_spawn instead of fork+exec:</b> calling <c>fork()</c>
/// from a multi-threaded managed process is unsafe. The .NET runtime spawns
/// auxiliary threads (GC, JIT, finalizer, timer); after <c>fork()</c> only
/// the calling thread exists in the child, but internal locks held by other
/// threads remain locked. Any P/Invoke into libc from the child can then
/// deadlock. <c>posix_spawn</c> sidesteps the problem entirely — the kernel
/// (or vfork-like helper) runs the file-action list and exec call in a
/// fresh address space without inheriting our runtime state.</para>
///
/// <para><b>Controlling-tty acquisition:</b> The trick that makes this work
/// without an explicit <c>ioctl(TIOCSCTTY)</c> is:</para>
/// <list type="number">
///   <item><description><c>POSIX_SPAWN_SETSID</c> makes the child a session
///     leader of a new session with no controlling terminal (glibc 2.26+
///     on Linux, macOS 11+, FreeBSD 12+).</description></item>
///   <item><description>The file actions then <c>open()</c> the slave
///     device path (<see cref="IPty.SlaveName"/>) <i>without</i>
///     <c>O_NOCTTY</c>. Per <c>tty(7)</c>: a session leader opening a
///     terminal without <c>O_NOCTTY</c> automatically acquires it as its
///     controlling terminal.</description></item>
///   <item><description>The opened fd is <c>dup2</c>'d onto stdin / stdout /
///     stderr and the original closed.</description></item>
/// </list>
///
/// <para>This is the contract called out in <c>docs/specs/pty.md</c>: the
/// child becomes a session leader and owns the slave as its controlling tty
/// before its <c>main()</c> executes.</para>
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed partial class PosixPtySpawner : PtySpawner
{
    private readonly int _pid;
    private int _exited;       // 0 = not yet, 1 = reaped (interlocked)
    private int _cachedStatus; // populated once Wait succeeds

    private PosixPtySpawner(int pid) { _pid = pid; }

    public override int Pid => _pid;

    public static PtySpawner SpawnInternal(
        string executablePath,
        IReadOnlyList<string> arguments,
        IPty pty,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (pty.SlaveName is not string slaveName || slaveName.Length == 0)
            throw new InvalidOperationException(
                "POSIX PTY spawn requires IPty.SlaveName to be a non-empty device path " +
                "(for example, /dev/pts/3). Got null/empty.");

        // ---- Pre-fork: build argv / envp / native buffers in managed land. ----

        // argv: [executablePath, args..., NULL]
        var argv = new IntPtr[arguments.Count + 2];
        var pinnedArgvBufs = new List<GCHandle>(arguments.Count + 1);
        var pinnedEnvpBufs = new List<GCHandle>();
        IntPtr argvBlock = IntPtr.Zero;
        IntPtr envpBlock = IntPtr.Zero;
        IntPtr pathPtr = IntPtr.Zero;
        IntPtr slaveNamePtr = IntPtr.Zero;
        IntPtr fileActions = IntPtr.Zero;
        IntPtr attr = IntPtr.Zero;
        bool fileActionsInit = false;
        bool attrInit = false;

        try
        {
            pathPtr = MarshalNullTerm(executablePath);
            slaveNamePtr = MarshalNullTerm(slaveName);

            argv[0] = MarshalPinNullTerm(executablePath, pinnedArgvBufs);
            for (int i = 0; i < arguments.Count; i++)
                argv[i + 1] = MarshalPinNullTerm(arguments[i], pinnedArgvBufs);
            argv[arguments.Count + 1] = IntPtr.Zero;

            // envp: merge current env + overrides, then "K=V\0" entries + NULL.
            var mergedEnv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            {
                if (kv.Key is string k && kv.Value is string v) mergedEnv[k] = v;
            }
            if (environment is not null)
            {
                foreach (var kv in environment) mergedEnv[kv.Key] = kv.Value;
            }

            var envp = new IntPtr[mergedEnv.Count + 1];
            int idx = 0;
            foreach (var kv in mergedEnv)
                envp[idx++] = MarshalPinNullTerm(kv.Key + "=" + kv.Value, pinnedEnvpBufs);
            envp[mergedEnv.Count] = IntPtr.Zero;

            argvBlock = MarshalPointerArray(argv);
            envpBlock = MarshalPointerArray(envp);

            // ---- File actions ----
            fileActions = Marshal.AllocHGlobal(NativeMethods.PosixSpawnFileActionsSize);
            CheckRc("posix_spawn_file_actions_init",
                NativeMethods.posix_spawn_file_actions_init(fileActions));
            fileActionsInit = true;

            // 1) open slave (without O_NOCTTY!) into fd 3.
            //    O_RDWR=2 on all POSIX. We deliberately omit O_NOCTTY so that
            //    after POSIX_SPAWN_SETSID the kernel attaches the slave as
            //    the child's controlling tty.
            CheckRc("posix_spawn_file_actions_addopen(slave -> 3)",
                NativeMethods.posix_spawn_file_actions_addopen(
                    fileActions, 3, slaveNamePtr, 0x002 /* O_RDWR */, 0));

            // 2) dup2(3, 0/1/2)
            CheckRc("posix_spawn_file_actions_adddup2(3 -> 0)",
                NativeMethods.posix_spawn_file_actions_adddup2(fileActions, 3, 0));
            CheckRc("posix_spawn_file_actions_adddup2(3 -> 1)",
                NativeMethods.posix_spawn_file_actions_adddup2(fileActions, 3, 1));
            CheckRc("posix_spawn_file_actions_adddup2(3 -> 2)",
                NativeMethods.posix_spawn_file_actions_adddup2(fileActions, 3, 2));

            // 3) close fd 3.
            CheckRc("posix_spawn_file_actions_addclose(3)",
                NativeMethods.posix_spawn_file_actions_addclose(fileActions, 3));

            // ---- Attributes ----
            attr = Marshal.AllocHGlobal(NativeMethods.PosixSpawnAttrSize);
            CheckRc("posix_spawnattr_init", NativeMethods.posix_spawnattr_init(attr));
            attrInit = true;

            // POSIX_SPAWN_SETSID: child becomes session leader.
            // POSIX_SPAWN_SETSIGMASK: reset sigmask to a clean empty mask so
            // signals are deliverable to the child.
            const short POSIX_SPAWN_SETSIGMASK = 0x08;
            short POSIX_SPAWN_SETSID = NativeMethods.PosixSpawnSetsidFlag;
            short flags = (short)(POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSID);

            // Empty sigmask. sigset_t is opaque; allocate a buffer big enough.
            using var sigmask = NativeMemoryBuffer.Alloc(NativeMethods.SigsetSize);
            CheckRc("sigemptyset", NativeMethods.sigemptyset(sigmask.Ptr));
            CheckRc("posix_spawnattr_setsigmask",
                NativeMethods.posix_spawnattr_setsigmask(attr, sigmask.Ptr));
            CheckRc("posix_spawnattr_setflags",
                NativeMethods.posix_spawnattr_setflags(attr, flags));

            // ---- Spawn ----
            int rc = NativeMethods.posix_spawn(
                out int pid, pathPtr, fileActions, attr, argvBlock, envpBlock);
            if (rc != 0)
            {
                throw new InvalidOperationException(
                    $"posix_spawn failed (rc={rc}): {Marshal.GetPInvokeErrorMessage(rc)}");
            }

            return new PosixPtySpawner(pid);
        }
        finally
        {
            if (fileActionsInit && fileActions != IntPtr.Zero)
                NativeMethods.posix_spawn_file_actions_destroy(fileActions);
            if (fileActions != IntPtr.Zero) Marshal.FreeHGlobal(fileActions);
            if (attrInit && attr != IntPtr.Zero)
                NativeMethods.posix_spawnattr_destroy(attr);
            if (attr != IntPtr.Zero) Marshal.FreeHGlobal(attr);

            if (argvBlock != IntPtr.Zero) Marshal.FreeHGlobal(argvBlock);
            if (envpBlock != IntPtr.Zero) Marshal.FreeHGlobal(envpBlock);
            if (pathPtr != IntPtr.Zero) Marshal.FreeHGlobal(pathPtr);
            if (slaveNamePtr != IntPtr.Zero) Marshal.FreeHGlobal(slaveNamePtr);

            foreach (var h in pinnedArgvBufs) if (h.IsAllocated) h.Free();
            foreach (var h in pinnedEnvpBufs) if (h.IsAllocated) h.Free();
        }
    }

    private static void CheckRc(string call, int rc)
    {
        if (rc != 0)
            throw new InvalidOperationException(
                $"{call} failed (rc={rc}): {Marshal.GetPInvokeErrorMessage(rc)}");
    }

    private static IntPtr MarshalNullTerm(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte(p, bytes.Length, 0);
        return p;
    }

    private static IntPtr MarshalPinNullTerm(string s, List<GCHandle> pins)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var buf = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, buf, 0, bytes.Length);
        buf[bytes.Length] = 0;
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        pins.Add(h);
        return h.AddrOfPinnedObject();
    }

    private static IntPtr MarshalPointerArray(IntPtr[] pointers)
    {
        IntPtr block = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
        Marshal.Copy(pointers, 0, block, pointers.Length);
        return block;
    }

    public override async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _exited) != 0)
            return DecodeStatus(_cachedStatus);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int rc = NativeMethods.waitpid(_pid, out int status, NativeMethods.WNOHANG);
            if (rc == _pid)
            {
                _cachedStatus = status;
                Volatile.Write(ref _exited, 1);
                return DecodeStatus(status);
            }
            if (rc < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == NativeMethods.ECHILD)
                {
                    // No such child / already reaped (e.g. SIGCHLD inherited).
                    Volatile.Write(ref _exited, 1);
                    return 0;
                }
                if (err == NativeMethods.EINTR) continue;
                throw new InvalidOperationException(
                    $"waitpid failed (errno={err}): {Marshal.GetPInvokeErrorMessage(err)}");
            }

            try { await Task.Delay(20, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
    }

    private static int DecodeStatus(int status)
    {
        // WIFEXITED(status): (status & 0x7f) == 0; WEXITSTATUS = (status >> 8) & 0xff
        if ((status & 0x7f) == 0)
            return (status >> 8) & 0xff;
        // WIFSIGNALED: bash convention 128 + signum
        return 128 + (status & 0x7f);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _exited) != 0) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* still running; leave to OS reaper */ }
        catch (InvalidOperationException) { /* race; ignore */ }
    }

    // -- tiny RAII for raw bytes ---------------------------------------------

    private sealed class NativeMemoryBuffer : IDisposable
    {
        public IntPtr Ptr { get; private set; }
        private NativeMemoryBuffer(IntPtr p) { Ptr = p; }
        public static NativeMemoryBuffer Alloc(int size)
        {
            var p = Marshal.AllocHGlobal(size);
            // zero-init so unset bytes don't look like real flags
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            return new NativeMemoryBuffer(p);
        }
        public void Dispose()
        {
            if (Ptr != IntPtr.Zero) { Marshal.FreeHGlobal(Ptr); Ptr = IntPtr.Zero; }
        }
    }

    // -- libc PInvoke --------------------------------------------------------

    private static partial class NativeMethods
    {
        private const string LibC = "libc";

        public const int ECHILD = 10;
        public const int EINTR = 4;
        public const int WNOHANG = 1;

        // POSIX_SPAWN_SETSID:
        //   Linux (glibc):     0x80
        //   macOS / FreeBSD:   0x400 (BSD libc family)
        public static short PosixSpawnSetsidFlag =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? (short)0x80
                : (short)0x400;

        // Opaque type sizes are over-provisioned to comfortably exceed the
        // platform layout on Linux, macOS, and FreeBSD. The libc functions
        // only ever read/write the prefix they understand. 1024 bytes is safe.
        public const int PosixSpawnFileActionsSize = 1024;
        public const int PosixSpawnAttrSize = 1024;
        public const int SigsetSize = 1024;

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_spawn_file_actions_init(IntPtr actions);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_spawn_file_actions_destroy(IntPtr actions);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_spawn_file_actions_addopen(
            IntPtr actions, int fd, IntPtr path, int oflag, uint mode);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_spawn_file_actions_adddup2(
            IntPtr actions, int fd, int newfd);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_spawn_file_actions_addclose(
            IntPtr actions, int fd);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_spawnattr_init(IntPtr attr);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_spawnattr_destroy(IntPtr attr);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_spawnattr_setflags(IntPtr attr, short flags);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_spawnattr_setsigmask(IntPtr attr, IntPtr sigmask);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int sigemptyset(IntPtr sigset);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int posix_spawn(
            out int pid,
            IntPtr path,
            IntPtr fileActions,
            IntPtr attr,
            IntPtr argv,
            IntPtr envp);

        [LibraryImport(LibC, SetLastError = true)]
        public static partial int waitpid(int pid, out int status, int options);
    }
}
