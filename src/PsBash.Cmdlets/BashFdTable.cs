using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;
using System.Text;
using PsBash.Core.Stdio;

namespace PsBash.Cmdlets;

/// <summary>
/// The shell's ONE descriptor table, fds 0 to N, as bash has one per shell process. Spec:
/// docs/specs/shell-fds-and-bash-source.md section The descriptor table.
/// <list type="bullet">
/// <item><c>n&gt;&amp;m</c> COPIES what m names at that moment (dup2): a later change to m does not move n
/// (<see cref="Dup"/>). Redirections apply left to right.</item>
/// <item>No entry for 0, 1 or 2 means the shell's ORIGINAL descriptor: the host's stdin / stdout / stderr. A copy of one
/// (<c>exec 3&gt;&amp;1</c>) is a <see cref="Kind.Terminal"/> entry that writes there (<see cref="ShellStdio.Terminal"/>),
/// past whatever the writing command is under (<c>x=$(echo hi &gt;&amp;3)</c> prints hi).</item>
/// <item><c>exec</c> changes the table for the rest of the shell, in any context (function, loop, if, group, sourced
/// file, eval). An exec-set fd 1 / 2 takes what reaches the shell's original stdout / stderr through
/// <see cref="ShellStdio.Divert"/>.</item>
/// <item>A redirection stage (<c>Invoke-BashRedirect</c>) and a command substitution (<c>ConvertTo-BashCapture</c>) are
/// fd 1 / 2 of the command under them: they <see cref="Register"/> their target for its duration, so an
/// <c>exec 3&gt;&amp;1</c> inside copies THEM and an <c>exec &gt;f</c> inside moves what they would have received.</item>
/// <item>A subshell or command substitution gets a COPY of the table (<see cref="Save"/> / <see cref="Restore"/>, through
/// BashShellState): what it opens or closes is gone when it ends.</item>
/// </list>
/// One table per shell: per runspace (<see cref="Runspace.DefaultRunspace"/> of the running code), so an in-process
/// background job works on its own copy taken at the <c>&amp;</c> (<see cref="AttachCopy"/>), as a forked job does, and
/// two runspaces running at once (a job and the shell, parallel tests) never see each other's redirections. A new
/// invocation closes what the previous ones left open (<see cref="CloseAllShells"/>), so a daemon never accumulates
/// open files.
/// </summary>
internal static class BashFdTable
{
    internal enum Kind
    {
        /// <summary>A file opened by exec (read and / or write).</summary>
        File,
        /// <summary>A copy of the shell's original stdout (<see cref="Handle.Term"/> 1) or stderr (2).</summary>
        Terminal,
        /// <summary>The target of a redirection stage (a shared <see cref="RedirectFileWriter"/>).</summary>
        Stage,
        /// <summary>The null device of a redirection stage.</summary>
        Null,
        /// <summary>A closed fd 0, 1 or 2 (<c>exec &gt;&amp;-</c>): a write fails "Bad file descriptor".</summary>
        Closed,
        /// <summary>A name for the shell's own stdin (<c>exec 5&lt;&amp;0</c>).</summary>
        ShellStdin,
        /// <summary>The output of a command substitution.</summary>
        Capture,
        /// <summary>
        /// The stream a <c>2&gt;&amp;1</c> stage passes on (<see cref="RegisterMerge"/>): fd 1 of the command under it, and
        /// fd 2 as its copy. What is written to it through the table is passed on by the stage.
        /// </summary>
        Merge,
    }

    internal sealed class Handle
    {
        public Kind Kind { get; init; }
        public int Term { get; init; }
        public Stream? Stream { get; init; }
        public StreamReader? Reader { get; set; }
        public StreamWriter? Writer { get; set; }
        public RedirectFileWriter? Out { get; init; }
        public Action<string>? Sink { get; init; }
        /// <summary>The file a <see cref="Kind.File"/> entry opened (full path), null for a device or text.</summary>
        public string? FilePath { get; init; }
        public int Refs { get; set; } = 1;
    }

    /// <summary>One shell's descriptors. <see cref="Job"/>: a background job's, whose output does not reach the host's
    /// streams through <see cref="ShellStdio"/> (its fd 1 / 2 never divert the shell's).</summary>
    internal sealed class Table
    {
        internal readonly Handle?[] Low = new Handle?[3];
        internal readonly Dictionary<int, Handle> Fds = new();
        internal bool Job;
        internal Func<int, string, bool>? Divert;
        /// <summary>The registrations in force, oldest first (<see cref="Register"/>): the stages a running command is under.</summary>
        internal readonly List<Registration> Active = new();
        internal int ActiveCount;
    }

    /// <summary>A copy of the table (<see cref="Save"/>): one reference to each entry.</summary>
    internal sealed class Snapshot
    {
        internal Handle?[] Low = new Handle?[3];
        internal Dictionary<int, Handle> High = new();
        /// <summary><see cref="SaveFds"/>: the descriptors saved (the rest of the table is not part of this copy).</summary>
        internal int[]? Only;
    }

    /// <summary>A redirection stage's or capture's hold on fd 1 / 2 (<see cref="Register"/>): the table it is in (the stage
    /// may be stopped from another thread), the descriptor, and what it displaced.</summary>
    internal sealed class Registration
    {
        internal required Table Table { get; init; }
        internal required int Fd { get; init; }
        internal required Handle Handle { get; init; }
        internal Handle? Prior { get; init; }
        /// <summary>The stage does not redirect this descriptor, it only passes the stream on (the fd 1 of a
        /// <c>2&gt;&amp;1</c>): an <c>exec</c> inside that moved it stays when the stage ends, as in bash.</summary>
        internal bool Keep { get; init; }
        /// <summary>The descriptor's entry now (null = the original), without the lock: a stage checks it per record.</summary>
        internal Handle? Current => Volatile.Read(ref Table.Low[Fd]);
        internal bool Done;
    }

    private static readonly object Gate = new();
    // Writes the host dropped on this thread because the shell's fd 1 is closed (`exec >&-`). The divert runs on the
    // pipeline thread while the writing command is in WriteObject, so a command compares the count around its writes.
    [ThreadStatic] private static int t_closedStdoutWrites;

    /// <summary>
    /// How many writes reached a closed shell fd 1 on this thread (<c>exec &gt;&amp;-; echo x</c>). A builtin reads it
    /// before and after it writes; a change means its write failed (<see cref="InvokeBashFdRouteCommand.FailClosedStdout"/>).
    /// </summary>
    public static int ClosedStdoutWrites => t_closedStdoutWrites;

    // Code with no runspace on its thread (a test calling in directly).
    private static readonly Table Fallback = new();
    private static readonly ConditionalWeakTable<Runspace, Table> ByRunspace = new();
    private static readonly ConditionalWeakTable<Runspace, Table>.CreateValueCallback NewTable = _ => new Table();
    // Every shell table that is not a job's, for CloseAllShells (weak: a discarded runspace's table goes with it).
    private static readonly List<WeakReference<Table>> Shells = new();

    private const string StdUnsupported = "0: redirecting the shell's own stdin with exec is not supported";

    /// <summary>The running shell's table.</summary>
    private static Table Current
    {
        get
        {
            if (Runspace.DefaultRunspace is not { } rs) return Fallback;
            if (ByRunspace.TryGetValue(rs, out var t)) return t;
            lock (Gate)
            {
                if (ByRunspace.TryGetValue(rs, out t)) return t;
                t = ByRunspace.GetValue(rs, NewTable);
                Shells.RemoveAll(w => !w.TryGetTarget(out _));
                Shells.Add(new WeakReference<Table>(t));
                return t;
            }
        }
    }

    /// <summary>Open <paramref name="path"/> as <paramref name="fd"/> for <paramref name="op"/> (<c>&lt;</c>, <c>&gt;</c>,
    /// <c>&gt;|</c>, <c>&gt;&gt;</c>, <c>&lt;&gt;</c>). Any descriptor already there is closed first. Null on success, else the error text.</summary>
    public static string? Open(int fd, string path, string op)
    {
        if (fd == 0) return StdUnsupported;
        FileMode mode;
        FileAccess access;
        switch (op)
        {
            case "<": mode = FileMode.Open; access = FileAccess.Read; break;
            case ">":
            case ">|": mode = FileMode.Create; access = FileAccess.Write; break;
            case ">>": mode = FileMode.Append; access = FileAccess.Write; break;
            // `exec 5<> f`: reading and writing through one descriptor, the file created when missing.
            case "<>": mode = FileMode.OpenOrCreate; access = FileAccess.ReadWrite; break;
            default: return $"{op}: unsupported redirection";
        }

        // A stream device is one of the shell's own descriptors, so opening it is a dup of that descriptor at this
        // point: `exec 3>/dev/stdout` is `exec 3>&1`, `{v}>/dev/fd/7` is `{v}>&7` (DevicePath: never a file).
        var kind = PsBash.Core.DevicePath.Classify(path, out int deviceFd);
        if (PsBash.Core.DevicePath.IsStream(kind))
        {
            // `exec 5</dev/stdin` is `exec 5<&0`. Reading the shell's stdout or stderr (`exec 5</dev/fd/1`) is not modelled.
            if (kind == PsBash.Core.DeviceKind.Stdin) return Dup(fd, 0);
            if (access == FileAccess.Read && deviceFd is 1 or 2)
                return $"{PsBash.Core.BashPath.ToShell(path)}: the shell's own stdout/stderr cannot be opened for reading as a numbered descriptor";
            return Dup(fd, deviceFd);
        }

        Stream? fs = null;
        StreamWriter? writer = null;
        try
        {
            if (access == FileAccess.ReadWrite)
            {
                if (kind != PsBash.Core.DeviceKind.None) return $"{PsBash.Core.BashPath.ToShell(path)}: cannot open a device for reading and writing";
                FileSystemHelpers.ThrowIfNotWritableName(path);   // `exec 3<> fx/`: "Is a directory"
                fs = new FileStream(path, mode, access, FileShare.ReadWrite | FileShare.Delete);
                // One file position, as one descriptor has: the writer writes where the stream is, flushed per write.
                writer = new StreamWriter(fs, PsBash.Core.RawBytes.Encoding, 4096, leaveOpen: true) { AutoFlush = true };
            }
            // A descriptor open for writing streams through the shared policy (RedirectFileWriter), as `> f` does.
            else if (access != FileAccess.Read)
                writer = RedirectFileWriter.OpenText(path, append: mode == FileMode.Append);
            else if (kind != PsBash.Core.DeviceKind.None)
                fs = DeviceStreams.OpenRead(kind, deviceFd, path);
            else
                fs = new FileStream(path, mode, access, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException) { return $"{path}: No such file or directory"; }
        catch (DirectoryNotFoundException) { return $"{path}: No such file or directory"; }
        catch (UnauthorizedAccessException) { return $"{path}: Permission denied"; }
        catch (IOException ex) { return $"{path}: {ex.Message}"; }

        var handle = new Handle { Kind = Kind.File, Stream = fs, Writer = writer, FilePath = kind == PsBash.Core.DeviceKind.None ? path : null };
        if (fs is not null)
            handle.Reader = new StreamReader(fs, PsBash.Core.RawBytes.Encoding, false, 4096, leaveOpen: true);

        var t = Current;
        lock (Gate) PutLocked(t, fd, handle);
        return null;
    }

    /// <summary><c>{name}&lt;&lt;&lt; word</c>: <paramref name="fd"/> reads <paramref name="text"/> (the here-string, its newline included).</summary>
    public static string? OpenText(int fd, string text)
    {
        if (fd == 0) return StdUnsupported;
        var stream = new MemoryStream(PsBash.Core.RawBytes.GetBytes(text), writable: false);
        var handle = new Handle { Kind = Kind.File, Stream = stream, Reader = new StreamReader(stream, PsBash.Core.RawBytes.Encoding, false, 4096, leaveOpen: true) };
        var t = Current;
        lock (Gate) PutLocked(t, fd, handle);
        return null;
    }

    /// <summary><c>N&gt;&amp;M</c> / <c>N&lt;&amp;M</c>: <paramref name="fd"/> becomes a COPY of what <paramref name="from"/> names now.</summary>
    public static string? Dup(int fd, int from)
    {
        if (fd == 0 && from != 0) return StdUnsupported;
        var t = Current;
        lock (Gate)
        {
            if (CopyLocked(t, from) is not { } copy) return $"{from}: Bad file descriptor";
            if (fd == from) { ReleaseLocked(copy); return null; }
            PutLocked(t, fd, copy);
        }
        return null;
    }

    /// <summary><c>N&gt;&amp;M-</c>: <paramref name="from"/> moves to <paramref name="fd"/> (a dup, then <paramref name="from"/> is closed).</summary>
    public static string? Move(int fd, int from)
    {
        string? error = Dup(fd, from);
        if (error is null && fd != from) Close(from);
        return error;
    }

    public static bool Close(int fd)
    {
        var t = Current;
        lock (Gate)
        {
            bool open = GetLocked(t, fd) is { Kind: not Kind.Closed } || fd < 3;
            PutLocked(t, fd, fd < 3 ? new Handle { Kind = Kind.Closed } : null);
            return open;
        }
    }

    /// <summary>Close every descriptor of the running shell's table.</summary>
    public static void CloseAll()
    {
        var t = Current;
        lock (Gate) CloseAllLocked(t);
    }

    /// <summary>A new invocation starts (<c>Clear-BashFd</c>): every shell's table is emptied, the earlier invocations'
    /// (their runspaces are done) included, and nothing diverts the host's streams any more.</summary>
    public static void CloseAllShells()
    {
        lock (Gate)
        {
            CloseAllLocked(Fallback);
            foreach (var w in Shells)
                if (w.TryGetTarget(out var t)) CloseAllLocked(t);
            Shells.RemoveAll(w => !w.TryGetTarget(out _));
            ShellStdio.Divert = null;
        }
    }

    private static void CloseAllLocked(Table t)
    {
        t.Active.Clear();
        t.ActiveCount = 0;
        for (int fd = 0; fd < 3; fd++) PutLocked(t, fd, null);
        foreach (var fd in t.Fds.Keys.ToArray()) PutLocked(t, fd, null);
    }

    public static bool IsOpen(int fd)
    {
        var t = Current;
        lock (Gate) return fd < 3 ? GetLocked(t, fd) is not { Kind: Kind.Closed } : t.Fds.ContainsKey(fd);
    }

    /// <summary>The full path of the regular file the shell's fd 1 names (<c>fx() { exec &gt;log; }</c>), else null: a
    /// reader of that same file would read what it writes, for ever (<c>cat log</c>; GNU: input file is output file).</summary>
    public static string? StdoutFilePath()
    {
        var t = Current;
        lock (Gate) return ResolveLocked(t, 1, t.Low[1]) is { Kind: Kind.File, FilePath: { } file } ? file : null;
    }

    /// <summary>True when <paramref name="fd"/> is a name for the shell's own stdin (<see cref="Dup"/> of 0): its reader
    /// reads the shell's stdin (<c>read -u 5</c> as <c>read</c>, <c>cmd &lt;&amp;5</c> as <c>cmd</c>), not this table.</summary>
    public static bool ReadsShellStdin(int fd)
    {
        var t = Current;
        lock (Gate) return GetLocked(t, fd) is { Kind: Kind.ShellStdin };
    }

    /// <summary>The lowest descriptor at or above <paramref name="min"/> that is not open (bash gives <c>{var}</c> redirections
    /// the lowest free one from 10 up).</summary>
    public static int LowestFree(int min)
    {
        var t = Current;
        lock (Gate)
        {
            int fd = Math.Max(min, 3);
            while (t.Fds.ContainsKey(fd)) fd++;
            return fd;
        }
    }

    /// <summary>The next line of a descriptor opened for reading: null at end of file, throws when it is not open for reading.</summary>
    public static string? ReadLine(int fd)
    {
        StreamReader reader;
        var t = Current;
        lock (Gate)
        {
            if (GetLocked(t, fd) is not { Kind: not Kind.Closed } h) throw new InvalidOperationException($"{fd}: invalid file descriptor: Bad file descriptor");
            reader = h.Reader ?? throw new InvalidOperationException($"{fd}: Bad file descriptor");
        }
        return reader.ReadLine();
    }

    /// <summary>Everything left on a descriptor opened for reading (<c>cat /dev/fd/N</c>); throws when it is not open for reading.</summary>
    public static string ReadToEnd(int fd)
    {
        StreamReader reader;
        var t = Current;
        lock (Gate)
        {
            if (GetLocked(t, fd) is not { Kind: not Kind.Closed } h) throw new IOException("No such file or directory");
            reader = h.Reader ?? throw new IOException("Bad file descriptor");
        }
        return reader.ReadToEnd();
    }

    /// <summary>
    /// <c>read -u N -d DELIM</c> / <c>-N COUNT</c>: characters up to the first <paramref name="delim"/> (consumed, not
    /// returned) or at most <paramref name="limit"/> characters (-1 = unbounded). Null at end of file with nothing read;
    /// <paramref name="terminated"/> is false when the data ran out before the delimiter or count was reached.
    /// </summary>
    public static string? ReadRecord(int fd, char? delim, int limit, out bool terminated)
    {
        StreamReader reader;
        var t = Current;
        lock (Gate)
        {
            if (GetLocked(t, fd) is not { Kind: not Kind.Closed } h) throw new InvalidOperationException($"{fd}: invalid file descriptor: Bad file descriptor");
            reader = h.Reader ?? throw new InvalidOperationException($"{fd}: Bad file descriptor");
        }
        var sb = new StringBuilder();
        terminated = false;
        while (limit < 0 || sb.Length < limit)
        {
            int c = reader.Read();
            if (c < 0) return sb.Length == 0 ? null : sb.ToString();
            if (delim is char d && c == d) { terminated = true; return sb.ToString(); }
            sb.Append((char)c);
        }
        terminated = true;
        return sb.ToString();
    }

    /// <summary>
    /// Write text to a descriptor. Returns 0 once written (to a file, a redirection stage, a capture, or the shell's
    /// original stdout / stderr through the host); 1 or 2 when the descriptor copies the original stdout / stderr but no
    /// host sink is attached (the caller sends it down that stream). Throws when the descriptor is not open for writing.
    /// </summary>
    public static int Write(int fd, string text)
    {
        Handle h;
        var t = Current;
        lock (Gate)
        {
            if (GetLocked(t, fd) is not { } found)
            {
                if (fd is 1 or 2) return fd;
                throw new InvalidOperationException($"{fd}: Bad file descriptor");
            }
            h = found;
            if (h.Kind is Kind.Closed or Kind.ShellStdin || (h.Kind == Kind.File && h.Writer is null))
                throw new InvalidOperationException($"{fd}: Bad file descriptor");
        }
        return WriteHandle(h, text);
    }

    /// <summary>
    /// The end of ONE command's writes to <paramref name="fd"/>: what it wrote reaches the file now. Every writer to a
    /// table descriptor (<c>&gt;&amp;N</c>, <c>/dev/fd/N</c>) calls this when its command ends. In bash each write is a
    /// write(2), so the file is whole once the command is done; here a burst (<c>seq 1 1000 &gt;&amp;3</c>) sat in the
    /// writer's buffer until the idle timer, and a program run next (<c>stat -c %s f</c>, <c>wc -c &lt; f</c> through a
    /// native) saw a short file. Nothing for a descriptor that is not open or copies stdout/stderr.
    /// Spec: paths.md section Device names (fd flush per command).
    /// </summary>
    public static void Flush(int fd)
    {
        Handle? h;
        var t = Current;
        lock (Gate) h = GetLocked(t, fd);
        try
        {
            if (h?.Writer is { } writer) RedirectFileWriter.FlushText(writer);
            else h?.Out?.Flush();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    // ---- The context of a running command: redirection stages, captures, subshells, jobs --------------------------

    /// <summary>A table entry for a redirection stage's target (null: the null device). The stage keeps one reference.</summary>
    internal static Handle ForStage(RedirectFileWriter? writer)
        => writer is null ? new Handle { Kind = Kind.Null } : new Handle { Kind = Kind.Stage, Out = writer };

    /// <summary>A table entry for a command substitution's output: what is written to it is captured.</summary>
    internal static Handle ForCapture(Action<string> sink) => new() { Kind = Kind.Capture, Sink = sink };

    /// <summary>
    /// Make <paramref name="handle"/> the running shell's <paramref name="fd"/> (1 or 2) while the caller's command runs,
    /// until <see cref="Unregister"/>; the caller keeps its own reference to <paramref name="handle"/>.
    /// </summary>
    internal static Registration Register(int fd, Handle handle)
    {
        var t = Current;
        lock (Gate) return RegisterLocked(t, fd, handle, keep: false);
    }

    /// <summary>
    /// <c>cmd 2&gt;&amp;1</c>: fd 1 of the command is the stream the stage passes on, and fd 2 is COPIED from it before the
    /// command runs, as bash's dup2 does: <c>{ exec &gt;log; echo e &gt;&amp;2; } 2&gt;&amp;1</c> still prints e where fd 1 was.
    /// <paramref name="sink"/> takes what is written to that stream through the table (<c>exec 3&gt;&amp;1</c> inside, then
    /// <c>&gt;&amp;3</c>). Both registrations are the caller's to <see cref="Unregister"/>; fd 1's keeps an exec inside.
    /// </summary>
    internal static (Registration Out, Registration Err) RegisterMerge(Action<string> sink)
    {
        var h = new Handle { Kind = Kind.Merge, Sink = sink };
        var t = Current;
        lock (Gate)
        {
            var outReg = RegisterLocked(t, 1, h, keep: true);
            h.Refs++;   // the caller's reference for the second registration (each Unregister lets one go)
            var errReg = RegisterLocked(t, 2, h, keep: false);
            return (outReg, errReg);
        }
    }

    private static Registration RegisterLocked(Table t, int fd, Handle handle, bool keep)
    {
        var prior = t.Low[fd];
        handle.Refs++;
        Volatile.Write(ref t.Low[fd], handle);
        var reg = new Registration { Table = t, Fd = fd, Handle = handle, Prior = prior, Keep = keep };
        t.Active.Add(reg);
        Volatile.Write(ref t.ActiveCount, t.Active.Count);
        UpdateDivertLocked(t);
        return reg;
    }

    /// <summary>Put back the entry <see cref="Register"/> displaced (whatever the command left in the descriptor is released),
    /// then let go of the caller's own reference: the target closes once no descriptor names it
    /// (<c>{ exec 3&gt;&amp;1; } &gt;f</c> keeps f open as fd 3). Once only. A <see cref="Registration.Keep"/> descriptor that an
    /// exec moved keeps what the exec put there.</summary>
    internal static void Unregister(Registration reg)
    {
        lock (Gate)
        {
            if (reg.Done) return;
            reg.Done = true;
            var t = reg.Table;
            t.Active.Remove(reg);
            Volatile.Write(ref t.ActiveCount, t.Active.Count);
            var current = t.Low[reg.Fd];
            if (reg.Keep && !ReferenceEquals(current, reg.Handle))
            {
                if (reg.Prior is not null) ReleaseLocked(reg.Prior);
            }
            else
            {
                Volatile.Write(ref t.Low[reg.Fd], reg.Prior);
                if (current is not null) ReleaseLocked(current);
            }
            UpdateDivertLocked(t);
            ReleaseLocked(reg.Handle);
        }
    }

    /// <summary>
    /// Where a record that reached <paramref name="reg"/>'s stage on its descriptor goes: false when it is the stage's
    /// own; true, with the entry it goes to (null = the shell's original descriptor), when the command moved the
    /// descriptor away (<c>{ exec &gt;g; echo x; } &gt;f</c> writes x to g). A stage registered after this one on the same
    /// descriptor (a nested redirection, capture or <c>2&gt;&amp;1</c>) sits between the command and this stage and already
    /// sent the record where it belongs: the record is this stage's.
    /// </summary>
    internal static bool Moved(Registration reg, out Handle? to)
    {
        to = reg.Current;
        if (ReferenceEquals(to, reg.Handle)) return false;
        lock (Gate)
        {
            to = null;
            if (reg.Done) return false;
            var active = reg.Table.Active;
            for (int i = active.Count - 1; i >= 0 && !ReferenceEquals(active[i], reg); i--)
                if (active[i].Fd == reg.Fd) return false;
            to = reg.Table.Low[reg.Fd];
            return !ReferenceEquals(to, reg.Handle);
        }
    }

    /// <summary>A copy of the whole table, for a subshell (<see cref="Restore"/> puts it back).</summary>
    internal static Snapshot Save()
    {
        var t = Current;
        lock (Gate) return CopyTableLocked(t);
    }

    private static Snapshot CopyTableLocked(Table t)
    {
        var s = new Snapshot();
        for (int fd = 0; fd < 3; fd++)
            if (t.Low[fd] is { } h) { h.Refs++; s.Low[fd] = h; }
        foreach (var (fd, h) in t.Fds) { h.Refs++; s.High[fd] = h; }
        return s;
    }

    /// <summary>The table as <see cref="Save"/> copied it: what the subshell opened or moved is closed.</summary>
    internal static void Restore(Snapshot snapshot)
    {
        var t = Current;
        lock (Gate) RestoreLocked(t, snapshot);
    }

    private static void RestoreLocked(Table t, Snapshot snapshot)
    {
        for (int fd = 0; fd < 3; fd++)
        {
            var current = t.Low[fd];
            Volatile.Write(ref t.Low[fd], snapshot.Low[fd]);
            if (current is not null) ReleaseLocked(current);
        }
        foreach (var fd in t.Fds.Keys.ToArray())
        {
            t.Fds.Remove(fd, out var h);
            ReleaseLocked(h!);
        }
        foreach (var (fd, h) in snapshot.High) t.Fds[fd] = h;
        snapshot.Low = new Handle?[3];
        snapshot.High = new();
        UpdateDivertLocked(t);
    }

    /// <summary>What <paramref name="fds"/> (3 and up) name now, for a command that opens them for itself only.</summary>
    internal static Snapshot SaveFds(IEnumerable<int> fds)
    {
        var t = Current;
        lock (Gate)
        {
            var s = new Snapshot { Only = [.. fds.Where(fd => fd >= 3)] };
            foreach (int fd in s.Only)
                if (t.Fds.TryGetValue(fd, out var h)) { h.Refs++; s.High[fd] = h; }
            return s;
        }
    }

    /// <summary>Put back the descriptors <see cref="SaveFds"/> saved (one it found closed is closed again).</summary>
    internal static void RestoreFds(Snapshot snapshot)
    {
        var t = Current;
        lock (Gate)
        {
            foreach (int fd in snapshot.Only ?? [])
                PutLocked(t, fd, snapshot.High.Remove(fd, out var h) ? h : null);
            snapshot.Only = null;
        }
    }

    /// <summary>A copy <see cref="Save"/> made that is not put back: its references are let go.</summary>
    internal static void Discard(Snapshot snapshot)
    {
        lock (Gate)
        {
            foreach (var h in snapshot.Low) if (h is not null) ReleaseLocked(h);
            foreach (var h in snapshot.High.Values) ReleaseLocked(h);
            snapshot.Low = new Handle?[3];
            snapshot.High = new();
        }
    }

    /// <summary>
    /// A background job starts in <paramref name="job"/> (bound, not started): it gets a copy of the launching shell's
    /// table, as a forked job inherits the descriptors; nothing it opens or closes reaches the shell (or another job).
    /// </summary>
    internal static void AttachCopy(Runspace job)
    {
        var from = Current;
        lock (Gate)
        {
            var copy = CopyTableLocked(from);
            var t = new Table { Job = true };
            RestoreLocked(t, copy);
            ByRunspace.AddOrUpdate(job, t);
        }
    }

    /// <summary>The job in <paramref name="job"/> ended: its descriptors are let go and the runspace has no table.</summary>
    internal static void Detach(Runspace job)
    {
        lock (Gate)
        {
            if (!ByRunspace.TryGetValue(job, out var t)) return;
            if (t.Job) CloseAllLocked(t);
            ByRunspace.Remove(job);
        }
    }

    /// <summary>
    /// Write <paramref name="text"/> where <paramref name="h"/> points. 0 once written; 1 / 2 for a copy of the original
    /// stdout / stderr when no host sink is attached (the caller writes it down that stream). Throws for a closed fd.
    /// </summary>
    internal static int WriteHandle(Handle h, string text)
    {
        switch (h.Kind)
        {
            case Kind.Terminal:
                return ShellStdio.WriteTerminal(h.Term, text) ? 0 : h.Term;
            case Kind.Stage:
                if (text.Length > 0) h.Out!.Write(PsBash.Core.RawBytes.GetBytes(text));
                return 0;
            case Kind.Null:
                return 0;
            case Kind.Capture or Kind.Merge:
                h.Sink!(text);
                return 0;
            case Kind.File when h.Writer is { } writer:
                writer.Write(text);
                return 0;
            default:
                throw new InvalidOperationException("Bad file descriptor");
        }
    }

    // The host offers what reaches the shell's original fd 1 / 2 (ShellStdio.Divert, set while 1 or 2 has an entry).
    // While stages are registered on that descriptor, what reaches the host came out of them (or out of a pipe stage
    // after them), which routed it already: it goes where the descriptor was when the first of them began
    // (`{ exec >log; echo e >&2; } 2>&1` prints e; `cmd 2>&1 >f` puts cmd's stderr where fd 1 was, not in f).
    private static bool DivertCore(Table t, int fd, string text)
    {
        if (fd is not (1 or 2)) return false;
        Handle? h;
        if (Volatile.Read(ref t.ActiveCount) == 0) h = Volatile.Read(ref t.Low[fd]);
        else lock (Gate) h = OuterLocked(t, fd);
        if (h is null) return false;
        if (h.Kind == Kind.Terminal && h.Term == fd) return false;
        if (h.Kind == Kind.Closed)
        {
            if (fd == 1 && text.Length > 0) t_closedStdoutWrites++;
            return true;
        }
        try
        {
            if (WriteHandle(h, text) != 0) return false;
            if (h.Writer is { } writer) RedirectFileWriter.FlushText(writer);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        return true;
    }

    // What fd names below every stage registered on it: the entry the first of them displaced.
    private static Handle? OuterLocked(Table t, int fd)
    {
        foreach (var r in t.Active)
            if (r.Fd == fd) return ResolveLocked(t, fd, r.Prior);
        return ResolveLocked(t, fd, t.Low[fd]);
    }

    // A 2>&1 stage's stream is where that stage passes it on: the entry its registration displaced.
    private static Handle? ResolveLocked(Table t, int fd, Handle? h)
    {
        while (h is { Kind: Kind.Merge })
        {
            Registration? owner = null;
            foreach (var r in t.Active)
                if (r.Fd == fd && ReferenceEquals(r.Handle, h)) { owner = r; break; }
            if (owner is null) break;
            h = owner.Prior;
        }
        return h;
    }

    private static Handle? GetLocked(Table t, int fd)
        => fd < 3 ? (fd < 0 ? null : t.Low[fd]) : t.Fds.TryGetValue(fd, out var h) ? h : null;

    // A new reference to what `from` names now, or null when it is not open. No entry for 0-2: the original.
    private static Handle? CopyLocked(Table t, int from)
    {
        if (from < 0) return null;
        var h = GetLocked(t, from);
        if (h is null)
        {
            if (from >= 3) return null;
            return from == 0 ? new Handle { Kind = Kind.ShellStdin } : new Handle { Kind = Kind.Terminal, Term = from };
        }
        if (h.Kind == Kind.Closed) return null;
        h.Refs++;
        return h;
    }

    // Install `h` (an owned reference, or null for the original / closed) as `fd`, releasing what was there.
    private static void PutLocked(Table t, int fd, Handle? h)
    {
        // A copy of the original fd 1 / 2 put back on that fd IS the original (`exec 5>&1 >f; exec >&5`).
        if (h is not null && fd < 3
            && ((h.Kind == Kind.Terminal && h.Term == fd) || (fd == 0 && h.Kind == Kind.ShellStdin)))
        {
            ReleaseLocked(h);
            h = null;
        }
        Handle? prior;
        if (fd < 3)
        {
            prior = t.Low[fd];
            Volatile.Write(ref t.Low[fd], h);
            UpdateDivertLocked(t);
        }
        else
        {
            t.Fds.Remove(fd, out prior);
            if (h is not null) t.Fds[fd] = h;
        }
        if (prior is not null) ReleaseLocked(prior);
    }

    // Only a shell's table (not a job's) takes the host's streams; the shell running now is the one that sets it.
    private static void UpdateDivertLocked(Table t)
    {
        if (t.Job) return;
        if (t.Low[1] is not null || t.Low[2] is not null)
            ShellStdio.Divert = t.Divert ??= (fd, text) => DivertCore(t, fd, text);
        else if (t.Divert is not null && ReferenceEquals(ShellStdio.Divert, t.Divert))
            ShellStdio.Divert = null;
    }

    private static void ReleaseLocked(Handle h)
    {
        if (--h.Refs > 0) return;
        try { h.Writer?.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { }
        try
        {
            h.Reader?.Dispose();
            h.Writer?.Dispose();
            h.Stream?.Dispose();
            h.Out?.Dispose();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
