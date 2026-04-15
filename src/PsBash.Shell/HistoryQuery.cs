namespace PsBash.Shell;

/// <summary>
/// Query parameters for searching history entries.
/// All properties are optional; unspecified filters are not applied.
/// </summary>
public sealed record HistoryQuery
{
    /// <summary>
    /// Prefix text filter for commands.
    /// When set, only commands starting with this text are returned (case-sensitive).
    /// Null or empty = no prefix filtering.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Working directory filter.
    /// When set, only commands executed in this directory are returned.
    /// Null = search all directories.
    /// </summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// Session ID filter.
    /// When set, only commands from this session are returned.
    /// Null = search all sessions.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Maximum number of entries to return.
    /// Prevents unbounded queries. Default: 100.
    /// </summary>
    public int Limit { get; init; } = 100;

    /// <summary>
    /// When true, returns results in ascending order (oldest first).
    /// When false (default), returns results in descending order (newest first).
    /// </summary>
    public bool Reverse { get; init; }

    /// <summary>
    /// Exit code filter.
    /// When set, only commands with this exit code are returned.
    /// Null = no exit code filtering.
    /// Common usage: 0 for successful commands, non-zero for failures.
    /// </summary>
    public int? ExitCode { get; init; }
}
