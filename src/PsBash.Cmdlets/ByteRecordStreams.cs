using System.Text;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>Receives a run of bytes (valid only for the duration of the call).</summary>
internal delegate void ByteSink(ReadOnlySpan<byte> bytes);

/// <summary>
/// Pipeline records -> the byte stream they stand for, ONE RECORD AT A TIME. The incremental
/// counterpart of <c>RawBytes.GetBytes(BashRuntime.RecordStreamText(items))</c>: each record
/// contributes its BashText plus a boundary <c>\n</c> unless it is unterminated or already ends
/// in one, encoded through the stateful <see cref="RawBytes"/> encoder so the bytes are identical
/// to encoding the joined string (escaped-byte markers included), while nothing but the current
/// record is ever held. Byte consumers (gzip, base64, md5sum/sha*sum on stdin) feed their
/// hasher / compressor / encoder from <see cref="Encode"/> inside <c>ProcessRecord</c>.
/// </summary>
internal sealed class RecordByteEncoder
{
    private readonly Encoder _encoder = RawBytes.Encoding.GetEncoder();
    private byte[] _buffer = new byte[1024];

    /// <summary>Encodes one record's bytes (text + boundary newline) into <paramref name="sink"/>.</summary>
    public void Encode(object? item, ByteSink sink)
    {
        string text = BashRuntime.GetBashText(item);
        bool boundary = !text.EndsWith('\n') && !BashRuntime.IsUnterminated(item);
        Emit(text, flush: false, sink);
        if (boundary) Emit("\n", flush: false, sink);
    }

    /// <summary>Flushes the encoder (a trailing lone high surrogate); call once at end of input.</summary>
    public void Finish(ByteSink sink) => Emit(string.Empty, flush: true, sink);

    private void Emit(string text, bool flush, ByteSink sink)
    {
        int max = RawBytes.Encoding.GetMaxByteCount(text.Length);
        if (_buffer.Length < max) _buffer = new byte[Math.Max(max, _buffer.Length * 2)];
        int n = _encoder.GetBytes(text.AsSpan(), _buffer, flush);
        if (n > 0) sink(_buffer.AsSpan(0, n));
    }
}

/// <summary>
/// A byte stream -> pipeline records, emitted AS THE BYTES ARRIVE: each <c>\n</c> ends a record
/// (a plain terminated line); whatever follows the last newline is held until more bytes or
/// <see cref="Finish"/>, then emitted as a <c>NoTrailingNewline</c> record. The incremental
/// counterpart of <c>BashRuntime.EmitBashLines(RawBytes.GetString(allBytes))</c> — the same
/// records, but the full text is never materialised. A stateful <see cref="RawBytes"/> decoder
/// carries a multi-byte character (or an escaped-byte run) across chunk boundaries.
/// </summary>
internal sealed class ByteRecordEmitter
{
    private readonly Decoder _decoder = RawBytes.CreateDecoder();
    private readonly StringBuilder _line = new();
    private readonly Action<object> _emit;
    private char[] _chars = new char[8192];

    public ByteRecordEmitter(Action<object> emit) => _emit = emit;

    /// <summary>Records emitted so far (test seam).</summary>
    public int Emitted { get; private set; }

    /// <summary>Chars held for the unfinished last line (test seam: bounded by one line, not the stream).</summary>
    public int PendingChars => _line.Length;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        int need = bytes.Length + 8; // a carried partial character may add a few chars
        if (_chars.Length < need) _chars = new char[Math.Max(need, _chars.Length * 2)];
        int n = _decoder.GetChars(bytes, _chars, flush: false);
        Scan(_chars.AsSpan(0, n));
    }

    public void Finish()
    {
        int n = _decoder.GetChars(ReadOnlySpan<byte>.Empty, _chars, flush: true);
        Scan(_chars.AsSpan(0, n));
        if (_line.Length > 0) EmitLine(terminated: false);
    }

    private void Scan(ReadOnlySpan<char> chars)
    {
        while (chars.Length > 0)
        {
            int nl = chars.IndexOf('\n');
            if (nl < 0) { _line.Append(chars); return; }
            _line.Append(chars[..nl]);
            EmitLine(terminated: true);
            chars = chars[(nl + 1)..];
        }
    }

    private void EmitLine(bool terminated)
    {
        string line = _line.ToString();
        _line.Clear();
        Emitted++;
        _emit(BashRuntime.NewBashObject(line, "PsBash.TextOutput", noTrailingNewline: !terminated));
    }
}

/// <summary>A write-only <see cref="Stream"/> that feeds a <see cref="ByteRecordEmitter"/>: lets a
/// <see cref="System.IO.Compression.GZipStream"/> compress straight into pipeline records.</summary>
internal sealed class ByteRecordStream : Stream
{
    private readonly ByteRecordEmitter _emitter;
    public ByteRecordStream(ByteRecordEmitter emitter) => _emitter = emitter;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _emitter.Append(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) => _emitter.Append(buffer);
}

/// <summary>
/// Incremental base64 encoder: bytes in, wrapped lines out. A 0-2 byte carry joins a triple split
/// across chunks, so the output equals encoding the concatenated bytes. With a wrap column each
/// full line is emitted as it completes (a terminated record); with <c>-w 0</c> the single
/// unwrapped line is emitted at <see cref="Finish"/> as one <c>NoTrailingNewline</c> record (it is
/// one line to every downstream consumer, so it cannot be split). Retained: the carry plus the
/// current line (plus the whole output only for <c>-w 0</c>).
/// </summary>
internal sealed class Base64LineEncoder
{
    private readonly int _wrap;
    private readonly Action<object> _emit;
    private readonly byte[] _carry = new byte[3];
    private int _carryLen;
    private readonly StringBuilder _line = new();
    private readonly char[] _chars = new char[4 * 3072];

    public Base64LineEncoder(int wrapColumn, Action<object> emit)
    {
        _wrap = wrapColumn;
        _emit = emit;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_carryLen > 0)
        {
            while (_carryLen < 3 && data.Length > 0) { _carry[_carryLen++] = data[0]; data = data[1..]; }
            if (_carryLen < 3) return;
            EncodeTriples(_carry);
            _carryLen = 0;
        }
        int full = data.Length / 3 * 3;
        if (full > 0) EncodeTriples(data[..full]);
        var rest = data[full..];
        rest.CopyTo(_carry);
        _carryLen = rest.Length;
    }

    public void Finish()
    {
        if (_carryLen > 0)
        {
            Convert.TryToBase64Chars(_carry.AsSpan(0, _carryLen), _chars, out int n);
            Push(_chars.AsSpan(0, n));
            _carryLen = 0;
        }
        if (_line.Length == 0) return;
        _emit(BashRuntime.TextRecord(_line.ToString(), unterminated: _wrap <= 0));
        _line.Clear();
    }

    private void EncodeTriples(ReadOnlySpan<byte> triples)
    {
        const int Piece = 3 * 3072;
        while (triples.Length > 0)
        {
            var piece = triples.Length > Piece ? triples[..Piece] : triples;
            Convert.TryToBase64Chars(piece, _chars, out int n);
            Push(_chars.AsSpan(0, n));
            triples = triples[piece.Length..];
        }
    }

    private void Push(ReadOnlySpan<char> chars)
    {
        if (_wrap <= 0) { _line.Append(chars); return; }
        while (chars.Length > 0)
        {
            int take = Math.Min(_wrap - _line.Length, chars.Length);
            _line.Append(chars[..take]);
            chars = chars[take..];
            if (_line.Length == _wrap)
            {
                _emit(BashRuntime.TextRecord(_line.ToString(), unterminated: false));
                _line.Clear();
            }
        }
    }
}

/// <summary>
/// Incremental base64 decoder for stdin: characters in, decoded bytes out, one quartet at a time
/// (<see cref="Convert.FromBase64CharArray"/> per group, so the error rules are the framework's).
/// Reproduces <c>Convert.FromBase64String(text.Trim())</c>: leading Unicode whitespace is
/// dropped, trailing whitespace is dropped, whitespace BETWEEN data may only be space / tab / CR /
/// LF (anything else is invalid), data after a padded quartet is invalid, and a final group of
/// 1-3 characters is invalid. With <c>-i</c> (ignore garbage) every non-alphabet character —
/// whitespace included — is skipped instead. A <see cref="FormatException"/> is thrown at the
/// offending character, so bytes decoded before it have already been delivered (GNU does the same).
/// </summary>
internal sealed class Base64StreamDecoder
{
    private readonly bool _ignoreGarbage;
    private readonly ByteSink _sink;
    private readonly char[] _quartet = new char[4];
    private int _quartetLen;
    private bool _seenData, _padded, _pendingBadWhitespace;

    private const string InvalidMessage =
        "The input is not a valid Base-64 string as it contains a non-base 64 character, more than two padding characters, or an illegal character among the padding characters.";

    public Base64StreamDecoder(bool ignoreGarbage, ByteSink sink)
    {
        _ignoreGarbage = ignoreGarbage;
        _sink = sink;
    }

    public void Append(ReadOnlySpan<char> chars)
    {
        foreach (char ch in chars)
        {
            if (_ignoreGarbage)
            {
                if (!IsAlphabet(ch)) continue;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (_seenData && ch is not (' ' or '\t' or '\r' or '\n')) _pendingBadWhitespace = true;
                continue;
            }

            if (_pendingBadWhitespace) throw new FormatException(InvalidMessage);
            _seenData = true;
            if (_padded) throw new FormatException(InvalidMessage);

            _quartet[_quartetLen++] = ch;
            if (_quartetLen == 4) Flush(padded: true);
        }
    }

    public void Finish()
    {
        if (_quartetLen > 0) Flush(padded: false);
    }

    private void Flush(bool padded)
    {
        byte[] bytes = Convert.FromBase64CharArray(_quartet, 0, _quartetLen);
        if (padded && Array.IndexOf(_quartet, '=', 0, 4) >= 0) _padded = true;
        _quartetLen = 0;
        _sink(bytes);
    }

    private static bool IsAlphabet(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')
           || (ch >= '0' && ch <= '9') || ch == '+' || ch == '/' || ch == '=';
}
