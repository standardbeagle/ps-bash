using System.Security.Cryptography;
using System.Text;

namespace PsBash.Core.Transpiler;

/// <summary>
/// Caches the pure bash→PowerShell transpile so a stable startup script / rc file is not re-parsed
/// and re-emitted on every run (on-disk, keyed by content hash), and a repeated or large command in
/// a long-lived session (interactive REPL, daemon host) is transpiled once (in-memory LRU).
///
/// SAFETY: transpilation is deterministic ONLY for a fixed (transpiler build, path-mode, context).
/// Every cache key folds in the transpiler assembly MVID (changes every build), the PSBASH_UNIX_PATHS
/// path mode (the one env var <c>PsEmitter</c> reads), and the <see cref="TranspileContext"/> — so a
/// new build, a flipped path mode, or a different context can never serve a stale transpile. Any cache
/// failure falls back to a direct transpile; the cache is an optimization, never a correctness
/// dependency. A transpile that throws <see cref="PsBash.Core.Parser.ParseException"/> is never cached.
/// </summary>
public static class TranspileCache
{
    // Below this length the in-memory tier is skipped: a short command transpiles in microseconds, so
    // the LRU bookkeeping (and the risk of evicting a genuinely hot large script) is not worth it. The
    // on-disk tier is for files, which are worth caching regardless of length (parse cost > a disk read).
    private const int MemoryMinLength = 256;
    private const int MemoryMaxEntries = 64;
    private const int DiskMaxEntries = 256;
    private const long DiskCacheEntryMaxBytes = 16 * 1024 * 1024;

    // Changes on every compile of the transpiler assembly — the strongest possible "the emitter may
    // have changed" signal, far safer than a hand-maintained version string for invalidation.
    private static readonly string ModuleId =
        typeof(BashTranspiler).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    private static readonly object MemLock = new();
    private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, string>>> MemMap = new();
    private static readonly LinkedList<KeyValuePair<string, string>> MemOrder = new();

    private static string? _cacheDir;
    private static bool _cacheDirResolved;

    /// <summary>
    /// Transpile a FILE's content (rc file, .sh script) through the on-disk hash cache: the first run
    /// transpiles and writes <c>{tmp}/ps-bash/transpile-cache/{key}.ps1</c>; later runs (even in a
    /// fresh process) read it back. Falls back to a direct transpile on any cache I/O failure.
    /// Propagates <see cref="PsBash.Core.Parser.ParseException"/>.
    /// </summary>
    public static string GetOrTranspileFile(string content, TranspileContext context = TranspileContext.Default)
    {
        string key;
        try { key = ComputeKey(content, context); }
        catch { return BashTranspiler.Transpile(content, context); }

        var dir = EnsureCacheDir();
        if (dir is not null)
        {
            var path = Path.Combine(dir, key + ".ps1");
            try
            {
                if (File.Exists(path))
                {
                    var cached = ReadCacheEntry(path);
                    // Touch so the LRU-by-mtime prune keeps frequently-used entries.
                    try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { /* touch is best-effort */ }
                    return cached;
                }
            }
            catch { /* unreadable cache entry — fall through to transpile + rewrite */ }
        }

        var pwsh = BashTranspiler.Transpile(content, context);   // may throw ParseException — not cached

        if (dir is not null)
            TryWriteDisk(dir, key, pwsh);

        return pwsh;
    }

    private static string ReadCacheEntry(string path)
    {
        return ReadAllTextBounded(
            path,
            DiskCacheEntryMaxBytes,
            "Cached transpile entry exceeds the maximum cached entry size.");
    }

    private static string ReadAllTextBounded(string path, long maxBytes, string tooLargeMessage)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytes.Length + read > maxBytes)
                throw new IOException(tooLargeMessage);
            bytes.Write(buffer, 0, read);
        }

        bytes.Position = 0;
        using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Transpile a command through the in-process LRU cache. Intended for a long-lived session
    /// (interactive REPL / daemon) where the same command (a re-run, or a fixed PROMPT_COMMAND) or a
    /// large script recurs. Short inputs (&lt; <see cref="MemoryMinLength"/>) bypass the cache and
    /// transpile directly. Propagates <see cref="PsBash.Core.Parser.ParseException"/> (never cached).
    /// </summary>
    public static string GetOrTranspileMemory(string content, TranspileContext context = TranspileContext.Default)
    {
        if (content.Length < MemoryMinLength)
            return BashTranspiler.Transpile(content, context);

        string key;
        try { key = ComputeKey(content, context); }
        catch { return BashTranspiler.Transpile(content, context); }

        lock (MemLock)
        {
            if (MemMap.TryGetValue(key, out var hit))
            {
                MemOrder.Remove(hit);
                MemOrder.AddFirst(hit);   // most-recently-used
                return hit.Value.Value;
            }
        }

        var pwsh = BashTranspiler.Transpile(content, context);

        lock (MemLock)
        {
            if (!MemMap.ContainsKey(key))
            {
                var node = MemOrder.AddFirst(new KeyValuePair<string, string>(key, pwsh));
                MemMap[key] = node;
                while (MemMap.Count > MemoryMaxEntries)
                {
                    var last = MemOrder.Last!;
                    MemOrder.RemoveLast();
                    MemMap.Remove(last.Value.Key);
                }
            }
        }
        return pwsh;
    }

    private static string ComputeKey(string content, TranspileContext context)
    {
        var pathMode = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS") == "1" ? "u" : "w";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return $"{ModuleId}-{pathMode}-{(int)context}-{hash}";
    }

    private static string? EnsureCacheDir()
    {
        if (_cacheDirResolved) return _cacheDir;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ps-bash", "transpile-cache");
            Directory.CreateDirectory(dir);
            _cacheDir = dir;
        }
        catch { _cacheDir = null; }
        _cacheDirResolved = true;
        return _cacheDir;
    }

    private static void TryWriteDisk(string dir, string key, string pwsh)
    {
        try
        {
            var path = Path.Combine(dir, key + ".ps1");
            // Write to a pid-unique temp then atomically move, so a concurrent reader never sees a
            // partially written entry (multiple processes may race on the same key; the content is
            // identical for a given key, so last-writer-wins is harmless).
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, pwsh);
            try { File.Move(tmp, path, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { /* ignore */ } return; }

            PruneDisk(dir);
        }
        catch { /* cache write is best-effort */ }
    }

    private static void PruneDisk(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "*.ps1");
            if (files.Length <= DiskMaxEntries) return;
            Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
            for (int i = 0; i < files.Length - DiskMaxEntries; i++)
            {
                try { File.Delete(files[i]); } catch { /* ignore */ }
            }
        }
        catch { /* prune is best-effort */ }
    }
}
