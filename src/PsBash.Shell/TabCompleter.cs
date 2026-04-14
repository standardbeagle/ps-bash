using PsBash.Core.Parser;

namespace PsBash.Shell;

/// <summary>
/// Provides tab completions for the interactive shell.
/// Handles: file/directory paths, $PATH commands, and aliases.
/// </summary>
internal static class TabCompleter
{
    /// <summary>
    /// Returns completion candidates for the partial token at <paramref name="cursor"/>
    /// within <paramref name="line"/>.
    /// </summary>
    public static IReadOnlyList<string> Complete(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases,
        string cwd)
    {
        var (_, token) = LineEditor.SplitAtWordBoundary(line, cursor);
        var (_, firstToken) = SplitFirstToken(line, cursor);

        bool isFirstWord = IsFirstWord(line, cursor);

        if (isFirstWord)
            return CompleteCommand(token, aliases, cwd);
        else
            return CompletePath(token, cwd);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Command completion (first word on the line)
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> CompleteCommand(
        string token,
        IReadOnlyDictionary<string, string> aliases,
        string cwd)
    {
        var results = new SortedSet<string>(StringComparer.Ordinal);

        // Aliases
        foreach (var name in aliases.Keys)
            if (name.StartsWith(token, StringComparison.Ordinal))
                results.Add(name);

        // Built-ins / known bash commands
        foreach (var name in KnownCommands)
            if (name.StartsWith(token, StringComparison.Ordinal))
                results.Add(name);

        // $PATH executables
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith(token, StringComparison.Ordinal))
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            // Only include executable extensions on Windows
                            var ext = Path.GetExtension(file).ToUpperInvariant();
                            if (ext is ".EXE" or ".CMD" or ".BAT" or ".PS1")
                                results.Add(OperatingSystem.IsWindows() ? name : Path.GetFileNameWithoutExtension(name));
                        }
                        else
                        {
                            results.Add(name);
                        }
                    }
                }
            }
            catch { /* skip inaccessible dirs */ }
        }

        // Local executables in cwd
        try
        {
            foreach (var file in Directory.EnumerateFiles(cwd))
            {
                var name = "./" + Path.GetFileName(file);
                if (name.StartsWith(token, StringComparison.Ordinal))
                    results.Add(name);
            }
        }
        catch { }

        return [.. results];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Path completion (arguments)
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> CompletePath(string token, string cwd)
    {
        try
        {
            string dir, prefix;
            if (token.Length == 0)
            {
                dir = cwd;
                prefix = "";
            }
            else if (token.EndsWith(Path.DirectorySeparatorChar) || token.EndsWith('/'))
            {
                dir = ResolveDir(token, cwd);
                prefix = token;
            }
            else
            {
                var parentPart = Path.GetDirectoryName(token);
                dir = parentPart is { Length: > 0 }
                    ? ResolveDir(parentPart, cwd)
                    : cwd;
                prefix = parentPart is { Length: > 0 }
                    ? token[..(parentPart.Length + 1)]
                    : "";
            }

            var filePrefix = Path.GetFileName(token);

            var results = new List<string>();

            if (!Directory.Exists(dir))
                return results;

            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith(filePrefix, StringComparison.Ordinal)
                    || name.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var completion = prefix + name;
                    if (Directory.Exists(entry))
                        completion += '/';
                    results.Add(completion);
                }
            }

            results.Sort(StringComparer.Ordinal);
            return results;
        }
        catch
        {
            return [];
        }
    }

    private static string ResolveDir(string path, string cwd)
    {
        if (path.StartsWith("~/") || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = home + path[1..];
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(cwd, path));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsFirstWord(string line, int cursor)
    {
        var before = cursor <= line.Length ? line[..cursor] : line;
        // Check if there's any non-whitespace before the current token
        var (_, token) = LineEditor.SplitAtWordBoundary(line, cursor);
        var beforeToken = before[..^token.Length];
        // Trim separators: spaces, semicolons, pipes, &
        var trimmed = beforeToken.TrimStart();
        // Skip any leading env var assignments (FOO=bar cmd)
        var i = 0;
        while (i < trimmed.Length)
        {
            // Skip whitespace
            while (i < trimmed.Length && trimmed[i] == ' ') i++;
            // Check if this word is a FOO=bar assignment
            var wordEnd = i;
            while (wordEnd < trimmed.Length
                   && trimmed[wordEnd] != ' '
                   && trimmed[wordEnd] != ';'
                   && trimmed[wordEnd] != '|'
                   && trimmed[wordEnd] != '&') wordEnd++;

            if (wordEnd == i) break;
            var word = trimmed[i..wordEnd];
            if (word.Contains('='))
            {
                i = wordEnd;
                continue;
            }
            // Non-assignment word found — we are NOT the first command word
            return false;
        }
        return true;
    }

    private static (string Line, string FirstToken) SplitFirstToken(string line, int cursor)
    {
        var before = cursor <= line.Length ? line[..cursor] : line;
        var i = 0;
        while (i < before.Length && before[i] == ' ') i++;
        var start = i;
        while (i < before.Length && before[i] != ' ') i++;
        return (line, before[start..i]);
    }

    // Commonly used bash builtins / known commands for first-word completion
    private static readonly string[] KnownCommands =
    [
        "alias", "bg", "bind", "break", "builtin", "caller", "case", "cd",
        "command", "compgen", "complete", "continue", "declare", "dirs",
        "disown", "echo", "enable", "eval", "exec", "exit", "export",
        "false", "fc", "fg", "for", "function", "getopts", "hash", "help",
        "history", "if", "jobs", "kill", "let", "local", "logout", "mapfile",
        "popd", "printf", "pushd", "pwd", "read", "readarray", "readonly",
        "return", "select", "set", "shift", "shopt", "source", "suspend",
        "test", "time", "times", "trap", "true", "type", "typeset", "ulimit",
        "umask", "unalias", "unset", "until", "wait", "while",
        // Common external tools
        "awk", "cat", "chmod", "chown", "cp", "curl", "cut", "date", "diff",
        "docker", "find", "git", "grep", "gzip", "head", "hostname", "jq",
        "less", "ln", "ls", "make", "man", "mkdir", "more", "mv", "node",
        "npm", "ps", "python", "python3", "rm", "rmdir", "rsync", "sed",
        "sort", "ssh", "stat", "tail", "tar", "tee", "touch", "tr", "uniq",
        "unzip", "vim", "wc", "wget", "which", "xargs", "zip",
    ];
}
