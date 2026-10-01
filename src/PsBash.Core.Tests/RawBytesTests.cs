using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// The escaped-byte codec (<see cref="RawBytes"/>): decode maps every byte of an invalid UTF-8 sequence to
/// U+DC80..U+DCFF, encode maps those markers back to the single original bytes. The oracle is the
/// identity law (bytes -> string -> bytes) plus the UTF-8 spec for valid text.
/// </summary>
public class RawBytesTests
{
    private static byte[] RoundTrip(byte[] bytes) => RawBytes.GetBytes(RawBytes.GetString(bytes));

    [Fact]
    public void EveryByteValue_RoundTrips()
    {
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        Assert.Equal(all, RoundTrip(all));
    }

    [Fact]
    public void EveryTwoByteSequence_RoundTrips()
    {
        for (int a = 0; a < 256; a++)
        {
            var batch = new byte[256 * 2];
            for (int b = 0; b < 256; b++) { batch[b * 2] = (byte)a; batch[b * 2 + 1] = (byte)b; }
            // Each pair separately (a pair can interact with its neighbour, so test them alone).
            for (int b = 0; b < 256; b++)
            {
                var pair = new[] { (byte)a, (byte)b };
                Assert.Equal(pair, RoundTrip(pair));
            }
            Assert.Equal(batch, RoundTrip(batch));
        }
    }

    [Fact]
    public void RandomBinary_RoundTrips()
    {
        var rng = new Random(12345);
        for (int n = 0; n < 200; n++)
        {
            var data = new byte[rng.Next(0, 4096)];
            rng.NextBytes(data);
            Assert.Equal(data, RoundTrip(data));
        }
    }

    [Fact]
    public void Utf8_BiasedRandomBinary_RoundTrips()
    {
        // Mostly-valid text with random byte flips: the interesting boundary between valid and invalid runs.
        var rng = new Random(777);
        var text = Encoding.UTF8.GetBytes("café € \U0001F600 日本語 plain ascii\r\n");
        for (int n = 0; n < 500; n++)
        {
            var data = (byte[])text.Clone();
            for (int k = 0; k < 1 + rng.Next(4); k++) data[rng.Next(data.Length)] = (byte)rng.Next(256);
            Assert.Equal(data, RoundTrip(data));
        }
    }

    [Fact]
    public void ValidUtf8_DecodesUnchanged_AndHasNoMarkers()
    {
        const string text = "café € \U0001F600 日本語 ascii";
        var bytes = Encoding.UTF8.GetBytes(text);
        var decoded = RawBytes.GetString(bytes);
        Assert.Equal(text, decoded);
        Assert.False(RawBytes.ContainsMarker(decoded));
        Assert.Equal(bytes, RawBytes.GetBytes(decoded));
        Assert.Equal(bytes.Length, RawBytes.GetByteCount(decoded));
    }

    [Theory]
    // Expected chars are given as hex code units: an attribute argument cannot carry a lone surrogate.
    [InlineData(new byte[] { 0xE9 }, "DCE9")]                      // lone Latin-1 e-acute
    [InlineData(new byte[] { 0xFF, 0xFE }, "DCFF DCFE")]           // never valid in UTF-8
    [InlineData(new byte[] { 0xC0, 0x80 }, "DCC0 DC80")]           // overlong NUL
    [InlineData(new byte[] { 0xE2, 0x82 }, "DCE2 DC82")]           // truncated euro
    [InlineData(new byte[] { 0x80 }, "DC80")]                      // stray continuation byte
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 }, "DCED DCA0 DC80")] // UTF-8-encoded surrogate
    [InlineData(new byte[] { 0x41, 0xE9, 0x42 }, "0041 DCE9 0042")]
    public void InvalidBytes_BecomeMarkers(byte[] input, string expectedHex)
    {
        var decoded = RawBytes.GetString(input);
        var expected = new string(expectedHex.Split(' ').Select(h => (char)Convert.ToInt32(h, 16)).ToArray());
        Assert.Equal(expected, decoded);
        Assert.True(RawBytes.ContainsMarker(decoded));
        Assert.Equal(input, RawBytes.GetBytes(decoded));
        Assert.Equal(input.Length, RawBytes.GetByteCount(decoded));
    }

    [Fact]
    public void ValidSequenceNextToInvalid_KeepsTheValidPart()
    {
        // E2 82 AC (euro) then a lone E9 then plain text.
        var bytes = new byte[] { 0xE2, 0x82, 0xAC, 0xE9, 0x61 };
        Assert.Equal("€\uDCE9a", RawBytes.GetString(bytes));
    }

    [Fact]
    public void SupplementaryCharacter_WithLowSurrogateInMarkerRange_IsNotAMarker()
    {
        // U+10080 = D800 DC80: its low half is numerically a marker, but it follows a high surrogate,
        // so it is a pair and must encode as the 4-byte UTF-8 sequence, never as byte 0x80.
        var s = char.ConvertFromUtf32(0x10080);
        Assert.False(RawBytes.ContainsMarker(s));
        Assert.Equal(Encoding.UTF8.GetBytes(s), RawBytes.GetBytes(s));
        Assert.Equal(s, RawBytes.GetString(Encoding.UTF8.GetBytes(s)));
    }

    [Fact]
    public void Marker_Helpers()
    {
        Assert.Equal('\uDCE9', RawBytes.ToMarker(0xE9));
        Assert.Equal((byte)0xE9, RawBytes.FromMarker('\uDCE9'));
        Assert.True(RawBytes.IsMarkerChar('\uDC80'));
        Assert.True(RawBytes.IsMarkerChar('\uDCFF'));
        Assert.False(RawBytes.IsMarkerChar('\uDC7F'));
        Assert.False(RawBytes.IsMarkerChar('\uDD00'));
        Assert.Throws<ArgumentOutOfRangeException>(() => RawBytes.ToMarker(0x41));
    }

    [Fact]
    public void Encoding_StreamRoundTrip_AcrossArbitraryChunkBoundaries()
    {
        // The Encoding's stateful decoder/encoder must give the same answer however the stream is chunked
        // (a multi-byte sequence split across reads, a surrogate pair split across writes).
        var rng = new Random(99);
        var data = new byte[5000];
        rng.NextBytes(data);
        var text = Encoding.UTF8.GetBytes("\U0001F600€é tail");
        text.CopyTo(data, 100);

        foreach (int chunk in new[] { 1, 2, 3, 5, 7, 64, 4096 })
        {
            var decoder = RawBytes.CreateDecoder();
            var chars = new System.Collections.Generic.List<char>();
            var buf = new char[chunk * 4 + 8];
            for (int i = 0; i < data.Length; i += chunk)
            {
                int n = Math.Min(chunk, data.Length - i);
                bool last = i + n >= data.Length;
                int c = decoder.GetChars(data, i, n, buf, 0, last);
                chars.AddRange(buf.Take(c));
            }
            var s = new string(chars.ToArray());
            Assert.Equal(data, RawBytes.GetBytes(s));

            // Encode in awkward chunks through the Encoding's Encoder (high surrogates split from lows).
            var encoder = RawBytes.Encoding.GetEncoder();
            var ms = new MemoryStream();
            var outBuf = new byte[chunk * 4 + 16];
            for (int i = 0; i < s.Length; i += chunk)
            {
                int n = Math.Min(chunk, s.Length - i);
                bool last = i + n >= s.Length;
                int b = encoder.GetBytes(s.ToCharArray(), i, n, outBuf, 0, last);
                ms.Write(outBuf, 0, b);
            }
            Assert.Equal(data, ms.ToArray());
        }
    }

    [Fact]
    public void StreamReaderAndWriter_UsingTheEncoding_AreLossless()
    {
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).Concat(Encoding.UTF8.GetBytes("€\n")).ToArray();
        using var reader = new StreamReader(new MemoryStream(data), RawBytes.Encoding, detectEncodingFromByteOrderMarks: false);
        var text = reader.ReadToEnd();
        var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, RawBytes.Encoding, 16, leaveOpen: true))
            writer.Write(text);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public void BomBytes_AreKeptNotStripped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x61 };
        using var reader = new StreamReader(new MemoryStream(bytes), RawBytes.Encoding, detectEncodingFromByteOrderMarks: false);
        Assert.Equal("﻿a", reader.ReadToEnd());
    }

    [Fact]
    public void LoneHighSurrogate_EncodesAsReplacement_NotAnEscapedByte()
    {
        // Documented non-ambiguity: only LOW surrogates DC80..DCFF are markers; a stray high surrogate
        // is not binary data and gets the standard U+FFFD replacement.
        Assert.Equal(new byte[] { 0xEF, 0xBF, 0xBD }, RawBytes.GetBytes("\uD800"));
    }
}
