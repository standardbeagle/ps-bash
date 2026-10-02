using System.IO.Compression;
using System.Text;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>How rg reads a file: <c>-a</c>, <c>-z</c>, <c>-E</c>, and whether byte offsets need CRLF kept.</summary>
internal sealed class RgReadOptions
{
    internal bool Text, SearchZip, Exact;

    /// <summary>The <c>-E</c> encoding; null = auto (UTF-8 with BOM sniffing).</summary>
    internal Encoding? Encoding;

    /// <summary><c>-E none</c>: no transcoding, bytes travel as escaped-byte markers.</summary>
    internal bool NoDecode;
}

/// <summary>File reading for rg: encodings (<c>-E</c>), gzip (<c>-z</c>), binary detection, line splitting.</summary>
internal static class RgInput
{
    static RgInput()
    {
        // windows-1252 (ripgrep's "latin1") lives in the code-pages provider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Resolve an <c>-E</c> label. <paramref name="auto"/> = default sniffing; error text matches ripgrep.</summary>
    internal static bool TryResolveEncoding(string label, out Encoding? encoding, out bool none, out string? error)
    {
        encoding = null; none = false; error = null;
        switch (label.Trim().ToLowerInvariant())
        {
            case "auto": return true;
            case "none": none = true; return true;
            case "utf-8" or "utf8" or "unicode-1-1-utf-8": encoding = new UTF8Encoding(false); return true;
            case "latin1" or "latin-1" or "iso-8859-1" or "l1" or "iso8859-1" or "windows-1252" or "cp1252":
                encoding = Encoding.GetEncoding(1252); return true;
            case "utf-16" or "utf-16le" or "utf16" or "ucs-2": encoding = new UnicodeEncoding(false, false); return true;
            case "utf-16be": encoding = new UnicodeEncoding(true, false); return true;
            case "ascii" or "us-ascii": encoding = Encoding.ASCII; return true;
        }
        try { encoding = Encoding.GetEncoding(label); return true; }
        catch { error = $"grep config error: unknown encoding: {label}"; return false; }
    }

    /// <summary>
    /// Read <paramref name="abs"/> into a source. null = skip silently (a binary file found by the directory walk).
    /// An explicitly named binary file is searched and flagged (<see cref="RgSource.BinaryOffset"/>) so a match is
    /// reported as ripgrep's "binary file matches" line.
    /// </summary>
    internal static RgSource? Read(string abs, string display, bool explicitFile, RgReadOptions o)
    {
        var src = new RgSource { Display = display };
        bool gz = o.SearchZip && abs.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

        if (gz || o.Encoding is not null || o.NoDecode)
        {
            byte[] bytes = gz ? Gunzip(abs) : BashFileSystem.ReadAllBytes(abs);
            src.ByteSize = bytes.Length;
            string text;
            if (o.NoDecode) text = RawBytes.GetString(bytes);
            else if (o.Encoding is { } enc) text = enc.GetString(bytes);
            else text = DecodeAuto(bytes);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            if (!o.Text && o.Encoding is null or { CodePage: 65001 } && Array.IndexOf(bytes, (byte)0) >= 0 && !HasUtf16Bom(bytes))
            {
                if (!explicitFile) return null;
                src.BinaryOffset = Array.IndexOf(bytes, (byte)0);
            }
            SplitLines(text, normalizeCr: !o.Exact, src);
            return src;
        }

        bool binary = !o.Text && IsBinary(abs);
        if (binary && !explicitFile) return null;
        if (binary) src.BinaryOffset = FirstNul(abs);

        try { src.ByteSize = new FileInfo(abs).Length; } catch { src.ByteSize = 0; }
        bool final = true;
        foreach (var tl in BashFileSystem.ReadTextLines(abs, exact: o.Exact))
        {
            src.Lines.Add(tl.Text);
            final = tl.HasTrailingNewline;
        }
        src.FinalNewline = final;
        return src;
    }

    private static byte[] Gunzip(string path)
    {
        using var fs = BashFileSystem.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    private static bool HasUtf16Bom(byte[] b) => b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF));

    private static string DecodeAuto(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return new UnicodeEncoding(false, true).GetString(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return new UnicodeEncoding(true, true).GetString(bytes);
        return RawBytes.GetString(bytes);
    }

    /// <summary>NUL in the first 8 KB, except a UTF-16 file (BOM) whose NULs are just wide characters.</summary>
    private static bool IsBinary(string path)
    {
        if (FileSystemHelpers.IsNullDevice(path)) return false;
        using var fs = BashFileSystem.OpenRead(path);
        var probe = new byte[8192];
        int total = 0, n;
        while (total < probe.Length && (n = fs.Read(probe, total, probe.Length - total)) > 0) total += n;
        if (total >= 2 && ((probe[0] == 0xFF && probe[1] == 0xFE) || (probe[0] == 0xFE && probe[1] == 0xFF))) return false;
        return Array.IndexOf(probe, (byte)0, 0, total) >= 0;
    }

    private static long FirstNul(string path)
    {
        using var fs = BashFileSystem.OpenRead(path);
        var probe = new byte[8192];
        int total = 0, n;
        while (total < probe.Length && (n = fs.Read(probe, total, probe.Length - total)) > 0) total += n;
        return Array.IndexOf(probe, (byte)0, 0, total);
    }

    /// <summary>Split decoded text on <c>\n</c> (a CR before it dropped when <paramref name="normalizeCr"/>); no phantom last line.</summary>
    internal static void SplitLines(string text, bool normalizeCr, RgSource into)
    {
        int start = 0;
        bool final = true;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            int end = i;
            if (normalizeCr && end > start && text[end - 1] == '\r') end--;
            into.Lines.Add(text.Substring(start, end - start));
            start = i + 1;
        }
        if (start < text.Length) { into.Lines.Add(text.Substring(start)); final = false; }
        into.FinalNewline = final;
    }
}
