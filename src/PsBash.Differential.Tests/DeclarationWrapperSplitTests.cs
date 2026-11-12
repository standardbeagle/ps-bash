using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Behind <c>builtin</c>/<c>command</c> a declaration's operands are ordinary words: with x='a b', <c>builtin export
/// z=$x</c> exports z=a and b (ps-bash assigned "a b"). <c>declare $y</c> / <c>typeset $y</c> / <c>export $y</c> with
/// y='p=1 q=2' declare each field (they reported "p=1: not found").
/// </summary>
public class DeclarationWrapperSplitTests
{
    [Fact]
    public Task WrappedDeclarationOperands_AreOrdinaryWords() => AssertOracle.EqualAsync(
        "x='a b'\nbuiltin export z=$x; echo \"[$z]\"\ncommand export c=$x; echo \"[$c]\"\n"
        + "builtin declare d=$x e=1; echo \"[$d] [$e]\"\nbuiltin declare g=\"$x\"; echo \"[$g]\"\n"
        + "y='p=1 q=2'; builtin declare $y; echo \"[$p] [$q]\"\n"
        + "f() { builtin local s=$x; echo \"[$s]\"; command local t=\"$x\"; echo \"[$t]\"; }; f\n"
        + "builtin declare h=*.zzz; echo \"[$h]\"; n='a;echo pwn'; builtin declare k=$n 2>/dev/null; echo \"[$k]\"");

    [Fact]
    public Task DeclarationOfRuntimeWords_DeclaresEachField() => AssertOracle.EqualAsync(
        "y='p=1 q=2'; declare $y; echo \"d [$p] [$q]\"\ny='p2=1 q2=2'; export $y; echo \"e [$p2] [$q2]\"; printenv q2\n"
        + "y='p4=1 q4=2'; typeset $y; echo \"t [$p4] [$q4]\"\ny='u=1'; declare -x w $y; echo \"[$u]\"; printenv u\n"
        + "x='a b'; declare z=$x; echo \"z [$z]\"");

    // `declare k=ab pwn`: a name holding the letter n was taken for a flag, so `pwn` overwrote the declaration and k
    // stayed empty. Declaring a read-only name fails (status 1, value kept) and, unlike `r=2`, the line goes on.
    [Fact]
    public Task DeclareNameWithN_AndReadonlyName() => AssertOracle.EqualAsync(
        "declare k=ab pwn; echo \"k [$k] st:$?\"; typeset nn=5 zz; echo \"nn [$nn]\"\n"
        + "readonly r=r1\ntypeset +r r=r2 2>/dev/null; echo \"t st:$? [$r]\"\n"
        + "declare r=r3 2>/dev/null; echo \"d st:$? [$r]\"; echo same-line");

    // assign-extended "declare -p var": brace-expanded names reached `export`/`local` as a command ("not found") and
    // `readonly -p` dropped them. `readonly -p NAMES` / `export -p NAMES` set the attribute and print nothing;
    // `local -p NAMES` prints only the function's locals and reports the rest as not found, status 1.
    [Fact]
    public Task PrintFlagWithBraceNames_SetsAttributes_LocalListsOnlyLocals() => AssertOracle.EqualAsync(
        "tv1=111; readonly tv2=222; export tv3=333\n"
        + "f1() { local tv5=555; echo '[readonly]'; readonly -p tv{0..5}; echo \"st:$?\"; echo '[export]'; export -p tv{0..5};"
        + " echo \"st:$?\"; echo '[local]'; local -p tv{0..5} 2>/dev/null; echo \"st:$?\"; local -p tv5 tv1 2>/dev/null; echo \"st:$?\"; }\n"
        + "f1; bash -c 'echo \"child tv1=$tv1 tv5=[$tv5]\"'; declare -p tv1 tv2 tv3");
}
