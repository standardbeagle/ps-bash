namespace PsBash.Cmdlets;

/// <summary>
/// The one place the fused lane reproduces a cmdlet's per-record expansion.
///
/// <para>Every pipeline-mode <c>Invoke-Bash*</c> starts its <c>ProcessRecord</c> the same
/// way: split the record into its lines with <see cref="BashRuntime.SplitRecord(object?, out bool)"/>
/// (the lines of the record's bytes — never trim every trailing <c>\n</c>). Fused producers yield ONE bare line per record, so the
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
    /// <summary>The lines of one fused record (a bare string = a plain record: its text plus the boundary
    /// newline), so every <c>\n</c> in it ends a line (<c>"a\n"</c> = <c>a</c>, <c>""</c>).</summary>
    internal static string[] SubLines(string item) => BashRuntime.SplitRecord(item, out _);
}
