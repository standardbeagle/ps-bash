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
}
