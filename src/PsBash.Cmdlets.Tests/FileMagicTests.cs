using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>file</c> content classification (<see cref="FileMagic"/>) against file-5.45 / libmagic. Every
/// expected string is real oracle output (WSL Ubuntu 24.04: a 1115-file corpus of crafted headers,
/// real ELF/PE binaries, gzip/zip/png/jpeg/pdf files and text variants; 1088 matched byte for byte on
/// the first run, the rest are the documented gaps below).
/// </summary>
public class FileMagicTests
{
    private static byte[] B(params object[] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts)
        {
            switch (p)
            {
                case byte[] a: ms.Write(a); break;
                case string s: ms.Write(Encoding.Latin1.GetBytes(s)); break;
                case int i: ms.WriteByte((byte)i); break;
            }
        }
        return ms.ToArray();
    }

    private static byte[] Le16(int v) => new[] { (byte)v, (byte)(v >> 8) };
    private static byte[] Le32(long v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
    private static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] Be32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    private static byte[] Zeros(int n) => new byte[n];

    private static void Check(byte[] data, string type, string? mime = null, string? charset = null)
    {
        var k = FileMagic.ClassifyArray(data);
        Assert.Equal(type, k.Type);
        if (mime is not null) Assert.Equal(mime, k.Mime);
        if (charset is not null) Assert.Equal(charset, k.Encoding);
    }

    // ── very short / empty ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\0")]
    public void OneByte_IsVeryShort(string s) =>
        Check(Encoding.Latin1.GetBytes(s), "very short file (no magic)", "application/octet-stream", "binary");

    [Fact]
    public void Empty() => Check(Array.Empty<byte>(), "empty", "inode/x-empty", "binary");

    [Fact]
    public void TwoBytes_AreText() => Check(Encoding.ASCII.GetBytes("ab"), "ASCII text, with no line terminators");

    // ── ELF ─────────────────────────────────────────────────────────────────────────────────────

    private static byte[] Elf(int machine = 62, int osabi = 0, int type = 2, int bits = 2, bool le = true, int version = 1, int extra = 40) =>
        B("\u007fELF", bits, le ? 1 : 2, 1, osabi, Zeros(8), le ? Le16(type) : Be16(type), le ? Le16(machine) : Be16(machine),
          le ? Le32(version) : Be32(version), Zeros(extra));

    [Theory]
    [InlineData(2, 62, "ELF 64-bit LSB executable, x86-64, version 1 (SYSV)", "application/x-executable")]
    [InlineData(1, 62, "ELF 64-bit LSB relocatable, x86-64, version 1 (SYSV)", "application/x-object")]
    [InlineData(3, 62, "ELF 64-bit LSB shared object, x86-64, version 1 (SYSV)", "application/x-sharedlib")]
    [InlineData(4, 62, "ELF 64-bit LSB core file, x86-64, version 1 (SYSV)", "application/x-coredump")]
    [InlineData(0, 62, "ELF 64-bit LSB no file type, x86-64, version 1 (SYSV)", "application/octet-stream")]
    [InlineData(5, 62, "ELF 64-bit LSB x86-64, version 1 (SYSV)", "application/octet-stream")]
    [InlineData(2, 183, "ELF 64-bit LSB executable, ARM aarch64, version 1 (SYSV)", "application/x-executable")]
    [InlineData(2, 40, "ELF 64-bit LSB executable, ARM, version 1 (SYSV)", "application/x-executable")]
    [InlineData(2, 3, "ELF 64-bit LSB executable, Intel 80386, version 1 (SYSV)", "application/x-executable")]
    [InlineData(2, 299, "ELF 64-bit LSB executable, *unknown arch 0x12b* version 1 (SYSV)", "application/x-executable")]
    [InlineData(2, 144, "ELF 64-bit LSB executable, TI Programmable Realtime Unit version 1 (SYSV)", "application/x-executable")]
    public void Elf_Header(int type, int machine, string expected, string mime) =>
        Check(Elf(machine: machine, type: type), expected, mime, "binary");

    [Fact]
    public void Elf_OsAbi_AndVersion()
    {
        Check(Elf(osabi: 3), "ELF 64-bit LSB executable, x86-64, version 1 (GNU/Linux)");
        Check(Elf(osabi: 9), "ELF 64-bit LSB executable, x86-64, version 1 (FreeBSD)");
        Check(Elf(osabi: 18), "ELF 64-bit LSB executable, x86-64, version 1");
        Check(Elf(version: 2, bits: 1, machine: 3), "ELF 32-bit LSB executable, Intel 80386, (SYSV), no program header, no section header");
    }

    [Fact]
    public void Elf_32BitBigEndian_NoHeaders()
    {
        Check(Elf(machine: 8, bits: 1, le: false), "ELF 32-bit MSB executable, MIPS, MIPS-I version 1 (SYSV), no program header, no section header");
    }

    [Fact]
    public void Elf_TruncatedHeaders()
    {
        // 8 bytes: class, data and OS ABI only; 20 bytes: through the machine, no version yet.
        Check(B("\u007fELF", 2, 1, 1, 0), "ELF 64-bit LSB (SYSV)");
        Check(B("\u007fELF", 2, 1, 1, 0, Zeros(8), Le16(3), Le16(62)), "ELF 64-bit LSB shared object, x86-64, (SYSV)");
    }

    /// <summary>A small but complete dynamic PIE: PT_INTERP, PT_DYNAMIC (DT_FLAGS_1 = DF_1_PIE), PT_NOTE (build id + ABI tag), .symtab.</summary>
    private static byte[] ElfPie(bool symtab, bool shdrBeyondEof = false)
    {
        // layout: 64 header | 3 x 56 phdr (232) | interp @232 | dyn @272 (3 x 16) | note @320 | sections
        byte[] interp = B("/lib64/ld-linux-x86-64.so.2", 0);
        var ms = new MemoryStream();
        void W(byte[] b) => ms.Write(b);
        byte[] note = B(Le32(4), Le32(20), Le32(3), "GNU\0", Enumerable.Range(1, 20).Select(i => (byte)i).ToArray(),
                        Le32(4), Le32(16), Le32(1), "GNU\0", Le32(0), Le32(3), Le32(2), Le32(0));
        long shoff = shdrBeyondEof ? 100000 : 320 + note.Length;
        // e_ident, type=3 (DYN), machine 62, version 1, entry, phoff=64, shoff, flags, ehsize 64, phentsize 56, phnum 3, shentsize 64, shnum, shstrndx
        W(B("\u007fELF", 2, 1, 1, 0, Zeros(8), Le16(3), Le16(62), Le32(1), new byte[8], BitConverter.GetBytes(64L),
            BitConverter.GetBytes(shoff), Le32(0), Le16(64), Le16(56), Le16(3), Le16(64), Le16(symtab ? 2 : 1), Le16(0)));
        byte[] Ph(uint t, long off, long sz) => B(Le32(t), Le32(0), BitConverter.GetBytes(off), new byte[8], new byte[8], BitConverter.GetBytes(sz), BitConverter.GetBytes(sz), new byte[8]);
        W(Ph(3, 232, interp.Length)); W(Ph(2, 272, 48)); W(Ph(4, 320, note.Length));
        W(interp); W(Zeros(272 - 232 - interp.Length));
        W(B(BitConverter.GetBytes(0x6ffffffbL), BitConverter.GetBytes(0x08000000L), BitConverter.GetBytes(0L), BitConverter.GetBytes(0L)));
        W(Zeros(16));
        W(note);
        if (!shdrBeyondEof)
        {
            W(Zeros(64));                                                // null section
            W(B(Le32(0), Le32(symtab ? 2 : 1), Zeros(56)));              // .symtab (type 2) or PROGBITS
        }
        return ms.ToArray();
    }

    [Fact]
    public void Elf_Pie_Dynamic_Interpreter_BuildId_AbiTag_NotStripped() =>
        Check(ElfPie(symtab: true),
            "ELF 64-bit LSB pie executable, x86-64, version 1 (SYSV), dynamically linked, interpreter /lib64/ld-linux-x86-64.so.2, " +
            "BuildID[sha1]=0102030405060708090a0b0c0d0e0f1011121314, for GNU/Linux 3.2.0, not stripped",
            "application/x-pie-executable");

    [Fact]
    public void Elf_Pie_NoSymtab_IsStripped() =>
        Assert.EndsWith(", for GNU/Linux 3.2.0, stripped", FileMagic.ClassifyArray(ElfPie(symtab: false)).Type);

    [Fact]
    public void Elf_SectionHeadersPastTheEnd_AreReported() =>
        Check(ElfPie(symtab: true, shdrBeyondEof: true),
            "ELF 64-bit LSB pie executable, x86-64, version 1 (SYSV), dynamically linked, interpreter /lib64/ld-linux-x86-64.so.2, missing section headers at 100000");

    // ── PE / MS-DOS ─────────────────────────────────────────────────────────────────────────────

    private static byte[] Pe(int machine = 0x8664, int nsec = 3, int chars = 0x22, int magic = 0x20b, int subsys = 3, bool clr = false)
    {
        int optSize = magic == 0x20b ? 240 : 224;
        var opt = new byte[optSize];
        opt[0] = (byte)magic; opt[1] = (byte)(magic >> 8);
        opt[68] = (byte)subsys; opt[69] = (byte)(subsys >> 8);
        int dd = magic == 0x20b ? 112 : 96;
        if (clr) { opt[dd + 14 * 8 + 1] = 0x20; opt[dd + 14 * 8 + 4] = 0x48; }
        var dos = new byte[0x80];
        dos[0] = (byte)'M'; dos[1] = (byte)'Z'; dos[0x3C] = 0x80;
        return B(dos, "PE\0\0", Le16(machine), Le16(nsec), Zeros(12), Le16(optSize), Le16(chars), opt, Zeros(nsec * 40));
    }

    [Theory]
    [InlineData(0x8664, 3, 0x22, 0x20b, 3, false, "PE32+ executable (console) x86-64, for MS Windows, 3 sections")]
    [InlineData(0x8664, 8, 0x2022, 0x20b, 3, false, "PE32+ executable (DLL) (console) x86-64, for MS Windows, 8 sections")]
    [InlineData(0x8664, 8, 0x22, 0x20b, 2, false, "PE32+ executable (GUI) x86-64, for MS Windows, 8 sections")]
    [InlineData(0x14c, 3, 0x102, 0x10b, 3, false, "PE32 executable (console) Intel 80386, for MS Windows, 3 sections")]
    [InlineData(0x14c, 3, 0x2102, 0x10b, 3, true, "PE32 executable (DLL) (console) Intel 80386 Mono/.Net assembly, for MS Windows, 3 sections")]
    [InlineData(0xaa64, 1, 0x22, 0x20b, 10, false, "PE32+ executable (EFI application) Aarch64, for MS Windows")]
    [InlineData(0x1c4, 2, 0x22, 0x20b, 1, false, "PE32+ executable (native) ARMv7 Thumb, for MS Windows, 2 sections")]
    [InlineData(0x0, 3, 0x22, 0x20b, 3, false, "PE32+ executable (console), for MS Windows, 3 sections")]
    public void Pe_Headers(int machine, int nsec, int chars, int magic, int subsys, bool clr, string expected) =>
        Check(Pe(machine, nsec, chars, magic, subsys, clr), expected, "application/vnd.microsoft.portable-executable", "binary");

    [Fact]
    public void Mz_WithoutPeHeader_IsMsDos()
    {
        Check(B("MZ", Zeros(100)), "MS-DOS executable, MZ for MS-DOS", "application/x-dosexec", "binary");
        // A relocation table at 0x40 (every PE stub) with the PE header outside the file: no ", MZ for MS-DOS".
        var stub = new byte[64];
        stub[0] = (byte)'M'; stub[1] = (byte)'Z'; stub[0x18] = 0x40; stub[0x3C] = 0x80;
        Check(stub, "MS-DOS executable", "application/octet-stream", "binary");
    }

    [Fact]
    public void Mz_TooShort_IsNotAnExecutable() => Check(B("MZ", 0x90, 0x00, 0x03, 0x00), "data");

    // ── gzip ────────────────────────────────────────────────────────────────────────────────────

    private static byte[] Gz(int flags = 0, long mtime = 0, int xfl = 0, int os = 3, byte[]? extra = null, string? name = null, int isize = 6, int method = 8)
    {
        var parts = new List<object> { Zeros(0), 0x1F, 0x8B, method, flags, Le32(mtime), xfl, os };
        if ((flags & 4) != 0) { parts.Add(Le16(extra!.Length)); parts.Add(extra); }
        if ((flags & 8) != 0) { parts.Add(name!); parts.Add(0); }
        parts.Add("x"); parts.Add(Le32(0)); parts.Add(Le32(isize));
        return B(parts.ToArray());
    }

    [Fact]
    public void Gzip_Variants()
    {
        Check(Gz(), "gzip compressed data, from Unix, original size modulo 2^32 6", "application/gzip", "binary");
        Check(Gz(os: 0), "gzip compressed data, from FAT filesystem (MS-DOS, OS/2, NT), original size modulo 2^32 6");
        Check(Gz(os: 11), "gzip compressed data, from NTFS filesystem (NT), original size modulo 2^32 6");
        Check(Gz(os: 255), "gzip compressed data, original size modulo 2^32 6");
        Check(Gz(xfl: 2), "gzip compressed data, max compression, from Unix, original size modulo 2^32 6");
        Check(Gz(xfl: 4), "gzip compressed data, max speed, from Unix, original size modulo 2^32 6");
        Check(Gz(flags: 8, name: "a.txt", mtime: 1700000000), "gzip compressed data, was \"a.txt\", last modified: Tue Nov 14 22:13:20 2023, from Unix, original size modulo 2^32 6");
        Check(Gz(mtime: 1), "gzip compressed data, last modified: Thu Jan  1 00:00:01 1970, from Unix, original size modulo 2^32 6");
        Check(Gz(flags: 1), "gzip compressed data, ASCII, from Unix, original size modulo 2^32 6");
        Check(Gz(flags: 2), "gzip compressed data, has CRC, from Unix, original size modulo 2^32 6");
        Check(Gz(flags: 16), "gzip compressed data, has comment, from Unix, original size modulo 2^32 6");
        Check(Gz(flags: 4, extra: new byte[] { 1, 2 }), "gzip compressed data, extra field, from Unix, original size modulo 2^32 6");
        // a name behind an extra field is not read (libmagic only looks at offset 10)
        Check(Gz(flags: 12, extra: new byte[] { 1, 2 }, name: "n"), "gzip compressed data, extra field, from Unix, original size modulo 2^32 6");
        Check(Gz(method: 7), "gzip compressed data, reserved method, from Unix, original size modulo 2^32 6");
        Check(Gz(isize: 0), "gzip compressed data, from Unix, original size modulo 2^32 0");
        Check(Gz(isize: -2), "gzip compressed data, from Unix, original size modulo 2^32 4294967294");
    }

    [Fact]
    public void Gzip_Truncated()
    {
        Check(Gz()[..3], "data");
        Check(Gz()[..4], "gzip compressed data");
        Check(Gz()[..9], "gzip compressed data");
        Check(Gz()[..10], "gzip compressed data, from Unix, original size modulo 2^32 50331648");
    }

    // ── zip ─────────────────────────────────────────────────────────────────────────────────────

    private static byte[] ZipLocal(int ver = 20, int method = 8, int pad = 80) =>
        B("PK\u0003\u0004", Le16(ver), Le16(0), Le16(method), Le16(0), Le16(0x21), Le32(0), Le32(0), Le32(0), Le16(5), Le16(0), "a.txt", Zeros(pad));

    [Theory]
    [InlineData(20, 8, "Zip archive data, at least v2.0 to extract, compression method=deflate")]
    [InlineData(10, 0, "Zip archive data, at least v1.0 to extract, compression method=store")]
    [InlineData(45, 9, "Zip archive data, at least v4.5 to extract, compression method=deflate64")]
    [InlineData(46, 12, "Zip archive data, at least v4.6 to extract, compression method=bzip2")]
    [InlineData(63, 14, "Zip archive data, at least v6.3 to extract, compression method=lzma")]
    [InlineData(255, 93, "Zip archive data, at least v25.5 to extract, compression method=Zstd")]
    [InlineData(20, 2, "Zip archive data, at least v2.0 to extract, compression method=[0x2]")]
    [InlineData(20, 100, "Zip archive data, at least v2.0 to extract, compression method=[0x64]")]
    [InlineData(20, 99, "Zip archive data, at least v2.0 to extract, compression method=AES Encrypted")]
    public void Zip_VersionAndMethod(int ver, int method, string expected) =>
        Check(ZipLocal(ver, method), expected, "application/zip", "binary");

    [Fact]
    public void Zip_NeedsFiftyBytes_Empty_IsEmpty()
    {
        Check(ZipLocal(pad: 15), "Zip archive data, at least v2.0 to extract, compression method=deflate");   // exactly 50 bytes
        Check(ZipLocal(pad: 14), "data");
        Check(B("PK\u0005\u0006", Zeros(18)), "Zip archive data (empty)", "application/zip", "binary");
        Check(B("PK\u0005\u0006"), "Zip archive data (empty)");
    }

    // ── PNG ─────────────────────────────────────────────────────────────────────────────────────

    private static byte[] Png(int w, int h, int depth, int ctype, int inter = 0) =>
        B(0x89, "PNG\r\n\u001a\n", Be32(13), "IHDR", Be32(w), Be32(h), depth, ctype, 0, 0, inter);

    [Theory]
    [InlineData(16, 16, 8, 2, 0, "PNG image data, 16 x 16, 8-bit/color RGB, non-interlaced")]
    [InlineData(4, 3, 8, 0, 0, "PNG image data, 4 x 3, 8-bit grayscale, non-interlaced")]
    [InlineData(5, 5, 8, 3, 0, "PNG image data, 5 x 5, 8-bit colormap, non-interlaced")]
    [InlineData(7, 9, 8, 4, 0, "PNG image data, 7 x 9, 8-bit gray+alpha, non-interlaced")]
    [InlineData(100, 50, 8, 6, 0, "PNG image data, 100 x 50, 8-bit/color RGBA, non-interlaced")]
    [InlineData(2, 2, 16, 2, 0, "PNG image data, 2 x 2, 16-bit/color RGB, non-interlaced")]
    [InlineData(8, 8, 8, 2, 1, "PNG image data, 8 x 8, 8-bit/color RGB, interlaced")]
    [InlineData(16, 8, 8, 1, 0, "PNG image data, 16 x 8, 8-bit non-interlaced")]
    public void Png_Ihdr(int w, int h, int depth, int ctype, int inter, string expected) =>
        Check(Png(w, h, depth, ctype, inter), expected, "image/png", "binary");

    [Fact]
    public void Png_RequiresTheIhdrChunkHeader_AndZeroFillsATruncatedIhdr()
    {
        Check(B(0x89, "PNG\r\n\u001a\n"), "data");                                           // signature only
        Check(B(0x89, "PNG\r\n\u001a\n", Be32(12), "IHDR", Zeros(13)), "data");               // wrong chunk length
        Check(B(0x89, "PNG\r\n\u001a\n", Be32(13), "IHDR"), "PNG image data, 0 x 0, 0-bit grayscale, non-interlaced");
        Check(B(0x89, "PNG\r\n\u001a\n", Be32(13), "IHDR", Be32(16)), "PNG image data, 16 x 0, 0-bit grayscale, non-interlaced");
    }

    // ── JPEG ────────────────────────────────────────────────────────────────────────────────────

    private static byte[] Seg(int m, byte[] d) => B(0xFF, m, Be16(d.Length + 2), d);
    private static byte[] Jfif(int units, int dx, int dy, int minor = 1) =>
        Seg(0xE0, B("JFIF\0", 1, minor, units, Be16(dx), Be16(dy), 0, 0));
    private static byte[] Sof(int m, int prec, int h, int w, int nc) =>
        Seg(m, B(prec, Be16(h), Be16(w), nc, new byte[nc * 3]));

    [Fact]
    public void Jpeg_Jfif_AndFrame()
    {
        var dqt = Seg(0xDB, B(0, Zeros(64)));
        Check(B(0xFF, 0xD8, Jfif(1, 72, 72), dqt, Sof(0xC0, 8, 480, 640, 3), 0xFF, 0xD9),
            "JPEG image data, JFIF standard 1.01, resolution (DPI), density 72x72, segment length 16, baseline, precision 8, 640x480, components 3",
            "image/jpeg", "binary");
        Check(B(0xFF, 0xD8, Jfif(0, 1, 1, 2), dqt, Sof(0xC0, 8, 1, 1, 3)),
            "JPEG image data, JFIF standard 1.02, aspect ratio, density 1x1, segment length 16, baseline, precision 8, 1x1, components 3");
        Check(B(0xFF, 0xD8, Jfif(2, 300, 300, 0), dqt, Sof(0xC2, 8, 100, 200, 3)),
            "JPEG image data, JFIF standard 1.00, resolution (DPCM), density 300x300, segment length 16, progressive, precision 8, 200x100, components 3");
        Check(B(0xFF, 0xD8, Jfif(1, 72, 72), dqt, Sof(0xC1, 8, 10, 20, 1)),
            "JPEG image data, JFIF standard 1.01, resolution (DPI), density 72x72, segment length 16, extended sequential, precision 8, 20x10, components 1");
    }

    [Fact]
    public void Jpeg_WithoutJfif_AndBare()
    {
        var dqt = Seg(0xDB, B(0, Zeros(64)));
        Check(B(0xFF, 0xD8, dqt, Sof(0xC0, 8, 33, 44, 3), 0xFF, 0xD9), "JPEG image data, baseline, precision 8, 44x33, components 3");
        Check(B(0xFF, 0xD8, Seg(0xFE, B("hi")), dqt, Sof(0xC0, 8, 5, 6, 3)), "JPEG image data, baseline, precision 8, 6x5, components 3");
        Check(B(0xFF, 0xD8, dqt, 0xFF, 0xD9), "JPEG image data");
        Check(B(0xFF, 0xD8, Jfif(1, 72, 72)), "JPEG image data, JFIF standard 1.01, resolution (DPI), density 72x72, segment length 16");
        Check(B(0xFF, 0xD8, 0xFF, 0xDB), "JPEG image data", "image/jpeg", "iso-8859-1");
    }

    [Fact]
    public void Jpeg_NeedsFourBytes() => Check(B(0xFF, 0xD8, 0xFF), "ISO-8859 text, with no line terminators");

    // ── PDF ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("%PDF-1.4\n%EOF\n", "PDF document, version 1.4")]
    [InlineData("%PDF-2.0\n", "PDF document, version 2.0")]
    [InlineData("%PDF-1.7", "PDF document, version 1.7")]
    [InlineData("%PDF-1.", "PDF document, version 1")]
    [InlineData("%PDF-1", "PDF document, version 1")]
    [InlineData("%PDF-", "PDF document")]
    [InlineData("%PDF-1.4\n<</Type/Pages/Count 5>>\n<</Type/Pages/Count 2>>\n", "PDF document, version 1.4, 5 page(s)")]
    [InlineData("%PDF-1.4\n<</Count 7>>\n", "PDF document, version 1.4, 7 page(s)")]
    [InlineData("%PDF-1.4\n<</Type/Pages/Count 0>>\n", "PDF document, version 1.4, 0 page(s)")]
    [InlineData("%PDF-1.4\n<</Type/Page>>\n", "PDF document, version 1.4")]
    public void Pdf(string text, string expected) =>
        Check(Encoding.Latin1.GetBytes(text), expected, "application/pdf", "us-ascii");

    [Fact]
    public void Pdf_Charset_FollowsTheContent()
    {
        Check(B("%PDF-1.5\n%", 0xE2, 0xE3, 0xCF, 0xD3, "\n"), "PDF document, version 1.5", "application/pdf", "iso-8859-1");
        Check(B("%PDF-1.5\n", 0, 0), "PDF document, version 1.5", "application/pdf", "binary");
    }

    // ── scripts ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("#!/bin/sh", "POSIX shell script, ASCII text executable", "text/x-shellscript")]
    [InlineData("#!/bin/bash", "Bourne-Again shell script, ASCII text executable", "text/x-shellscript")]
    [InlineData("#!/usr/bin/env bash", "Bourne-Again shell script, ASCII text executable", "text/x-shellscript")]
    [InlineData("#!/usr/bin/env sh", "a sh script, ASCII text executable", "text/plain")]
    [InlineData("#!/bin/sh -e", "POSIX shell script, ASCII text executable", "text/x-shellscript")]
    [InlineData("#! /bin/sh", "POSIX shell script, ASCII text executable", "text/x-shellscript")]
    [InlineData("#!/usr/local/bin/bash", "Bourne-Again shell script, ASCII text executable", "text/x-shellscript")]
    [InlineData("#!/bin/zsh", "Paul Falstad's zsh script, ASCII text executable", "text/x-shellscript")]
    [InlineData("#!/bin/dash", "a /bin/dash script, ASCII text executable", "text/plain")]
    [InlineData("#!/usr/bin/python3", "Python script, ASCII text executable", "text/x-script.python")]
    [InlineData("#!/usr/bin/env python3", "Python script, ASCII text executable", "text/x-script.python")]
    [InlineData("#!/bin/python3", "a /bin/python3 script, ASCII text executable", "text/plain")]
    [InlineData("#!/usr/bin/perl", "Perl script text executable", "text/x-perl")]
    [InlineData("#!/usr/bin/env perl", "Perl script text executable", "text/x-perl")]
    [InlineData("#!/usr/bin/ruby", "Ruby script, ASCII text executable", "text/x-ruby")]
    [InlineData("#!/usr/bin/env node", "Node.js script executable, ASCII text", "application/javascript")]
    [InlineData("#!/usr/bin/awk -f", "awk script, ASCII text executable", "text/x-awk")]
    [InlineData("#!/usr/bin/foo", "a /usr/bin/foo script, ASCII text executable", "text/plain")]
    [InlineData("#!/usr/bin/env foo", "a foo script, ASCII text executable", "text/plain")]
    [InlineData("#!/usr/bin/env -S foo -x", "a -S foo -x script, ASCII text executable", "text/plain")]
    [InlineData("#!/bin/env sh", "a /bin/env sh script, ASCII text executable", "text/plain")]
    public void Shebang(string line, string expected, string mime) =>
        Check(Encoding.Latin1.GetBytes(line + "\necho hi\n"), expected, mime, "us-ascii");

    [Fact]
    public void Shebang_TextVariants()
    {
        Check(Encoding.Latin1.GetBytes("#!/bin/sh\r\necho hi\r\n"), "POSIX shell script, ASCII text executable, with CRLF line terminators");
        Check(Encoding.Latin1.GetBytes("#!/bin/sh\recho\r"), "POSIX shell script, ASCII text executable, with CR line terminators");
        Check(B("#!/bin/sh\necho caf", 0xC3, 0xA9, "\n"), "POSIX shell script, Unicode text, UTF-8 text executable", "text/x-shellscript", "utf-8");
        Check(B("#!/bin/sh\necho caf", 0xE9, "\n"), "POSIX shell script, ISO-8859 text executable", "text/x-shellscript", "iso-8859-1");
        Check(B("#!/bin/sh\necho ", 0x80, "\n"), "POSIX shell script, Non-ISO extended-ASCII text executable", "text/x-shellscript", "unknown-8bit");
        Check(B("#!/bin/sh\n", 0, 1, 2), "POSIX shell script executable (binary data)", "text/x-shellscript", "binary");
        Check(B("#!/usr/bin/perl\nprint \"caf", 0xC3, 0xA9, "\";\n"), "Perl script text executable", "text/x-perl", "utf-8");
        Check(Encoding.Latin1.GetBytes("#!/usr/bin/perl\r\nprint 1;\r\n"), "Perl script text executable");
        Check(Encoding.Latin1.GetBytes("#!/usr/bin/env node\r\n1\r\n"), "Node.js script executable, ASCII text, with CRLF line terminators");
        Check(Encoding.Latin1.GetBytes("#!/bin/sh\n" + new string('x', 400) + "\n"), "POSIX shell script, ASCII text executable, with very long lines (400)");
        Check(Encoding.Latin1.GetBytes("#!/bin/sh\necho \u001b[0m\n"), "POSIX shell script, ASCII text executable, with escape sequences");
    }

    [Fact]
    public void Shebang_WithoutACompleteFirstLine_IsTheGenericForm()
    {
        Check(Encoding.Latin1.GetBytes("#!/bin/sh"), "a /bin/sh script, ASCII text executable, with no line terminators", "text/plain");
        Check(Encoding.Latin1.GetBytes("#!/usr/bin/perl"), "a /usr/bin/perl script, ASCII text executable, with no line terminators");
        Check(Encoding.Latin1.GetBytes("#!/bin/sh\n"), "POSIX shell script, ASCII text executable");
        Check(Encoding.Latin1.GetBytes("#!"), "ASCII text, with no line terminators");
    }

    // ── text and line terminators ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a\nb\n", "ASCII text")]
    [InlineData("ab\n", "ASCII text")]
    [InlineData("a\r\nb\r\n", "ASCII text, with CRLF line terminators")]
    [InlineData("a\r\nb", "ASCII text, with CRLF line terminators")]
    [InlineData("\r\n", "ASCII text, with CRLF line terminators")]
    [InlineData("a\r\nb\nc\r\n", "ASCII text, with CRLF, LF line terminators")]
    [InlineData("a\rb\rc\r", "ASCII text, with CR line terminators")]
    [InlineData("a\rb\r\n", "ASCII text, with CRLF, CR line terminators")]
    [InlineData("a\rb\n", "ASCII text, with CR, LF line terminators")]
    [InlineData("a\rb\r\nc\n", "ASCII text, with CRLF, CR, LF line terminators")]
    [InlineData("a\n\rb\n", "ASCII text, with CR, LF line terminators")]
    [InlineData("a\n\r", "ASCII text")]
    [InlineData("x\r", "ASCII text, with no line terminators")]
    [InlineData("abc\r", "ASCII text, with no line terminators")]
    [InlineData("hello world", "ASCII text, with no line terminators")]
    [InlineData("a\n", "ASCII text")]
    public void LineTerminators(string text, string expected) =>
        Check(Encoding.Latin1.GetBytes(text), expected, "text/plain", "us-ascii");

    [Fact]
    public void Text_Extras_AreInLibmagicOrder()
    {
        Check(Encoding.Latin1.GetBytes(new string('x', 400) + "\r\n"), "ASCII text, with very long lines (400), with CRLF line terminators");
        Check(Encoding.Latin1.GetBytes(new string('x', 400) + "\r\n\u001b[0m\n"), "ASCII text, with very long lines (400), with CRLF, LF line terminators, with escape sequences");
        Check(Encoding.Latin1.GetBytes("a\u0008b\r\nc\r\n"), "ASCII text, with CRLF line terminators, with overstriking");
        Check(Encoding.Latin1.GetBytes("a\u0008b\u001b[0m\n"), "ASCII text, with escape sequences, with overstriking");
        Check(Encoding.Latin1.GetBytes(new string('x', 400)), "ASCII text, with very long lines (400), with no line terminators");
        Check(Encoding.Latin1.GetBytes("short\n" + new string('y', 301) + "\n"), "ASCII text, with very long lines (301)");
        Check(Encoding.Latin1.GetBytes("a\u0007b\n"), "ASCII text");     // BEL is text
        Check(Encoding.Latin1.GetBytes("a\u000cb\n"), "ASCII text");
    }

    [Fact]
    public void Text_Encodings()
    {
        Check(B("caf", 0xC3, 0xA9, "\r\n"), "Unicode text, UTF-8 text, with CRLF line terminators", "text/plain", "utf-8");
        Check(B("caf", 0xE9, "\r\n"), "ISO-8859 text, with CRLF line terminators", "text/plain", "iso-8859-1");
        Check(B("a", 0x80, "\r\n"), "Non-ISO extended-ASCII text, with CRLF line terminators", "text/plain", "unknown-8bit");
        Check(B("a", 0x9F, "b\n"), "Non-ISO extended-ASCII text", "text/plain", "unknown-8bit");
        Check(B("a", 0xA0, "b\n"), "ISO-8859 text", "text/plain", "iso-8859-1");
        Check(B(0xEF, 0xBB, 0xBF, "hello\r\n"), "Unicode text, UTF-8 (with BOM) text, with CRLF line terminators", "text/plain", "utf-8");
        Check(B(0xEF, 0xBB, 0xBF, "a"), "Unicode text, UTF-8 (with BOM) text, with no line terminators");
        Check(B("a", 0xC2, 0x85, "b\n"), "Unicode text, UTF-8 text, with LF, NEL line terminators");
    }

    [Fact]
    public void Text_LongLines_CountCharactersNotBytes()
    {
        var line = string.Concat(Enumerable.Repeat("é", 200));   // 400 bytes, 200 characters
        Check(B(Encoding.UTF8.GetBytes(line), "\n"), "Unicode text, UTF-8 text");
        Check(B(Enumerable.Repeat((byte)0xE9, 400).ToArray(), "\r\n"), "ISO-8859 text, with very long lines (400), with CRLF line terminators");
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x1C)]
    [InlineData(0x7F)]
    public void ControlBytes_MakeData(int b) =>
        Check(B("a", b, "b\n"), "data", "application/octet-stream", "binary");
}
