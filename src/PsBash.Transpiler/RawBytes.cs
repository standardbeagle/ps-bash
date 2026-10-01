using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace PsBash.Core;

/// <summary>
/// The ONE byte&lt;-&gt;string codec every ps-bash boundary uses: UTF-8 with escaped-byte markers
/// (Python <c>surrogateescape</c> / Cygwin style), so text that flows through the pipeline as .NET
/// strings can still carry arbitrary BYTES losslessly.
/// <list type="bullet">
/// <item><b>Decode</b> (bytes to string): valid UTF-8 decodes normally; every byte of an INVALID
/// UTF-8 sequence (a lone <c>E9</c>, an overlong <c>C0 80</c>, a truncated <c>E2 82</c>, a stray
/// continuation byte, <c>FF</c>) becomes one marker char <c>U+DC00 + byte</c>, i.e.
/// <c>U+DC80..U+DCFF</c>.</item>
/// <item><b>Encode</b> (string to bytes): a marker char becomes the single original byte; everything
/// else is encoded as UTF-8. Decode followed by encode is the identity on any byte sequence.</item>
/// </list>
/// <para>
/// Why a lone LOW SURROGATE: valid UTF-8 can never decode to a surrogate (the decoder refuses the
/// <c>ED A0..BF xx</c> range), so a marker can never be produced by valid input and there is no
/// ambiguity with real text — unlike a private-use-area marker (U+F780..), which a user may type. The
/// one residual case is a marker char that directly follows a HIGH surrogate, which is by definition
/// a (valid) surrogate pair — a supplementary-plane character — and encodes as such; a
/// lone high surrogate immediately followed by an escaped byte cannot be produced by any ps-bash
/// decoder, only by string surgery that splits a pair (<c>Substring</c> in the middle of an emoji).
/// </para>
/// <para>
/// The decoder is hand-written (not a <see cref="DecoderFallback"/>): .NET refuses to let a fallback
/// buffer return lone surrogates ("String contains invalid Unicode code points").
/// </para>
/// <para>
/// Lives in the leaf <c>PsBash.Transpiler</c> assembly because that is the only project every layer
/// (emitter, cmdlets, Core IPC, host, launcher) can reference.
/// </para>
/// </summary>
public static class RawBytes
{
    /// <summary>First marker char (the escape of byte 0x80).</summary>
    public const char FirstMarker = '\uDC80';

    /// <summary>Last marker char (the escape of byte 0xFF).</summary>
    public const char LastMarker = '\uDCFF';

    private static readonly UTF8Encoding Utf8Enc = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The marker-aware UTF-8 <see cref="System.Text.Encoding"/>; use for writers, readers and
    /// process-stream encodings (<see cref="System.Diagnostics.ProcessStartInfo.StandardOutputEncoding"/>).</summary>
    public static Encoding Encoding { get; } = new RawUtf8Encoding();

    /// <summary>True when <paramref name="c"/> is in the marker range (it is a marker only when not the
    /// low half of a surrogate pair — see <see cref="IsMarkerAt"/>).</summary>
    public static bool IsMarkerChar(char c) => c >= FirstMarker && c <= LastMarker;

    /// <summary>The marker char for byte <paramref name="b"/> (0x80..0xFF).</summary>
    public static char ToMarker(byte b)
    {
        if (b < 0x80) throw new ArgumentOutOfRangeException(nameof(b), "only bytes >= 0x80 are escaped");
        return (char)(0xDC00 + b);
    }

    /// <summary>The byte a marker char escapes.</summary>
    public static byte FromMarker(char marker)
    {
        if (!IsMarkerChar(marker)) throw new ArgumentOutOfRangeException(nameof(marker));
        return (byte)(marker - 0xDC00);
    }

    /// <summary>True when <c>s[i]</c> is a marker: in range AND not the low half of a surrogate pair.</summary>
    public static bool IsMarkerAt(ReadOnlySpan<char> s, int i)
        => IsMarkerChar(s[i]) && (i == 0 || !char.IsHighSurrogate(s[i - 1]));

    /// <summary>True when the text contains at least one escaped-byte marker.</summary>
    public static bool ContainsMarker(ReadOnlySpan<char> s)
    {
        int from = 0;
        while (true)
        {
            int rel = s.Slice(from).IndexOfAnyInRange(FirstMarker, LastMarker);
            if (rel < 0) return false;
            int i = from + rel;
            if (IsMarkerAt(s, i)) return true;
            from = i + 1;
        }
    }

    // -- Decode --------------------------------------------------------------------------------

    /// <summary>Decode <paramref name="bytes"/>: valid UTF-8 normally, each invalid byte as a marker.</summary>
    public static string GetString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        if (Utf8.IsValid(bytes)) return Utf8Enc.GetString(bytes);
        // One char per byte at most (a 2-4 byte sequence decodes to 1-2 chars).
        var rented = ArrayPool<char>.Shared.Rent(bytes.Length);
        try
        {
            int n = DecodeCore(bytes, rented, final: true, out _);
            return new string(rented, 0, n);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    public static string GetString(byte[] bytes) => GetString(bytes.AsSpan());

    public static string GetString(byte[] bytes, int index, int count) => GetString(bytes.AsSpan(index, count));

    /// <summary>A fresh stateful decoder (an incomplete trailing sequence waits for the next chunk).</summary>
    public static Decoder CreateDecoder() => new RawDecoder();

    /// <summary>
    /// Decode <paramref name="src"/> into <paramref name="dst"/>; <paramref name="consumed"/> is how many
    /// bytes were used (less than <c>src.Length</c> only when <paramref name="final"/> is false and the
    /// tail is an incomplete sequence the caller must carry into the next chunk).
    /// </summary>
    private static int DecodeCore(ReadOnlySpan<byte> src, Span<char> dst, bool final, out int consumed)
    {
        int read = 0, written = 0;
        while (true)
        {
            var status = Utf8.ToUtf16(src.Slice(read), dst.Slice(written), out int r, out int w,
                replaceInvalidSequences: false, isFinalBlock: final);
            read += r;
            written += w;
            if (status == OperationStatus.Done || status == OperationStatus.NeedMoreData) break;
            if (status == OperationStatus.DestinationTooSmall)
                throw new ArgumentException("The output buffer is too small.", nameof(dst));

            // InvalidData: escape the maximal invalid subsequence, byte by byte.
            Rune.DecodeFromUtf8(src.Slice(read), out _, out int bad);
            if (bad < 1) bad = 1;
            if (written + bad > dst.Length)
                throw new ArgumentException("The output buffer is too small.", nameof(dst));
            for (int k = 0; k < bad; k++)
            {
                byte b = src[read + k];
                dst[written++] = b >= 0x80 ? (char)(0xDC00 + b) : (char)b;
            }
            read += bad;
        }
        consumed = read;
        return written;
    }

    // -- Encode --------------------------------------------------------------------------------

    /// <summary>The exact bytes of <paramref name="s"/>: markers become single bytes, the rest UTF-8.</summary>
    public static byte[] GetBytes(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return GetBytes(s.AsSpan());
    }

    public static byte[] GetBytes(ReadOnlySpan<char> s)
    {
        int count = GetByteCount(s);
        var result = new byte[count];
        EncodeCore(s, result);
        return result;
    }

    /// <summary>Byte length of <paramref name="s"/> once encoded (what <c>wc -c</c> counts).</summary>
    public static int GetByteCount(ReadOnlySpan<char> s)
    {
        if (s.IndexOfAnyInRange(FirstMarker, LastMarker) < 0) return Utf8Enc.GetByteCount(s);
        return EncodeCore(s, default, countOnly: true);
    }

    public static int GetByteCount(string s) => GetByteCount(s.AsSpan());

    /// <summary>Encode into <paramref name="dest"/> (must be large enough); returns the byte count.</summary>
    public static int GetBytes(ReadOnlySpan<char> s, Span<byte> dest)
    {
        if (s.IndexOfAnyInRange(FirstMarker, LastMarker) < 0) return Utf8Enc.GetBytes(s, dest);
        return EncodeCore(s, dest);
    }

    private static int EncodeCore(ReadOnlySpan<char> s, Span<byte> dest, bool countOnly = false)
    {
        int written = 0;
        int segStart = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (!IsMarkerAt(s, i)) continue;
            written += EncodeSegment(s.Slice(segStart, i - segStart), dest, written, countOnly);
            if (!countOnly) dest[written] = (byte)(s[i] - 0xDC00);
            written++;
            segStart = i + 1;
        }
        written += EncodeSegment(s.Slice(segStart), dest, written, countOnly);
        return written;
    }

    private static int EncodeSegment(ReadOnlySpan<char> seg, Span<byte> dest, int at, bool countOnly)
    {
        if (seg.IsEmpty) return 0;
        return countOnly ? Utf8Enc.GetByteCount(seg) : Utf8Enc.GetBytes(seg, dest.Slice(at));
    }

    // -- The Encoding / Encoder / Decoder -----------------------------------------------------

    /// <summary>UTF-8 with escaped-byte markers, as a normal <see cref="System.Text.Encoding"/>.</summary>
    private sealed class RawUtf8Encoding : Encoding
    {
        // Code page 65001: the Windows console APIs (SetConsoleOutputCP) take the page number, and this
        // IS UTF-8 on the wire for every valid sequence.
        public RawUtf8Encoding() : base(65001) { }

        public override string WebName => "utf-8";
        public override string EncodingName => "Unicode (UTF-8, escaped bytes)";
        public override byte[] GetPreamble() => Array.Empty<byte>();
        public override ReadOnlySpan<byte> Preamble => default;

        public override int GetMaxByteCount(int charCount) => checked((charCount + 1) * 3);
        public override int GetMaxCharCount(int byteCount) => byteCount + 1;

        public override int GetByteCount(char[] chars, int index, int count)
            => RawBytes.GetByteCount(chars.AsSpan(index, count));

        public override int GetByteCount(string s) => RawBytes.GetByteCount(s.AsSpan());
        public override int GetByteCount(ReadOnlySpan<char> chars) => RawBytes.GetByteCount(chars);

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
            => RawBytes.GetBytes(chars.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex));

        public override int GetBytes(string s, int charIndex, int charCount, byte[] bytes, int byteIndex)
            => RawBytes.GetBytes(s.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex));

        public override int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes) => RawBytes.GetBytes(chars, bytes);

        public override int GetCharCount(byte[] bytes, int index, int count)
            => CountChars(bytes.AsSpan(index, count));

        public override int GetCharCount(ReadOnlySpan<byte> bytes) => CountChars(bytes);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            => DecodeCore(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex), final: true, out _);

        public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars)
            => DecodeCore(bytes, chars, final: true, out _);

        public override string GetString(byte[] bytes, int index, int count) => RawBytes.GetString(bytes, index, count);

        public override Decoder GetDecoder() => new RawDecoder();

        public override Encoder GetEncoder() => new RawEncoder();

        private static int CountChars(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return 0;
            var rented = ArrayPool<char>.Shared.Rent(bytes.Length);
            try { return DecodeCore(bytes, rented, final: true, out _); }
            finally { ArrayPool<char>.Shared.Return(rented); }
        }
    }

    /// <summary>Stateful decoder: an incomplete multi-byte tail waits for the next chunk.</summary>
    private sealed class RawDecoder : Decoder
    {
        private readonly byte[] _left = new byte[4];
        private int _leftLen;

        public override void Reset() => _leftLen = 0;

        public override int GetCharCount(byte[] bytes, int index, int count) => GetCharCount(bytes, index, count, false);

        public override int GetCharCount(byte[] bytes, int index, int count, bool flush)
            => GetCharCount(bytes.AsSpan(index, count), flush);

        public override int GetCharCount(ReadOnlySpan<byte> bytes, bool flush)
            => Run(bytes, default, flush, countOnly: true, commit: false);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            => GetChars(bytes, byteIndex, byteCount, chars, charIndex, false);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
            => GetChars(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex), flush);

        public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars, bool flush)
            => Run(bytes, chars, flush, countOnly: false, commit: true);

        private int Run(ReadOnlySpan<byte> input, Span<char> dest, bool flush, bool countOnly, bool commit)
        {
            byte[]? combined = null;
            ReadOnlySpan<byte> src = input;
            if (_leftLen > 0)
            {
                combined = new byte[_leftLen + input.Length];
                _left.AsSpan(0, _leftLen).CopyTo(combined);
                input.CopyTo(combined.AsSpan(_leftLen));
                src = combined;
            }

            int written, consumed;
            if (countOnly)
            {
                var rented = ArrayPool<char>.Shared.Rent(Math.Max(1, src.Length));
                try { written = DecodeCore(src, rented, flush, out consumed); }
                finally { ArrayPool<char>.Shared.Return(rented); }
            }
            else
            {
                written = DecodeCore(src, dest, flush, out consumed);
            }

            if (commit)
            {
                int left = src.Length - consumed; // only non-zero when !flush and the tail is incomplete
                if (left > 0) src.Slice(consumed).CopyTo(_left);
                _leftLen = left;
            }
            return written;
        }
    }

    /// <summary>Stateful encoder: a high surrogate at the end of one chunk waits for its low half.</summary>
    private sealed class RawEncoder : Encoder
    {
        private char _pendingHigh;

        public override void Reset() => _pendingHigh = '\0';

        public override int GetByteCount(char[] chars, int index, int count, bool flush)
            => GetByteCount(chars.AsSpan(index, count), flush);

        public override int GetByteCount(ReadOnlySpan<char> chars, bool flush)
            => Run(chars, default, flush, countOnly: true, commit: false);

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, bool flush)
            => GetBytes(chars.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex), flush);

        public override int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes, bool flush)
            => Run(chars, bytes, flush, countOnly: false, commit: true);

        private int Run(ReadOnlySpan<char> chars, Span<byte> dest, bool flush, bool countOnly, bool commit)
        {
            int written = 0;
            char pending = _pendingHigh;
            if (pending != '\0')
            {
                Span<char> pair = stackalloc char[2];
                int n;
                if (!chars.IsEmpty && char.IsLowSurrogate(chars[0]))
                {
                    pair[0] = pending;
                    pair[1] = chars[0];
                    chars = chars.Slice(1);
                    n = 2;
                }
                else
                {
                    pair[0] = pending; // lone high surrogate: the UTF-8 encoder writes U+FFFD
                    n = 1;
                }
                written += countOnly ? Utf8Enc.GetByteCount(pair.Slice(0, n)) : Utf8Enc.GetBytes(pair.Slice(0, n), dest);
                pending = '\0';
            }

            if (!flush && !chars.IsEmpty && char.IsHighSurrogate(chars[^1]))
            {
                pending = chars[^1];
                chars = chars.Slice(0, chars.Length - 1);
            }

            written += countOnly
                ? RawBytes.GetByteCount(chars)
                : RawBytes.GetBytes(chars, dest.Slice(written));
            if (commit) _pendingHigh = pending;
            return written;
        }
    }
}
