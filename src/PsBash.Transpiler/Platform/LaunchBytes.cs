using System.Text;

namespace PsBash.Core.Platform;

/// <summary>
/// The launcher's own argv and environment as the bytes it was started with. The .NET runtime decodes both with UTF-8
/// before any code runs, so an invalid byte arrives as U+FFFD: <c>export E=$'\xff'; bash -c 'printf %s "$E"'</c> and
/// <c>bash -c 'printf %s "$1"' _ $'\xff'</c> (ps-bash starting itself, as <c>bash</c> / <c>sh</c>) wrote <c>ef bf bd</c>
/// where bash writes <c>ff</c>. Linux keeps them in <c>/proc/self/cmdline</c> and <c>/proc/self/environ</c>; a value
/// is taken from there, decoded with <see cref="RawBytes"/>, only when .NET's decode of those very bytes is the value it
/// has (so a variable the process changed since its start keeps its current value).
/// <para>
/// Elsewhere .NET's values stand: Windows hands a process UTF-16, where an invalid byte has no form; macOS has no
/// <c>/proc</c> (a gap: its child ps-bash still gets U+FFFD).
/// </para>
/// </summary>
public static class LaunchBytes
{
    private const char Replacement = '�';

    /// <summary><paramref name="args"/> (Main's) with each argument that lost a byte replaced by its exact form.</summary>
    public static string[] Arguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!OperatingSystem.IsLinux() || !AnyReplaced(args)) return args;
        var raw = ReadNulSeparated("/proc/self/cmdline");
        // The arguments are the last ones of the command line (before them: the program, or `dotnet x.dll`).
        if (raw is null || raw.Count < args.Length) return args;
        return Exact(args, raw.GetRange(raw.Count - args.Length, args.Length));
    }

    /// <summary>
    /// The value <paramref name="name"/> was started with, exactly, when <paramref name="value"/> (its value now) lost a
    /// byte in .NET's decode of it; otherwise <paramref name="value"/>.
    /// </summary>
    public static string EnvironmentValue(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!OperatingSystem.IsLinux() || value.IndexOf(Replacement) < 0) return value;
        var raw = s_environ.Value;
        if (raw is null) return value;
        var prefix = RawBytes.GetBytes(name + "=");
        foreach (var entry in raw)
        {
            if (!entry.AsSpan().StartsWith(prefix)) continue;
            var bytes = entry[prefix.Length..];
            return Decodes(bytes, value) ? RawBytes.GetString(bytes) : value;
        }
        return value;
    }

    private static readonly Lazy<List<byte[]>?> s_environ = new(() => ReadNulSeparated("/proc/self/environ"));

    /// <summary>Each of <paramref name="lossy"/> that has a U+FFFD and is .NET's decode of its <paramref name="raw"/> entry, exactly.</summary>
    internal static string[] Exact(string[] lossy, IReadOnlyList<byte[]> raw)
    {
        var exact = (string[])lossy.Clone();
        for (int i = 0; i < lossy.Length; i++)
        {
            if (lossy[i].IndexOf(Replacement) < 0) continue;
            if (!Decodes(raw[i], lossy[i])) return lossy; // not the same argv: keep .NET's
            exact[i] = RawBytes.GetString(raw[i]);
        }
        return exact;
    }

    private static bool AnyReplaced(string[] values)
    {
        foreach (var v in values)
            if (v.IndexOf(Replacement) >= 0) return true;
        return false;
    }

    // The decoder .NET used at start: equal means these are the bytes the value came from.
    private static bool Decodes(byte[] bytes, string value)
        => string.Equals(new UTF8Encoding(false, false).GetString(bytes), value, StringComparison.Ordinal);

    internal static List<byte[]> SplitNul(ReadOnlySpan<byte> data)
    {
        var list = new List<byte[]>();
        while (data.Length > 0)
        {
            int end = data.IndexOf((byte)0);
            if (end < 0) end = data.Length;
            list.Add(data[..end].ToArray());
            data = end < data.Length ? data[(end + 1)..] : default;
        }
        return list;
    }

    private static List<byte[]>? ReadNulSeparated(string path)
    {
        try { return SplitNul(File.ReadAllBytes(path)); }
        catch (Exception) { return null; }
    }
}
