namespace PsBash.Core;

/// <summary>What a <c>/dev/*</c> name is (<see cref="DevicePath.Classify(string?)"/>).</summary>
public enum DeviceKind
{
    /// <summary>Not a device name: an ordinary path.</summary>
    None,
    /// <summary><c>/dev/null</c> (Windows: also <c>NUL</c>): reads as empty, discards writes.</summary>
    Null,
    /// <summary><c>/dev/stdin</c>, <c>/dev/fd/0</c>: the command's own standard input.</summary>
    Stdin,
    /// <summary><c>/dev/stdout</c>, <c>/dev/fd/1</c>: the command's own standard output.</summary>
    Stdout,
    /// <summary><c>/dev/stderr</c>, <c>/dev/fd/2</c>: the command's own standard error.</summary>
    Stderr,
    /// <summary><c>/dev/fd/N</c>, N &gt;= 3: the shell's open descriptor N (the fd table).</summary>
    Fd,
    /// <summary><c>/dev/full</c>: reads as NUL bytes, every write fails "No space left on device".</summary>
    Full,
    /// <summary><c>/dev/zero</c>: an endless run of NUL bytes; discards writes.</summary>
    Zero,
    /// <summary><c>/dev/random</c>, <c>/dev/urandom</c>: endless random bytes; discards writes.</summary>
    Random,
    /// <summary><c>/dev/tty</c>: the controlling terminal.</summary>
    Tty,
}

/// <summary>
/// The ONE place the <c>/dev/*</c> device names are recognised (spec: <c>docs/specs/paths.md</c>, "Device names").
/// A device name is not a file: Windows has no <c>/dev</c>, so a site that does not ask here first hands the name to
/// the filesystem and gets <c>C:\dev\stdout</c> - "No such file or directory" on most machines, and a junk file with
/// the command's output silently missing on a machine that happens to have a <c>C:\dev</c> directory. On Linux the
/// stream names exist but name the HOST process's descriptors, not the stdout/stderr/stdin of the command the
/// pipeline is running. So:
/// <list type="bullet">
/// <item>every site that used a private list (<c>"/dev/stdout"</c>, <c>"/dev/fd/2"</c>, …) calls
/// <see cref="Classify(string?)"/>; <c>DevicePathGuardTests</c> rejects the literals anywhere else in product code;</item>
/// <item>the two file choke points - <c>RedirectFileWriter.OpenFile</c> (every streaming write) and
/// <c>BashFileSystem.OpenRead</c> (every read) - classify the path themselves and never pass a device name to a
/// <see cref="System.IO.FileStream"/>, so a site that forgets still cannot reach the filesystem.</item>
/// </list>
/// Spellings accepted: the bash name itself (<c>/dev/stdout</c>), and on Windows the forms the inbound path mappers
/// turn it into before a site sees it - drive-rooted (<c>C:\dev\stdout</c>, <c>C:/dev/stdout</c>; what
/// <c>GetUnresolvedProviderPathFromPSPath</c> and <see cref="System.IO.Path.GetFullPath(string)"/> return) and
/// UNC-rooted (<c>\\server\share\dev\stdout</c>, the same with the working directory on a share) - plus the Windows
/// null device <c>NUL</c>. A device name always wins over a real file of that name directly under a drive's
/// <c>dev</c> directory: after mapping the two cannot be told apart, and a script that says <c>/dev/stdout</c> must
/// never write there. Pure string logic: no I/O.
/// </summary>
public static class DevicePath
{
    /// <summary>The bash spelling of the null device (for text ps-bash itself produces, e.g. <c>ls /dev/null</c>).</summary>
    public const string Null = "/dev/null";

    /// <summary>The bash spelling of standard input as a file name (<c>file -</c> reports it under this name).</summary>
    public const string Stdin = "/dev/stdin";

    /// <summary>The bash spelling of the controlling terminal.</summary>
    public const string Tty = "/dev/tty";

    private const string Prefix = "/dev/";

    /// <summary>The device <paramref name="path"/> names on this OS, or <see cref="DeviceKind.None"/>.</summary>
    public static DeviceKind Classify(string? path) => Classify(path, System.OperatingSystem.IsWindows(), out _);

    /// <summary>As <see cref="Classify(string?)"/>; <paramref name="fd"/> is the descriptor number a stream device
    /// names (0, 1, 2, or N of <c>/dev/fd/N</c>), -1 for every other kind.</summary>
    public static DeviceKind Classify(string? path, out int fd) => Classify(path, System.OperatingSystem.IsWindows(), out fd);

    /// <summary>
    /// <see cref="Classify(string?, out int)"/> with the platform explicit: <paramref name="windows"/> true also accepts the
    /// drive- and UNC-rooted spellings, <c>NUL</c>, and any letter case.
    /// </summary>
    public static DeviceKind Classify(string? path, bool windows, out int fd)
    {
        fd = -1;
        if (string.IsNullOrEmpty(path)) return DeviceKind.None;
        if (windows && IsWindowsNul(path)) return DeviceKind.Null;
        int start = DevRootLength(path, windows);
        if (start < 0) return DeviceKind.None;
        var name = path.AsSpan(start);
        var cmp = windows ? System.StringComparison.OrdinalIgnoreCase : System.StringComparison.Ordinal;
        if (name.Equals("null", cmp)) return DeviceKind.Null;
        if (name.Equals("stdin", cmp)) { fd = 0; return DeviceKind.Stdin; }
        if (name.Equals("stdout", cmp)) { fd = 1; return DeviceKind.Stdout; }
        if (name.Equals("stderr", cmp)) { fd = 2; return DeviceKind.Stderr; }
        if (name.Equals("full", cmp)) return DeviceKind.Full;
        if (name.Equals("zero", cmp)) return DeviceKind.Zero;
        if (name.Equals("random", cmp) || name.Equals("urandom", cmp)) return DeviceKind.Random;
        if (name.Equals("tty", cmp)) return DeviceKind.Tty;
        if (name.Length > 3 && name.Slice(0, 2).Equals("fd", cmp) && IsSeparator(name[2], windows))
        {
            var digits = name.Slice(3);
            if (digits.Length > 9) return DeviceKind.None;
            int n = 0;
            foreach (char c in digits)
            {
                if (c < '0' || c > '9') return DeviceKind.None;
                n = n * 10 + (c - '0');
            }
            fd = n;
            return n switch { 0 => DeviceKind.Stdin, 1 => DeviceKind.Stdout, 2 => DeviceKind.Stderr, _ => DeviceKind.Fd };
        }
        return DeviceKind.None;
    }

    /// <summary>True when <paramref name="path"/> names any device.</summary>
    public static bool IsDevice(string? path) => Classify(path) != DeviceKind.None;

    /// <summary>True when <paramref name="path"/> names the null device.</summary>
    public static bool IsNull(string? path) => Classify(path) == DeviceKind.Null;

    /// <summary>
    /// True for the kinds that name one of the running command's own descriptors (<see cref="DeviceKind.Stdin"/>,
    /// <see cref="DeviceKind.Stdout"/>, <see cref="DeviceKind.Stderr"/>, <see cref="DeviceKind.Fd"/>). They are never
    /// opened as an OS file on any platform: the OS device of that name is the host process's descriptor.
    /// </summary>
    public static bool IsStream(DeviceKind kind) =>
        kind is DeviceKind.Stdin or DeviceKind.Stdout or DeviceKind.Stderr or DeviceKind.Fd;

    /// <summary>
    /// The OS device to open for <paramref name="kind"/>, or null when ps-bash must serve it itself: every stream
    /// kind, and on Windows everything except <see cref="DeviceKind.Null"/> (<c>NUL</c>). Off Windows the real
    /// <c>/dev/null|zero|full|urandom|tty</c> are used, as bash uses them.
    /// </summary>
    public static string? NativeDevice(DeviceKind kind) => NativeDevice(kind, System.OperatingSystem.IsWindows());

    /// <summary><see cref="NativeDevice(DeviceKind)"/> with the platform explicit.</summary>
    public static string? NativeDevice(DeviceKind kind, bool windows) => kind switch
    {
        DeviceKind.Null => windows ? "NUL" : Null,
        DeviceKind.Zero => windows ? null : Prefix + "zero",
        DeviceKind.Full => windows ? null : Prefix + "full",
        DeviceKind.Random => windows ? null : Prefix + "urandom",
        DeviceKind.Tty => windows ? null : Tty,
        _ => null,
    };

    private static bool IsSeparator(char c, bool windows) => c == '/' || (windows && c == '\\');

    /// <summary>The index just past the <c>/dev/</c> root of <paramref name="path"/> (<c>/dev/</c>, or on Windows
    /// <c>X:\dev\</c> / <c>\\server\share\dev\</c> with either separator), or -1.</summary>
    private static int DevRootLength(string path, bool windows)
    {
        if (path.StartsWith(Prefix, System.StringComparison.Ordinal)) return Prefix.Length;
        if (!windows) return -1;
        int root;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2], true))
        {
            root = 2;
        }
        else if (path.Length > 2 && IsSeparator(path[0], true) && IsSeparator(path[1], true))
        {
            // \\server\share: the root ends after the share name.
            int server = IndexOfSeparator(path, 2);
            if (server < 0) return -1;
            root = IndexOfSeparator(path, server + 1);
            if (root < 0) return -1;
        }
        else if (IsSeparator(path[0], true))
        {
            root = 0; // \dev\x, /DEV/x
        }
        else
        {
            return -1;
        }
        // path[root] is a separator; "dev" and one more separator must follow.
        if (path.Length < root + 6) return -1;
        if (string.Compare(path, root + 1, "dev", 0, 3, System.StringComparison.OrdinalIgnoreCase) != 0) return -1;
        return IsSeparator(path[root + 4], true) ? root + 5 : -1;
    }

    private static int IndexOfSeparator(string path, int from)
    {
        for (int i = from; i < path.Length; i++)
            if (path[i] is '/' or '\\') return i;
        return -1;
    }

    /// <summary><c>NUL</c> itself, or <c>NUL</c> as the last segment of a path: Windows resolves both to the null device.</summary>
    private static bool IsWindowsNul(string path)
    {
        if (path.Length < 3 || !path.EndsWith("NUL", System.StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == 3 || path[path.Length - 4] is '/' or '\\';
    }
}
