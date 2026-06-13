namespace PsBash.Host.Shell;

/// <summary>
/// A zoxide-style directory frecency database: tracks visited directories with a
/// frequency+recency ("frecency") score so the shell can jump to, complete, and
/// suggest the directories you actually use. The <c>z</c> / <c>zi</c> commands and
/// the cd-aware completion / ghost-text providers consume this.
/// </summary>
public interface IFrecencyStore
{
    /// <summary>
    /// Record a visit to <paramref name="path"/> (bumps its rank and recency).
    /// Best-effort: never throws; advisory like the history store.
    /// </summary>
    Task AddAsync(string path);

    /// <summary>
    /// Return existing directories matching <paramref name="keywords"/> (matched in
    /// order, the last keyword against the final path component — zoxide semantics),
    /// ranked best-frecency-first. Non-existent directories are skipped and pruned.
    /// An empty keyword list ranks all tracked directories.
    /// </summary>
    Task<IReadOnlyList<FrecencyMatch>> QueryAsync(IReadOnlyList<string> keywords, int limit = 50);
}

/// <summary>A scored directory match from <see cref="IFrecencyStore"/>.</summary>
public sealed record FrecencyMatch
{
    public required string Path { get; init; }
    public required double Score { get; init; }
}
