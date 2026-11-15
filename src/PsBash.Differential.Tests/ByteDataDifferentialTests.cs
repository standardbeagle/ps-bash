using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// bash-vs-ps-bash for data that is bytes rather than text: byte counting and byte matching in the C locale;
/// sed's \xHH byte escapes; /dev/zero as a cat operand; printf %q of an invalid UTF-8 byte. Output goes
/// through od so no byte is hidden by the canonicalizer. Oracle: bash 5.2 / GNU coreutils 9.4 (oils serialize,
/// unicode, var-op-len, var-op-patsub, bugs spec cases).
/// </summary>
public class ByteDataDifferentialTests
{
    // ───────────── the C locale counts and matches bytes ─────────────

    [SkippableFact] public Task Length_InCLocale_CountsBytes_OtherwiseCharacters() =>
        AssertOracle.EqualAsync("x=$'\\U00010000'; y='aμb'; echo ${#x} ${#y}; LC_ALL=C; echo ${#x} ${#y}; LC_ALL=; echo ${#x} ${#y}");

    [SkippableFact] public Task ElementLength_InCLocale_CountsBytes() =>
        AssertOracle.EqualAsync("a=(μμ x); LC_ALL=C; echo ${#a[0]} ${#a[1]}");

    [SkippableFact] public Task PatternSubstitution_InCLocale_QuestionMarkIsOneByte() =>
        AssertOracle.EqualAsync("s='aμb'; LC_ALL=C; echo \"${s/a?b/X}\" \"${s//?/-}\"; LC_ALL=; echo \"${s/a?b/X}\" \"${s//?/-}\"");

    [SkippableFact] public Task PatternSubstitution_UnsetVariable_IsEmpty() =>
        AssertOracle.EqualAsync("unset nv; LC_ALL=C; echo \"[${nv//a/b}]\"");

    // ───────────── sed \xHH ─────────────

    [SkippableFact] public Task Sed_HexByteEscape_MatchesOneByteOfACharacter() =>
        AssertOracle.EqualAsync(@"printf 'μa\n' | sed 's/\xce//g' | od -A n -t x1");

    [SkippableFact] public Task Sed_HexByteEscapes_MatchAWholeCharacter() =>
        AssertOracle.EqualAsync(@"printf 'μa\n' | sed 's/\xce\xbc/M/'");

    [SkippableFact] public Task Sed_EscapedBackslashBeforeX_IsNotAByteEscape() =>
        AssertOracle.EqualAsync(@"printf '\\xce μ\n' | sed 's/\\xce/Q/'");

    // ───────────── /dev/zero ─────────────

    [SkippableFact] public Task Cat_DevZeroOperand_StreamsZeroBytes() =>
        AssertOracle.EqualAsync("cat /dev/zero | head -c 5 | od -A n -t x1");

    // ───────────── printf %q ─────────────

    // Literal arguments: the bytes never pass through a variable (on Linux/macOS a variable's invalid
    // UTF-8 byte is lost to the environment store: intentional-differences.md).
    [SkippableFact] public Task PrintfQ_InvalidByte_IsOctalAnsiCQuoted() =>
        AssertOracle.EqualAsync(@"printf '%q\n' $'\xff' $'a\tb\xce' $'\xce\xbc' $'\xcea'");
}
