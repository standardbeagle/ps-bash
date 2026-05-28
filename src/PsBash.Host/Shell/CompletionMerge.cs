namespace PsBash.Host.Shell;

/// <summary>
/// The one dedup-merge for completion result lists. Keeps <paramref name="primary"/> first with
/// its order intact, then appends the items of <paramref name="secondary"/> whose
/// <see cref="CompletionItem.InsertText"/> is not already present. Replaces three near-identical
/// hand-rolled merges (the engine's command-name and parameter merges and the tab completer's
/// sequence merge) so the dedup rule lives in one place.
/// </summary>
/// <remarks>
/// Dedup and sort key is <see cref="CompletionItem.InsertText"/> (Ordinal): two candidates that
/// insert the same text are the same completion regardless of how they are labelled, and the
/// first (primary) one wins.
/// </remarks>
internal static class CompletionMerge
{
    public static IReadOnlyList<CompletionItem> Append(
        IReadOnlyList<CompletionItem> primary,
        IReadOnlyList<CompletionItem> secondary,
        bool sortSecondary)
    {
        if (secondary.Count == 0)
        {
            return primary;
        }

        var seen = new HashSet<string>(primary.Select(p => p.InsertText), StringComparer.Ordinal);
        var extra = new List<CompletionItem>();
        foreach (var item in secondary)
        {
            if (seen.Add(item.InsertText))
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
            extra.Sort((a, b) => string.CompareOrdinal(a.InsertText, b.InsertText));
        }

        if (primary.Count == 0)
        {
            return extra;
        }

        var merged = new List<CompletionItem>(primary.Count + extra.Count);
        merged.AddRange(primary);
        merged.AddRange(extra);
        return merged;
    }
}
