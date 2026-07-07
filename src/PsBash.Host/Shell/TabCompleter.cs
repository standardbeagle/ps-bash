using PsBash.Core.Parser;

namespace PsBash.Host.Shell;

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
    public static IReadOnlyList<CompletionItem> Complete(
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
    public static IReadOnlyList<CompletionItem> Complete(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases,
        string cwd,
        string? lastCommand,
        IHistoryStore? historyStore)
    {
        return CompleteCore(line, cursor, aliases, cwd, lastCommand, sequenceSuggestions: []);
    }

    public static async Task<IReadOnlyList<CompletionItem>> CompleteAsync(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases,
        string cwd,
        string? lastCommand,
        IHistoryStore? historyStore,
        CancellationToken ct = default)
    {
        IReadOnlyList<SequenceSuggestion> sequenceSuggestions = [];
        if (historyStore is not null && !string.IsNullOrEmpty(lastCommand))
        {
            try
            {
                // Bound the sqlite sequence query by the Tab deadline — its 3s busy
                // timeout must never hang the prompt (completion is advisory). WaitAsync
                // enforces the deadline even if the query blocks on the busy timeout
                // internally without observing ct.
                sequenceSuggestions = await historyStore.GetSequenceSuggestionsAsync(lastCommand, cwd, ct)
                    .WaitAsync(ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Routine: tab completion is advisory and must never crash the shell.
                sequenceSuggestions = [];
            }
        }

        return CompleteCore(line, cursor, aliases, cwd, lastCommand, sequenceSuggestions);
    }

    private static IReadOnlyList<CompletionItem> CompleteCore(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases,
        string cwd,
        string? lastCommand,
        IReadOnlyList<SequenceSuggestion> sequenceSuggestions)
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
            if (sequenceSuggestions.Count > 0)
            {
                var sequenceCompletions = CompleteSequence(token, sequenceSuggestions);
                if (sequenceCompletions.Count > 0)
                {
                    // Merge with regular command completions, prioritizing matches
                    var commandCompletions = CompleteCommand(token, aliases, cwd);
                    return CompletionMerge.Append(sequenceCompletions, commandCompletions, sortSecondary: false);
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

        if (!afterRedirect && TryCompleteGrepPatternValue(line, cursor, aliases, token, cwd, out var grepPatternCompletions))
            return grepPatternCompletions;

        return CompletePath(token, cwd);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sequence completion (after a known command)
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<CompletionItem> CompleteSequence(
        string token,
        IReadOnlyList<SequenceSuggestion> suggestions)
    {
        if (suggestions.Count == 0)
            return [];

        // Filter by token prefix if provided
        var results = new List<CompletionItem>();
        foreach (var suggestion in suggestions)
        {
            if (string.IsNullOrEmpty(token) ||
                suggestion.Command.StartsWith(token, StringComparison.Ordinal))
            {
                results.Add(new CompletionItem(suggestion.Command));
            }
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Command completion (first word on the line)
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<CompletionItem> CompleteCommand(
        string token,
        IReadOnlyDictionary<string, string> aliases,
        string cwd)
    {
        // Bash rule: a command word containing a slash (or a ~ home reference) is a path to an
        // executable, so it gets filename completion — directories AND files, not a $PATH search.
        // CompletePath appends '/' to directories, so "./sc"<tab> -> "./scripts/" lets the user
        // keep drilling into the directory. (CompleteCommand's $PATH/cwd scan only sees files, so
        // without this a "./dir" prefix completed to nothing.)
        if (LooksLikeCommandPath(token))
            return CompletePath(token, cwd);

        var results = CollectCommandNames(token);

        // Aliases (a user's own shortcuts) also complete at the command position.
        foreach (var name in aliases.Keys)
            if (name.StartsWith(token, StringComparison.Ordinal))
                results.Add(name);

        return [.. results.Select(r => new CompletionItem(r))];
    }

    /// <summary>
    /// Command names matching <paramref name="token"/>: aliases, known bash builtins/coreutils, and
    /// <c>$PATH</c> executables, deduped and ordinal-sorted. Shared by Tab command completion and the
    /// live command-doc panel.
    /// </summary>
    private static SortedSet<string> CollectCommandNames(string token)
    {
        var results = new SortedSet<string>(StringComparer.Ordinal);

        // Built-ins / known bash commands
        foreach (var name in KnownCommands)
            if (name.StartsWith(token, StringComparison.Ordinal))
                results.Add(name);

        // $PATH executables (from the cached snapshot — see GetPathCommands).
        foreach (var name in GetPathCommands())
            if (name.StartsWith(token, StringComparison.Ordinal))
                results.Add(name);

        return results;
    }

    /// <summary>
    /// Command-name matches for the live type-ahead panel at the command position (mirrors the
    /// flag-doc panel, but for the first word). Returns the matching alias and command names, with
    /// aliases first so a user's own shortcuts surface above the builtin list. Empty unless the
    /// cursor is on a plain command-name token (non-empty, not a flag, not a path) at the first word.
    /// Pure/synchronous — no runspace, so it stays inside the keystroke budget.
    /// </summary>
    internal static IReadOnlyList<string> MatchingCommandNames(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyList<string>? powerShellCommands = null)
    {
        if (!IsFirstWord(line, cursor))
            return [];

        var (_, token) = SplitAtWordBoundaryQuoteAware(line, cursor);
        // A flag, a path-like token (./x, /usr/bin/x, ~/x), or an empty prompt are not command-name
        // prefixes — the path/flag providers (or nothing) own those.
        if (token.Length == 0 || token[0] == '-' || LooksLikeCommandPath(token))
            return [];

        // Aliases first (a user's own shortcuts), then bash builtins/$PATH, then PowerShell
        // commands — each prefix-filtered. PowerShell command resolution is case-insensitive, so a
        // lowercase "get-c" prefix still surfaces "Get-Command"; bash commands stay case-sensitive.
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in aliases.Keys)
            if (name.StartsWith(token, StringComparison.Ordinal) && seen.Add(name))
                ordered.Add(name);
        foreach (var name in CollectCommandNames(token))
            if (seen.Add(name))
                ordered.Add(name);

        // PowerShell commands: the live runspace snapshot (loaded modules + session-defined
        // functions/aliases) when available, else the curated static fallback during warmup.
        var psCommands = powerShellCommands is { Count: > 0 } ? powerShellCommands : KnownPowerShellCommands;
        foreach (var name in psCommands)
            if (name.StartsWith(token, StringComparison.OrdinalIgnoreCase) && seen.Add(name))
                ordered.Add(name);

        return ordered;
    }

    // $PATH executable-name snapshot, cached and invalidated by the PATH value.
    // The old code re-walked every PATH directory on EVERY command-position Tab
    // (Directory.EnumerateFiles across all dirs), which is a synchronous disk
    // scan that can blow the completion deadline on a large or slow (networked /
    // AV-scanned) PATH. The set of executables only changes when PATH changes or
    // something is installed, so we snapshot once and reuse until PATH differs.
    private static readonly object _pathCacheLock = new();
    private static string? _pathCacheKey;
    private static string[] _pathCommandCache = [];

    private static string[] GetPathCommands()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        lock (_pathCacheLock)
        {
            if (_pathCacheKey == pathVar)
                return _pathCommandCache;

            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            // Only executable extensions count as commands on Windows.
                            var ext = Path.GetExtension(file).ToUpperInvariant();
                            if (ext is ".EXE" or ".CMD" or ".BAT" or ".PS1")
                                names.Add(Path.GetFileName(file));
                        }
                        else
                        {
                            names.Add(Path.GetFileName(file));
                        }
                    }
                }
                catch (Exception) { /* skip inaccessible dirs */ }
            }

            _pathCommandCache = [.. names];
            _pathCacheKey = pathVar;
            return _pathCommandCache;
        }
    }

    /// <summary>
    /// True when a command-position token is a path to an executable (contains a directory
    /// separator or starts with a <c>~</c> home reference) rather than a bare command name to
    /// resolve against <c>$PATH</c>. Mirrors bash: <c>./x</c>, <c>../x</c>, <c>/usr/bin/x</c>,
    /// <c>dir/x</c>, and <c>~/x</c> get filename completion; <c>foo</c> gets command completion.
    /// </summary>
    private static bool LooksLikeCommandPath(string token)
        => token.Length > 0
           && (token.Contains('/') || token.Contains('\\') || token[0] == '~');

    // ─────────────────────────────────────────────────────────────────────────
    // Flag completion (after command name)
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<CompletionItem> CompleteFlags(string command, string partial)
    {
        var flags = FlagSpecs.GetFlags(command);
        if (flags is null)
            return [];

        var results = new List<CompletionItem>();
        foreach (var spec in flags)
        {
            if (spec.Flag.StartsWith(partial, StringComparison.Ordinal))
            {
                // Insert only the flag ("-name"); LIST it with its arg + description
                // ("-name PATTERN  - match base name..."). Only the flag is ever inserted —
                // the arg placeholder and description are display-only (the CompletionItem split).
                var label = spec.Arg is { Length: > 0 }
                    ? $"{spec.Flag} {spec.Arg}  - {spec.Desc}"
                    : $"{spec.Flag}  - {spec.Desc}";
                results.Add(CompletionItem.Labeled(spec.Flag, label));
            }
        }
        return results;
    }

    internal static bool TryGetGrepPatternValueContext(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases,
        out string mode)
    {
        mode = "basic";
        if (IsFirstWord(line, cursor))
            return false;

        var before = cursor <= line.Length ? line[..cursor] : line;
        var (_, token) = SplitAtWordBoundaryQuoteAware(line, cursor);
        var tokens = Tokenize(before);
        if (tokens.Count < 2)
            return false;

        if (token.Length > 0 && string.Equals(tokens[^1], token, StringComparison.Ordinal))
            tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count < 2)
            return false;

        var command = ResolveCommandToken(tokens[0], aliases);
        if (!string.Equals(command, "grep", StringComparison.Ordinal))
            return false;

        var previous = tokens[^1];
        if (previous is not ("-e" or "--regexp"))
            return false;

        mode = tokens.Any(t => t == "-F" || t == "--fixed-strings")
            ? "fixed"
            : tokens.Any(t => t == "-E" || t == "--extended-regexp")
                ? "extended"
                : "basic";
        return true;
    }

    private static bool TryCompleteGrepPatternValue(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases,
        string token,
        string cwd,
        out IReadOnlyList<CompletionItem> completions)
    {
        completions = Array.Empty<CompletionItem>();
        if (!TryGetGrepPatternValueContext(line, cursor, aliases, out var mode))
            return false;

        var snippets = mode switch
        {
            "fixed" => GrepFixedPatternSnippets,
            "extended" => GrepExtendedRegexSnippets,
            _ => GrepBasicRegexSnippets,
        };

        var matches = snippets
            .Where(s => token.Length == 0 || s.InsertText.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            return false;

        completions = CompletionMerge.Append(matches, CompletePath(token, cwd), sortSecondary: false);
        return true;
    }

    /// <summary>
    /// The flag specs (flag + description) matching the flag-prefix token under the cursor for the
    /// command at the cursor — the data behind the interactive floating flag-doc panel. Returns
    /// empty unless the cursor is on an argument token that starts with <c>-</c> (a lone <c>-</c>
    /// matches every flag) for a command that has flag specs. Pure/synchronous — no runspace.
    /// </summary>
    internal static IReadOnlyList<FlagSpec> MatchingFlagSpecs(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (IsFirstWord(line, cursor))
            return [];

        var (_, token) = SplitAtWordBoundaryQuoteAware(line, cursor);
        if (token.Length == 0 || token[0] != '-')
            return [];

        // A redirect target that happens to start with '-' is a path, not a flag.
        if (IsAfterRedirectOp(line, cursor))
            return [];

        var command = GetCommandNameAtCursor(line, cursor, aliases);
        if (command is null)
            return [];

        var flags = FlagSpecs.GetFlags(command);
        if (flags is null)
            return [];

        var matches = new List<FlagSpec>();
        foreach (var spec in flags)
        {
            if (spec.Flag.StartsWith(token, StringComparison.Ordinal))
                matches.Add(spec);
        }
        return matches;
    }

    /// <summary>
    /// Every flag spec for the command at the cursor, unfiltered by the current token — the data
    /// for the F1 man-page browser even when the cursor is not on a flag (e.g. <c>find ⎵</c>).
    /// Empty at the command position or for a command with no flag specs.
    /// </summary>
    internal static IReadOnlyList<FlagSpec> AllFlagSpecsForCommand(
        string line,
        int cursor,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (IsFirstWord(line, cursor))
            return [];
        var command = GetCommandNameAtCursor(line, cursor, aliases);
        if (command is null)
            return [];
        return FlagSpecs.GetFlags(command) ?? [];
    }

    internal static string? GetCommandNameAtCursor(
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

    private static string ResolveCommandToken(string command, IReadOnlyDictionary<string, string> aliases)
    {
        if (!aliases.TryGetValue(command, out var aliasValue))
            return command;
        var spaceIdx = aliasValue.IndexOf(' ');
        return spaceIdx >= 0 ? aliasValue[..spaceIdx] : aliasValue;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Path completion (arguments)
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<CompletionItem> CompletePath(string token, string cwd)
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
                return [];

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
            // Paths are plain candidates: the inserted text and the list label are identical.
            return [.. results.Select(r => new CompletionItem(r))];
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

    internal static bool IsFirstWord(string line, int cursor)
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

    // Warm-up fallback for the type-ahead command panel: common PowerShell cmdlets shown while the
    // runspace is still starting and CommandNameCache has no live snapshot yet. Once the worker is
    // ready the panel uses the dynamic snapshot (loaded modules + session-defined functions/aliases)
    // passed to MatchingCommandNames; this static set is only the pre-warmup placeholder. Kept small
    // on purpose — the full, accurate set arrives from the background cache within a moment.
    private static readonly string[] KnownPowerShellCommands =
    [
        "Add-Content", "Clear-Content", "Clear-Host", "Compare-Object",
        "ConvertFrom-Csv", "ConvertFrom-Json", "ConvertTo-Csv", "ConvertTo-Json",
        "Copy-Item", "ForEach-Object", "Format-List", "Format-Table",
        "Get-ChildItem", "Get-Command", "Get-Content", "Get-Date", "Get-Help",
        "Get-Item", "Get-ItemProperty", "Get-Location", "Get-Member",
        "Get-Process", "Get-Service", "Group-Object", "Import-Csv", "Import-Module",
        "Invoke-Expression", "Invoke-RestMethod", "Invoke-WebRequest", "Join-Path",
        "Measure-Object", "Move-Item", "New-Item", "Out-File", "Out-Host",
        "Out-Null", "Out-String", "Remove-Item", "Rename-Item", "Resolve-Path",
        "Select-Object", "Select-String", "Set-Content", "Set-Item",
        "Set-Location", "Sort-Object", "Split-Path", "Start-Process", "Stop-Process",
        "Tee-Object", "Test-Connection", "Test-Path", "Where-Object", "Write-Error",
        "Write-Host", "Write-Output", "Write-Warning",
    ];

    /// <summary>
    /// True when <paramref name="name"/> is one of the curated <see cref="KnownPowerShellCommands"/>
    /// (case-insensitive, matching PowerShell's command resolution). Lets the type-ahead panel mark a
    /// PowerShell command distinctly from a bash builtin or alias.
    /// </summary>
    internal static bool IsKnownPowerShellCommand(string name)
    {
        foreach (var ps in KnownPowerShellCommands)
            if (string.Equals(ps, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly CompletionItem[] GrepBasicRegexSnippets =
    [
        CompletionItem.Labeled("'^TODO'", "^TODO  - line starts with TODO"),
        CompletionItem.Labeled("'FIXME'", "FIXME  - literal word in basic regex"),
        CompletionItem.Labeled("'[0-9][0-9]*'", "[0-9][0-9]*  - one or more digits"),
        CompletionItem.Labeled("'\\.log$'", "\\.log$  - line ends with .log"),
    ];

    private static readonly CompletionItem[] GrepExtendedRegexSnippets =
    [
        CompletionItem.Labeled("'TODO|FIXME'", "TODO|FIXME  - either word"),
        CompletionItem.Labeled("'error|warning'", "error|warning  - either log level"),
        CompletionItem.Labeled("'^[A-Z_]+='", "^[A-Z_]+=  - env-style assignment"),
        CompletionItem.Labeled("'\\.(cs|ps1)$'", "\\.(cs|ps1)$  - file extension"),
    ];

    private static readonly CompletionItem[] GrepFixedPatternSnippets =
    [
        CompletionItem.Labeled("TODO", "TODO  - literal text"),
        CompletionItem.Labeled("FIXME", "FIXME  - literal text"),
        CompletionItem.Labeled("error|warning", "error|warning  - literal pipe text"),
        CompletionItem.Labeled("[brackets]", "[brackets]  - literal brackets"),
    ];
}
