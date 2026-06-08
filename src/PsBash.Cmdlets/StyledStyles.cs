using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Shared stylesheet resolution for the styled-output cmdlets (<c>Format-Styled</c>,
/// <c>Show-Styled</c>): load a built-in sheet embedded in this assembly and append any user
/// override of the same base name (the CSS cascade — later rules win). One implementation so the
/// static and interactive renderers resolve sheets identically.
/// </summary>
internal static class StyledStyles
{
    /// <summary>
    /// Resolve a stylesheet <paramref name="name"/> (e.g. <c>default</c>, <c>fs</c>, <c>procsvc</c>)
    /// to CSS text: the embedded built-in first, then a user override of the same name appended
    /// after it (cascade). Throws <see cref="ItemNotFoundException"/> when neither exists.
    /// </summary>
    public static string Resolve(string name)
    {
        var builtin = ReadEmbeddedStyle(name);
        var user = ReadUserOverride(name);
        if (builtin is null && user is null)
        {
            var names = string.Join(", ", BuiltinStyleNames());
            throw new ItemNotFoundException(
                $"No stylesheet named '{name}'. Built-in: {names}. " +
                "Pass inline CSS, a .css path, or drop '<name>.css' in $PSBASH_STYLE_PATH or ~/.config/ps-bash/styles.");
        }

        return string.Join("\n", new[] { builtin, user }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>
    /// Pick the family stylesheet that best fits a row's Strata <paramref name="kind"/>: filesystem
    /// objects → <c>fs</c>, processes/services → <c>procsvc</c>, error records → <c>error</c>,
    /// anything else → the generic <c>object</c> fallback.
    /// </summary>
    public static string AutoStyleForKind(string kind) => kind switch
    {
        "FileInfo" or "DirectoryInfo" or "FileSystemInfo" => "fs",
        "Process" or "Service" or "ServiceController" => "procsvc",
        "ErrorRecord" => "error",
        "PingReply" or "TraceHop" => "net",
        _ => "object",
    };

    /// <summary>Read the embedded built-in stylesheet <c>styles/&lt;name&gt;.css</c>, or null.</summary>
    public static string? ReadEmbeddedStyle(string name)
    {
        var asm = typeof(StyledStyles).Assembly;
        var suffix = $".styles.{name}.css";
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (resource is null)
        {
            return null;
        }

        using var stream = asm.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        return BashFileSystem.ReadAllTextRaw(stream);
    }

    /// <summary>Names of the embedded built-in stylesheets (for error messages).</summary>
    public static IEnumerable<string> BuiltinStyleNames()
    {
        const string mid = ".styles.";
        foreach (var n in typeof(StyledStyles).Assembly.GetManifestResourceNames())
        {
            var i = n.IndexOf(mid, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && n.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            {
                yield return n.Substring(i + mid.Length, n.Length - (i + mid.Length) - ".css".Length);
            }
        }
    }

    /// <summary>Read a user override stylesheet <c>&lt;name&gt;.css</c> from the style dirs, or null.</summary>
    public static string? ReadUserOverride(string name)
    {
        foreach (var dir in UserStyleDirs())
        {
            try
            {
                var path = Path.Combine(dir, name + ".css");
                if (File.Exists(path))
                {
                    return BashFileSystem.ReadAllTextRaw(path);
                }
            }
            catch
            {
                // Malformed path entry — skip and try the next dir.
            }
        }

        return null;
    }

    /// <summary>User stylesheet search dirs: $PSBASH_STYLE_PATH (dir or list), then ~/.config and ~/.psbash.</summary>
    public static IEnumerable<string> UserStyleDirs()
    {
        var env = Environment.GetEnvironmentVariable("PSBASH_STYLE_PATH");
        if (!string.IsNullOrEmpty(env))
        {
            foreach (var d in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return d;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".config", "ps-bash", "styles");
            yield return Path.Combine(home, ".psbash", "styles");
        }
    }
}
