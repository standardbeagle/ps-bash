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
}
