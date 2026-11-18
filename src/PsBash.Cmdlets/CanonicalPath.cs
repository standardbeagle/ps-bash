using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU's file-name canonicalization (gnulib <c>canonicalize_filename_mode</c>), the one primitive behind
/// <c>readlink</c> and <c>realpath</c>. It walks the name ONE COMPONENT AT A TIME from the root, following each
/// symbolic link where it is met, so <c>dl/..</c> is the parent of the link's TARGET, not the directory holding the link.
/// The two commands used to ask PowerShell (<c>Resolve-Path</c>) instead: that folds <c>..</c> by text, follows no link,
/// globs <c>[</c>/<c>*</c> in a name, and knows no "all but the last component must exist", so <c>readlink -f d/new</c>
/// failed, <c>readlink -m</c> was <c>-f</c>, and <c>realpath nosuch/x</c> printed a path.
/// <para>
/// Modes (<see cref="Mode"/>): <b>Existing</b> (<c>-e</c>) every component exists; <b>AllButLast</b> (<c>readlink -f</c>,
/// <c>realpath</c>'s default) all but the last; <b>Missing</b> (<c>-m</c>) none need to, and a missing component is kept
/// as written (a later <c>..</c> removes it). <c>followLinks</c> false is gnulib's CAN_NOLINKS (<c>realpath -s</c>, the
/// first pass of <c>-L</c>): no link is read; a component is checked where a following <c>/</c>, <c>.</c> or <c>..</c>
/// needs it to be a directory, and the last one must exist (stat) as the mode requires. A symbolic-link loop is "Too many levels of symbolic links", found as gnulib
/// finds it: after 20 links, the first link met twice; under Missing that link is kept as a plain name.
/// </para>
/// Pure over a probe (<see cref="Entry"/> per absolute name) so the walk is tested on a fake file system on any platform;
/// the <see cref="PSCmdlet"/> overloads are the real-file-system entry points.
/// </summary>
public static class CanonicalPath
{
    /// <summary>Which components must exist (gnulib CAN_EXISTING / CAN_ALL_BUT_LAST / CAN_MISSING).</summary>
    public enum Mode { Existing, AllButLast, Missing }

    /// <summary>What a name is, without following a link at that name.</summary>
    public enum Kind { Missing, File, Directory, Link }

    /// <summary>
    /// One probed name: its kind (a link not followed), a link's target as stored, and the kind the name resolves to
    /// (links followed; for a non-link, its own kind).
    /// </summary>
    public readonly record struct Entry(Kind Kind, string? Target = null, Kind Resolved = Kind.Missing)
    {
        /// <summary>A name that is not a link: it resolves to itself.</summary>
        public static Entry Of(Kind kind) => new(kind, null, kind);
    }

    /// <summary>The canonical name (or, from <see cref="ReadLink(string, Func{string, Entry}, bool)"/>, a link's
    /// target), or the <c>strerror</c> text of why there is none.</summary>
    public readonly record struct Result(string? Path, string? Error)
    {
        public bool Ok => Path is not null;
    }

    public const string NoSuchFile = "No such file or directory";
    public const string NotADirectory = "Not a directory";
    public const string LinkLoop = "Too many levels of symbolic links";
    public const string InvalidArgument = "Invalid argument";   // readlink(2) EINVAL: exists, not a link

    /// <summary>gnulib checks for a loop only after this many links (a long legal chain costs no bookkeeping).</summary>
    private const int LinksBeforeLoopCheck = 20;

    /// <summary>A hard stop for a chain that never repeats a name (each hop a new link): an ELOOP backstop.</summary>
    private const int MaxLinks = 4096;

    /// <summary>
    /// Canonicalize the operand <paramref name="operand"/> as typed (a shell path: <c>/tmp</c>, <c>/c/x</c> map first)
    /// against the session's working directory, on the real file system.
    /// </summary>
    public static Result Canonicalize(PSCmdlet cmdlet, string operand, Mode mode, bool followLinks = true)
    {
        if (operand.Length == 0) return new Result(null, NoSuchFile);
        return Canonicalize(AbsoluteOperand(cmdlet, operand), mode, followLinks, Probe, OperatingSystem.IsWindows());
    }

    /// <summary><c>readlink FILE</c> on the real file system: the link's target as stored, or why there is none.</summary>
    public static Result ReadLink(PSCmdlet cmdlet, string operand)
    {
        if (operand.Length == 0) return new Result(null, NoSuchFile);
        return ReadLink(AbsoluteOperand(cmdlet, operand), Probe, OperatingSystem.IsWindows());
    }

    private static string AbsoluteOperand(PSCmdlet cmdlet, string operand)
    {
        string cwd;
        try { cwd = cmdlet.SessionState.Path.CurrentFileSystemLocation.ProviderPath; }
        catch (InvalidOperationException) { cwd = Environment.CurrentDirectory; }
        return Absolute(FileSystemHelpers.NormalizeOperandPath(operand), cwd, OperatingSystem.IsWindows());
    }

    /// <summary>
    /// <paramref name="path"/> made absolute against <paramref name="cwd"/> by joining, never by folding <c>..</c>
    /// (that is the walk's job, after links). Windows: <c>\x</c> is on the working directory's drive.
    /// </summary>
    internal static string Absolute(string path, string cwd, bool windows)
    {
        if (RootLength(path, windows) > 0) return path;
        if (windows && path.Length > 0 && IsSep(path[0], windows))
            return cwd[..RootLength(cwd, windows)].TrimEnd('\\', '/') + path;
        return cwd.TrimEnd(windows ? ['\\', '/'] : ['/']) + (windows ? "\\" : "/") + path;
    }

    /// <summary>
    /// The walk, ported from gnulib <c>canonicalize_filename_mode</c>. <paramref name="absolute"/> must be rooted. The
    /// unwalked rest is kept as TEXT (a link's target is spliced in front of it), because what follows a component
    /// decides how it is checked (<see cref="SuffixRequiresDirCheck"/>). Per component: append it; ask what it is
    /// (readlink(2): a link; EINVAL = exists and is not one; else ENOENT/ENOTDIR — with <paramref name="followLinks"/>
    /// false nothing is asked, as if EINVAL); follow a link; otherwise it is acceptable when a following <c>/</c>,
    /// <c>/.</c> or <c>/..</c> finds a directory there, or (nothing like that follows) when the answer was EINVAL. An
    /// unacceptable one fails unless the mode tolerates it (Missing always; AllButLast for ENOENT on the last component).
    /// </summary>
    internal static Result Canonicalize(string absolute, Mode mode, bool followLinks, Func<string, Entry> probe, bool windows)
    {
        int rootLen = RootLength(absolute, windows);
        if (rootLen == 0) return new Result(null, NoSuchFile);
        string root = NormalizeRoot(absolute[..rootLen], windows);
        char sep = windows ? '\\' : '/';
        string name = absolute[rootLen..];
        var parts = new List<string>();
        HashSet<string>? seen = null;
        int links = 0;

        int start = 0;
        while (true)
        {
            while (start < name.Length && IsSep(name[start], windows)) start++;
            if (start >= name.Length) break;
            int end = start;
            while (end < name.Length && !IsSep(name[end], windows)) end++;
            string c = name[start..end];
            if (c == ".") { start = end; continue; }
            if (c == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                start = end;
                continue;
            }

            parts.Add(c);
            string rname = Join(root, parts, sep);
            Entry entry = default;
            string? errno = InvalidArgument;
            if (followLinks)
            {
                entry = probe(rname);
                errno = entry.Kind switch
                {
                    Kind.Link => null,
                    Kind.Missing => MissingErrno(rname, probe, windows),
                    _ => InvalidArgument,
                };
            }

            if (errno is null)   // a link: splice its target in front of the rest
            {
                links++;
                bool loop = links > MaxLinks;
                if (!loop && links > LinksBeforeLoopCheck)
                {
                    seen ??= new HashSet<string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    loop = !seen.Add(rname);
                }
                if (loop)
                {
                    if (mode != Mode.Missing) return new Result(null, LinkLoop);
                    start = end;          // gnulib under CAN_MISSING: the repeated link stays a plain name
                    continue;
                }
                string target = entry.Target ?? "";
                if (target.Length == 0) return new Result(null, NoSuchFile);
                parts.RemoveAt(parts.Count - 1);
                int targetRoot = RootLength(target, windows);
                if (targetRoot > 0)
                {
                    root = NormalizeRoot(target[..targetRoot], windows);
                    parts.Clear();
                    target = target[targetRoot..];
                }
                else if (windows && IsSep(target[0], windows))
                {
                    parts.Clear();        // \x: from the current drive's root
                }
                name = target + name[end..];
                start = 0;
                continue;
            }

            // Reading no link, only the LAST component is checked (stat, links followed): what lies above it cannot
            // be looked at without following links. GNU realpath -s: nosuch/x is fine, f/x is "Not a directory".
            string? failure = SuffixRequiresDirCheck(name, end, windows) ? DirCheck(rname, probe, windows)
                : !followLinks ? (RestIsEmpty(name, end, windows) ? ExistsCheck(rname, probe, windows) : null)
                : errno == InvalidArgument ? null : errno;
            if (failure is not null)
            {
                bool tolerated = mode == Mode.Missing
                    || (mode == Mode.AllButLast && failure == NoSuchFile && RestIsEmpty(name, end, windows));
                if (!tolerated) return new Result(null, failure);
            }
            start = end;
        }
        return new Result(Join(root, parts, sep), null);
    }

    /// <summary>
    /// What <c>readlink FILE</c> (no option) reports: the link's target as stored, or why there is none (readlink(2):
    /// EINVAL "Invalid argument" for an existing non-link, ENOENT, ENOTDIR, ELOOP). The directories leading to the last
    /// component are walked like the canonical walk (links followed, each must be a directory); a trailing <c>/</c>
    /// makes the name a directory (a link there is followed, so the answer is EINVAL or ENOTDIR).
    /// </summary>
    internal static Result ReadLink(string absolute, Func<string, Entry> probe, bool windows)
    {
        int rootLen = RootLength(absolute, windows);
        if (rootLen == 0) return new Result(null, NoSuchFile);
        int stop = absolute.Length;
        while (stop > rootLen && IsSep(absolute[stop - 1], windows)) stop--;
        if (stop == rootLen) return new Result(null, InvalidArgument);   // the root
        bool trailing = stop < absolute.Length;
        int lastStart = stop;
        while (lastStart > rootLen && !IsSep(absolute[lastStart - 1], windows)) lastStart--;
        string last = absolute[lastStart..stop];

        // The leading directories: each must exist and be a directory (their trailing separator asks exactly that).
        var dir = Canonicalize(absolute[..lastStart], Mode.Existing, followLinks: true, probe, windows);
        if (!dir.Ok) return dir;
        if (last is "." or "..") return new Result(null, InvalidArgument);
        char sep = windows ? '\\' : '/';
        string full = dir.Path!.EndsWith(sep) ? dir.Path + last : dir.Path + sep + last;
        var entry = probe(full);
        if (trailing)
        {
            return entry.Resolved switch
            {
                Kind.Directory => new Result(null, InvalidArgument),
                Kind.File => new Result(null, NotADirectory),
                _ => new Result(null, MissingErrno(full, probe, windows)),
            };
        }
        return entry.Kind switch
        {
            Kind.Link => new Result(entry.Target ?? "", null),
            Kind.Missing => new Result(null, MissingErrno(full, probe, windows)),
            _ => new Result(null, InvalidArgument),
        };
    }

    /// <summary>
    /// gnulib <c>suffix_requires_dir_check</c>: true when the text after the component ending at <paramref name="end"/>
    /// is separators only (a trailing <c>/</c>) or begins, after separators, with a <c>.</c> or <c>..</c> component; an
    /// ordinary component first makes it false.
    /// </summary>
    internal static bool SuffixRequiresDirCheck(string name, int end, bool windows)
    {
        int i = end;
        if (i >= name.Length || !IsSep(name[i], windows)) return false;
        while (i < name.Length && IsSep(name[i], windows)) i++;
        if (i >= name.Length) return true;                                     // trailing separator
        if (name[i] != '.') return false;                                      // an ordinary component
        i++;
        if (i >= name.Length || IsSep(name[i], windows)) return true;          // "/."
        return name[i] == '.' && (i + 1 >= name.Length || IsSep(name[i + 1], windows));   // "/.."
    }

    private static bool RestIsEmpty(string name, int end, bool windows)
    {
        for (int i = end; i < name.Length; i++)
            if (!IsSep(name[i], windows)) return false;
        return true;
    }

    /// <summary>gnulib <c>dir_check</c>: null when <paramref name="path"/> (links followed) is a directory, else why not.</summary>
    private static string? DirCheck(string path, Func<string, Entry> probe, bool windows)
        => probe(path).Resolved switch
        {
            Kind.Directory => null,
            Kind.File => NotADirectory,
            _ => MissingErrno(path, probe, windows),
        };

    /// <summary>stat(2): null when <paramref name="path"/> (links followed) exists, else why not.</summary>
    private static string? ExistsCheck(string path, Func<string, Entry> probe, bool windows)
        => probe(path).Resolved is Kind.Directory or Kind.File ? null : MissingErrno(path, probe, windows);

    /// <summary>
    /// The errno for a name that does not resolve: ENOTDIR when the nearest name above it that exists is a file
    /// (<c>f/x</c>), else ENOENT.
    /// </summary>
    private static string MissingErrno(string path, Func<string, Entry> probe, bool windows)
    {
        int rootLen = RootLength(path, windows);
        int cut = path.Length;
        while (true)
        {
            while (cut > rootLen && IsSep(path[cut - 1], windows)) cut--;
            while (cut > rootLen && !IsSep(path[cut - 1], windows)) cut--;
            while (cut > rootLen && IsSep(path[cut - 1], windows)) cut--;
            if (cut <= rootLen) return NoSuchFile;
            switch (probe(path[..cut]).Resolved)
            {
                case Kind.File: return NotADirectory;
                case Kind.Directory: return NoSuchFile;
            }
        }
    }

    /// <summary>The real file system: what <paramref name="path"/> is (a link not followed), and what it resolves to.</summary>
    internal static Entry Probe(string path)
    {
        try
        {
            bool dir = Directory.Exists(path);   // follows a link
            FileSystemInfo info = dir ? new DirectoryInfo(path) : new FileInfo(path);
            string? target = null;
            try { target = info.LinkTarget; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (target is null)
                return Entry.Of(dir ? Kind.Directory : File.Exists(path) ? Kind.File : Kind.Missing);

            Kind resolved = Kind.Missing;
            if (dir) resolved = Kind.Directory;
            else
            {
                // File.Exists is true for a dangling link on some platforms: resolve the chain to its end instead.
                try
                {
                    var final = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (final is { Exists: true }) resolved = final is DirectoryInfo ? Kind.Directory : Kind.File;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return new Entry(Kind.Link, target, resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Entry.Of(Kind.Missing);
        }
    }

    private static string Join(string root, List<string> parts, char sep)
    {
        var sb = new System.Text.StringBuilder(root);
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(sep);
            sb.Append(parts[i]);
        }
        return sb.ToString();
    }

    private static bool IsSep(char ch, bool windows) => ch == '/' || (windows && ch == '\\');

    /// <summary><c>C:\</c>, <c>C:/</c>, <c>\\server\share\</c> (Windows); <c>/</c> (Unix). 0 when not rooted.</summary>
    internal static int RootLength(string path, bool windows)
    {
        if (path.Length == 0) return 0;
        if (!windows) return path[0] == '/' ? 1 : 0;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return path.Length >= 3 && IsSep(path[2], true) ? 3 : 0;   // C: alone is drive-relative
        if (path.Length >= 2 && IsSep(path[0], true) && IsSep(path[1], true))
        {
            int server = path.IndexOfAny(['\\', '/'], 2);
            if (server < 0) return 0;
            int share = path.IndexOfAny(['\\', '/'], server + 1);
            return share < 0 ? path.Length : share + 1;
        }
        return 0;
    }

    private static string NormalizeRoot(string root, bool windows)
    {
        if (!windows) return "/";
        // The walk's own native spelling of a root it parsed (RootLength accepts either separator); the walk joins
        // with '\' and its result leaves through BashPath.ToShell. Not a path mapping (PathMappingGuardTests).
        var sb = new System.Text.StringBuilder(root.Length + 1);
        foreach (char ch in root) sb.Append(IsSep(ch, true) ? '\\' : ch);
        if (sb[^1] != '\\') sb.Append('\\');
        return sb.ToString();
    }
}
