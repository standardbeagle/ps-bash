namespace PsBash.Core.Runtime.Compaction;

/// <summary>
/// Entry point for compact-output reduction. Selects the first command-aware
/// <see cref="FilterSpec"/> that matches the command and runs its pipeline; with no
/// matching filter (or none supplied) it falls back to the generic
/// <see cref="OutputCompactor"/> digest — so unmatched commands behave exactly as
/// before this layer existed. Pure: filters are passed in, no I/O here.
/// </summary>
public static class FilterEngine
{
    private const int DefaultMaxLines = 120;
    private const string GitStageCommitPushRoute = "git.stage-commit-push.v1";

    private static readonly IReadOnlyList<FilterSpec> RouteFilters =
    [
        new()
        {
            Name = "git/stage-commit-push",
            Match = new FilterMatch { Command = "git", RouteKey = GitStageCommitPushRoute },
            OnSuccess = "changes staged, committed, and pushed"
            // On failure the unmodified body is retained; IpcWorker also writes its tee.
        }
    ];

    public static string Apply(
        string command,
        int exitCode,
        bool timedOut,
        IReadOnlyList<OutputFrame> frames,
        IReadOnlyList<FilterSpec>? filters = null,
        int maxLines = DefaultMaxLines,
        GenericFallback fallback = GenericFallback.None,
        string? routeKey = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(frames);

        var spec = SelectRouteFilter(routeKey)
            ?? (filters is null || filters.Count == 0 ? null : SelectFilter(command, filters));
        if (spec is not null)
            return FilterStage.Run(spec, command, exitCode, timedOut, frames, maxLines);

        // No command-aware filter matched. ErrorExtract trims a failing command's noise;
        // None keeps the byte-identical plain digest (default — P0 regression guard).
        return fallback == GenericFallback.ErrorExtract
            ? GenericFallbacks.Apply(command, exitCode, timedOut, frames, maxLines)
            : OutputCompactor.CompactCommandOutput(command, exitCode, timedOut, frames, maxLines);
    }

    private static FilterSpec? SelectRouteFilter(string? routeKey)
    {
        if (string.IsNullOrEmpty(routeKey)) return null;
        foreach (var filter in RouteFilters)
        {
            if (string.Equals(filter.Match.RouteKey, routeKey, StringComparison.Ordinal))
                return filter;
        }
        return null;
    }

    /// <summary>First filter whose <see cref="FilterMatch"/> matches the command, else null.</summary>
    internal static FilterSpec? SelectFilter(string command, IReadOnlyList<FilterSpec> filters)
    {
        var (cmd, argv) = SplitCommand(command);
        foreach (var filter in filters)
        {
            if (IsMatch(filter.Match, cmd, argv)) return filter;
        }
        return null;
    }

    /// <summary>Return the launch-time argv override for the first matching filter.</summary>
    public static IReadOnlyList<string>? SelectOverride(
        string command, IReadOnlyList<FilterSpec>? filters)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (filters is null || filters.Count == 0) return null;
        return SelectFilter(command, filters)?.Override;
    }

    /// <summary>
    /// Return an override only when the user's argv is exactly the filter's match prefix.
    /// Extra options, revisions, and pathspecs carry user semantics and must never be discarded.
    /// </summary>
    public static IReadOnlyList<string>? SelectLaunchOverride(
        string command, IReadOnlyList<FilterSpec>? filters)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (filters is null || filters.Count == 0) return null;

        var spec = SelectFilter(command, filters);
        if (spec?.Override is not { Count: > 0 } argv) return null;
        var (_, commandArgv) = SplitCommand(command);
        return commandArgv.Count == spec.Match.Args.Count ? argv : null;
    }

    /// <summary>
    /// Split a command label into name + argv on whitespace. The command name is matched
    /// on its leaf (last path segment) so <c>/usr/bin/git</c> matches a <c>git</c> filter.
    /// Quote-aware tokenizing is deferred to P1 — P0 routes via the simple split only.
    /// </summary>
    internal static (string Command, IReadOnlyList<string> Argv) SplitCommand(string command)
    {
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return (string.Empty, []);

        var name = tokens[0];
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0 && slash < name.Length - 1) name = name[(slash + 1)..];

        return (name, tokens[1..]);
    }

    internal static bool IsMatch(FilterMatch match, string command, IReadOnlyList<string> argv)
    {
        if (!string.Equals(match.Command, command, StringComparison.OrdinalIgnoreCase)) return false;
        for (var i = 0; i < match.Args.Count; i++)
        {
            if (i >= argv.Count || !string.Equals(match.Args[i], argv[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
