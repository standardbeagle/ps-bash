using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// An unquoted variable glued to text is field-split (x='a b': <c>a$x</c> is <c>aa</c> and <c>b</c>); ps-bash passed one
/// argument. A run-time command name of <c>builtin</c> (<c>$b declare f=$x</c>, <c>${b}uiltin …</c>) was "command not
/// found", and <c>declare 'b'</c> printed the variable.
/// </summary>
public class AffixWordSplitTests
{
    [Fact]
    public Task UnquotedVarGluedToText_IsSplit() => AssertOracle.EqualAsync(
        "x='a b'; printf '[%s]' a$x; echo; f() { printf '[%s]' \"$@\"; echo; }; f a$x $x.c \"q\"$x $x'z'\n"
        + "p=printf; $p '[%s]' f=$x; echo; e=ech; ${e}o hi\n"
        + "y='1  2'; echo z$y; IFS=:; w='u:v'; printf '[%s]' k$w; echo; IFS=' '; x=''; printf '[%s]' a$x; echo\n"
        + "n='a;echo pwn'; printf '[%s]' q$n; echo; m='$(echo pwn)'; printf '[%s]' q$m; echo");

    [Fact]
    public Task DynamicBuiltinAndCommandNames_RunTheDeclaration() => AssertOracle.EqualAsync(
        "x='a b'\nb=builtin; $b declare f=$x; echo \"f[$f]\"\nb=b; ${b}uiltin declare g=$x; echo \"g[$g]\"\n"
        + "z=command; $z readonly c=$x; echo \"c[$c]\"\nz=c; ${z}ommand readonly d=$x; echo \"d[$d]\"\n"
        + "q=builtin; $q echo hi; $q; echo \"st:$?\"\nk=1; declare 'k'; declare \"k\"; echo \"st:$? [$k]\"");

    // Text glued to a quoted "${a[@]}" / "$@" goes on the first and last element; each element stays whole.
    [Fact]
    public Task QuotedArrayWithText_DistributesOverTheElements() => AssertOracle.EqualAsync(
        "a=(x 'foo=F' 'b c'); printf '<%s>' -\"${a[@]}\"; echo; printf '<%s>' \"p${a[@]}q\"; echo\n"
        + "set -- u 'v w'; printf '<%s>' \"<$@>\"; echo; printf '<%s>' k\"$@\"; echo\n"
        + "b=(); printf '<%s>' -\"${b[@]}\"; echo; printf '<%s>' \"${b[@]}\"''; echo\n"
        + "for w in -\"${a[@]}\"; do printf '<%s>' \"$w\"; done; echo; for w in 1 \"z${a[@]}\" 2; do printf '<%s>' \"$w\"; done; echo\n"
        + "c=('a b' '*' '$(echo pwn)'); printf '<%s>' \"-I${c[@]}\"; echo");

    [Fact]
    public Task DeclarationNamedAtRunTimeOrFedAnArray_Declares() => AssertOracle.EqualAsync(
        "myvar=typeset; x='a b'; $myvar y=$x; echo \"y[$y]\"\n"
        + "a=(x 'foo=F' 'bar=B'); typeset -\"${a[@]}\"; echo \"foo=$foo bar=$bar\"; printenv foo\n"
        + "n=(foo bar); declare -p \"${n[@]}\"; q=('k1=a b' 'k2=;echo pwn'); declare \"${q[@]}\"; echo \"[$k1] [$k2]\"");
}
