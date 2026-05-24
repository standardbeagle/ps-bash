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
    private readonly IWorker? _worker;

    public CompletionEngine(
        IReadOnlyDictionary<string, string> aliases,
        Func<string> cwd,
        Func<string?> lastCommand,
        IHistoryStore? history,
        IWorker? worker)
    {
        _aliases = aliases;
        _cwd = cwd;
        _lastCommand = lastCommand;
        _history = history;
        _worker = worker;
    }

    /// <summary>
    /// Compute completion candidates for the token at <paramref name="cursor"/> in
    /// <paramref name="line"/>. Never throws — completion is advisory.
    /// </summary>
    public async Task<IReadOnlyList<string>> CompleteAsync(string line, int cursor, CancellationToken ct)
    {
        // Base set: the existing static/local providers (command list, flag specs, path,
        // history/sequence). This always runs and is the fallback if a live query is slow.
        var baseResults = TabCompleter.Complete(line, cursor, _aliases, _cwd(), _lastCommand(), _history);

        if (_worker is not { HasExited: false })
        {
            return baseResults;
        }

        var (beforeToken, token) = TabCompleter.SplitAtWordBoundaryQuoteAware(line, cursor);

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
                baseResults = CompletionMerge.Append(baseResults, live, sortSecondary: true);
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
                var names = await QueryParameterNamesAsync(cmd, token, ct).ConfigureAwait(false);
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
                        values = await QueryParameterValuesAsync(cmd, paramFlag, token, ct).ConfigureAwait(false);
                    }

                    baseResults = CompletionMerge.Append(values, baseResults, sortSecondary: false);
                }
            }
        }

        return baseResults;
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

            var raw = await _worker!.QueryAsync(expr, ct).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<string>> QueryParameterNamesAsync(string cmd, string token, CancellationToken ct)
    {
        var escaped = cmd.Replace("'", "''");
        var expr =
            $"$c = Get-Command -Name '{escaped}' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            "if ($c -and $c.Parameters) { $c.Parameters.Keys | Sort-Object | ForEach-Object { '-' + $_ } }";

        var names = await QueryLinesAsync(expr, ct).ConfigureAwait(false);
        // token includes the leading '-' (e.g. "-Pa"); match parameter names case-insensitively.
        return names.Where(n => n.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// PowerShell-engine value completion via a synthesized fragment "&lt;cmd&gt; &lt;-Param&gt;
    /// &lt;partial&gt;" with the caret pinned at the end — so there is no bash→PS cursor mapping.
    /// Returns the engine's completion texts (already filtered to the partial), or empty when the
    /// worker has no completion capability or the engine yields nothing.
    /// </summary>
    private async Task<IReadOnlyList<string>> CompleteValuesViaPsAsync(string cmd, string paramFlag, string token, CancellationToken ct)
    {
        if (_worker is not ICompletionWorker completer)
        {
            return Array.Empty<string>();
        }

        var fragment = $"{cmd} {paramFlag} {token}";
        return await completer.CompleteInputAsync(fragment, fragment.Length, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> QueryParameterValuesAsync(string cmd, string paramFlag, string token, CancellationToken ct)
    {
        var cmdEsc = cmd.Replace("'", "''");
        var paramEsc = paramFlag.TrimStart('-').Replace("'", "''");
        if (paramEsc.Length == 0)
        {
            return Array.Empty<string>();
        }

        // ValidateSet values first, else enum names for an enum-typed parameter.
        var expr =
            $"$c = Get-Command -Name '{cmdEsc}' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            $"if ($c) {{ $p = $c.Parameters['{paramEsc}']; if ($p) {{ " +
            "$vs = $p.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] } | Select-Object -First 1; " +
            "if ($vs) { $vs.ValidValues } elseif ($p.ParameterType -and $p.ParameterType.IsEnum) { [System.Enum]::GetNames($p.ParameterType) } } }";

        var values = await QueryLinesAsync(expr, ct).ConfigureAwait(false);
        return values.Where(v => v.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Run a worker expression and return its non-empty output lines; never throws.</summary>
    private async Task<IReadOnlyList<string>> QueryLinesAsync(string expr, CancellationToken ct)
    {
        try
        {
            var raw = await _worker!.QueryAsync(expr, ct).ConfigureAwait(false);
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
