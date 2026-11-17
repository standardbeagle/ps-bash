using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A subscript is a word: quotes, nested <c>${…}</c> and nested <c>[…]</c> stay inside it, and its expansions are
/// expanded first. <c>${a[$i+1]}</c>, <c>${m["$k b"]}</c> and <c>${m[${k}b]}</c> looked up the raw text;
/// <c>m["${a[@]}"]=v</c> was run as a command; <c>${m["${a[@]}"]}</c> ended at the inner <c>]</c>.
/// </summary>
public class ArraySubscriptWordTests
{
    [SkippableFact]
    public Task SubscriptWords_AreExpanded() => AssertOracle.EqualAsync(
        "a=(x y z); i=1; echo ${a[$i+1]} ${a[\"1\"]} \"${a[$i]}\" ${a[${i}]} \"${a[\"$i\"]}\"\n"
        + "declare -A m; m[ab]=1; m[\"a b\"]=2; m['$k']=lit; k=a; echo ${m[${k}b]} \"${m[\"$k b\"]}\" \"${m['a b']}\" \"${m['$k']}\" \"${m[\\$k]}\"\n"
        + "echo \"${m[\"$k\"b]:-none}\" \"${m[$k$k]-unset}\"\n"
        + "i='1;echo pwn'; echo \"${m[\"$i\"]-safe}\"\n"
        + "declare -A assoc; array=(1 2 3); assoc[\"${array[@]}\"]=foo; assoc[42]=43\n"
        + "echo \"${assoc[\"${array[@]}\"]}\" ${assoc[\"${array[@]}\"]:-d} \"${#assoc[\"${array[@]}\"]}\"\n"
        + "for e in \"${!assoc[@]}\"; do echo \"$e\"; done | sort\n"
        + "n=(5 0 9); echo \"${n[n[1]]}\" \"${n[n[1]+1]}\" \"${n[ n[1] ]}\"");

    // A scalar and element 0 are the same storage: `a=z` on an array writes a[0]; `c[1]=t` on a scalar keeps it as c[0].
    [SkippableFact]
    public Task ScalarAndElementZero_AreOneVariable() => AssertOracle.EqualAsync(
        "b[0]=x; echo \"$b\" ${b}; b=y; echo \"$b ${b[0]}\"; unset b; echo \"[$b]\"\n"
        + "c=s; c[1]=t; echo \"$c ${c[1]}\"; c=u; echo \"$c ${c[@]}\"\n"
        + "f() { local d; d[0]=in; echo \"$d\"; }; f; echo \"[$d]\"\n"
        + "e[3]=z; echo \"[$e]\"; e=w; echo \"${e[@]}\"\n"
        + "a=(x y); a+=q; echo \"${a[@]}\"\n"
        + "declare -A m; m[1]=1; echo \"[${m}]\"; m[0]=zero; echo \"$m\"; m=string; echo \"${m[1]} $m\"");

    // `a=(1 10); (( a++ ))` / `let b=7`: arithmetic on an array's bare name writes element 0 (it replaced the whole
    // array with the number).
    [SkippableFact]
    public Task ArithmeticOnArrayName_WritesElementZero() => AssertOracle.EqualAsync(
        "a=(1 10); (( a++ )); echo \"${a[@]}\"; (( a+=5 )); echo \"${a[@]}\"; b=(3 4); let b=7; echo \"${b[@]}\"\n"
        + "declare -A m=([x]=1); (( m=4 )); declare -p m; echo $(( m + 1 ))\n"
        + "f() { local -a c=(5 6); (( c *= 2 )); echo \"${c[@]}\"; }; f\n"
        + "for i in 1 2; do (( i += 10 )); echo $i; done\n"
        + "s=3; (( s++ )); echo $s; e[2]=9; (( e = 1 )); declare -p e");

    // REGRESSION: a builtin's `NAME[subscript]` operand is resolved at run time as bash does (BashArrayElement):
    // `printf -v 'm[$k]'` added a key named `$k`, `printf -v 'a[$i]'` replaced the array with a scalar, and an
    // arithmetic write to a scalar's element dropped the scalar's value.
    [SkippableFact]
    public Task RunTimeElementOperand_ResolvesLikeBash() => AssertOracle.EqualAsync(
        "declare -A m; k=K\n"
        + "printf -v \"m['q']\" x1; printf -v 'm[\"$k\"]' x2; printf -v 'm[${k}2]' x3; printf -v 'm[a b]' x4; printf -v 'm[$(echo c)]' x5\n"
        + "for e in \"${!m[@]}\"; do echo \"$e=${m[$e]}\"; done | sort\n"
        + "a=(0 1 2); i=1\n"
        + "printf -v 'a[$i]' y1; printf -v 'a[i+1]' y2; printf -v 'a[-1]' y3; printf -v 'a[5]' y4; printf -v 'a[$(echo 0)]' y5\n"
        + "declare -p a\n"
        + "printf -v 'm[$u]' z 2>/dev/null; echo st=$?; printf -v 'a[-9]' z 2>/dev/null; echo st=$?\n"
        + "x=5; printf -v 'x[2]' z; declare -p x; s=3; (( s[2] = 7 )); declare -p s\n"
        + "f() { printf -v 'nw[1]' q; (( na[1] = 4 )); }; f; declare -p nw na\n"
        + "unset 'm[$k]' \"m['q']\"; for e in \"${!m[@]}\"; do echo \"$e\"; done | sort");

    // `ref='A[$key]'; ${!ref}`: the variables in an indirect subscript expand (single quotes keep them literal).
    [SkippableFact]
    public Task IndirectSubscript_ExpandsVariables() => AssertOracle.EqualAsync(
        "declare -A A=(['K']=val ['a b']=sp ['$key']=lit)\n"
        + "key=K; ref='A[$key]'; echo \"1 ${!ref}\"\nref='A[\"$key\"]'; echo \"2 ${!ref}\"\nref='A[${key}]'; echo \"3 ${!ref}\"\n"
        + "ref=\"A['\\$key']\"; echo \"4 ${!ref}\"\nk2='a b'; ref='A[$k2]'; echo \"5 ${!ref}\"\n"
        + "a=(p q r); i=1; ref='a[$i+1]'; echo \"7 ${!ref}\"");
}
