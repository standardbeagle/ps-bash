using System.Management.Automation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU quotes an operand AS TYPED in every diagnostic (<c>cat: nosuchfile: No such file or
/// directory</c>), never the resolved full path. Readers resolve operands against PowerShell's
/// location before touching the file, so their messages were built from the full path. This is the
/// ONE place that maps a path in a message back to the operand: <see cref="FileSystemHelpers.WriteStderr"/>
/// (the sink every cmdlet diagnostic goes through) calls <see cref="Rewrite"/>.
/// <list type="number">
/// <item>An operand a cmdlet resolved through <see cref="FileSystemHelpers.ResolveOperandPaths"/> /
/// <see cref="FileSystemHelpers.ResolveOperands"/> was <see cref="Remember"/>ed (resolved path →
/// text as typed, so <c>./x</c> stays <c>./x</c>); its resolved path in the message becomes that text.</item>
/// <item>Any other path below the working directory (a cmdlet that resolves on its own) loses the
/// directory prefix: relative, forward slashes.</item>
/// </list>
/// A path that is already as typed (a mutator's message, an absolute operand outside the working
/// directory) contains no unmapped cwd prefix and passes through untouched.
/// </summary>
internal static class OperandDisplay
{
    private sealed class Names
    {
        public readonly Dictionary<string, string> ResolvedToTyped = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Typed = new(StringComparer.OrdinalIgnoreCase);
    }

    // Per cmdlet INSTANCE: a fresh invocation starts clean, and the table dies with the cmdlet.
    private static readonly ConditionalWeakTable<PSCmdlet, Names> Table = new();

    private static string Key(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>Record that <paramref name="resolved"/> is what the user typed as <paramref name="typed"/>.</summary>
    public static void Remember(PSCmdlet cmdlet, string resolved, string typed)
    {
        if (string.IsNullOrEmpty(resolved) || string.IsNullOrEmpty(typed)) return;
        var names = Table.GetOrCreateValue(cmdlet);
        names.ResolvedToTyped[Key(resolved)] = typed;
        names.Typed.Add(Key(typed));
    }

    /// <summary><paramref name="message"/> with every resolved path shown as the operand was typed.</summary>
    public static string Rewrite(PSCmdlet cmdlet, string message)
    {
        if (message.Length == 0) return message;
        string? cwd;
        try { cwd = cmdlet.SessionState.Path.CurrentFileSystemLocation.ProviderPath; }
        catch { return message; } // non-filesystem location / no session state
        if (string.IsNullOrEmpty(cwd)) return message;

        Table.TryGetValue(cmdlet, out var names);
        return PathInMessage(cwd).Replace(message, m => Display(m.Value, m.Groups["rest"].Value, names));
    }

    private static string Display(string full, string rest, Names? names)
    {
        string key = Key(full);
        if (names is not null)
        {
            if (names.ResolvedToTyped.TryGetValue(key, out var typed)) return typed;
            // Already the operand as typed (an absolute operand inside the working directory).
            if (names.Typed.Contains(key)) return full;
        }
        return rest.Length == 0 ? "." : rest.Replace('\\', '/');
    }

    // cwd (either slash style, case-insensitive on Windows) then an optional separator + remainder
    // that runs to the closing quote / ": " / whitespace / end of message.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Patterns = new();

    private static Regex PathInMessage(string cwd)
    {
        var dir = cwd.Length > 1 ? cwd.TrimEnd('\\', '/') : cwd;
        if (Patterns.TryGetValue(dir, out var cached)) return cached;
        if (Patterns.Count > 64) Patterns.Clear(); // a script that cds through many directories
        var sb = new StringBuilder();
        var parts = dir.Split('\\', '/');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append(@"[\\/]");
            sb.Append(Regex.Escape(parts[i]));
        }
        var options = RegexOptions.CultureInvariant;
        if (OperatingSystem.IsWindows()) options |= RegexOptions.IgnoreCase;
        // The cwd must start a path token (not sit inside a longer one), and end at a separator or
        // delimiter so /tmp/abc does not match inside /tmp/abcd.
        var re = new Regex(
            @"(?<![\w.\-])" + sb + @"(?:[\\/](?<rest>[^'`\r\n]*?))?(?=['`]|: |\s|$)",
            options);
        Patterns[dir] = re;
        return re;
    }
}
