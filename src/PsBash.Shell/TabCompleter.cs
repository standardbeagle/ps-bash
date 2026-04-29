using PsBash.Core.Parser;

namespace PsBash.Shell;

/// <summary>
/// Provides tab completions for the interactive shell.
/// Handles: file/directory paths, $PATH commands, aliases, and sequence-aware suggestions.
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
        return Complete(line, cursor, aliases, cwd, lastCommand: null, historyStore: null);
    }

    /// <summary>
    /// Returns completion candidates for the partial token at <paramref name="cursor"/>
    /// within <paramref name="line"/>, with optional sequence-aware suggestions.
    /// </summary>
    public static IReadOnlyList<string> Complete(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases,
        string cwd,
        string? lastCommand,
        IHistoryStore? historyStore)
    {
        var (_, token) = SplitAtWordBoundaryQuoteAware(line, cursor);
        var (_, firstToken) = SplitFirstToken(line, cursor);

        bool isFirstWord = IsFirstWord(line, cursor);

        // Special case: token starts with "$(" — inside a command substitution.
        // Strip the "$(" and treat the rest as a command-name prefix.
        if (token.StartsWith("$(", StringComparison.Ordinal))
        {
            var innerToken = token[2..];
            return CompleteCommand(innerToken, aliases, cwd);
        }

        if (isFirstWord)
        {
            // Check for sequence suggestions on empty line or matching prefix
            if (historyStore is not null && !string.IsNullOrEmpty(lastCommand))
            {
                var sequenceSuggestions = CompleteSequence(token, lastCommand, cwd, historyStore);
                if (sequenceSuggestions.Count > 0)
                {
                    // Merge with regular command completions, prioritizing matches
                    var commandCompletions = CompleteCommand(token, aliases, cwd);
                    return MergeCompletions(sequenceSuggestions, commandCompletions);
                }
            }
            return CompleteCommand(token, aliases, cwd);
        }

        // Check if cursor is immediately after a redirect operator (>, <, >>)
        // In that case, do path completion regardless of token content.
        bool afterRedirect = IsAfterRedirectOp(line, cursor);

        // Check if current token starts with '-' (flag completion)
        if (!afterRedirect && token.Length > 0 && token[0] == '-')
        {
            var commandName = GetCommandNameAtCursor(line, cursor, aliases);
            if (commandName is not null)
            {
                var flagCompletions = CompleteFlags(commandName, token);
                if (flagCompletions.Count > 0)
                    return flagCompletions;
            }
        }

        return CompletePath(token, cwd);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sequence completion (after a known command)
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> CompleteSequence(
        string token,
        string lastCommand,
        string cwd,
        IHistoryStore historyStore)
    {
        // Only suggest on empty input or when we have a partial match
        // Get suggestions synchronously for tab completion (fire-and-forget if it fails)
        try
        {
            var suggestions = historyStore.GetSequenceSuggestionsAsync(lastCommand, cwd)
                .GetAwaiter().GetResult();

            if (suggestions.Count == 0)
                return [];

            // Filter by token prefix if provided
            var results = new List<string>();
            foreach (var suggestion in suggestions)
            {
                if (string.IsNullOrEmpty(token) ||
                    suggestion.Command.StartsWith(token, StringComparison.Ordinal))
                {
                    results.Add(suggestion.Command);
                }
            }

            return results;
        }
        catch (Exception)
        {
            // Routine: tab completion is advisory and must never crash the shell.
            return [];
        }
    }

    private static IReadOnlyList<string> MergeCompletions(
        IReadOnlyList<string> sequenceSuggestions,
        IReadOnlyList<string> commandCompletions)
    {
        // Sequence suggestions get priority, followed by regular commands
        var seen = new HashSet<string>(sequenceSuggestions, StringComparer.Ordinal);
        var merged = new List<string>(sequenceSuggestions);

        foreach (var cmd in commandCompletions)
        {
            if (!seen.Contains(cmd))
            {
                merged.Add(cmd);
                seen.Add(cmd);
            }
        }

        return merged;
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
            catch (Exception) { /* skip inaccessible dirs */ }
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
        catch (Exception) { }

        return [.. results];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Flag completion (after command name)
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> CompleteFlags(string command, string partial)
    {
        var flags = FlagSpecs.GetFlags(command);
        if (flags is null)
            return [];

        var results = new List<string>();
        foreach (var spec in flags)
        {
            if (spec.Flag.StartsWith(partial, StringComparison.Ordinal))
            {
                // Format: "-l  - long listing" (flag + padding + description)
                results.Add($"{spec.Flag}  - {spec.Desc}");
            }
        }
        return results;
    }

    private static string? GetCommandNameAtCursor(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases)
    {
        // Get the text before the cursor
        var before = cursor <= line.Length ? line[..cursor] : line;

        // Tokenize the line before the cursor and work backwards
        var tokens = Tokenize(before);
        if (tokens.Count == 0)
            return null;

        // Walk backwards from the last token, skipping flags
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (string.IsNullOrEmpty(token))
                continue;

            // Skip flags (tokens starting with -)
            if (token[0] == '-')
                continue;

            // This should be the command name
            var command = token;

            // Expand aliases
            if (aliases.TryGetValue(command, out var aliasValue))
            {
                // Alias might be a full command like "git status"
                // Extract just the first word
                var spaceIdx = aliasValue.IndexOf(' ');
                command = spaceIdx >= 0 ? aliasValue[..spaceIdx] : aliasValue;
            }

            return command;
        }

        return null;
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var i = 0;

        while (i < line.Length)
        {
            // Skip whitespace and separators
            while (i < line.Length && (char.IsWhiteSpace(line[i]) || line[i] == ';' || line[i] == '|' || line[i] == '&'))
                i++;

            if (i >= line.Length)
                break;

            // Find end of token
            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != ';' && line[i] != '|' && line[i] != '&')
                i++;

            tokens.Add(line[start..i]);
        }

        return tokens;
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
        catch (Exception)
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
        var (_, token) = SplitAtWordBoundaryQuoteAware(line, cursor);
        var beforeToken = before.Length >= token.Length
            ? before[..^token.Length]
            : before;
        // Trim leading whitespace
        var trimmed = beforeToken.TrimStart();

        // Walk through the text before the current token. Any command separator
        // (|, ||, &&, ;, &, or $( ) resets the "is first word" context.
        var i = 0;
        bool isFirst = true;
        while (i < trimmed.Length)
        {
            // Skip whitespace
            while (i < trimmed.Length && trimmed[i] == ' ') i++;
            if (i >= trimmed.Length) break;

            // Check for two-character operators first: ||, &&, $(
            if (i + 1 < trimmed.Length)
            {
                var two = trimmed.Substring(i, 2);
                if (two is "||" or "&&")
                {
                    i += 2;
                    isFirst = true;
                    continue;
                }
                if (two == "$(")
                {
                    i += 2;
                    isFirst = true;
                    continue;
                }
            }

            // Single-character separators that start a new command: | ; &
            if (trimmed[i] is '|' or ';' or '&')
            {
                i++;
                isFirst = true;
                continue;
            }

            // Redirect operators > < >> — consume them and their target (path arg), reset isFirst=false
            if (trimmed[i] is '>' or '<')
            {
                // Skip the operator (and optional second char for >>)
                i++;
                if (i < trimmed.Length && trimmed[i] == '>') i++;
                // Skip whitespace
                while (i < trimmed.Length && trimmed[i] == ' ') i++;
                // Skip the redirect target word
                while (i < trimmed.Length && trimmed[i] != ' ' && trimmed[i] != ';' && trimmed[i] != '|') i++;
                // After a redirect target, we're still in the same command context
                continue;
            }

            // Collect a word
            var wordStart = i;
            while (i < trimmed.Length
                   && trimmed[i] != ' '
                   && trimmed[i] != ';'
                   && trimmed[i] != '|'
                   && trimmed[i] != '&'
                   && trimmed[i] != '>'
                   && trimmed[i] != '<') i++;

            if (i == wordStart) { i++; continue; }
            var word = trimmed[wordStart..i];

            if (isFirst && word.Contains('='))
            {
                // env-var assignment prefix: still first-word context for command
                continue;
            }

            // A real command word was found — subsequent words are args
            isFirst = false;
        }

        return isFirst;
    }

    /// <summary>
    /// Returns true when the cursor is positioned right after a redirect operator
    /// (&gt;, &lt;, &gt;&gt;) and optional whitespace — meaning the current token
    /// is a redirect target and should always use path completion.
    /// </summary>
    private static bool IsAfterRedirectOp(string line, int cursor)
    {
        var before = cursor <= line.Length ? line[..cursor] : line;
        var (_, token) = SplitAtWordBoundaryQuoteAware(line, cursor);
        var beforeToken = before.Length >= token.Length
            ? before[..^token.Length].TrimEnd()
            : before.TrimEnd();

        if (beforeToken.Length == 0) return false;

        // Check if beforeToken ends with > or < (possibly preceded by another >)
        var last = beforeToken[^1];
        if (last is '>' or '<') return true;
        if (last == '>' && beforeToken.Length >= 2 && beforeToken[^2] == '>') return true;

        return false;
    }

    /// <summary>
    /// Split at word boundary, respecting quotes. For a quoted token like
    /// <c>cat "my fi</c>, the token is <c>my fi</c> (inside the open quote),
    /// and the base is <c>cat "</c>.
    /// </summary>
    internal static (string Base, string Token) SplitAtWordBoundaryQuoteAware(string line, int cursor)
    {
        var before = cursor <= line.Length ? line[..cursor] : line;

        // Scan forward to identify last unquoted whitespace boundary.
        // Track open quotes so spaces inside quotes don't act as separators.
        int tokenStart = 0;
        bool sq = false, dq = false;
        for (int j = 0; j < before.Length; j++)
        {
            char c = before[j];
            if (sq)
            {
                if (c == '\'') sq = false;
                continue;
            }
            if (dq)
            {
                if (c == '\\' && j + 1 < before.Length) { j++; continue; }
                if (c == '"') dq = false;
                continue;
            }
            if (c == '\'') { sq = true; continue; }
            if (c == '"') { dq = true; continue; }
            if (c == ' ' || c == '\t')
            {
                tokenStart = j + 1;
            }
        }

        // The raw token includes the opening quote if present.
        var rawToken = before[tokenStart..];

        // If the token starts with a quote character, include that quote in the
        // base (so completion restoration rebuilds the quoted form correctly) and
        // return only the bare content as the token that path-completion works on.
        if (rawToken.Length > 0 && rawToken[0] is '"' or '\'')
        {
            // base = everything up to and including the open quote
            // token = bare content after the open quote
            return (before[..(tokenStart + 1)], rawToken[1..]);
        }

        return (before[..tokenStart], rawToken);
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
