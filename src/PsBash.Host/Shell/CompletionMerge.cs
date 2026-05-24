namespace PsBash.Host.Shell;

/// <summary>
/// The one dedup-merge for completion result lists. Keeps <paramref name="primary"/> first with
/// its order intact, then appends the items of <paramref name="secondary"/> that are not already
/// present. Replaces three near-identical hand-rolled merges (the engine's command-name and
/// parameter merges and the tab completer's sequence merge) so the dedup rule lives in one place.
/// </summary>
internal static class CompletionMerge
{
    public static IReadOnlyList<string> Append(
        IReadOnlyList<string> primary,
        IReadOnlyList<string> secondary,
        bool sortSecondary)
    {
        if (secondary.Count == 0)
        {
            return primary;
        }

        var seen = new HashSet<string>(primary, StringComparer.Ordinal);
        var extra = new List<string>();
        foreach (var item in secondary)
        {
            if (seen.Add(item))
            {
                extra.Add(item);
            }
        }

        if (extra.Count == 0)
        {
            return primary;
        }

        if (sortSecondary)
        {
            extra.Sort(StringComparer.Ordinal);
        }

        if (primary.Count == 0)
        {
            return extra;
        }

        var merged = new List<string>(primary.Count + extra.Count);
        merged.AddRange(primary);
        merged.AddRange(extra);
        return merged;
    }
}
