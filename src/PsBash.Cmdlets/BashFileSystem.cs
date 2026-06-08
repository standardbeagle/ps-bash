using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Core file-system primitives for the <c>Invoke-Bash*</c> cmdlets. This is the
/// SINGLE place file content is read — no cmdlet opens a <see cref="FileStream"/>
/// / <see cref="StreamReader"/> or calls <see cref="File.ReadAllText(string)"/> /
/// <see cref="File.ReadAllBytes(string)"/> directly. Two reasons, both load-bearing
/// for "first-rate performance and reasonable output across the board":
/// <list type="number">
/// <item><b>STREAMING.</b> <see cref="ReadLines"/> yields line-by-line and
/// <see cref="OpenRead"/> hands back a stream, so <c>head -n 5</c> on a 2 GB file
/// reads ~5 lines, not 2 GB. Whole-file slurps (<c>File.ReadAllText</c> /
/// <c>ReadAllBytes</c>) materialize the entire file in memory and are a perf /
/// memory disaster on large inputs — they are deliberately NOT used here.
/// <see cref="ReadAllText"/> is the one exception, reserved for whole-document
/// parsers (jq/yq/sed-script) and named loudly so its cost is obvious.</item>
/// <item><b>BINARY AWARENESS.</b> A NUL byte in the first 8 KB marks a file binary
/// (the GNU grep / ripgrep heuristic, <see cref="IsBinary"/>). Each tool decides
/// what "binary" means FOR IT — grep/rg skip them, <c>cat</c>/<c>strings</c>/
/// checksums are binary-native — but the detection lives in exactly one place.</item>
/// </list>
/// </summary>
public static class BashFileSystem
{
    private const int BinaryProbeBytes = 8192;
    private const int DefaultWholeDocumentMaxChars = 16 * 1024 * 1024;
    private const int DefaultWholeDocumentMaxBytes = 16 * 1024 * 1024;

    public readonly record struct TextLine(string Text, bool HasTrailingNewline);

    // -- Streams ------------------------------------------------------------

    /// <summary>
    /// Open a file for shared reading. <see cref="FileShare.ReadWrite"/> so a
    /// concurrent writer (or another ps-bash process) is never locked out — the
    /// temp-files rule. The caller owns disposal.
    /// </summary>
    public static FileStream OpenRead(string path)
    {
        // A /dev/null (or Windows NUL) OPERAND is an empty file. Serve it from the OS-native
        // null device so every read method — ReadLines / ReadAllText / ReadAllBytes / IsBinary
        // all route through here — sees empty content, instead of throwing on the non-existent
        // resolved path (e.g. C:\dev\null on Windows). Both devices read as zero bytes.
        if (FileSystemHelpers.IsNullDevice(path))
            path = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        return new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BinaryProbeBytes,
            FileOptions.SequentialScan);
    }

    // -- Binary detection ---------------------------------------------------

    /// <summary>True when the file has a NUL byte in its first 8 KB.</summary>
    public static bool IsBinary(string path)
    {
        using var fs = OpenRead(path);
        return ProbeBinary(fs);
    }

    /// <summary>
    /// Probe an OPEN stream for binary content (NUL in first 8 KB) and rewind it.
    /// Reads at most 8 KB regardless of file size — the whole point is to avoid
    /// touching a multi-gigabyte binary.
    /// </summary>
    private static bool ProbeBinary(Stream fs)
    {
        var probe = new byte[BinaryProbeBytes];
        int total = 0;
        int n;
        // One Read may return short; fill up to the probe window so a NUL just
        // past the first read is still seen.
        while (total < probe.Length && (n = fs.Read(probe, total, probe.Length - total)) > 0)
        {
            total += n;
        }
        bool binary = Array.IndexOf(probe, (byte)0, 0, total) >= 0;
        if (fs.CanSeek) fs.Seek(0, SeekOrigin.Begin);
        return binary;
    }

    // -- Text line streaming ------------------------------------------------

    /// <summary>
    /// Lazily stream a text file's lines — CRLF normalized to LF, BOM-aware UTF-8,
    /// no trailing empty line. Splits ONLY on <c>\n</c> (a <c>\r</c> immediately
    /// before <c>\n</c> is dropped; a lone <c>\r</c> stays in the line), matching
    /// the old <c>File.ReadAllText(p).Replace("\r\n","\n").Split('\n')</c> path
    /// byte-for-byte — but without ever holding the whole file in memory.
    /// <para>
    /// When <paramref name="skipBinary"/> is true and the file is binary (NUL in
    /// first 8 KB), yields nothing. The file is opened (and any IO error thrown)
    /// only when enumeration starts; iterate inside the caller's try/catch.
    /// </para>
    /// </summary>
    public static IEnumerable<string> ReadLines(string path, bool skipBinary = false)
    {
        foreach (var line in ReadTextLines(path, skipBinary))
        {
            yield return line.Text;
        }
    }

    /// <summary>
    /// Lazily stream text lines while preserving whether the source line ended
    /// in <c>\n</c>. This is for commands like <c>cat</c> where the final
    /// no-newline marker affects downstream serialization. CRLF is normalized
    /// the same way <see cref="ReadLines"/> normalizes it.
    /// </summary>
    public static IEnumerable<TextLine> ReadTextLines(string path, bool skipBinary = false)
    {
        var fs = OpenRead(path);
        return ReadTextLinesIterator(fs, skipBinary);
    }

    private static IEnumerable<TextLine> ReadTextLinesIterator(FileStream fs, bool skipBinary)
    {
        using (fs)
        {
            if (skipBinary && ProbeBinary(fs)) yield break;

            using var reader = new StreamReader(
                fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            // Scan each buffer chunk for '\n' and BULK-append the segment between
            // newlines (one Append per line, not per char — the per-char version
            // was 6× slower than ReadAllText+Split on a 95 MB file). A '\r'
            // immediately before '\n' is dropped (CRLF→LF); a lone '\r' stays in
            // the line. CRLF straddling a chunk boundary is handled: the '\r' is
            // carried in `sb` and stripped when the next chunk's leading '\n' lands.
            var sb = new StringBuilder(256);
            var buf = new char[16384];
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            {
                int start = 0;
                for (int i = 0; i < read; i++)
                {
                    if (buf[i] != '\n') continue;
                    sb.Append(buf, start, i - start);
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                    yield return new TextLine(sb.ToString(), HasTrailingNewline: true);
                    sb.Clear();
                    start = i + 1;
                }
                if (start < read) sb.Append(buf, start, read - start);
            }
            // Trailing content with no final newline is the last line. A file that
            // ends in '\n' leaves sb empty here → no spurious trailing "" (parity).
            if (sb.Length > 0) yield return new TextLine(sb.ToString(), HasTrailingNewline: false);
        }
    }

    /// <summary>
    /// Read an ENTIRE file as text (CRLF normalized to LF, BOM-aware UTF-8).
    /// WHOLE-FILE READ — reserved for whole-document consumers that genuinely need
    /// the full content (a JSON/YAML parser, a sed script). Do NOT use for
    /// line-oriented tools; use <see cref="ReadLines"/> so large inputs stream.
    /// </summary>
    public static string ReadAllText(string path)
    {
        using var fs = OpenRead(path);
        return ReadAllText(fs);
    }

    /// <summary>
    /// Read an ENTIRE stream as text (CRLF normalized to LF, BOM-aware UTF-8).
    /// The caller owns the stream lifetime. Reserved for whole-document
    /// consumers that already opened or received a stream.
    /// </summary>
    public static string ReadAllText(Stream stream)
    {
        return ReadAllTextBounded(stream, normalizeCrLf: true);
    }

    /// <summary>
    /// Read an ENTIRE file as bytes. WHOLE-FILE READ — reserved for the few
    /// operations whose output is a transform of every byte (base64 of a file
    /// produces one blob ~1.33× its size, so streaming the input buys nothing).
    /// For "take/scan a prefix" or "hash" use <see cref="OpenRead"/> and consume
    /// the stream incrementally instead — never load a file you don't fully need.
    /// </summary>
    public static byte[] ReadAllBytes(string path)
    {
        using var fs = OpenRead(path);
        using var ms = new MemoryStream();
        var maxBytes = WholeDocumentMaxBytes();
        var buffer = new byte[8192];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > maxBytes)
                throw new IOException($"Whole-document binary read exceeds {maxBytes} bytes.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Like <see cref="ReadAllText"/> but WITHOUT CRLF normalization — the raw
    /// decoded text. For the few whole-document consumers that must see bytes as
    /// written (a JSON/YAML parser, a sed script). Same whole-file caveat.
    /// </summary>
    public static string ReadAllTextRaw(string path)
    {
        using var fs = OpenRead(path);
        return ReadAllTextRaw(fs);
    }

    /// <summary>
    /// Like <see cref="ReadAllText(Stream)"/> but WITHOUT CRLF normalization.
    /// The caller owns the stream lifetime.
    /// </summary>
    public static string ReadAllTextRaw(Stream stream)
    {
        return ReadAllTextBounded(stream, normalizeCrLf: false);
    }

    private static string ReadAllTextBounded(Stream stream, bool normalizeCrLf)
    {
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var maxChars = WholeDocumentMaxChars();
        var sb = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (sb.Length + read > maxChars)
                throw new IOException($"Whole-document text read exceeds {maxChars} characters.");
            sb.Append(buffer, 0, read);
        }

        var text = sb.ToString();
        return normalizeCrLf ? text.Replace("\r\n", "\n") : text;
    }

    private static int WholeDocumentMaxChars()
    {
        var raw = Environment.GetEnvironmentVariable("PSBASH_WHOLE_DOCUMENT_MAX_CHARS");
        return int.TryParse(raw, out var value) && value > 0
            ? Math.Max(value, 1024)
            : DefaultWholeDocumentMaxChars;
    }

    private static int WholeDocumentMaxBytes()
    {
        var raw = Environment.GetEnvironmentVariable("PSBASH_WHOLE_DOCUMENT_MAX_BYTES");
        return int.TryParse(raw, out var value) && value > 0
            ? Math.Max(value, 1024)
            : DefaultWholeDocumentMaxBytes;
    }

    // -- Recursive search enumeration (pruned, streaming) -------------------

    /// <summary>
    /// Directory names pruned by default from recursive <c>grep -r</c> / <c>rg</c>
    /// search: version-control metadata and build / dependency output trees. Two
    /// reasons they are pruned: users almost never want hits inside them, and
    /// walking into them is what tripped the host idle-timeout watchdog. Pruning
    /// happens BEFORE descent, so their contents are never enumerated. Diverges
    /// from GNU <c>grep -r</c> (which prunes nothing); <see cref="NoIgnoreEnvVar"/>
    /// restores the unfiltered walk.
    /// </summary>
    public static readonly IReadOnlyCollection<string> DefaultPrunedDirectories =
        new[] { ".git", ".hg", ".svn", ".vs", "node_modules", "bin", "obj" };

    private static readonly HashSet<string> PrunedDirLookup =
        new(DefaultPrunedDirectories, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Environment variable that, when truthy (<c>1</c>/<c>true</c>/<c>yes</c>/
    /// <c>on</c>), turns off BOTH default directory pruning and binary-file
    /// skipping for <c>grep</c>/<c>rg</c> — the single "search everything" knob.
    /// </summary>
    public const string NoIgnoreEnvVar = "PSBASH_SEARCH_NO_IGNORE";

    /// <summary>True when <see cref="NoIgnoreEnvVar"/> is set to a truthy value.</summary>
    public static bool DefaultFilteringDisabled()
    {
        var v = Environment.GetEnvironmentVariable(NoIgnoreEnvVar)?.Trim();
        return v is not null
            && (v.Equals("1", StringComparison.OrdinalIgnoreCase)
                || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || v.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lazily enumerate files under <paramref name="root"/> for recursive search,
    /// pruning <see cref="DefaultPrunedDirectories"/> (unless
    /// <paramref name="includeIgnored"/>) and dot-entries (unless
    /// <paramref name="includeHidden"/>) BEFORE descending. Deterministic
    /// pre-order DFS, ordinal-sorted, streaming — so callers emit matches as the
    /// tree is walked. Unreadable directories are skipped best-effort.
    /// </summary>
    public static IEnumerable<string> EnumerateSearchFiles(
        string root, bool includeIgnored, bool includeHidden)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string[] files;
            try { files = Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal).ToArray(); }
            catch { files = Array.Empty<string>(); }
            foreach (var f in files)
            {
                if (!includeHidden && IsHiddenName(Path.GetFileName(f))) continue;
                yield return f;
            }

            string[] subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir).OrderDescending(StringComparer.Ordinal).ToArray(); }
            catch { subdirs = Array.Empty<string>(); }
            foreach (var sd in subdirs)
            {
                string name = Path.GetFileName(sd);
                if (!includeHidden && IsHiddenName(name)) continue;
                if (!includeIgnored && PrunedDirLookup.Contains(name)) continue;
                stack.Push(sd);
            }
        }
    }

    private static bool IsHiddenName(string name)
        => name.Length > 0 && name[0] == '.';
}
