using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// The one NUL-record codec behind GNU's <c>-z</c> / <c>--zero-terminated</c> (sort, cut, uniq, paste,
/// join, comm, tac...). In that mode the RECORD terminator is <c>\0</c> instead of <c>\n</c>, on input
/// and on output; <c>\n</c> becomes an ordinary data character. Pipeline records are first rendered
/// to their exact byte stream (<see cref="BashRuntime.RecordStreamText"/>: BashText + the boundary
/// <c>\n</c> unless unterminated), then re-split on NUL — so <c>printf 'a\0b\0' | sort -z</c> works and a
/// typed multi-line record keeps its <c>\n</c>s. Files are read byte-exactly (no CRLF rewrite, no BOM
/// strip: a NUL-separated list is data, not text). Output is an exact-bytes record ending in NUL.
/// </summary>
internal static class NulRecords
{
    /// <summary>Split an exact byte-stream text on NUL; a final unterminated piece is a record, a trailing NUL adds none.</summary>
    internal static IEnumerable<string> Split(string stream)
    {
        int start = 0;
        for (int i = 0; i < stream.Length; i++)
        {
            if (stream[i] != '\0') continue;
            yield return stream.Substring(start, i - start);
            start = i + 1;
        }
        if (start < stream.Length) yield return stream.Substring(start);
    }

    /// <summary>The NUL-separated records of a pipeline's records (the whole stream is buffered: O(input)).</summary>
    internal static IEnumerable<string> FromPipeline(IEnumerable<object> items)
        => Split(BashRuntime.RecordStreamText(items));

    /// <summary>Stream a file's NUL-separated records byte-exactly. IO errors surface on enumeration.</summary>
    internal static IEnumerable<string> ReadFile(string path)
    {
        using var fs = BashFileSystem.OpenRead(path);
        using var reader = BashFileSystem.OpenRawReader(fs, leaveOpen: true);
        var sb = new StringBuilder(256);
        var buf = new char[16384];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buf[i] != '\0') continue;
                sb.Append(buf, start, i - start);
                yield return sb.ToString();
                sb.Clear();
                start = i + 1;
            }
            if (start < read) sb.Append(buf, start, read - start);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>One output record: the text plus a NUL, emitted exactly (no <c>\n</c> boundary).</summary>
    internal static object Record(string text) => BashRuntime.TextRecord(text + "\0", unterminated: true);

    /// <summary>Record terminator for a mode: <c>\0</c> under <c>-z</c>, else <c>\n</c>.</summary>
    internal static char Terminator(bool zero) => zero ? '\0' : '\n';
}
