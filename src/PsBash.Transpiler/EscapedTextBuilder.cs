using System.Buffers;
using System.Text;

namespace PsBash.Core;

/// <summary>
/// Builds the text of a backslash-escape expansion (<c>printf</c>, <c>echo -e</c>, <c>$'...'</c>) in which
/// <c>\xHH</c> / <c>\NNN</c> name BYTES, as bash does. Consecutive bytes &gt;= 0x80 are collected and, at
/// the next non-byte append, UTF-8 decoded: <c>\xe2\x82\xac</c> / <c>\342\202\254</c> become the single
/// character U+20AC, which every ps-bash output boundary (stdout frames, <c>&gt;</c>, <c>tee</c>) then
/// encodes back to exactly E2 82 AC — the bytes bash writes. A byte run that is NOT valid UTF-8 (a lone
/// <c>\xe9</c>, an overlong <c>\xc0\x80</c>, a truncated <c>\xe2\x82</c>) becomes one escaped-byte marker
/// per byte (<see cref="RawBytes"/>, U+DC80..U+DCFF), which every boundary writes back as the single
/// original byte (see docs/specs/runtime-functions.md "Raw bytes").
/// </summary>
public sealed class EscapedTextBuilder
{
    private readonly StringBuilder _sb;
    private List<byte>? _raw;

    public EscapedTextBuilder(int capacity) => _sb = new StringBuilder(capacity);

    public EscapedTextBuilder Append(char c)
    {
        Flush();
        _sb.Append(c);
        return this;
    }

    public EscapedTextBuilder Append(string s)
    {
        Flush();
        _sb.Append(s);
        return this;
    }

    /// <summary>A byte an escape named (<c>\xHH</c>, <c>\NNN</c>); ASCII goes straight in, the rest waits to be decoded.</summary>
    public EscapedTextBuilder AppendByte(int value)
    {
        value &= 0xFF;
        if (value < 0x80) return Append((char)value);
        (_raw ??= new List<byte>()).Add((byte)value);
        return this;
    }

    private void Flush()
    {
        if (_raw is not { Count: > 0 }) return;
        // Valid UTF-8 runs become their characters; every byte of an invalid run becomes an
        // escaped-byte marker (RawBytes), which each output boundary writes back as the single byte.
        _sb.Append(RawBytes.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_raw)));
        _raw.Clear();
    }

    public override string ToString()
    {
        Flush();
        return _sb.ToString();
    }
}
