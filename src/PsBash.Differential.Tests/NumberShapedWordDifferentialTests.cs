using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A bash word is text, whatever it looks like. PowerShell reads an unquoted <c>1e5</c>, <c>0x10</c>, <c>1kb</c>,
/// <c>10d</c>, <c>08</c>, <c>.5</c>, <c>1.</c> as a number (spec emitter-strategy.md §Number-shaped words). REGRESSION:
/// <c>for f in 33441e77</c> printed <c>3.3441E+81</c>, <c>f 1e5</c> handed the function 100000, <c>echo {-0,1}</c> printed
/// <c>0 1</c>. The corpus runs through every word-emitting construct: for-list, command and function argument, assignment,
/// array, <c>set --</c>, brace list, case, <c>[[ ]]</c> / <c>[ ]</c>, here-doc, here-string, and the cmdlets that take
/// a word as data (<c>echo</c>, <c>printf %s</c>, <c>seq</c>, <c>test -eq</c>). The predicate's guard against PowerShell's
/// tokenizer is PsNumberLiteralGuardTests.
/// </summary>
public class NumberShapedWordDifferentialTests
{
    private const string Corpus =
        "33441e77 1e5 0x10 1kb 10d 5l 7u 3ul 2y 1s 1n +5 -5 1. .5 1_000 0b101 1e5x 0x 1.2.3 08 1kbx 1mb 1gb 1pb 1E5 "
        + "0X1F 1.5e3 -0 00 0b 1e+5 1.5n 0x1d";

    [SkippableTheory]
    [InlineData("for f in " + Corpus + "; do printf '[%s]' \"$f\"; done; echo")]
    [InlineData("printf '[%s]' " + Corpus + "; echo")]
    [InlineData("f() { printf '[%s]' \"$@\"; echo; }; f " + Corpus)]
    [InlineData("f() { printf '[%s]' \"$@\"; echo; }; f 1\\e5 1\"e5\" 1'e5' 0\\x10; g() { return 0; }; g 1e5 && echo ok-g")]
    [InlineData("set -- " + Corpus + "; printf '[%s]' \"$@\" \"$#\"; echo")]
    [InlineData("a=(" + Corpus + "); printf '[%s]' \"${a[@]}\"; echo; a+=(1kb .5); echo \"${a[-1]} ${#a[@]}\"")]
    [InlineData("declare -a d=(1e5 08); printf '[%s]' \"${d[@]}\"; echo; k=(); k[0]=1e5; echo \"${k[0]}\"")]
    [InlineData("x=1e5; y=08; h() { local z=0x10; echo \"$z\"; }; h; echo \"$x $y\"; export e=1kb; echo \"$e\"")]
    [InlineData("echo {1e5,08,-0,+5} {-0,1} x{1e5,2} {1,2,3}; for i in {08..10}; do printf '%s ' $i; done; echo")]
    public Task NumberShapedWord_KeepsItsText(string script) => AssertOracle.EqualAsync(script);

    [SkippableTheory]
    [InlineData("case 08 in 8) echo bad;; 08) echo good08;; esac; case 1e5 in 100000) echo bad;; 1e5) echo good1e5;; esac")]
    [InlineData("[[ 08 == 8 ]] && echo bad || echo good08; [[ +5 == 5 ]] && echo bad || echo good5; [[ 1. == 1 ]] && echo bad || echo good1")]
    [InlineData("[ 08 = 8 ] && echo bad || echo good08; [ 1e5 = 100000 ] && echo bad || echo good1e5; test -0 = 0 && echo bad || echo good0")]
    [InlineData("cat <<E\n1e5 0x10 08\nE\ncat <<< 1e5; r=$(echo 0x10); echo \"$r\"; IFS=, read -r p q <<< \"1e5,08\"; echo \"$p $q\"")]
    public Task NumberShapedWord_ComparesAndFeedsAsText(string script) => AssertOracle.EqualAsync(script);

    // The cmdlet side takes the word as data: echo prints it, test -eq reads it as a decimal integer, seq as a number.
    [SkippableTheory]
    [InlineData("echo 1e5 0x10 08 -1e5 -08 -.5 -0; printf '%s\\n' 0x10 1e5")]
    [InlineData("[ 08 -eq 8 ] && echo eq08; test 010 -eq 10 && echo eq010; seq 1e1 | tail -1; seq 3 | tr '\\n' ' '; echo")]
    public Task NumberShapedWord_ReachesCmdletsAsText(string script) => AssertOracle.EqualAsync(script);
}
