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

    public static string Apply(
        string command,
        int exitCode,
        bool timedOut,
        IReadOnlyList<OutputFrame> frames,
        IReadOnlyList<FilterSpec>? filters = null,
        int maxLines = DefaultMaxLines)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(frames);

        var spec = filters is null || filters.Count == 0 ? null : SelectFilter(command, filters);
        return spec is null
            ? OutputCompactor.CompactCommandOutput(command, exitCode, timedOut, frames, maxLines)
            : FilterStage.Run(spec, command, exitCode, timedOut, frames, maxLines);
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
