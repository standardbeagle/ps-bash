using System.Reflection;
using System.Text;

namespace PsBash.Core.Runtime.Compaction;

/// <summary>
/// Discovers and merges <see cref="FilterSpec"/>s from three sources, lowest to highest
/// precedence: embedded built-ins, the user dir (<c>~/.config/ps-bash/filters</c>), and
/// the project dir (<c>.ps-bash/filters</c>). A spec from a higher source shadows a
/// lower one with the same <see cref="FilterSpec.Name"/>. Results are cached and
/// invalidated when any source file's mtime changes. Disk reads share the file
/// (<c>FileShare.ReadWrite</c>) so a concurrent writer never locks the loader out.
/// </summary>
public sealed class FilterLibrary
{
    /// <summary>Logical-name infix marking an embedded built-in filter resource.</summary>
    private const string EmbeddedInfix = ".Compaction.Filters.";
    private const int MaxFilterFileChars = 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _cacheKey;
    private static IReadOnlyList<FilterSpec>? _cache;

    /// <summary>
    /// Pure precedence merge. <paramref name="user"/> shadows <paramref name="builtin"/>,
    /// <paramref name="project"/> shadows both, by name. Filters whose command is in
    /// <paramref name="excludeCommands"/> are dropped. The result is ordered most-specific
    /// first (more matched args win) so a narrow <c>git/status</c> beats a hypothetical
    /// <c>git</c> catch-all in <see cref="FilterEngine.SelectFilter"/>.
    /// </summary>
    public static IReadOnlyList<FilterSpec> Merge(
        IReadOnlyList<FilterSpec> builtin,
        IReadOnlyList<FilterSpec> user,
        IReadOnlyList<FilterSpec> project,
        IReadOnlySet<string>? excludeCommands = null)
    {
        var byName = new Dictionary<string, FilterSpec>(StringComparer.Ordinal);
        foreach (var f in builtin) byName[f.Name] = f;
        foreach (var f in user) byName[f.Name] = f;
        foreach (var f in project) byName[f.Name] = f;

        IEnumerable<FilterSpec> result = byName.Values;
        if (excludeCommands is { Count: > 0 })
            result = result.Where(f => !excludeCommands.Contains(f.Match.Command));

        return result
            .OrderByDescending(f => f.Match.Args.Count)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Load and merge the full filter set (embedded + user + project), cached by source
    /// mtime. Returns an empty list if no filters are found. Individual malformed files
    /// are skipped (a bad user filter must not suppress every other filter).
    /// </summary>
    public static IReadOnlyList<FilterSpec> Load(
        string? userDir, string? projectDir, IReadOnlySet<string>? excludeCommands = null)
    {
        var key = BuildCacheKey(userDir, projectDir, excludeCommands);
        lock (Gate)
        {
            if (key == _cacheKey && _cache is not null) return _cache;

            var merged = Merge(
                LoadEmbedded(),
                userDir is null ? [] : LoadDirectory(userDir),
                projectDir is null ? [] : LoadDirectory(projectDir),
                excludeCommands);

            _cacheKey = key;
            _cache = merged;
            return merged;
        }
    }

    /// <summary>Resolve the active filter set from the standard user/project locations and environment.</summary>
    public static IReadOnlyList<FilterSpec>? ResolveActive()
    {
        if (EnvFlags.IsTruthy("PSBASH_NO_FILTER")) return null;

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userDir = string.IsNullOrEmpty(home)
                ? null
                : Path.Combine(home, ".config", "ps-bash", "filters");
            var projectDir = Path.Combine(Directory.GetCurrentDirectory(), ".ps-bash", "filters");
            var exclude = ParseExcludeCommands(Environment.GetEnvironmentVariable("PSBASH_FILTER_EXCLUDE"));
            var filters = Load(userDir, projectDir, exclude);
            return filters.Count == 0 ? null : filters;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlySet<string>? ParseExcludeCommands(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<FilterSpec> LoadEmbedded()
    {
        var assembly = typeof(FilterLibrary).Assembly;
        var specs = new List<FilterSpec>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.Contains(EmbeddedInfix, StringComparison.Ordinal) ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                specs.AddRange(FilterJson.ParseFile(ReadStreamBounded(stream)));
            }
            catch
            {
                // A corrupt embedded resource should not break the whole compaction layer.
            }
        }
        return specs;
    }

    internal static IReadOnlyList<FilterSpec> LoadDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return [];

        var specs = new List<FilterSpec>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            try { specs.AddRange(FilterJson.ParseFile(ReadShared(path))); }
            catch { /* skip a single malformed/locked file, keep the rest */ }
        }
        return specs;
    }

    private static string ReadShared(string path)
    {
        if (new FileInfo(path).Length > MaxFilterFileChars)
            throw new IOException("Filter file exceeds the maximum supported size.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadStreamBounded(stream);
    }

    private static string ReadStreamBounded(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sb = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (sb.Length + read > MaxFilterFileChars)
                throw new IOException("Filter file exceeds the maximum supported size.");
            sb.Append(buffer, 0, read);
        }
        return sb.ToString();
    }

    private static string BuildCacheKey(
        string? userDir, string? projectDir, IReadOnlySet<string>? excludeCommands)
    {
        var sb = new StringBuilder();
        // Embedded set is fixed per build — pin to the assembly identity.
        sb.Append(typeof(FilterLibrary).Assembly.ManifestModule.ModuleVersionId).Append('|');
        AppendDirSignature(sb, userDir);
        AppendDirSignature(sb, projectDir);
        if (excludeCommands is { Count: > 0 })
        {
            sb.Append("ex:");
            foreach (var c in excludeCommands.OrderBy(c => c, StringComparer.Ordinal))
                sb.Append(c).Append(',');
        }
        return sb.ToString();
    }

    private static void AppendDirSignature(StringBuilder sb, string? dir)
    {
        sb.Append(dir ?? "-").Append('[');
        if (dir is not null && Directory.Exists(dir))
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                                          .OrderBy(p => p, StringComparer.Ordinal))
            {
                sb.Append(Path.GetFileName(path)).Append(':');
                try { sb.Append(File.GetLastWriteTimeUtc(path).Ticks); }
                catch { sb.Append('?'); }
                sb.Append(';');
            }
        }
        sb.Append(']');
    }
}
