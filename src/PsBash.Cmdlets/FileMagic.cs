using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>Type description, MIME type and MIME encoding of one file's content.</summary>
internal readonly record struct FileKind(string Type, string Mime, string Encoding);

/// <summary>
/// The content classifier behind <c>file</c> (file-5.45 / libmagic wording, oracle-checked): a very
/// short file, ELF, PE / MS-DOS, gzip, zip, PNG, JPEG, PDF, GIF, RIFF, a <c>#!</c> script, then the
/// text scan (ASCII / UTF-8 / ISO-8859 / Non-ISO extended-ASCII, line terminators, very long lines,
/// escape sequences, overstriking) and finally <c>data</c>.
///
/// <para>Pure over a re-openable byte source so it is unit-testable and serves a file, a pipe's
/// buffered bytes and a test array alike. Anything the table does not model keeps libmagic's own
/// fallback (text or <c>data</c>) rather than a guess.</para>
/// </summary>
internal static class FileMagic
{
    /// <summary>libmagic examines at most this many leading bytes for the text / terminator scan.</summary>
    private const int ScanLimit = 1 << 20;

    public static FileKind Classify(Func<Stream> open)
    {
        try
        {
            using var stream = open();
            long size = stream.CanSeek ? stream.Length : -1;
            byte[] head = ReadHead(stream, ScanLimit);
            if (size < 0 || head.Length < ScanLimit) size = Math.Max(size < 0 ? 0 : size, head.Length);
            return ClassifyBytes(new Source(stream, head, size));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Data;
        }
    }

    /// <summary>Test seam: classify an in-memory array.</summary>
    internal static FileKind ClassifyArray(byte[] bytes) =>
        Classify(() => new MemoryStream(bytes, writable: false));

    private static readonly FileKind Data = new("data", "application/octet-stream", "binary");

    private static byte[] ReadHead(Stream s, int limit)
    {
        var buf = new byte[limit];
        int total = 0;
        while (total < limit)
        {
            int n = s.Read(buf, total, limit - total);
            if (n <= 0) break;
            total += n;
        }
        return total == limit ? buf : buf.AsSpan(0, total).ToArray();
    }

    /// <summary>The leading bytes plus random access to the rest of a seekable stream.</summary>
    private sealed class Source
    {
        private readonly Stream _stream;
        public byte[] Head { get; }
        public long Size { get; }

        public Source(Stream stream, byte[] head, long size)
        {
            _stream = stream;
            Head = head;
            Size = size;
        }

        /// <summary>Bytes [offset, offset+count) or null when any part lies outside the file.</summary>
        public byte[]? ReadAt(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > Size) return null;
            if (offset + count <= Head.Length) return Head.AsSpan((int)offset, count).ToArray();
            if (!_stream.CanSeek) return null;
            var buf = new byte[count];
            _stream.Seek(offset, SeekOrigin.Begin);
            int total = 0;
            while (total < count)
            {
                int n = _stream.Read(buf, total, count - total);
                if (n <= 0) return null;
                total += n;
            }
            return buf;
        }
    }

    private static FileKind ClassifyBytes(Source src)
    {
        byte[] h = src.Head;
        if (src.Size == 0) return new FileKind("empty", "inode/x-empty", "binary");
        if (src.Size == 1) return new FileKind("very short file (no magic)", "application/octet-stream", "binary");

        FileKind? k = TryElf(src) ?? TryPe(src) ?? TryGzip(src) ?? TryZip(src) ?? TryPng(src)
            ?? TryJpeg(src) ?? TryPdf(src) ?? TrySimpleMagic(h);
        if (k is { } found) return found;

        var info = ScanText(h);
        if (h.Length >= 2 && h[0] == '#' && h[1] == '!' && src.Size >= 3)
            return DescribeScript(h, info);
        return DescribeText(info, scriptName: null);
    }

    // ── small magics kept as before (not part of the verified set) ───────────────────────────────

    private static FileKind? TrySimpleMagic(byte[] h)
    {
        if (h.Length >= 4 && h[0] == 0x47 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x38)
            return new FileKind("GIF image data", "image/gif", "binary");
        if (h.Length >= 4 && h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46)
            return new FileKind("RIFF data", "application/octet-stream", "binary");
        return null;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static ushort U16(byte[] b, int o, bool le) =>
        le ? (ushort)(b[o] | b[o + 1] << 8) : (ushort)(b[o] << 8 | b[o + 1]);

    private static uint U32(byte[] b, int o, bool le) =>
        le ? (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24)
           : (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    private static ulong U64(byte[] b, int o, bool le)
    {
        ulong lo = U32(b, le ? o : o + 4, le), hi = U32(b, le ? o + 4 : o, le);
        return le ? lo | hi << 32 : hi | lo << 32;
    }

    /// <summary>Charset of non-text-magic files (PDF) from the text scan: us-ascii / iso-8859-1 / ... or binary.</summary>
    private static string EncodingOf(byte[] head)
    {
        var t = ScanText(head);
        return t.IsText ? t.Charset : "binary";
    }

    // ── gzip ────────────────────────────────────────────────────────────────────────────────────

    private static readonly string?[] GzipOs =
    {
        "FAT filesystem (MS-DOS, OS/2, NT)", "Amiga", "VMS", "Unix", "VM/CMS", "Atari",
        "HPFS filesystem (OS/2, NT)", "MacOS", "Z-System", "CP/M", "TOPS/20", "NTFS filesystem (NT)",
        "QDOS", "Acorn RISCOS",
    };

    private static FileKind? TryGzip(Source src)
    {
        byte[] h = src.Head;
        if (h.Length < 4 || h[0] != 0x1F || h[1] != 0x8B) return null;
        var sb = new StringBuilder("gzip compressed data");
        const string mime = "application/gzip";
        if (src.Size >= 10 && h.Length >= 10)
        {
            byte method = h[2], flags = h[3];
            if (method < 8) sb.Append(", reserved method");
            if ((flags & 0x01) != 0) sb.Append(", ASCII");
            if ((flags & 0x02) != 0) sb.Append(", has CRC");
            if ((flags & 0x04) != 0) sb.Append(", extra field");
            if ((flags & 0x0C) == 0x08)
            {
                int end = Array.IndexOf(h, (byte)0, 10);
                int stop = end < 0 ? h.Length : end;
                sb.Append(", was \"").Append(RawLatin1(h, 10, stop - 10)).Append('"');
            }
            if ((flags & 0x10) != 0) sb.Append(", has comment");
            if ((flags & 0x20) != 0) sb.Append(", encrypted");
            int mtime = (int)U32(h, 4, true);
            if (mtime > 0)
            {
                DateTime t = DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime;
                sb.Append(", last modified: ").Append(Ctime(t));
            }
            if (h[8] == 2) sb.Append(", max compression");
            else if (h[8] == 4) sb.Append(", max speed");
            if (h[9] < GzipOs.Length) sb.Append(", from ").Append(GzipOs[h[9]]);
            byte[]? tail = src.ReadAt(src.Size - 4, 4);
            if (tail is not null)
                sb.Append(", original size modulo 2^32 ").Append(U32(tail, 0, true).ToString(CultureInfo.InvariantCulture));
        }
        return new FileKind(sb.ToString(), mime, "binary");
    }

    private static string Ctime(DateTime utc) =>
        $"{utc.ToString("ddd MMM", CultureInfo.InvariantCulture)} {utc.Day,2} {utc.ToString("HH:mm:ss yyyy", CultureInfo.InvariantCulture)}";

    private static string RawLatin1(byte[] b, int off, int len)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte c = b[off + i];
            if (c >= 0x20 && c < 0x7F) sb.Append((char)c);
            else sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
        }
        return sb.ToString();
    }

    // ── zip ─────────────────────────────────────────────────────────────────────────────────────

    private static string ZipMethod(int m) => m switch
    {
        0 => "store", 1 => "Shrinking", 6 => "Imploding", 7 => "Tokenizing", 8 => "deflate",
        9 => "deflate64", 10 => "Library imploding", 12 => "bzip2", 14 => "lzma",
        16 => "CMPSC (IBM z/OS)", 18 => "IBM TERSE", 19 => "IBM LZ77 (z/Architecture)",
        93 => "Zstd", 94 => "MP3", 95 => "xz", 96 => "Jpeg", 97 => "WavPack", 98 => "PPMd",
        99 => "AES Encrypted",
        _ => $"[0x{m:x}]",
    };

    private static FileKind? TryZip(Source src)
    {
        byte[] h = src.Head;
        if (h.Length >= 4 && h[0] == 'P' && h[1] == 'K')
        {
            // End-of-central-directory first = an archive with no entries.
            if (h[2] == 5 && h[3] == 6) return new FileKind("Zip archive data (empty)", "application/zip", "binary");
            // libmagic describes a local file header only once enough of the archive is there (>= 50 bytes).
            if (h[2] == 3 && h[3] == 4 && src.Size >= 50 && h.Length >= 10)
            {
                int ver = U16(h, 4, true), method = U16(h, 8, true);
                return new FileKind(
                    $"Zip archive data, at least v{ver / 10}.{ver % 10} to extract, compression method={ZipMethod(method)}",
                    "application/zip", "binary");
            }
        }
        return null;
    }

    // ── PNG ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly byte[] PngMagic =
        { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52 };

    private static FileKind? TryPng(Source src)
    {
        byte[] h = src.Head;
        if (h.Length < 16 || !h.AsSpan(0, 16).SequenceEqual(PngMagic)) return null;
        // Bytes past the end of a truncated file read as zero, as libmagic's does.
        int At(int o) => o < h.Length ? h[o] : 0;
        int W = (int)(uint)(At(16) << 24 | At(17) << 16 | At(18) << 8 | At(19));
        int H = (int)(uint)(At(20) << 24 | At(21) << 16 | At(22) << 8 | At(23));
        int depth = At(24), ctype = At(25), inter = At(28);
        string ct = ctype switch
        {
            0 => " grayscale,", 2 => "/color RGB,", 3 => " colormap,", 4 => " gray+alpha,", 6 => "/color RGBA,", _ => string.Empty,
        };
        string il = inter switch { 0 => "non-interlaced", 1 => "interlaced", _ => string.Empty };
        return new FileKind($"PNG image data, {W} x {H}, {depth}-bit{ct} {il}".TrimEnd(), "image/png", "binary");
    }

    // ── JPEG ────────────────────────────────────────────────────────────────────────────────────

    private static FileKind? TryJpeg(Source src)
    {
        byte[] h = src.Head;
        if (h.Length < 4 || h[0] != 0xFF || h[1] != 0xD8 || h[2] != 0xFF) return null;
        var sb = new StringBuilder("JPEG image data");
        // JFIF APP0 directly after SOI.
        if (h.Length >= 18 && h[3] == 0xE0 && h[6] == 'J' && h[7] == 'F' && h[8] == 'I' && h[9] == 'F' && h[10] == 0)
        {
            sb.Append(", JFIF standard ").Append(h[11]).Append('.').Append(h[12].ToString("00", CultureInfo.InvariantCulture));
            sb.Append(h[13] switch { 0 => ", aspect ratio", 1 => ", resolution (DPI)", 2 => ", resolution (DPCM)", _ => string.Empty });
            sb.Append(", density ").Append(U16(h, 14, false)).Append('x').Append(U16(h, 16, false));
            sb.Append(", segment length ").Append(U16(h, 4, false));
        }
        // Walk the marker segments to the first start-of-frame.
        int p = 2;
        while (p + 4 <= h.Length)
        {
            if (h[p] != 0xFF) break;
            byte m = h[p + 1];
            if (m == 0xFF) { p++; continue; }
            if (m == 0xD9 || m == 0xDA || m == 0x00) break;
            int len = U16(h, p + 2, false);
            string? name = SofName(m);
            if (name is not null)
            {
                if (p + 10 <= h.Length)
                {
                    int prec = h[p + 4], height = U16(h, p + 5, false), width = U16(h, p + 7, false), nc = h[p + 9];
                    sb.Append(", ").Append(name).Append(", precision ").Append(prec).Append(", ")
                      .Append(width).Append('x').Append(height).Append(", components ").Append(nc);
                }
                break;
            }
            p += 2 + len;
        }
        // libmagic's charset comes from the text scan here too (a bare "FF D8 FF DB" is all high bytes = iso-8859-1).
        return new FileKind(sb.ToString(), "image/jpeg", EncodingOf(h));
    }

    private static string? SofName(byte m) => m switch
    {
        0xC0 => "baseline", 0xC1 => "extended sequential", 0xC2 => "progressive", 0xC3 => "lossless",
        0xC5 => "differential sequential", 0xC6 => "differential progressive", 0xC7 => "differential lossless",
        0xC9 => "extended sequential, arithmetic coding", 0xCA => "progressive, arithmetic coding",
        0xCB => "lossless, arithmetic coding", 0xCD => "differential sequential, arithmetic coding",
        0xCE => "differential progressive, arithmetic coding", 0xCF => "differential lossless, arithmetic coding",
        _ => null,
    };

    // ── PDF ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex PdfCount = new(@"/Count\s+(\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static FileKind? TryPdf(Source src)
    {
        byte[] h = src.Head;
        if (h.Length < 5 || h[0] != '%' || h[1] != 'P' || h[2] != 'D' || h[3] != 'F' || h[4] != '-') return null;
        var sb = new StringBuilder("PDF document");
        if (h.Length >= 6) sb.Append(", version ").Append(PdfChar(h[5]));
        if (h.Length >= 8) sb.Append('.').Append(PdfChar(h[7]));
        // The first "/Count N" in the leading megabyte is libmagic's page count.
        var m = PdfCount.Match(Encoding.Latin1.GetString(h));
        if (m.Success) sb.Append(", ").Append(m.Groups[1].Value).Append(" page(s)");
        return new FileKind(sb.ToString(), "application/pdf", EncodingOf(h));
    }

    private static string PdfChar(byte b) =>
        b >= 0x20 && b < 0x7F ? ((char)b).ToString() : "\\" + Convert.ToString(b, 8).PadLeft(3, '0');

    // ── PE / MS-DOS ─────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<int, string> PeMachines = new()
    {
        [0x14c] = "Intel 80386", [0x166] = "MIPS R4000", [0x169] = "MIPS WCE v2", [0x184] = "Alpha",
        [0x1a2] = "Hitachi SH3", [0x1a3] = "Hitachi SH3 DSP", [0x1a6] = "Hitachi SH4", [0x1c0] = "ARM",
        [0x1c2] = "ARM Thumb", [0x1c4] = "ARMv7 Thumb", [0x1d3] = "Matsushita AM33", [0x1f0] = "PowerPC",
        [0x1f1] = "PowerPC with FPU", [0x200] = "Intel Itanium", [0x266] = "MIPS16", [0x366] = "MIPSIV",
        [0x466] = "MIPS16 with FPU", [0xebc] = "EFI byte code", [0x5032] = "RISC-V 32-bit",
        [0x5064] = "RISC-V 64-bit", [0x6232] = "LoongArch 32-bit", [0x6264] = "LoongArch 64-bit",
        [0x8664] = "x86-64", [0x9041] = "Mitsubishi M32R", [0xaa64] = "Aarch64", [0xc0ee] = "MSIL",
    };

    private static string PeSubsystem(int s) => s switch
    {
        0 => "Control Panel Item", 1 => "native", 2 => "GUI", 3 => "console", 5 => "OS/2", 7 => "POSIX",
        8 => "Win9x", 9 => "Windows CE", 10 => "EFI application", 11 => "EFI boot service driver",
        12 => "EFI runtime driver", 13 => "EFI ROM", 14 => "XBOX", 16 => "Windows boot application",
        _ => $"Unknown subsystem {s}",
    };

    private static FileKind? TryPe(Source src)
    {
        byte[] h = src.Head;
        if (h.Length < 2 || h[0] != 'M' || h[1] != 'Z') return null;
        if (src.Size < 64 || h.Length < 64) return null;   // too short to be an MZ header: text / data

        uint lfanew = U32(h, 0x3C, true);
        byte[]? sig = lfanew + 2 <= src.Size ? src.ReadAt(lfanew, 2) : null;
        if (sig is null || sig[0] != 'P' || sig[1] != 'E')
        {
            // An MZ header whose PE header is absent: plain DOS executable. A relocation table inside the
            // first 64 bytes marks a real DOS program (libmagic's ", MZ for MS-DOS").
            bool dos = U16(h, 0x18, true) < 0x40;
            return dos
                ? new FileKind("MS-DOS executable, MZ for MS-DOS", "application/x-dosexec", "binary")
                : new FileKind("MS-DOS executable", "application/octet-stream", "binary");
        }

        byte[]? coff = src.ReadAt(lfanew + 4, 20);
        if (coff is null) return new FileKind("data", "application/octet-stream", "binary");
        int machine = U16(coff, 0, true), nsec = U16(coff, 2, true), optSize = U16(coff, 16, true), chars = U16(coff, 18, true);
        long optOff = lfanew + 24;
        byte[]? magicBytes = src.ReadAt(optOff, 2);
        int magic = magicBytes is null ? 0 : U16(magicBytes, 0, true);
        var sb = new StringBuilder();
        bool plus = magic == 0x20B;
        if (magic == 0x10B || plus) sb.Append(plus ? "PE32+" : "PE32").Append(" executable");
        else if (magic == 0x107) sb.Append("PE ROM image");
        else if (magicBytes is null) sb.Append("PE32 executable");
        else sb.Append("PE Unknown PE signature 0x").Append(magic.ToString("x", CultureInfo.InvariantCulture));
        if ((chars & 0x2000) != 0) sb.Append(" (DLL)");
        byte[]? subsys = src.ReadAt(optOff + 68, 2);
        if (subsys is not null) sb.Append(" (").Append(PeSubsystem(U16(subsys, 0, true))).Append(')');
        if (PeMachines.TryGetValue(machine, out var mname)) sb.Append(' ').Append(mname);
        // A non-empty CLR runtime header directory entry marks a .NET assembly.
        byte[]? clr = src.ReadAt(optOff + (plus ? 224 : 208), 8);
        if (clr is not null && (U32(clr, 0, true) != 0 || U32(clr, 4, true) != 0)) sb.Append(" Mono/.Net assembly");
        if ((chars & 0x1000) != 0) sb.Append(" system file");
        sb.Append(", for MS Windows");
        if (nsec >= 2) sb.Append(", ").Append(nsec).Append(" sections");
        _ = optSize;
        return new FileKind(sb.ToString(), "application/vnd.microsoft.portable-executable", "binary");
    }

    // ── ELF ─────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<int, string> ElfMachines = BuildElfMachines();

    /// <summary>Machines whose name is followed by " version" without the comma (libmagic's <c>\b</c> quirk).</summary>
    private static readonly HashSet<int> ElfNoComma = new() { 144, 219, 220, 221, 222, 223, 224 };

    private static Dictionary<int, string> BuildElfMachines()
    {
        var d = new Dictionary<int, string>();
        void A(int n, string s) => d[n] = s;
        A(0, "no machine"); A(1, "AT&T WE32100"); A(2, "SPARC"); A(3, "Intel 80386"); A(4, "Motorola m68k");
        A(5, "Motorola m88k"); A(6, "Intel 80486"); A(7, "Intel 80860"); A(9, "Amdahl"); A(11, "RS6000");
        A(15, "PA-RISC"); A(16, "nCUBE"); A(17, "Fujitsu VPP500"); A(18, "SPARC32PLUS"); A(19, "Intel 80960");
        A(20, "PowerPC or cisco 4500"); A(22, "IBM S/390"); A(23, "Cell SPU"); A(24, "cisco SVIP");
        A(25, "cisco 7200"); A(36, "NEC V800 or cisco 12000"); A(37, "Fujitsu FR20"); A(38, "TRW RH-32");
        A(39, "Motorola RCE"); A(40, "ARM"); A(41, "Alpha"); A(42, "Renesas SH");
        A(44, "Siemens Tricore Embedded Processor"); A(45, "Argonaut RISC Core, Argonaut Technologies Inc.");
        A(46, "Renesas H8/300"); A(47, "Renesas H8/300H"); A(48, "Renesas H8S"); A(49, "Renesas H8/500");
        A(50, "IA-64"); A(51, "Stanford MIPS-X"); A(52, "Motorola Coldfire"); A(53, "Motorola M68HC12");
        A(54, "Fujitsu MMA"); A(55, "Siemens PCP"); A(56, "Sony nCPU"); A(57, "Denso NDR1"); A(58, "Start*Core");
        A(59, "Toyota ME16"); A(60, "ST100"); A(61, "Tinyj emb."); A(62, "x86-64"); A(63, "Sony DSP");
        A(64, "DEC PDP-10"); A(65, "DEC PDP-11"); A(66, "FX66"); A(67, "ST9+ 8/16 bit"); A(68, "ST7 8 bit");
        A(69, "MC68HC16"); A(70, "MC68HC11"); A(71, "MC68HC08"); A(72, "MC68HC05"); A(73, "SGI SVx or Cray NV1");
        A(74, "ST19 8 bit"); A(75, "Digital VAX"); A(76, "Axis cris"); A(77, "Infineon 32-bit embedded");
        A(78, "Element 14 64-bit DSP"); A(79, "LSI Logic 16-bit DSP"); A(80, "MMIX"); A(81, "Harvard machine-independent");
        A(82, "SiTera Prism"); A(83, "Atmel AVR 8-bit"); A(84, "Fujitsu FR30"); A(85, "Mitsubishi D10V");
        A(86, "Mitsubishi D30V"); A(87, "NEC v850"); A(88, "Renesas M32R"); A(89, "Matsushita MN10300");
        A(90, "Matsushita MN10200"); A(91, "picoJava"); A(92, "OpenRISC"); A(93, "Synopsys ARCompact ARC700 cores");
        A(94, "Tensilica Xtensa"); A(95, "Alphamosaic VideoCore"); A(96, "Thompson Multimedia"); A(97, "NatSemi 32k");
        A(98, "Tenor Network TPC"); A(99, "Trebia SNP 1000"); A(100, "STMicroelectronics ST200"); A(101, "Ubicom IP2022");
        A(102, "MAX Processor"); A(103, "NatSemi CompactRISC"); A(104, "Fujitsu F2MC16"); A(105, "TI msp430");
        A(106, "Analog Devices Blackfin"); A(107, "S1C33 Family of Seiko Epson"); A(108, "Sharp embedded");
        A(109, "Arca RISC"); A(110, "PKU-Unity Ltd."); A(111, "eXcess: 16/32/64-bit"); A(112, "Icera Deep Execution Processor");
        A(113, "Altera Nios II"); A(114, "NatSemi CRX"); A(115, "Motorola XGATE"); A(116, "Infineon C16x/XC16x");
        A(117, "Renesas M16C series"); A(118, "Microchip dsPIC30F"); A(119, "Freescale RISC core"); A(120, "Renesas M32C series");
        A(131, "Altium TSK3000 core"); A(132, "Freescale RS08"); A(134, "Cyan Technology eCOG2"); A(135, "Sunplus S+core7 RISC");
        A(136, "New Japan Radio (NJR) 24-bit DSP"); A(137, "Broadcom VideoCore III"); A(138, "LatticeMico32");
        A(139, "Seiko Epson C17 family"); A(140, "TI TMS320C6000 DSP family"); A(141, "TI TMS320C2000 DSP family");
        A(142, "TI TMS320C55x DSP family"); A(144, "TI Programmable Realtime Unit"); A(160, "STMicroelectronics 64bit VLIW DSP");
        A(161, "Cypress M8C"); A(162, "Renesas R32C series"); A(163, "NXP TriMedia family"); A(164, "QUALCOMM DSP6");
        A(165, "Intel 8051 and variants"); A(166, "STMicroelectronics STxP7x family"); A(167, "Andes embedded RISC");
        A(168, "Cyan eCOG1X family"); A(169, "Dallas MAXQ30"); A(170, "New Japan Radio (NJR) 16-bit DSP");
        A(171, "M2000 Reconfigurable RISC"); A(172, "Cray NV2 vector architecture"); A(173, "Renesas RX family");
        A(174, "META"); A(175, "MCST Elbrus"); A(176, "Cyan Technology eCOG16 family"); A(177, "NatSemi CompactRISC");
        A(178, "Freescale Extended Time Processing Unit"); A(179, "Infineon SLE9X"); A(180, "Intel L1OM");
        A(181, "Intel K1OM"); A(183, "ARM aarch64"); A(185, "Atmel 32-bit family"); A(186, "STMicroeletronics STM8 8-bit");
        A(187, "Tilera TILE64"); A(188, "Tilera TILEPro"); A(189, "Xilinx MicroBlaze 32-bit RISC"); A(190, "NVIDIA CUDA architecture");
        A(191, "Tilera TILE-Gx"); A(195, "Synopsys ARCv2/HS3x/HS4x cores"); A(197, "Renesas RL78 family");
        A(199, "Renesas 78K0R"); A(200, "Freescale 56800EX"); A(201, "Beyond BA1"); A(202, "Beyond BA2");
        A(203, "XMOS xCORE"); A(204, "Microchip 8-bit PIC(r)"); A(210, "KM211 KM32"); A(211, "KM211 KMX32");
        A(212, "KM211 KMX16"); A(213, "KM211 KMX8"); A(214, "KM211 KVARC"); A(215, "Paneve CDP");
        A(216, "Cognitive Smart Memory"); A(217, "iCelero CoolEngine"); A(218, "Nanoradio Optimized RISC");
        A(219, "CSR Kalimba architecture family"); A(220, "Zilog Z80"); A(221, "Controls and Data Services VISIUMcore processor");
        A(222, "FTDI Chip FT32 high performance 32-bit RISC architecture"); A(223, "Moxie processor family");
        A(224, "AMD GPU architecture"); A(244, "Lanai 32-bit processor"); A(245, "CEVA Processor Architecture Family");
        A(246, "CEVA X2 Processor Family"); A(247, "eBPF"); A(248, "Graphcore Intelligent Processing Unit");
        A(249, "Imagination Technologies"); A(250, "Netronome Flow Processor"); A(251, "NEC Vector Engine");
        A(252, "C-SKY processor family"); A(253, "Synopsys ARCv3 64-bit ISA/HS6x cores"); A(254, "MOS Technology MCS 6502 processor");
        A(255, "Synopsys ARCv3 32-bit"); A(256, "Kalray VLIW core of the MPPA family"); A(257, "WDC 65816/65C816");
        A(258, "LoongArch"); A(259, "ChipON KungFu32");
        return d;
    }

    private static readonly string?[] ElfOsAbi =
    {
        "SYSV", "HP-UX", "NetBSD", "GNU/Linux", "GNU/Hurd", "86Open", "Solaris", "Monterey", "IRIX", "FreeBSD",
        "Tru64", "Novell Modesto", "OpenBSD", "OpenVMS", "HP NonStop Kernel", "AROS Research Operating System",
        "FenixOS", "Nuxi CloudABI",
    };

    private static string ElfMachineName(int machine, uint flags, bool hasFlags, out bool noComma)
    {
        noComma = ElfNoComma.Contains(machine);
        // The e_flags text only exists when the header is long enough to hold e_flags.
        switch (machine)
        {
            case 8:
                if (!hasFlags) return "MIPS";
                noComma = true;
                return "MIPS, " + MipsArch(flags);
            case 10: return "MIPS, MIPS (deprecated)";
            case 21:
                if (!hasFlags) return "64-bit PowerPC or cisco 7500";
                return "64-bit PowerPC or cisco 7500, " + ((flags & 3) switch { 1 => "Power ELF V1 ABI", 2 => "OpenPOWER ELF V2 ABI", _ => "Unspecified or Power ELF V1 ABI" });
            case 43: return hasFlags ? "SPARC V9, total store ordering" : "SPARC V9";
            case 243:
            {
                if (!hasFlags) return "UCB RISC-V";
                var parts = new List<string> { "UCB RISC-V" };
                if ((flags & 1) != 0) parts.Add("RVC");
                parts.Add((flags & 6) switch { 0 => "soft-float ABI", 2 => "single-float ABI", 4 => "double-float ABI", _ => "quad-float ABI" });
                if ((flags & 8) != 0) parts.Add("RVE");
                if ((flags & 0x10) != 0) parts.Add("TSO");
                return string.Join(", ", parts);
            }
        }
        if (ElfMachines.TryGetValue(machine, out var name)) return name;
        noComma = true;
        return $"*unknown arch 0x{machine:x}*";
    }

    private static string MipsArch(uint flags) => (flags >> 28) switch
    {
        0 => "MIPS-I", 1 => "MIPS-II", 2 => "MIPS-III", 3 => "MIPS-IV", 4 => "MIPS-V", 5 => "MIPS32", 6 => "MIPS64",
        7 => "MIPS32 rel2", 8 => "MIPS64 rel2", 9 => "MIPS32 rel6", 10 => "MIPS64 rel6", _ => "MIPS-I",
    };

    private static FileKind? TryElf(Source src)
    {
        byte[] h = src.Head;
        if (h.Length < 6 || h[0] != 0x7F || h[1] != 'E' || h[2] != 'L' || h[3] != 'F') return null;   // file-5.45 names class+data from 6 bytes
        int cls = h[4], data = h[5];
        if ((cls != 1 && cls != 2) || (data != 1 && data != 2)) return null;
        bool is64 = cls == 2, le = data == 1;
        int hdr = is64 ? 64 : 52;

        int type = h.Length >= 18 ? U16(h, 16, le) : -1;
        int machine = h.Length >= 20 ? U16(h, 18, le) : -1;
        uint version = h.Length >= 24 ? U32(h, 20, le) : 0;
        bool hasFlags = h.Length >= (is64 ? 52 : 40);
        uint flags = hasFlags ? U32(h, is64 ? 48 : 36, le) : 0;
        string? osabi = h.Length > 7 && h[7] < ElfOsAbi.Length ? ElfOsAbi[h[7]] : null;

        // Program headers (read first: whether an ET_DYN is a PIE executable depends on them).
        ulong phoff = 0, shoff = 0;
        int phentsize = 0, phnum = 0, shentsize = 0, shnum = 0, shstrndx = 0;
        if (src.Size > hdr && h.Length >= hdr)
        {
            phoff = is64 ? U64(h, 32, le) : U32(h, 28, le);
            shoff = is64 ? U64(h, 40, le) : U32(h, 32, le);
            phentsize = U16(h, is64 ? 54 : 42, le); phnum = U16(h, is64 ? 56 : 44, le);
            shentsize = U16(h, is64 ? 58 : 46, le); shnum = U16(h, is64 ? 60 : 48, le); shstrndx = U16(h, is64 ? 62 : 50, le);
        }
        bool pie = false, hasDynamic = false;
        string? interp = null;
        string? buildId = null, abiTag = null;
        var notes = new List<(long Off, long Size)>();
        if (phnum > 0 && phentsize >= (is64 ? 56 : 32))
        {
            for (int i = 0; i < phnum && i < 4096; i++)
            {
                byte[]? ph = src.ReadAt((long)phoff + (long)i * phentsize, is64 ? 56 : 32);
                if (ph is null) break;
                uint ptype = U32(ph, 0, le);
                long off = is64 ? (long)U64(ph, 8, le) : U32(ph, 4, le);
                long filesz = is64 ? (long)U64(ph, 32, le) : U32(ph, 16, le);
                if (ptype == 3 && interp is null)
                {
                    byte[]? s = src.ReadAt(off, (int)Math.Min(filesz, 4096));
                    if (s is not null)
                    {
                        int z = Array.IndexOf(s, (byte)0);
                        interp = Encoding.Latin1.GetString(s, 0, z < 0 ? s.Length : z);
                    }
                }
                else if (ptype == 2)
                {
                    hasDynamic = true;
                    pie |= DynamicHasPieFlag(src, off, filesz, is64, le);
                }
                else if (ptype == 4) notes.Add((off, filesz));
            }
            foreach (var (off, sz) in notes) ParseNotes(src, off, sz, le, ref buildId, ref abiTag);
        }

        string typeText = type switch
        {
            0 => "no file type", 1 => "relocatable", 2 => "executable",
            3 => pie ? "pie executable" : "shared object", 4 => "core file",
            >= 0xFF00 => "processor-specific", _ => string.Empty,
        };
        string mime = type switch
        {
            1 => "application/x-object", 2 => "application/x-executable",
            3 => pie ? "application/x-pie-executable" : "application/x-sharedlib",
            4 => "application/x-coredump", _ => "application/octet-stream",
        };

        var sb = new StringBuilder("ELF ");
        sb.Append(is64 ? "64" : "32").Append("-bit ").Append(le ? "LSB" : "MSB");
        if (type >= 0 && typeText.Length > 0) sb.Append(' ').Append(typeText);
        string tail = osabi is null ? string.Empty : $"({osabi})";
        if (machine >= 0)
        {
            string mname = ElfMachineName(machine, flags, hasFlags, out bool noComma);
            sb.Append(type >= 0 && typeText.Length > 0 ? ", " : " ").Append(mname);
            if (version == 1) sb.Append(noComma ? " " : ", ").Append("version 1").Append(tail.Length > 0 ? " " + tail : string.Empty);
            else if (tail.Length > 0) sb.Append(", ").Append(tail);
        }
        else if (tail.Length > 0) sb.Append(' ').Append(tail);

        if (src.Size > hdr && type != 4)
        {
            if (phnum == 0 && type != 1) sb.Append(", no program header");
            else if (phnum > 0)
            {
                if (hasDynamic)
                {
                    sb.Append(", dynamically linked");
                    if (interp is not null) sb.Append(", interpreter ").Append(interp);
                }
                else sb.Append(", statically linked");
                // libmagic reads the build id / ABI tag only when the section headers it goes with are present.
                bool sectionsMissing = shoff != 0 && shnum > 0 && (long)shoff + (long)shnum * shentsize > src.Size;
                if (buildId is not null && !sectionsMissing) sb.Append(", BuildID[sha1]=").Append(buildId);
                if (abiTag is not null && !sectionsMissing) sb.Append(", for ").Append(abiTag);
            }
            AppendElfSections(sb, src, shoff, shentsize, shnum, shstrndx, is64, le, type);
        }
        return new FileKind(sb.ToString(), mime, "binary");
    }

    private static bool DynamicHasPieFlag(Source src, long off, long size, bool is64, bool le)
    {
        int entry = is64 ? 16 : 8;
        long count = Math.Min(size / entry, 4096);
        byte[]? dyn = src.ReadAt(off, (int)(count * entry));
        if (dyn is null) return false;
        for (int i = 0; i < count; i++)
        {
            long tag = is64 ? (long)U64(dyn, i * entry, le) : U32(dyn, i * entry, le);
            long val = is64 ? (long)U64(dyn, i * entry + 8, le) : U32(dyn, i * entry + 4, le);
            if (tag == 0) break;
            if (tag == 0x6ffffffb && (val & 0x08000000) != 0) return true;   // DT_FLAGS_1 & DF_1_PIE
        }
        return false;
    }

    private static void ParseNotes(Source src, long off, long size, bool le, ref string? buildId, ref string? abiTag)
    {
        byte[]? n = src.ReadAt(off, (int)Math.Min(size, 1 << 16));
        if (n is null) return;
        int p = 0;
        while (p + 12 <= n.Length)
        {
            int namesz = (int)U32(n, p, le), descsz = (int)U32(n, p + 4, le);
            uint ntype = U32(n, p + 8, le);
            int nameOff = p + 12, descOff = nameOff + ((namesz + 3) & ~3);
            int next = descOff + ((descsz + 3) & ~3);
            if (namesz < 0 || descsz < 0 || next > n.Length + 3 || descOff + descsz > n.Length) break;
            bool gnu = namesz == 4 && n[nameOff] == 'G' && n[nameOff + 1] == 'N' && n[nameOff + 2] == 'U';
            if (gnu && ntype == 3 && buildId is null && descsz == 20)
                buildId = Convert.ToHexString(n, descOff, descsz).ToLowerInvariant();
            else if (gnu && ntype == 1 && abiTag is null && descsz >= 16)
            {
                uint os = U32(n, descOff, le);
                string osName = os switch { 0 => "Linux", 1 => "Hurd", 2 => "Solaris", 3 => "kFreeBSD", 4 => "kNetBSD", _ => null! };
                if (osName is not null)
                    abiTag = $"GNU/{osName} {U32(n, descOff + 4, le)}.{U32(n, descOff + 8, le)}.{U32(n, descOff + 12, le)}";
            }
            p = next;
        }
    }

    private static void AppendElfSections(StringBuilder sb, Source src, ulong shoff, int shentsize, int shnum,
        int shstrndx, bool is64, bool le, int type)
    {
        if (shoff == 0 || shnum == 0)
        {
            if (shnum == 0 && type != 1) sb.Append(", no section header");
            return;
        }
        long table = (long)shoff;
        if (shentsize < (is64 ? 64 : 40) || table + (long)shnum * shentsize > src.Size)
        {
            sb.Append(", missing section headers at ").Append(shoff.ToString(CultureInfo.InvariantCulture));
            return;
        }
        byte[]? sh = src.ReadAt(table, shnum * shentsize);
        if (sh is null) return;
        bool symtab = false, debugInfo = false;
        // Section name string table (for .debug_info).
        byte[]? names = null;
        if (shstrndx < shnum)
        {
            int e = shstrndx * shentsize;
            long noff = is64 ? (long)U64(sh, e + 24, le) : U32(sh, e + 16, le);
            long nsz = is64 ? (long)U64(sh, e + 32, le) : U32(sh, e + 20, le);
            if (nsz > 0 && nsz < (1 << 24)) names = src.ReadAt(noff, (int)nsz);
        }
        for (int i = 0; i < shnum; i++)
        {
            int e = i * shentsize;
            uint stype = U32(sh, e + 4, le);
            if (stype == 2) symtab = true;
            if (names is not null)
            {
                uint nameIdx = U32(sh, e, le);
                if (nameIdx + 12 <= names.Length && Encoding.Latin1.GetString(names, (int)nameIdx, 12) == ".debug_info")
                    debugInfo = true;
            }
        }
        if (debugInfo) sb.Append(", with debug_info");
        sb.Append(symtab ? ", not stripped" : ", stripped");
    }

    // ── scripts ─────────────────────────────────────────────────────────────────────────────────

    private sealed record ScriptKind(string Name, string Mime, ScriptStyle Style = ScriptStyle.Standard);

    private enum ScriptStyle { Standard, Perl, Node }

    private static readonly Regex PythonPath = new(@"^/usr(/local)?/bin/python[0-9.]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PhpPath = new(@"^/usr(/local)?/bin/php[0-9.]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LuaPath = new(@"^/usr(/local)?/bin/lua\w*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static ScriptKind? ScriptByPath(string path)
    {
        const string sh = "text/x-shellscript";
        switch (path)
        {
            case "/bin/sh": return new("POSIX shell script", sh);
            case "/bin/bash" or "/usr/bin/bash" or "/usr/local/bin/bash": return new("Bourne-Again shell script", sh);
            case "/bin/ksh": return new("Korn shell script", sh);
            case "/bin/zsh" or "/usr/bin/zsh" or "/usr/local/bin/zsh": return new("Paul Falstad's zsh script", sh);
            case "/bin/csh": return new("C shell script", sh);
            case "/bin/tcsh" or "/usr/bin/tcsh" or "/usr/local/bin/tcsh": return new("Tenex C shell script", sh);
            case "/bin/ash" or "/usr/bin/ash" or "/usr/local/bin/ash": return new("Neil Brown's ash script", sh);
            case "/usr/bin/fish" or "/usr/local/bin/fish": return new("fish shell script", sh);
            case "/bin/rc": return new("Plan 9 rc shell script", "text/plain");
            case "/bin/perl" or "/usr/bin/perl" or "/usr/local/bin/perl": return new("Perl script", "text/x-perl", ScriptStyle.Perl);
            case "/usr/bin/ruby" or "/usr/local/bin/ruby": return new("Ruby script", "text/x-ruby");
            case "/bin/node" or "/usr/bin/node" or "/bin/nodejs" or "/usr/bin/nodejs":
                return new("Node.js script", "application/javascript", ScriptStyle.Node);
            case "/bin/awk" or "/usr/bin/awk": return new("awk script", "text/x-awk");
            case "/bin/gawk" or "/usr/bin/gawk" or "/usr/local/bin/gawk": return new("GNU awk script", "text/x-gawk");
            case "/bin/nawk" or "/usr/bin/nawk" or "/usr/local/bin/nawk": return new("new awk script", "text/x-nawk");
            case "/usr/bin/tclsh" or "/usr/bin/wish" or "/usr/local/bin/wish": return new("Tcl/Tk script", "text/x-tcl");
            case "/usr/local/bin/tclsh": return new("Tcl script", "text/x-tcl");
        }
        if (PythonPath.IsMatch(path)) return new("Python script", "text/x-script.python");
        if (PhpPath.IsMatch(path)) return new("PHP script", "text/x-php");
        if (LuaPath.IsMatch(path)) return new("Lua script", "text/x-lua");
        return null;
    }

    private static readonly HashSet<string> SpacedBangKinds = new()
    {
        "POSIX shell script", "Bourne-Again shell script", "Korn shell script", "Paul Falstad's zsh script",
        "C shell script", "Tenex C shell script", "Neil Brown's ash script", "Plan 9 rc shell script",
        "awk script", "GNU awk script", "new awk script", "Perl script",
    };

    private static ScriptKind? ScriptByEnv(string name)
    {
        switch (name)
        {
            case "bash": return new("Bourne-Again shell script", "text/x-shellscript");
            case "zsh": return new("Paul Falstad's zsh script", "text/x-shellscript");
            case "fish": return new("fish shell script", "text/x-shellscript");
            case "ruby": return new("Ruby script", "text/x-ruby");
            case "node" or "nodejs": return new("Node.js script", "application/javascript", ScriptStyle.Node);
            case "tclsh": return new("Tcl script", "text/x-tcl");
            case "wish": return new("Tcl/Tk script", "text/x-tcl");
        }
        if (Regex.IsMatch(name, @"^python[0-9.]*$", RegexOptions.CultureInvariant)) return new("Python script", "text/x-script.python");
        if (Regex.IsMatch(name, @"^perl[0-9.]*$", RegexOptions.CultureInvariant)) return new("Perl script", "text/x-perl", ScriptStyle.Perl);
        if (Regex.IsMatch(name, @"^lua\w*$", RegexOptions.CultureInvariant)) return new("Lua script", "text/x-lua");
        return null;
    }

    private static FileKind DescribeScript(byte[] h, TextInfo info)
    {
        int nl = Array.FindIndex(h, 2, b => b == '\n' || b == '\r');
        // The interpreter rules only apply to a complete first line; a lone "#!/bin/sh" is the generic form.
        string rawLine = Encoding.Latin1.GetString(h, 2, (nl < 0 ? h.Length : nl) - 2);
        bool spaced = rawLine.Length > 0 && (rawLine[0] == ' ' || rawLine[0] == '\t');
        string line = rawLine.Trim(' ', '\t');
        string[] toks = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        ScriptKind? kind = null;
        if (nl >= 0 && toks.Length > 0)
        {
            kind = toks[0] == "/usr/bin/env"
                ? (toks.Length > 1 ? ScriptByEnv(toks[1]) : null)
                : ScriptByPath(toks[0]);
            // "#! /bin/x" (a space after the bang) only reaches libmagic's old-style shell / awk / perl rules.
            if (spaced && kind is not null && !SpacedBangKinds.Contains(kind.Name)) kind = null;
        }
        const string envPrefix = "/usr/bin/env ";
        string generic = line.StartsWith(envPrefix, StringComparison.Ordinal) ? line[envPrefix.Length..].Trim() : line;
        kind ??= new ScriptKind($"a {generic} script", "text/plain");
        return DescribeText(info, kind);
    }

    // ── text ────────────────────────────────────────────────────────────────────────────────────

    private sealed class TextInfo
    {
        public bool IsText;
        public bool AnyHigh, Utf8Valid = true, HasC1;      // any byte >= 0x80 / valid UTF-8 / any byte in 0x80..0x9F
        public bool Bom;
        public int Crlf, Cr, Lf, Nel;
        public int LongestLine;
        public bool Esc, Backspace;
        public bool IsUtf8 => AnyHigh && Utf8Valid;
        public string Charset => !AnyHigh ? "us-ascii" : Utf8Valid ? "utf-8" : HasC1 ? "unknown-8bit" : "iso-8859-1";
    }

    private static TextInfo ScanText(byte[] b)
    {
        var t = new TextInfo { IsText = true };
        int n = b.Length;
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c < 0x07 || (c > 0x0D && c < 0x20 && c != 0x1B) || c == 0x7F)
            {
                t.IsText = false;
                return t;
            }
            if (c >= 0x80)
            {
                t.AnyHigh = true;
                if (c < 0xA0) t.HasC1 = true;
            }
            else if (c == 0x1B) t.Esc = true;
            else if (c == 0x08) t.Backspace = true;
        }
        t.Utf8Valid = t.AnyHigh && IsValidUtf8(b);
        if (!t.AnyHigh) t.Utf8Valid = true;
        t.Bom = n > 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF && t.Utf8Valid;
        bool utf8 = t.IsUtf8;

        // Line terminators: libmagic does not look at the final byte for a lone CR.
        int lineLen = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c == '\r' && i + 1 < n && b[i + 1] == '\n') { t.Crlf++; i++; t.LongestLine = Math.Max(t.LongestLine, lineLen); lineLen = 0; continue; }
            if (c == '\r') { if (i < n - 1) t.Cr++; t.LongestLine = Math.Max(t.LongestLine, lineLen); lineLen = 0; continue; }
            if (c == '\n') { t.Lf++; t.LongestLine = Math.Max(t.LongestLine, lineLen); lineLen = 0; continue; }
            if (utf8 && c == 0xC2 && i + 1 < n && b[i + 1] == 0x85) { t.Nel++; i++; t.LongestLine = Math.Max(t.LongestLine, lineLen); lineLen = 0; continue; }
            if (utf8 && (c & 0xC0) == 0x80) continue;   // continuation byte: not a new character
            lineLen++;
        }
        t.LongestLine = Math.Max(t.LongestLine, lineLen);
        return t;
    }

    private static bool IsValidUtf8(byte[] b)
    {
        try { _ = new UTF8Encoding(false, true).GetCharCount(b); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static FileKind DescribeText(TextInfo t, ScriptKind? scriptName)
    {
        if (!t.IsText)
        {
            return scriptName is null
                ? Data
                : new FileKind($"{ScriptLabel(scriptName)} executable (binary data)", scriptName.Mime, "binary");
        }

        string enc = !t.AnyHigh ? "ASCII text"
            : t.Utf8Valid ? (t.Bom ? "Unicode text, UTF-8 (with BOM) text" : "Unicode text, UTF-8 text")
            : t.HasC1 ? "Non-ISO extended-ASCII text" : "ISO-8859 text";

        var extras = new List<string>();
        if (t.LongestLine > 300) extras.Add($"with very long lines ({t.LongestLine})");
        var terms = new List<string>();
        if (t.Crlf > 0) terms.Add("CRLF");
        if (t.Cr > 0) terms.Add("CR");
        if (t.Lf > 0) terms.Add("LF");
        if (t.Nel > 0) terms.Add("NEL");
        if (terms.Count == 0) extras.Add("with no line terminators");
        else if (!(terms.Count == 1 && terms[0] == "LF")) extras.Add($"with {string.Join(", ", terms)} line terminators");
        if (t.Esc) extras.Add("with escape sequences");
        if (t.Backspace) extras.Add("with overstriking");

        string suffix = extras.Count == 0 ? string.Empty : ", " + string.Join(", ", extras);
        string mime = "text/plain";
        string desc;
        if (scriptName is null)
        {
            desc = enc + suffix;
        }
        else
        {
            mime = scriptName.Mime;
            desc = scriptName.Style switch
            {
                ScriptStyle.Perl => "Perl script text executable",
                ScriptStyle.Node => $"Node.js script executable, {enc}{suffix}",
                _ => $"{scriptName.Name}, {enc} executable{suffix}",
            };
        }
        return new FileKind(desc, mime, t.Charset);
    }

    private static string ScriptLabel(ScriptKind k) => k.Name;
}
