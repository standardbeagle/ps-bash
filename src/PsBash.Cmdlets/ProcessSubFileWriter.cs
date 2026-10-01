using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The temp-file sink behind <c>&lt;(producer)</c> (psm1 <c>Invoke-ProcessSub</c>): the producer's records are
/// written to the file AS THEY ARE PRODUCED, through a 64 KB buffered stream and the stateful
/// <see cref="RawBytes"/> encoder, so the whole output is never held in a collection, a
/// <c>StringBuilder</c> and a string before it reaches the disk (the old path kept all three).
/// A record is its BashText plus a boundary <c>\n</c> unless it is unterminated or already ends in one
/// (<see cref="RecordByteEncoder"/>), so the bytes — including a missing final newline — are exactly what
/// the joined form wrote. The file exists (empty) from <see cref="ProcessSubFileWriter(string)"/> on, so an
/// empty producer still yields a readable empty file; the caller deletes it on a producer error.
/// </summary>
/// <remarks>
/// The consumer still starts only after the producer finished: handing a reader a live stream needs a FIFO
/// (POSIX) or a named pipe (Windows) plus early-exit and cleanup rules, which is deliberately out of scope
/// (see emitter-strategy.md, process substitution).
/// </remarks>
public sealed class ProcessSubFileWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly RecordByteEncoder _bytes = new();
    private readonly ByteSink _sink;
    private bool _disposed;

    /// <summary>Records written so far.</summary>
    public int Records { get; private set; }

    public ProcessSubFileWriter(string path)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, bufferSize: 64 * 1024);
        _sink = bytes => _file.Write(bytes);
    }

    /// <summary>Writes one producer record.</summary>
    public void Add(PSObject? item)
    {
        Records++;
        _bytes.Encode(item, _sink);
    }

    /// <summary>Pushes the file buffer to disk without closing (test seam: shows the bytes are already on disk).</summary>
    public void Flush() => _file.Flush();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _bytes.Finish(_sink); }
        finally { _file.Dispose(); }
    }
}
