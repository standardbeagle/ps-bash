using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// R21 differential oracle for sed. Each script runs in real bash AND ps-bash and
/// the bytes are diffed against GNU sed.
///
/// Covered regressions:
///   - trailing-newline loss on single-line input (printf already embeds the \n;
///     sed reused the input object's stale NoTrailingNewline flag)
///   - a BashObject piped into a native program (od) rendered as a property table
///     instead of its bash text
///   - multi-command expressions (N;s/\n/+/), N on the last input line
///   - range-aware c (text once per range)
///   - the address-prefixed, continuation-form $a\ text command
/// </summary>
public class SedDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(15));

    [SkippableFact]
    public Task Sed_TrailingNewline_SingleLineInput_NextCommandNotGlued()
        => Eq("printf 'x.y\\n' | sed 's/\\./-/'; echo Z");

    [SkippableFact]
    public async Task Sed_NativePipe_OdSeesBashTextNotTable()
    {
        // `od` is a Unix tool; on the Windows dev box ps-bash has no native `od`,
        // so this case runs on the Linux/macOS CI legs where it does. The
        // conversion rule itself is proven deterministically (and on every OS) by
        // PsEmitterTests.Transpile_PipeToNativeCommand_ConvertsObjectsToBashText.
        Skip.If(OperatingSystem.IsWindows(), "no native od on Windows");
        // BSD od (macOS) pads its -c columns one space wider than the GNU od the oracle recorded; the program's output
        // layout is not what this case tests (what bytes ps-bash feeds it is), so there is nothing to compare there.
        Skip.If(OperatingSystem.IsMacOS(), "BSD od formats -c differently from GNU od");
        await Eq("printf 'a\\n' | sed s/a/b/ | od -c");
    }

    [SkippableFact]
    public Task Sed_NextJoin_MultiCommandExpression()
        => Eq("printf 'a\\nb\\nc\\n' | sed 'N;s/\\n/+/'");

    [SkippableFact]
    public Task Sed_Next_LastLine_NoExtraPrint()
        => Eq("printf 'a\\nb\\nc\\n' | sed -n 'N;p'");

    [SkippableFact]
    public Task Sed_Change_Range_EmitsOnce()
        => Eq("printf '1\\n2\\n3\\n' | sed '1,2c\\\nREPL'");

    [SkippableFact]
    public Task Sed_DollarAddress_AppendAfterLastLine()
        => Eq("printf '1\\n2\\n3\\n' | sed '$a\\\nAPP'");

    [SkippableFact]
    public Task Sed_AddressRegex_Semicolon_DeletesMatchingLines()
        => Eq("printf 'a;b\\nc\\n' | sed '/;/d'");

    [SkippableFact]
    public Task Sed_AddressRegex_EscapedSemicolon_DeletesMatchingLines()
        => Eq("printf 'a;b\\nc\\n' | sed '/\\;/d'");

    [SkippableFact]
    public Task Sed_RangeAddress_EndRegexSemicolonAnchor()
        => Eq("printf 'a;b\\nc;d\\ne\\n' | sed '1,/;$/d'");

    // `[[:punct:]]` rewrites to `\p{P}\p{S}`; the BRE pass escaped its braces → "Malformed \p{X}".
    [SkippableFact]
    public Task Sed_Bre_PunctClass_Compiles()
        => Eq("printf 'a!b\\n' | sed 's/[[:punct:]]/_/'");

    // ---- delimited regexes: one scanner (SedDelimited) for addresses, s and y --------------------------
    // An address regex took the first `/` as its end: `/a<\/c>/d` compiled `a<\` and failed as
    // "sed: can't read f: Invalid pattern ..." (printed twice, status 1); `\,re,` was "unknown command: `\'".

    private const string InFile = @"cd ""$(mktemp -d)"" || exit; printf 'x\na</c>\ny\n' > f; ";
    private const string Lines = @"printf 'a/b\nc\nd|e\nAB\nab\n' | ";

    [SkippableFact]
    public Task Sed_Address_EscapedDelimiter_DeletesLine()
        => Eq(InFile + @"sed '/a<\/c>/d' f; echo ""s=$?""");

    [SkippableFact]
    public Task Sed_Address_CustomDelimiter_CommaAndBar()
        => Eq(InFile + @"sed -n '\,a</c>,p' f; sed -n '\|a</c>|p' f; echo ""s=$?""");

    [SkippableFact]
    public Task Sed_Address_CustomDelimiter_EscapedDelimiterIsTheBareChar()
        => Eq(Lines + @"sed -n '\|d\|e|p'; " + Lines + @"sed -E -n '\|c\|d|p'; " + Lines + @"sed -n '\.a\.b.p'");

    [SkippableFact]
    public Task Sed_Address_DelimiterInsideBracket_DoesNotEndRegex()
        => Eq(Lines + @"sed -n '/[/]/p'; " + Lines + @"sed -n '/[^/]$/p'; " + Lines + @"sed -n '/[]/]/p'");

    [SkippableFact]
    public Task Sed_AddressRange_EscapedAndCustomDelimiters()
        => Eq(Lines + @"sed -n '/a\/b/,/c/p'; " + Lines + @"sed -n '\,a/b,,\|c|p'; " + Lines + @"sed -n '2,\,d|e,p'");

    [SkippableFact]
    public Task Sed_Address_IgnoreCaseAndMultilineFlags()
        => Eq(Lines + @"sed -n '/ab/Ip'; " + Lines + @"sed -n '\,ab, I p'; " + Lines + @"sed -n '/AB/I,/D/Ip'; "
            + @"printf 'a\nb\n' | sed -n 'N;/^b/Mp'; printf 'a\nb\n' | sed -n 'N;/^b/p'; printf 'a\nb\n' | sed -n 'N;/b/p'");

    [SkippableFact]
    public Task Sed_Subst_CustomDelimiters_EscapedDelimiter()
        => Eq(Lines + @"sed 's|d\|e|X|'; " + Lines + @"sed -E 's|d\|e|X|'; " + Lines + @"sed 's/[/]/X/'; "
            + Lines + @"sed 's&a&[\&]&'; " + Lines + @"sed 's,a/b,X\,Y,'");

    [SkippableFact]
    public Task Sed_Transliterate_EscapedDelimiter()
        => Eq(Lines + @"sed 'y/\//|/'; " + Lines + @"sed 'y,a\,,xy,'");

    [SkippableFact]
    public Task Sed_BadAddressRegex_IsOneScriptError_NotCantRead()
        => Eq(InFile + @"sed '/a\(/d' f; echo ""s=$?""; sed -E '/a(/d' f; echo ""s=$?""; sed -n '1,/a\(/p' f; echo ""s=$?""");

    [SkippableFact]
    public Task Sed_BadRegex_CharPositionCountsTheWholeScript()
        => Eq(InFile + @"sed '/a/d;/b\(/d' f; echo ""s=$?""; sed 's/a\(/x/;s/b/c/' f; echo ""s=$?""; "
            + @"sed -e '/a/d' -e '/b\(/d' f; echo ""s=$?""; sed '2{/a\(/d}' f; echo ""s=$?""");

    [SkippableFact]
    public Task Sed_UnterminatedAndBadCommands_GnuMessages()
        => Eq(InFile + @"sed -n '/abc' f; echo ""s=$?""; sed -n '\,abc' f; echo ""s=$?""; sed 's/a/b' f; echo ""s=$?""; "
            + @"sed 'y/ab/x/' f; echo ""s=$?""; sed 's/a/b/gx' f; echo ""s=$?""; sed 's/a/\1/' f; echo ""s=$?""");

    // ---- the engine: N's line number, the multi-line pattern space, the empty regex ----------------------

    private const string Abcd = @"printf 'a\nb\nc\nd\n' | ";

    // After N the line number is the pulled line's: `1~2N;1p` printed "a\nb" (line 1 was still the cycle's).
    [SkippableFact]
    public Task Sed_LineNumberAfterN_IsThePulledLine()
        => Eq(Abcd + @"sed -n '1~2N;1p'; echo ""s=$?""; " + Abcd + @"sed 'N;='; " + Abcd + @"sed -n 'N;4p'");

    // Without M the pattern space is one buffer: `.` and `[^x]` match its newline, `$` is only its end.
    [SkippableFact]
    public Task Sed_MultiLineBuffer_DotAndDollar()
        => Eq(@"printf 'a\nb\n' | sed -n 'N;/a.b/p'; printf 'a\nb\n' | sed -n 'N;/a$/p'; printf 'a\nb\n' | sed -n 'N;/a$/Mp'; "
            + @"printf 'a\nb\n' | sed 'N;s/[^x]/_/g'; printf 'a\nb\n' | sed 'N;s/./_/Mg'; printf 'a\nb\n' | sed 'N;s/$/</Mg'");

    // `//` is the last regex used, at run time (with its I flag and its groups).
    [SkippableFact]
    public Task Sed_EmptyRegex_IsTheLastRegexUsed()
        => Eq(@"printf 'ab\n' | sed -n '/b/s//X/p;s/a/Y/;s//Z/p'; printf 'ab\n' | sed -n 's/\(a\)/\1/;s//[\1]/p'; "
            + @"printf 'a\nb\n' | sed -n '/a/,//p'; printf 'Ab\n' | sed -n '/a/Is//X/p'; printf 'a\nb\na\nc\n' | sed -n '/a/,/a/p'");

    // With no regex used before it, `//` stops sed at run time: what was written stays, status 1.
    [SkippableFact]
    public Task Sed_EmptyRegex_NoPrevious_IsARunTimeError()
        => Eq(@"printf 'a\n' | sed '//p'; echo ""s=$?""; printf '' | sed '//p'; echo ""s=$?""; "
            + @"printf 'a\nb\n' | sed '2s//x/'; echo ""s=$?""; sed '/o/Ip;//Ip' /dev/null; echo ""s=$?""; "
            + @"sed 's/o/0/;s//1/I' /dev/null; echo ""s=$?""");

    // ---- script errors: -f files name the line, each -e its own number ---------------------------------

    [SkippableFact]
    public Task Sed_ScriptFile_ErrorNamesTheFileLine()
        => Eq(@"cd ""$(mktemp -d)"" || exit; printf ':\n' > f1; sed -f f1 /dev/null; echo ""s=$?""; "
            + @"printf 'p\ns/a/b/w\n' > f2; sed -f f2 /dev/null; echo ""s=$?""; "
            + @"printf 'p\n{\np\n' > f3; sed -f f3 /dev/null; echo ""s=$?""; "
            + @"printf '#n\nk\n' > f4; sed -f f4 /dev/null; echo ""s=$?""; "
            + @"printf 'p\nk' > f5; sed -f f5 /dev/null; echo ""s=$?""; "
            + @"printf 'p\n\n' > f6; printf 'x\n' | sed -f f6 -e '//p'; echo ""s=$?""; "
            + @"printf 'x\n' | sed -e '//p' -f f6; echo ""s=$?""");

    [SkippableFact]
    public Task Sed_MissingFilenameLabelAndBraces_GnuChar()
        => Eq(@"sed r /dev/null; echo ""s=$?""; sed 'p;r' /dev/null; echo ""s=$?""; sed 's/a/b/w' /dev/null; echo ""s=$?""; "
            + @"sed ':' /dev/null; echo ""s=$?""; sed 'p;:' /dev/null; echo ""s=$?""; sed -e p -e r /dev/null; echo ""s=$?""; "
            + @"sed -e p -e '{p' /dev/null; echo ""s=$?""; sed -e '{p' -e p /dev/null; echo ""s=$?""; "
            + @"sed -e p -e '}' /dev/null; echo ""s=$?""; sed -e p -e 'p;}' /dev/null; echo ""s=$?""");

    [SkippableFact]
    public Task Sed_SubstFlags_RepeatedOrZero_AreErrors()
        => Eq(@"sed 's/a/b/gg' /dev/null; echo ""s=$?""; sed 's/a/b/pp' /dev/null; echo ""s=$?""; "
            + @"sed 's/a/b/2g3' /dev/null; echo ""s=$?""; sed 's/a/b/0' /dev/null; echo ""s=$?""; "
            + @"echo aaaa | sed 's/a/b/g3'; echo aaaa | sed 's/a/b/12'");

    [SkippableFact]
    public Task Sed_Address_BlanksBeforeTheComma()
        => Eq(Abcd + "sed -n '2 ,3p'; " + Abcd + "sed -n '2\t,3p'; " + Abcd + "sed -n '/b/ ,3p'; " + Abcd + "sed -n '2,3\tp'");

    // ---- character escapes: \t \xHH \dNNN \oNNN \cX in a regex, a replacement and y ------------------------

    [SkippableFact]
    public Task Sed_Transliterate_CharacterEscapes()
        => Eq(@"echo abc | sed 'y/b/\t/' | od -c; echo abc | sed 'y/abc/\x41\x42\x43/'; echo abc | sed 'y/abc/\x41\x4/'; echo ""s=$?""; "
            + @"echo 'aqb' | sed 'y/\q/Z/'");

    [SkippableFact]
    public Task Sed_Replacement_CharacterEscapesAreLiteral()
        => Eq(@"echo abc | sed 's/b/[\n]/'; echo abc | sed 's/b/[\q]/'; echo abc | sed 's/b/\x26/'; "
            + @"echo abc | sed 's/b/\d065\o102/'; echo 'a b' | sed 's/\t*b/X/'; printf 'a\tb\n' | sed 's/\t/<TAB>/'");
}
