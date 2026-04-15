namespace PsBash.Shell;

/// <summary>
/// A single command execution record with full metadata.
/// </summary>
public sealed record HistoryEntry
{
    /// <summary>
    /// The command line as typed by the user (before transpilation).
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// The working directory where the command was executed.
    /// Used for CWD-aware history filtering and ranking.
    /// </summary>
    public required string Cwd { get; init; }

    /// <summary>
    /// The exit code from command execution.
    /// Null if the command is still running or exit code is unknown.
    /// 0 = success, non-zero = failure.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// UTC timestamp when the command was executed.
    /// Used for recency ranking and history navigation.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Command execution duration in milliseconds.
    /// Null if duration is unknown or command is still running.
    /// Used for performance analysis and long-command filtering.
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Unique identifier for the shell session.
    /// Used to group commands by session and track session-level patterns.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional database row ID (for stores that use auto-incrementing IDs).
    /// Null for in-memory or remote stores without integer IDs.
    /// </summary>
    public long? Id { get; init; }
}
