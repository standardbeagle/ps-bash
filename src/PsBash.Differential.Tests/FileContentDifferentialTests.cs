using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>file</c> content classification against file-5.45: the fixtures are built with <c>printf</c> in
/// both shells (identical bytes), then <c>file</c> and <c>file -b --mime</c> run on them. Covers an empty
/// file, one byte, ELF, PE / MS-DOS, gzip, zip, PNG, JPEG, PDF, shebang scripts and CRLF text.
/// </summary>
public class FileContentDifferentialTests
{
    private static Task Both(string setup) => EqualAsync(setup, "file f; file -b --mime f");

    /// <summary>n NUL bytes as printf escapes.</summary>
    private static string Z(int n) => string.Concat(Enumerable.Repeat("\\x00", n));

    // printf '' rather than `: > f`: ps-bash does not create the file for a redirect on the no-op builtin.
    [SkippableFact] public Task Empty() => Both("printf '' > f");
    [SkippableFact] public Task OneByte() => Both("printf a > f");
    [SkippableFact] public Task OneNewline() => Both("printf '\\n' > f");
    [SkippableFact] public Task TwoBytes() => Both("printf ab > f");

    [SkippableFact] public Task Elf_Executable64() =>
        Both("printf '\\x7fELF\\x02\\x01\\x01\\x00" + Z(8) + "\\x02\\x00\\x3e\\x00\\x01\\x00\\x00\\x00' > f");
    [SkippableFact] public Task Elf_SharedObject32Arm() =>
        Both("printf '\\x7fELF\\x01\\x01\\x01\\x00" + Z(8) + "\\x03\\x00\\x28\\x00\\x01\\x00\\x00\\x00' > f");
    [SkippableFact] public Task Elf_GnuLinuxOsAbi() =>
        Both("printf '\\x7fELF\\x02\\x01\\x01\\x03" + Z(8) + "\\x02\\x00\\x3e\\x00\\x01\\x00\\x00\\x00' > f");
    [SkippableFact] public Task Elf_TruncatedAfterMachine() =>
        Both("printf '\\x7fELF\\x02\\x01\\x01\\x00" + Z(8) + "\\x03\\x00\\x3e\\x00' > f");

    // MZ + e_lfanew=0x80 + "PE\0\0" + COFF (x86-64, 3 sections, console) + a PE32+ optional header.
    private static readonly string PeStub =
        "printf 'MZ" + Z(58) + "\\x80\\x00\\x00\\x00" + Z(64) +
        "PE\\x00\\x00\\x64\\x86\\x03\\x00" + Z(12) + "\\xf0\\x00\\x22\\x00\\x0b\\x02" +
        Z(66) + "\\x03\\x00" + Z(290) + "' > f";
    [SkippableFact] public Task Pe_Console64() => Both(PeStub);
    [SkippableFact] public Task Mz_OnlyIsMsDos() => Both("printf 'MZ" + Z(100) + "' > f");

    [SkippableFact] public Task Gzip_NoName() => Both("printf '\\x1f\\x8b\\x08\\x00\\x00\\x00\\x00\\x00\\x00\\x03xxxxxxxx\\x00\\x00\\x00\\x00\\x06\\x00\\x00\\x00' > f");
    [SkippableFact] public Task Gzip_NameAndTime() =>
        Both("printf '\\x1f\\x8b\\x08\\x08\\x00\\x65\\x53\\x65\\x00\\x03a.txt\\x00xxxx\\x00\\x00\\x00\\x00\\x06\\x00\\x00\\x00' > f");
    [SkippableFact] public Task Gzip_MaxCompression_Ntfs() =>
        Both("printf '\\x1f\\x8b\\x08\\x00\\x00\\x00\\x00\\x00\\x02\\x0bxxxx\\x00\\x00\\x00\\x00\\x2a\\x00\\x00\\x00' > f");
    [SkippableFact] public Task Gzip_Truncated() => Both("printf '\\x1f\\x8b\\x08\\x00\\x00\\x00' > f");

    private static readonly string ZipHeader =
        "PK\\x03\\x04\\x14\\x00\\x00\\x00\\x08\\x00\\x00\\x00\\x21\\x00" + Z(12) + "\\x05\\x00\\x00\\x00a.txt";
    [SkippableFact] public Task Zip_Deflate() => Both("printf '" + ZipHeader + Z(80) + "' > f");
    [SkippableFact] public Task Zip_TooShortToDescribe() => Both("printf '" + ZipHeader + "' > f");
    [SkippableFact] public Task Zip_Empty() => Both("printf 'PK\\x05\\x06" + Z(18) + "' > f");

    [SkippableFact] public Task Png_Rgb() =>
        Both("printf '\\x89PNG\\r\\n\\x1a\\n\\x00\\x00\\x00\\rIHDR\\x00\\x00\\x00\\x10\\x00\\x00\\x00\\x10\\x08\\x02\\x00\\x00\\x00' > f");
    [SkippableFact] public Task Png_Palette_Interlaced() =>
        Both("printf '\\x89PNG\\r\\n\\x1a\\n\\x00\\x00\\x00\\rIHDR\\x00\\x00\\x00\\x05\\x00\\x00\\x00\\x07\\x04\\x03\\x00\\x00\\x01' > f");
    [SkippableFact] public Task Png_SignatureOnly_IsData() => Both("printf '\\x89PNG\\r\\n\\x1a\\n' > f");

    [SkippableFact] public Task Jpeg_Jfif_Baseline() =>
        Both("printf '\\xff\\xd8\\xff\\xe0\\x00\\x10JFIF\\x00\\x01\\x01\\x01\\x00\\x48\\x00\\x48\\x00\\x00\\xff\\xc0\\x00\\x0b\\x08\\x01\\xe0\\x02\\x80\\x01\\x01\\x11\\x00\\xff\\xd9' > f");
    [SkippableFact] public Task Jpeg_NoJfif() =>
        Both("printf '\\xff\\xd8\\xff\\xfe\\x00\\x04hi\\xff\\xc2\\x00\\x0b\\x08\\x00\\x21\\x00\\x2c\\x01\\x01\\x11\\x00\\xff\\xd9' > f");
    [SkippableFact] public Task Jpeg_Bare() => Both("printf '\\xff\\xd8\\xff\\xdb' > f");

    [SkippableFact] public Task Pdf_Version() => Both("printf '%%PDF-1.4\\n%%EOF\\n' > f");
    [SkippableFact] public Task Pdf_PageCount() => Both("printf '%%PDF-1.7\\n<</Type/Pages/Count 3>>\\n' > f");
    [SkippableFact] public Task Pdf_BinaryComment() => Both("printf '%%PDF-1.5\\n%%\\xe2\\xe3\\xcf\\xd3\\n' > f");

    [SkippableFact] public Task Shebang_Sh() => Both("printf '#!/bin/sh\\necho hi\\n' > f");
    [SkippableFact] public Task Shebang_Bash() => Both("printf '#!/bin/bash\\necho hi\\n' > f");
    [SkippableFact] public Task Shebang_EnvBash() => Both("printf '#!/usr/bin/env bash\\necho hi\\n' > f");
    [SkippableFact] public Task Shebang_EnvSh_IsGeneric() => Both("printf '#!/usr/bin/env sh\\necho hi\\n' > f");
    [SkippableFact] public Task Shebang_Python() => Both("printf '#!/usr/bin/env python3\\nprint(1)\\n' > f");
    [SkippableFact] public Task Shebang_Perl() => Both("printf '#!/usr/bin/perl\\nprint 1;\\n' > f");
    [SkippableFact] public Task Shebang_Node() => Both("printf '#!/usr/bin/env node\\n1\\n' > f");
    [SkippableFact] public Task Shebang_Unknown() => Both("printf '#!/usr/bin/foo\\necho\\n' > f");
    [SkippableFact] public Task Shebang_Crlf() => Both("printf '#!/bin/sh\\r\\necho hi\\r\\n' > f");
    [SkippableFact] public Task Shebang_Utf8() => Both("printf '#!/bin/sh\\necho caf\\xc3\\xa9\\n' > f");
    [SkippableFact] public Task Shebang_NoNewline() => Both("printf '#!/bin/sh' > f");

    [SkippableFact] public Task Text_Crlf() => Both("printf 'a\\r\\nb\\r\\n' > f");
    [SkippableFact] public Task Text_CrlfNoFinalNewline() => Both("printf 'a\\r\\nb' > f");
    [SkippableFact] public Task Text_MixedCrlfLf() => Both("printf 'a\\r\\nb\\nc\\r\\n' > f");
    [SkippableFact] public Task Text_CrOnly() => Both("printf 'a\\rb\\rc\\r' > f");
    [SkippableFact] public Task Text_AllThreeTerminators() => Both("printf 'a\\rb\\r\\nc\\n' > f");
    [SkippableFact] public Task Text_LoneCrAtEnd_IsNoTerminator() => Both("printf 'x\\r' > f");
    [SkippableFact] public Task Text_NoTerminator() => Both("printf 'hello world' > f");
    [SkippableFact] public Task Text_VeryLongLine() => Both("printf '" + new string('x', 400) + "\\r\\n' > f");
    [SkippableFact] public Task Text_EscapeAndBackspace() => Both("printf 'a\\bb\\033[0m\\n' > f");
    [SkippableFact] public Task Text_Utf8Crlf() => Both("printf 'caf\\xc3\\xa9\\r\\n' > f");
    [SkippableFact] public Task Text_Latin1Crlf() => Both("printf 'caf\\xe9\\r\\n' > f");
    [SkippableFact] public Task Text_Utf8Bom() => Both("printf '\\xef\\xbb\\xbfhello\\r\\n' > f");
    [SkippableFact] public Task Text_Del_IsData() => Both("printf 'abc\\x7f\\n' > f");
}
