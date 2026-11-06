using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// The shell's numbered file descriptors above 2, opened by <c>exec 7&lt;file</c> / <c>exec 8&gt;file</c> and used
/// by <c>read -u 7</c>, <c>cmd &gt;&amp;8</c> and <c>cmd &lt;&amp;7</c>. Process-global like the rest of the shell's
/// state (env, cwd): a command's descriptors are closed when the next invocation starts
/// (<see cref="CloseAll"/>, from the per-invocation reset), so a daemon never accumulates open files.
/// Descriptors 0-2 are the host's real stdio and are not managed here.
/// </summary>
internal static class BashFdTable
{
    private sealed class Handle
    {
        /// <summary>Null for a duplicate of the shell's own stdout (<see cref="Std"/> 1) or stderr (2).</summary>
        public FileStream? Stream { get; init; }
        public int Std { get; init; }
        public StreamReader? Reader { get; set; }
        public StreamWriter? Writer { get; set; }
        public int Refs { get; set; } = 1;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Handle> Fds = new();

    /// <summary>Open <paramref name="path"/> as <paramref name="fd"/> for <paramref name="op"/> (<c>&lt;</c>, <c>&gt;</c>,
    /// <c>&gt;|</c>, <c>&gt;&gt;</c>). Any descriptor already there is closed first. Null on success, else the error text.</summary>
    public static string? Open(int fd, string path, string op)
    {
        if (fd < 3) return $"{fd}: redirecting the shell's own stdin/stdout/stderr with exec is not supported";
        FileMode mode;
        FileAccess access;
        switch (op)
        {
            case "<": mode = FileMode.Open; access = FileAccess.Read; break;
            case ">":
            case ">|": mode = FileMode.Create; access = FileAccess.Write; break;
            case ">>": mode = FileMode.Append; access = FileAccess.Write; break;
            default: return $"{op}: unsupported redirection";
        }

        FileStream fs;
        try
        {
            fs = new FileStream(path, mode, access, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException) { return $"{path}: No such file or directory"; }
        catch (DirectoryNotFoundException) { return $"{path}: No such file or directory"; }
        catch (UnauthorizedAccessException) { return $"{path}: Permission denied"; }
        catch (IOException ex) { return $"{path}: {ex.Message}"; }

        var handle = new Handle { Stream = fs };
        if (access == FileAccess.Read)
            handle.Reader = new StreamReader(fs, PsBash.Core.RawBytes.Encoding, false, 4096, leaveOpen: true);
        else
            handle.Writer = new StreamWriter(fs, PsBash.Core.RawBytes.Encoding, 4096, leaveOpen: true) { AutoFlush = true };

        lock (Gate)
        {
            CloseLocked(fd);
            Fds[fd] = handle;
        }
        return null;
    }

    /// <summary><c>N&gt;&amp;M</c> / <c>N&lt;&amp;M</c>: make <paramref name="fd"/> another name for the open <paramref name="from"/>.</summary>
    public static string? Dup(int fd, int from)
    {
        if (fd < 3) return $"{fd}: redirecting the shell's own stdin/stdout/stderr with exec is not supported";
        lock (Gate)
        {
            if (from is 1 or 2)
            {
                // `exec 3>&1`: a name for the shell's own stdout/stderr, so `cmd >&3` still reaches the terminal.
                CloseLocked(fd);
                Fds[fd] = new Handle { Std = from };
                return null;
            }
            if (!Fds.TryGetValue(from, out var handle)) return $"{from}: Bad file descriptor";
            if (fd == from) return null;
            CloseLocked(fd);
            handle.Refs++;
            Fds[fd] = handle;
        }
        return null;
    }

    public static bool Close(int fd)
    {
        lock (Gate) return CloseLocked(fd);
    }

    public static void CloseAll()
    {
        lock (Gate)
        {
            foreach (var fd in Fds.Keys.ToArray()) CloseLocked(fd);
        }
    }

    public static bool IsOpen(int fd)
    {
        lock (Gate) return Fds.ContainsKey(fd);
    }

    /// <summary>The next line of a descriptor opened for reading: null at end of file, throws when it is not open for reading.</summary>
    public static string? ReadLine(int fd)
    {
        StreamReader reader;
        lock (Gate)
        {
            if (!Fds.TryGetValue(fd, out var h)) throw new InvalidOperationException($"{fd}: invalid file descriptor: Bad file descriptor");
            reader = h.Reader ?? throw new InvalidOperationException($"{fd}: Bad file descriptor");
        }
        return reader.ReadLine();
    }

    /// <summary>
    /// <c>read -u N -d DELIM</c> / <c>-N COUNT</c>: characters up to the first <paramref name="delim"/> (consumed, not
    /// returned) or at most <paramref name="limit"/> characters (-1 = unbounded). Null at end of file with nothing read;
    /// <paramref name="terminated"/> is false when the data ran out before the delimiter or count was reached.
    /// </summary>
    public static string? ReadRecord(int fd, char? delim, int limit, out bool terminated)
    {
        StreamReader reader;
        lock (Gate)
        {
            if (!Fds.TryGetValue(fd, out var h)) throw new InvalidOperationException($"{fd}: invalid file descriptor: Bad file descriptor");
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
    /// Write text to a descriptor opened for writing. Returns 0 once written to a file; 1 or 2 when the descriptor
    /// is a duplicate of the shell's stdout/stderr (the caller sends it down that stream). Throws when the descriptor
    /// is not open for writing.
    /// </summary>
    public static int Write(int fd, string text)
    {
        StreamWriter writer;
        lock (Gate)
        {
            if (!Fds.TryGetValue(fd, out var h)) throw new InvalidOperationException($"{fd}: Bad file descriptor");
            if (h.Std != 0) return h.Std;
            writer = h.Writer ?? throw new InvalidOperationException($"{fd}: Bad file descriptor");
        }
        writer.Write(text);
        return 0;
    }

    private static bool CloseLocked(int fd)
    {
        if (!Fds.Remove(fd, out var h)) return false;
        if (--h.Refs > 0) return true;
        try { h.Writer?.Flush(); } catch (IOException) { }
        h.Reader?.Dispose();
        h.Writer?.Dispose();
        h.Stream?.Dispose();
        return true;
    }
}
