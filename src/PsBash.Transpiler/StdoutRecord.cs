namespace PsBash.Core;

/// <summary>
/// THE bytes of one stdout pipeline record — the single rule every place a record becomes bytes or lines shares:
/// the host renderer (SdkWorker), a background job's output (BashJobOutput), the fused lane's batch renderer,
/// <c>&gt; file</c> / <c>tee</c>, <c>$(…)</c> (ConvertTo-BashCapture), <c>source &lt;(…)</c>, a child's stdin, and every
/// line reader (<c>BashRuntime.SplitRecord</c> / <c>LineAssembler</c> / <c>RecordStreamText</c>). SMA-free on purpose: the
/// host (via Core) and the cmdlets both reference this assembly; <c>BashRuntime.RecordBytes</c> is the record-level
/// wrapper the cmdlets call.
/// <para>
/// A plain record is a LINE: its text plus the newline the record boundary stands for — ALWAYS, even when the text
/// itself already ends in <c>\n</c> (<c>jq -r</c> of <c>"a\n"</c>, <c>basename $'a\n'</c> emit the plain record
/// <c>"a\n"</c>, which is <c>a\n\n</c> in bash). A record marked <see cref="ExactBytesProperty"/> (<c>printf</c>,
/// <c>echo -n</c>, an unterminated piece) is exactly its text, no boundary added. REGRESSION: the renderer appended
/// the boundary always while the readers skipped it for a plain text ending in <c>\n</c>, so <c>jq -r . | wc -l</c> was
/// 1 where the screen showed (and bash counts) 2 (spec <c>runtime-functions.md</c> §Partial lines).
/// </para>
/// </summary>
public static class StdoutRecord
{
    /// <summary>The note property that marks a record as carrying its exact bytes (no record boundary appended).</summary>
    public const string ExactBytesProperty = "NoTrailingNewline";

    /// <summary>The bytes of a record whose text is <paramref name="text"/>: the text verbatim when it is
    /// <paramref name="exact"/>, otherwise the text plus the line boundary <paramref name="eol"/>.</summary>
    public static string Bytes(string text, bool exact, string eol = "\n") => exact ? text : text + eol;
}
