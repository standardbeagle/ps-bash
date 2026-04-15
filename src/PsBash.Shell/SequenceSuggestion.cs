namespace PsBash.Shell;

/// <summary>
/// A command suggestion based on sequence pattern analysis.
/// Produced by analyzing command pair frequencies in history.
/// </summary>
public sealed record SequenceSuggestion
{
    /// <summary>
    /// The suggested command text.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Relevance score (0.0 to 1.0).
    /// Higher scores indicate stronger sequence correlation.
    /// Scoring factors:
    /// - Frequency: How often this command follows the previous command
    /// - Recency: How recently this sequence was used
    /// - CWD match: Whether the sequence occurred in the current directory
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Human-readable explanation for why this was suggested.
    /// Examples:
    /// - "Followed 'git commit' 12 times in this directory"
    /// - "Common sequence after 'docker build'"
    /// </summary>
    public required string Reason { get; init; }
}
