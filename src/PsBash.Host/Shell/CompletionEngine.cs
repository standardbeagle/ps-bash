using PsBash.Core.Runtime;
using PsBash.Host.Runtime;

namespace PsBash.Host.Shell;

/// <summary>
/// Async, cancellable completion seam for the interactive shell. Composes the static
/// <see cref="TabCompleter"/> base set (commands / flags / paths / history) with live,
/// runspace-backed providers — command names from the live runspace today, PowerShell
/// parameter understanding in later phases.
/// </summary>
/// <remarks>
/// Every runspace round-trip is bounded by the caller's <see cref="CancellationToken"/>
/// (the line editor passes a short deadline), so a slow or busy worker can never hang the
/// prompt: on timeout, cancellation, or any failure the static base set is returned. The
/// engine is the single place new providers are added; the line editor stays oblivious to
/// where candidates come from.
/// </remarks>
internal sealed class CompletionEngine
{
    private readonly IReadOnlyDictionary<string, string> _aliases;
    private readonly Func<string> _cwd;
    private readonly Func<string?> _lastCommand;
    private readonly IHistoryStore? _history;
    private readonly IFrecencyStore? _frecency;
    private readonly Func<IWorker?> _getWorker;

    // Commands whose directory ARGUMENT is completed from the frecency DB.
    private static readonly HashSet<string> FrecencyDirCommands = new(StringComparer.Ordinal) { "cd", "z", "zi" };

    // Late-bound worker: null while the runspace is still warming up (startup type-ahead), then the
    // live worker once ready. Every live-completion path already guards on a null/exited worker and
    // falls back to the static base set, so completion degrades gracefully during warmup.
    private IWorker? Worker => _getWorker();

    public CompletionEngine(
        IReadOnlyDictionary<string, string> aliases,
        Func<string> cwd,
        Func<string?> lastCommand,
        IHistoryStore? history,
        IWorker? worker,
        IFrecencyStore? frecency = null)
        : this(aliases, cwd, lastCommand, history, () => worker, frecency)
    {
    }

    public CompletionEngine(
        IReadOnlyDictionary<string, string> aliases,
        Func<string> cwd,
        Func<string?> lastCommand,
        IHistoryStore? history,
        Func<IWorker?> worker,
        IFrecencyStore? frecency = null)
    {
        _aliases = aliases;
        _cwd = cwd;
        _lastCommand = lastCommand;
        _history = history;
        _getWorker = worker;
        _frecency = frecency;
    }

    /// <summary>
    /// Compute completion candidates for the token at <paramref name="cursor"/> in
    /// <paramref name="line"/>. Never throws — completion is advisory.
    /// </summary>
    public async Task<IReadOnlyList<CompletionItem>> CompleteAsync(string line, int cursor, CancellationToken ct)
    {
        // Base set: the existing static/local providers (command list, flag specs, path,
        // history/sequence). This always runs and is the fallback if a live query is slow.
        var baseResults = await TabCompleter.CompleteAsync(line, cursor, _aliases, _cwd(), _lastCommand(), _history)
            .ConfigureAwait(false);

        var (beforeToken, token) = TabCompleter.SplitAtWordBoundaryQuoteAware(line, cursor);

        // Phase 5: bash programmable completion. When completing an ARGUMENT of a command that has a
        // registered `complete` spec (Tier 1: a -W word list), offer those candidates first. This is
        // local registry state — no runspace round-trip — so it runs even when the worker is absent,
        // and a user `complete -W` overrides path/flag completion for that command.
        if (!TabCompleter.IsFirstWord(line, cursor))
        {
            var specCmd = TabCompleter.GetCommandNameAtCursor(beforeToken, beforeToken.Length, _aliases);
            if (specCmd is not null && BashCompletionRegistry.HasSpec(specCmd))
            {
                var spec = BashCompletionRegistry.GetCandidates(specCmd, token);
                if (spec.Count > 0)
                {
                    // Word-list entries are plain candidates (insert == label).
                    var specItems = spec.Select(s => new CompletionItem(s)).ToList();
                    return CompletionMerge.Append(specItems, baseResults, sortSecondary: false);
                }
            }
        }

        // Frecency directory completion for cd / z / zi arguments. Local (no
        // runspace), so it runs even during warmup. Offers the highest-frecency
        // directories whose final component matches the typed token — inserted as
        // full paths (z's path-passthrough then cd's straight there). Skipped when
        // the token already looks like a literal path (base path completion owns
        // that). Merged ahead of the base set.
        if (_frecency is not null && !TabCompleter.IsFirstWord(line, cursor) && !TokenLooksLikePath(token))
        {
            var argCmd = TabCompleter.GetCommandNameAtCursor(beforeToken, beforeToken.Length, _aliases);
            if (argCmd is not null && FrecencyDirCommands.Contains(argCmd))
            {
                var dirs = await QueryFrecencyDirsAsync(token).ConfigureAwait(false);
                if (dirs.Count > 0)
                    baseResults = CompletionMerge.Append(dirs, baseResults, sortSecondary: false);
            }
        }

        if (Worker is not { HasExited: false })
        {
            return baseResults;
        }

        // Phase 1: at the command position, merge command names that are actually resolvable in
        // the live runspace (auto-loaded cmdlets, dot-sourced functions, module commands, the
        // ps-bash aliases) — things the static KnownCommands list and $PATH scan cannot know.
        if (TabCompleter.IsFirstWord(line, cursor))
        {
            // Skip empty / flag tokens: Get-Command -Name '*' would enumerate the entire
            // session (slow), and flags are not command names.
            if (token.Length >= 1 && token[0] != '-')
            {
                var live = await QueryCommandNamesAsync(token, ct).ConfigureAwait(false);
                baseResults = CompletionMerge.Append(baseResults, AsItems(live), sortSecondary: true);
            }

            return baseResults;
        }

        // Phase 2: real parameter understanding for a PowerShell cmdlet/function under the cursor
        // (NOT a ps-bash-mapped bash command — those keep their bash flag specs). Pure
        // introspection on Get-Command.Parameters, so no bash->PS cursor mapping is needed.
        // Resolve the command from the text BEFORE the current token: GetCommandNameAtCursor
        // walks back skipping flags, so on a value token it would otherwise return the value.
        var cmd = TabCompleter.GetCommandNameAtCursor(beforeToken, beforeToken.Length, _aliases);
        if (cmd is not null && !IsBashCommand(cmd))
        {
            if (token.StartsWith('-'))
            {
                // Parameter NAME completion: -Pa<tab> -> -Path, -PathType, ...
                var names = await QueryParameterNameItemsAsync(cmd, token, ct).ConfigureAwait(false);
                baseResults = CompletionMerge.Append(names, baseResults, sortSecondary: false);
            }
            else
            {
                // Parameter VALUE completion for the preceding -Param. Prefer PowerShell's own
                // engine (P3) — dynamic Register-ArgumentCompleter, [ValidateSet], enums, provider
                // paths — then fall back to static ValidateSet/enum introspection (P2).
                var paramFlag = PreviousParamFlag(line, cursor);
                if (paramFlag is not null)
                {
                    var values = await CompleteValuesViaPsAsync(cmd, paramFlag, token, ct).ConfigureAwait(false);
                    if (values.Count == 0)
                    {
                        var valueItems = await QueryParameterValueItemsAsync(cmd, paramFlag, token, ct).ConfigureAwait(false);
                        baseResults = CompletionMerge.Append(valueItems, baseResults, sortSecondary: false);
                    }
                    else
                    {
                        baseResults = CompletionMerge.Append(AsItems(values), baseResults, sortSecondary: false);
                    }
                }
            }
        }

        return baseResults;
    }

    // Worker/introspection queries return raw strings; command names, parameter names, and
    // parameter values are all plain candidates (the inserted text is the list label too).
    private static IReadOnlyList<CompletionItem> AsItems(IReadOnlyList<string> texts)
        => texts.Count == 0 ? Array.Empty<CompletionItem>() : texts.Select(t => new CompletionItem(t)).ToList();

    // Frecency directory candidates for a cd/z/zi argument: the token is a single
    // keyword (empty → all tracked dirs, ranked); inserts the full directory path.
    private async Task<IReadOnlyList<CompletionItem>> QueryFrecencyDirsAsync(string token)
    {
        if (_frecency is null) return Array.Empty<CompletionItem>();
        var keywords = string.IsNullOrEmpty(token) ? Array.Empty<string>() : new[] { token };
        try
        {
            var matches = await _frecency.QueryAsync(keywords, limit: 10).ConfigureAwait(false);
            return matches.Count == 0
                ? Array.Empty<CompletionItem>()
                : matches.Select(m => new CompletionItem(m.Path)).ToList();
        }
        catch
        {
            // Advisory — never let a completion query surface an error.
            return Array.Empty<CompletionItem>();
        }
    }

    // A token already containing a separator / drive / ~ / . is a literal path the
    // user is typing; base path completion owns it, so frecency stays out of the way.
    private static bool TokenLooksLikePath(string token)
        => token.Length > 0
           && (token.Contains('/') || token.Contains('\\') || token[0] == '~'
               || token == "." || token == ".."
               || (token.Length >= 2 && token[1] == ':'));

    /// <summary>
    /// Floating-panel parameter hints for a PowerShell cmdlet under the cursor (the type-ahead
    /// doc panel, not Tab). Returns one <see cref="FlagHint"/> per parameter whose name starts with
    /// the typed <c>-</c>-prefixed token, carrying its type and any ValidateSet / enum value-set
    /// (e.g. <c>-CommonTCPPort &lt;String&gt;</c> → <c>HTTP, RDP, SMB, WINRM</c>). Returns empty for
    /// bash commands (their flags are shown synchronously from the flag specs), at the command
    /// position, on a non-flag token, or when no live worker is available. Never throws; bounded by
    /// <paramref name="ct"/> (the caller passes a short deadline so typing never blocks).
    /// </summary>
    public async Task<IReadOnlyList<FlagHint>> GetFlagHintsAsync(string line, int cursor, CancellationToken ct)
    {
        if (TabCompleter.IsFirstWord(line, cursor))
            return Array.Empty<FlagHint>();

        var (beforeToken, token) = TabCompleter.SplitAtWordBoundaryQuoteAware(line, cursor);
        if (token.Length == 0 || token[0] != '-')
            return Array.Empty<FlagHint>();

        var cmd = TabCompleter.GetCommandNameAtCursor(beforeToken, beforeToken.Length, _aliases);
        if (cmd is null || IsBashCommand(cmd))
            return Array.Empty<FlagHint>();

        if (Worker is not { HasExited: false })
            return Array.Empty<FlagHint>();

        var prefix = token.TrimStart('-');
        var cmdEsc = cmd.Replace("'", "''");
        var prefixEsc = prefix.Replace("'", "''");

        // For each parameter whose name starts with the prefix, emit "name|type|v1,v2,...".
        // Value-set is a [ValidateSet] if present, else the enum names for an enum-typed parameter.
        var expr =
            $"$c = Get-Command -Name '{cmdEsc}' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            "if ($c -and $c.Parameters) { $c.Parameters.GetEnumerator() | " +
            $"Where-Object {{ $_.Key -like '{prefixEsc}*' }} | Sort-Object Key | ForEach-Object {{ " +
            "$p = $_.Value; " +
            "$vs = ($p.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] } | Select-Object -First 1).ValidValues; " +
            "$vals = if ($vs) { $vs -join ',' } elseif ($p.ParameterType -and $p.ParameterType.IsEnum) { [System.Enum]::GetNames($p.ParameterType) -join ',' } else { '' }; " +
            "\"$($_.Key)|$($p.ParameterType.Name)|$vals\" } }";

        var lines = await QueryLinesAsync(expr, ct).ConfigureAwait(false);
        if (lines.Count == 0)
            return Array.Empty<FlagHint>();

        var hints = new List<FlagHint>(lines.Count);
        foreach (var raw in lines)
        {
            var parts = raw.Split('|');
            if (parts.Length < 1 || parts[0].Length == 0)
                continue;
            var name = parts[0];
            var type = parts.Length > 1 ? parts[1] : string.Empty;
            var vals = parts.Length > 2 ? parts[2] : string.Empty;

            var head = type.Length > 0 ? $"-{name} <{type}>" : $"-{name}";
            var desc = vals.Length > 0 ? vals.Replace(",", ", ") : string.Empty;
            hints.Add(new FlagHint($"-{name}", head, desc));
        }
        return hints;
    }

    private async Task<IReadOnlyList<string>> QueryCommandNamesAsync(string token, CancellationToken ct)
    {
        try
        {
            // Single-quote the prefix so wildcard/quote characters in the token can neither
            // break the query nor inject; Get-Command -Name '<prefix>*' is prefix-filtered.
            var escaped = token.Replace("'", "''");
            var expr =
                $"Get-Command -Name '{escaped}*' -All -ErrorAction SilentlyContinue " +
                "| Select-Object -ExpandProperty Name -Unique";

            var raw = await Worker!.QueryAsync(expr, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw))
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (var entry in raw.Split('\n'))
            {
                var name = entry.Trim('\r', ' ', '\t');
                // Re-filter by the typed prefix: -Name's wildcard is case-insensitive and can
                // match mid-string for some providers, so keep only true prefix matches.
                if (name.Length > 0 && name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<string>();
        }
        catch (Exception)
        {
            // Worker busy / unavailable — never let completion surface an error to the prompt.
            return Array.Empty<string>();
        }
    }

    // A command keeps its bash flag specs (handled by the static base set) when it is a known
    // bash command or a ps-bash alias; everything else is treated as a candidate PS command for
    // parameter introspection. A false positive is harmless: the worker query returns nothing
    // for a name that has no PS Parameters.
    private bool IsBashCommand(string cmd)
        => FlagSpecs.GetFlags(cmd) is not null || _aliases.ContainsKey(cmd);

    private async Task<IReadOnlyList<CompletionItem>> QueryParameterNameItemsAsync(string cmd, string token, CancellationToken ct)
    {
        var escaped = cmd.Replace("'", "''");
        var expr =
            $"$c = Get-Command -Name '{escaped}' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            "if ($c -and $c.Parameters) { $c.Parameters.GetEnumerator() | Sort-Object Key | ForEach-Object { " +
            "$p = $_.Value; " +
            "$aliases = if ($p.Aliases) { $p.Aliases -join ',' } else { '' }; " +
            "\"$($_.Key)|$($p.ParameterType.Name)|$aliases\" } }";

        var rows = await QueryLinesAsync(expr, ct).ConfigureAwait(false);
        var candidates = ParseParameterCandidates(rows);
        return BuildParameterNameItems(candidates, token);
    }

    internal static IReadOnlyList<CompletionItem> BuildParameterNameItems(
        IReadOnlyList<PowerShellParameterCandidate> candidates,
        string token)
    {
        if (candidates.Count == 0)
            return Array.Empty<CompletionItem>();

        var matches = candidates
            .Where(c => ParameterMatchesToken(c, token))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count == 0)
            return Array.Empty<CompletionItem>();

        var useCondensed = UseCondensedParameterInsertion();
        var items = new List<CompletionItem>(matches.Count);
        foreach (var candidate in matches)
        {
            var canonical = "-" + candidate.Name;
            var insert = useCondensed
                ? CondensedParameterInsert(candidate, candidates, token)
                : canonical;
            var display = string.IsNullOrWhiteSpace(candidate.TypeName)
                ? canonical
                : $"{canonical} <{candidate.TypeName}>";
            items.Add(CompletionItem.Labeled(insert, display));
        }
        return items;
    }

    private static IReadOnlyList<PowerShellParameterCandidate> ParseParameterCandidates(IReadOnlyList<string> rows)
    {
        var result = new List<PowerShellParameterCandidate>(rows.Count);
        foreach (var row in rows)
        {
            var parts = row.Split('|');
            var name = parts[0].Trim().TrimStart('-');
            if (name.Length == 0)
                continue;
            var type = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            var aliases = parts.Length > 2
                ? parts[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            result.Add(new PowerShellParameterCandidate(name, type, aliases));
        }
        return result;
    }

    private static bool ParameterMatchesToken(PowerShellParameterCandidate candidate, string token)
    {
        if (("-" + candidate.Name).StartsWith(token, StringComparison.OrdinalIgnoreCase))
            return true;
        return candidate.Aliases.Any(alias => ("-" + alias).StartsWith(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string CondensedParameterInsert(
        PowerShellParameterCandidate candidate,
        IReadOnlyList<PowerShellParameterCandidate> allCandidates,
        string token)
    {
        foreach (var alias in candidate.Aliases.OrderBy(a => a.Length).ThenBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            var aliasToken = "-" + alias;
            if (aliasToken.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                && IsSafeParameterToken(aliasToken, candidate, allCandidates))
            {
                return aliasToken;
            }
        }

        var typed = token.TrimStart('-');
        var min = Math.Clamp(typed.Length, 1, candidate.Name.Length);
        for (var length = min; length <= candidate.Name.Length; length++)
        {
            var candidateToken = "-" + candidate.Name[..length];
            if (IsSafeParameterToken(candidateToken, candidate, allCandidates))
                return candidateToken;
        }
        return "-" + candidate.Name;
    }

    private static bool IsSafeParameterToken(
        string token,
        PowerShellParameterCandidate owner,
        IReadOnlyList<PowerShellParameterCandidate> allCandidates)
    {
        var ownerCanonical = "-" + owner.Name;
        foreach (var reserved in CommonParameterTokens)
        {
            if (!string.Equals(reserved, ownerCanonical, StringComparison.OrdinalIgnoreCase)
                && reserved.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var candidate in allCandidates)
        {
            var canonical = "-" + candidate.Name;
            if (!ReferenceEquals(candidate, owner)
                && canonical.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            foreach (var alias in candidate.Aliases)
            {
                var aliasToken = "-" + alias;
                if (!ReferenceEquals(candidate, owner)
                    && aliasToken.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool UseCondensedParameterInsertion()
    {
        var mode = Environment.GetEnvironmentVariable("PSBASH_PS_PARAMETER_INSERT");
        return !string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "canonical", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] CommonParameterTokens =
    [
        "-Debug",
        "-ErrorAction",
        "-ErrorVariable",
        "-InformationAction",
        "-InformationVariable",
        "-OutBuffer",
        "-OutVariable",
        "-PipelineVariable",
        "-ProgressAction",
        "-Verbose",
        "-WarningAction",
        "-WarningVariable",
        "-WhatIf",
        "-Confirm",
        "-db",
        "-ea",
        "-ev",
        "-infa",
        "-iv",
        "-ob",
        "-ov",
        "-pv",
        "-vb",
        "-wa",
        "-wv",
    ];

    /// <summary>
    /// PowerShell-engine value completion via a synthesized fragment "&lt;cmd&gt; &lt;-Param&gt;
    /// &lt;partial&gt;" with the caret pinned at the end — so there is no bash→PS cursor mapping.
    /// Returns the engine's completion texts (already filtered to the partial), or empty when the
    /// worker has no completion capability or the engine yields nothing.
    /// </summary>
    private async Task<IReadOnlyList<string>> CompleteValuesViaPsAsync(string cmd, string paramFlag, string token, CancellationToken ct)
    {
        if (Worker is not ICompletionWorker completer)
        {
            return Array.Empty<string>();
        }

        var fragment = $"{cmd} {paramFlag} {token}";
        return await completer.CompleteInputAsync(fragment, fragment.Length, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CompletionItem>> QueryParameterValueItemsAsync(string cmd, string paramFlag, string token, CancellationToken ct)
    {
        var cmdEsc = cmd.Replace("'", "''");
        var paramEsc = paramFlag.TrimStart('-').Replace("'", "''");
        if (paramEsc.Length == 0)
        {
            return Array.Empty<CompletionItem>();
        }

        // ValidateSet values first, else enum names for an enum-typed parameter.
        // Fields are joined with the ASCII unit separator (U+001F), not '|', so a
        // ValidateSet value or type name that itself contains '|' is not truncated
        // or mis-split by BuildParameterValueItems.
        var expr =
            $"$c = Get-Command -Name '{cmdEsc}' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            $"if ($c) {{ $p = $c.Parameters['{paramEsc}']; if ($p) {{ " +
            "$vs = $p.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] } | Select-Object -First 1; " +
            "if ($vs) { $vs.ValidValues | ForEach-Object { \"$($_)$([char]31)ValidateSet$([char]31)$($p.ParameterType.Name)\" } } " +
            "elseif ($p.ParameterType -and $p.ParameterType.IsEnum) { [System.Enum]::GetNames($p.ParameterType) | ForEach-Object { \"$($_)$([char]31)Enum$([char]31)$($p.ParameterType.Name)\" } } } }";

        var values = await QueryLinesAsync(expr, ct).ConfigureAwait(false);
        return BuildParameterValueItems(values, paramFlag, token);
    }

    // Field separator for the "<value><sep><source><sep><type>" rows produced by
    // QueryParameterValueItemsAsync. ASCII unit separator (U+001F) — chosen because
    // it cannot appear in a ValidateSet value or a .NET type name, so splitting is
    // unambiguous even when a value contains '|'.
    private const char ParameterValueFieldSeparator = '\u001f';

    internal static IReadOnlyList<CompletionItem> BuildParameterValueItems(
        IReadOnlyList<string> rows,
        string paramFlag,
        string token)
    {
        var items = new List<CompletionItem>();
        foreach (var row in rows)
        {
            var parts = row.Split(ParameterValueFieldSeparator);
            var value = parts[0].Trim();
            if (value.Length == 0 || !value.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                continue;

            var source = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            var type = parts.Length > 2 ? parts[2].Trim() : string.Empty;
            var detail = source.Length > 0
                ? $"{source} value for {paramFlag}{(type.Length > 0 ? " <" + type + ">" : "")}"
                : $"value for {paramFlag}";
            items.Add(CompletionItem.Labeled(value, $"{value}  - {detail}"));
        }
        return items;
    }

    /// <summary>Run a worker expression and return its non-empty output lines; never throws.</summary>
    private async Task<IReadOnlyList<string>> QueryLinesAsync(string expr, CancellationToken ct)
    {
        try
        {
            var raw = await Worker!.QueryAsync(expr, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw))
            {
                return Array.Empty<string>();
            }

            var lines = new List<string>();
            foreach (var entry in raw.Split('\n'))
            {
                var s = entry.Trim('\r', ' ', '\t');
                if (s.Length > 0)
                {
                    lines.Add(s);
                }
            }

            return lines;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The <c>-Flag</c> token immediately preceding the token under the cursor, or null.</summary>
    private static string? PreviousParamFlag(string line, int cursor)
    {
        var (before, _) = TabCompleter.SplitAtWordBoundaryQuoteAware(line, cursor);
        before = before.TrimEnd();
        if (before.Length == 0)
        {
            return null;
        }

        var lastSpace = before.LastIndexOfAny([' ', '\t']);
        var prev = lastSpace >= 0 ? before[(lastSpace + 1)..] : before;
        return prev.StartsWith('-') ? prev : null;
    }

}

internal sealed record PowerShellParameterCandidate(
    string Name,
    string TypeName,
    IReadOnlyList<string> Aliases);
