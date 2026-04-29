using System.Net.Sockets;
using System.Text;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Discovery file written by the host on startup so the launcher can locate
/// the running IPC endpoint without scanning. Layout:
/// <c>$TEMP/ps-bash/host-{user}-{sessionId}.lock</c>.
/// File contents:
/// <code>
/// pid=12345
/// endpoint=unix:/tmp/ps-bash/host-andyb.sock
/// </code>
/// or <c>endpoint=pipe:psbash-host-andyb-1234</c> on Windows.
/// </summary>
/// <remarks>
/// Atomic write: write to a sibling <c>{path}.tmp</c> then <see cref="File.Move(string,string,bool)"/>
/// with overwrite=true. This guarantees a reader either sees the prior file
/// content or the new content — never a half-written line. The pattern matches
/// <see cref="PwshWorker"/>'s extracted-script convention (FileShare.Read +
/// retry on IOException) but adds explicit rename-after-write to defeat
/// torn-read races on small files.
/// </remarks>
public sealed class HostLockFile
{
    private readonly string _path;

    public string Path => _path;

    public HostLockFile(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path required", nameof(path));
        _path = path;
    }

    /// <summary>
    /// Build the canonical lock-file path:
    /// <c>$TEMP/ps-bash/host-{user}-{sessionId}.lock</c>. Sanitizes the user
    /// component so a username containing path separators can't escape the
    /// ps-bash subdirectory.
    /// </summary>
    public static HostLockFile ForSession(string sessionId, string? user = null)
    {
        if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId required", nameof(sessionId));
        var u = SanitizeUser(user ?? Environment.UserName);
        var sid = SanitizeUser(sessionId);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ps-bash");
        Directory.CreateDirectory(dir);
        return new HostLockFile(System.IO.Path.Combine(dir, $"host-{u}-{sid}.lock"));
    }

    /// <summary>
    /// Atomically write the lock file advertising <paramref name="transport"/>
    /// and the host process id. Overwrites any existing file (stale lock from
    /// a crashed previous host).
    /// </summary>
    public void Write(IIpcTransport transport, int pid)
    {
        if (transport is null) throw new ArgumentNullException(nameof(transport));
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var body = $"pid={pid}\nendpoint={transport.Scheme}:{transport.Endpoint}\n";
        var tmp = _path + ".tmp";

        // Write to sibling tmp, then atomic rename. FileShare.Read so a
        // concurrent reader doesn't fail the writer.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Read the lock file and return the advertised endpoint. Throws
    /// <see cref="FileNotFoundException"/> if no host is listening.
    /// </summary>
    public HostLockEntry Read()
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException("Host lock file not found", _path);

        // FileShare.ReadWrite so we don't lock out a host that is rewriting
        // the file (write-temp-then-rename is atomic on the rename, but a
        // reader catching the new file mid-flush is harmless given UTF-8
        // line buffering).
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return Parse(text, _path);
    }

    /// <summary>
    /// Best-effort delete on host shutdown. Swallows IOException because a
    /// crashed host's lock file is normal and the next host's <see cref="Write"/>
    /// will overwrite it via rename anyway.
    /// </summary>
    public void Delete()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (IOException) { /* best effort */ }
    }

    /// <summary>
    /// Detect a stale lock file (host crashed without cleanup) by attempting
    /// a connection on the advertised endpoint. If the connection raises
    /// <see cref="SocketException"/> or the named-pipe equivalent, deletes
    /// the lock file and rethrows so the caller can fall through to "no host
    /// running" handling.
    /// </summary>
    public async Task<HostLockEntry> ReadOrPurgeAsync(CancellationToken ct = default)
    {
        var entry = Read();
        try
        {
            await using var probe = entry.Scheme == "unix"
                ? (IIpcTransport)new UnixSocketTransport(entry.Endpoint)
                : new NamedPipeTransport(entry.Endpoint);
            using var stream = await probe.ConnectAsync(ct);
            // Connection accepted — host is alive. Leave the file in place.
            return entry;
        }
        catch (SocketException)
        {
            Delete();
            throw;
        }
        catch (IOException)
        {
            // Named-pipe "the system cannot find the file specified" surfaces
            // as IOException. Treat the same as SocketException for parity.
            Delete();
            throw new SocketException((int)SocketError.ConnectionRefused);
        }
        catch (TimeoutException)
        {
            // NamedPipeClientStream.ConnectAsync with no listener can time out
            // rather than raise a connect-refused — treat as stale.
            Delete();
            throw new SocketException((int)SocketError.TimedOut);
        }
    }

    private static HostLockEntry Parse(string text, string sourcePath)
    {
        int? pid = null;
        string? scheme = null;
        string? endpoint = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq);
            var value = line.Substring(eq + 1);
            switch (key)
            {
                case "pid":
                    if (int.TryParse(value, out var p)) pid = p;
                    break;
                case "endpoint":
                    var colon = value.IndexOf(':');
                    if (colon <= 0) throw new FormatException($"Malformed endpoint in {sourcePath}: {value}");
                    scheme = value.Substring(0, colon);
                    endpoint = value.Substring(colon + 1);
                    if (scheme is not ("unix" or "pipe"))
                        throw new FormatException($"Unknown endpoint scheme '{scheme}' in {sourcePath}");
                    break;
            }
        }

        if (pid is null) throw new FormatException($"Missing pid in {sourcePath}");
        if (scheme is null || endpoint is null) throw new FormatException($"Missing endpoint in {sourcePath}");
        return new HostLockEntry(pid.Value, scheme, endpoint);
    }

    private static string SanitizeUser(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            else sb.Append('_');
        }
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }
}

/// <summary>
/// Parsed lock-file contents. <see cref="Scheme"/> is <c>"unix"</c> or
/// <c>"pipe"</c>; <see cref="Endpoint"/> is the path or pipe name.
/// </summary>
public readonly record struct HostLockEntry(int Pid, string Scheme, string Endpoint);
