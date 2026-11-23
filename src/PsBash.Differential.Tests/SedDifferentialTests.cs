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
}
