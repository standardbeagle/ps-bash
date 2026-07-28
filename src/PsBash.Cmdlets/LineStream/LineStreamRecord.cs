namespace PsBash.Cmdlets;

/// <summary>
/// The one place the fused lane reproduces a cmdlet's per-record expansion.
///
/// <para>Every pipeline-mode <c>Invoke-Bash*</c> starts its <c>ProcessRecord</c> the same
/// way: trim the record's trailing <c>\n</c>, and if what remains still contains a <c>\n</c>,
/// treat the record as several lines. Fused producers yield ONE bare line per record, so the
/// split is a no-op in practice — but reproducing it keeps the two lanes identical for any
/// producer that ever hands over a multi-line record, and doing it once means the four S3
/// cores cannot drift from each other.</para>
///
/// <para><b>Not usable by every stage.</b> <c>tail</c> deliberately does NOT go through this
/// helper: its cmdlet emits the ORIGINAL object for a single-line record (not the trimmed
/// text), so it does its own expansion — see <see cref="TailStage"/>. And <c>tr</c> must not
/// expand at all: to <c>tr</c> the newline is an ordinary translatable character, not a
/// record boundary.</para>
/// </summary>
internal static class LineStreamRecord
{
    /// <summary>Trailing <c>\n</c> trimmed; an embedded <c>\n</c> splits the record.</summary>
    internal static IEnumerable<string> SubLines(string item)
    {
        string trimmed = item.TrimEnd('\n');
        if (trimmed.Contains('\n'))
        {
            foreach (var subLine in trimmed.Split('\n')) yield return subLine;
            yield break;
        }
        yield return trimmed;
    }
}
