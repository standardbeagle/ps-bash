using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A nameref whose target is known only at run time (<c>declare -n r; r=$v</c>, <c>local -n r; r=$1</c>) is read as its
/// TARGET in every expansion form: the scalar operators, <c>${!r}</c> (the name it stands for), <c>${r@a}</c>, the
/// assigning and error forms, <c>[[ -v r ]]</c>, and every array form when the target is an array. REGRESSION: only
/// <c>$r</c> and <c>${r}</c> followed it; <c>${r[@]}</c> read nothing, <c>${!r}</c> expanded r's value, and
/// <c>${r:=x}</c> was a PowerShell parse error that lost the whole script.
/// </summary>
public class NamerefRuntimeReadTests
{
    [SkippableFact]
    public Task ScalarForms_ReadTheTarget() => AssertOracle.EqualAsync(
        "declare -n r; v=tg; r=$v; tg=\"hello world\"\n"
        + "echo \"[${r:-x}] [${r-x}] [${r:+set}] [${r+set}] [${#r}]\"\n"
        + "echo \"[${r#h}] [${r##*o}] [${r%d}] [${r%%o*}]\"\n"
        + "echo \"[${r/o/0}] [${r//o/0}] [${r/#h/H}] [${r/%d/D}]\"\n"
        + "echo \"[${r^^}] [${r^}] [${r,,}] [${r@Q}] [${r@U}] [${r:1:3}] [${r: -2}]\"\n"
        + "for w in ${r}; do echo \"w=$w\"; done\n"
        + "unset tg; echo \"[${r:-unset}] [${#r}] [${r+set}]\"");

    [SkippableFact]
    public Task Indirect_IsTheNameItStandsFor() => AssertOracle.EqualAsync(
        "declare -n r; v=tg; r=$v; echo \"[${!r}]\"\n"
        + "declare -n b=c; declare -n a; w=b; a=$w; echo \"[${!a}]\"\n"
        + "declare -n n0; (echo \"[${!n0}]\"); echo \"st=$?\"");

    [SkippableFact]
    public Task ArrayTarget_EveryFormReadsIt() => AssertOracle.EqualAsync(
        "declare -n ar; w=arr; ar=$w; arr=(a \"b c\" d)\n"
        + "echo \"[${ar[@]}] [${ar[1]}] [${#ar[@]}] [${!ar[@]}] [${ar}] [${ar@a}]\"\n"
        + "for e in \"${ar[@]}\"; do echo \"e=$e\"; done\n"
        + "x=(\"${ar[@]}\"); echo \"x=${#x[@]}\"\n"
        + "echo \"[${ar[@]:1}] [${ar[*]^^}] [${ar[@]/b/B}]\"\n"
        + "[[ -v ar[1] ]]; echo \"v1=$?\"; [[ -v ar[7] ]]; echo \"v7=$?\"\n"
        + "ar+=(z); ar[0]=A; declare -p arr ar\n"
        + "declare -n as; z=m; as=$z; declare -A m=([k]=v)\n"
        + "echo \"[${as[k]}] [${!as[@]}] [${as@a}]\"");

    [SkippableFact]
    public Task SetTestAndListing_FollowTheTarget() => AssertOracle.EqualAsync(
        "declare -n r; v=tg; r=$v; [[ -v r ]]; echo \"unset=$?\"; tg=1; [[ -v r ]]; echo \"set=$?\"\n"
        + "declare -n q; [[ -v q ]]; echo \"none=$?\"\n"
        + "declare -p r; set | grep -c '^r='; declare -n ar; w=arr; ar=$w; arr=(1 2); declare -p | grep -c '^declare -a ar='");

    [SkippableFact]
    public Task AssignAndErrorForms_GoThroughTheReference() => AssertOracle.EqualAsync(
        "declare -n e1; w=tg1; e1=$w; : ${e1:=dflt}; declare -p tg1\n"
        + "tg2=; declare -n e2; w=tg2; e2=$w; echo \"[${e2:=d2}] [${e2=d3}]\"; declare -p tg2\n"
        + "declare -n u; w=tu; u=$w; (echo ${u?}); (echo ${u:?boom}); echo \"[${u-unset}] [${u+set}]\"");

    [SkippableFact]
    public Task InAFunction_ALocalNamerefFromAParameter() => AssertOracle.EqualAsync(
        "g() { local -n lr; lr=$1; echo \"[${lr:-none}] [${#lr}] [${lr^^}] [${!lr}] [${lr[@]}]\"; }\n"
        + "tf=abc; g tf; ta=(p q); g ta; echo \"[${lr-gone}]\"");

    /// <summary>REGRESSION: in a subshell (or function) that also assigned <c>ar</c>, <c>${ar[@]}</c> read a copy taken when
    /// ar was declared (PowerShell's local slot), and <c>ar+=(z)</c> replaced the reference instead of writing arr.</summary>
    [SkippableFact]
    public Task ArrayTarget_InASubshellThatWritesIt() => AssertOracle.EqualAsync(
        "( declare -n ar; w=arr; ar=$w; arr=(a \"b c\" d); echo \"[${ar[1]}] [${#ar[@]}]\"; ar+=(z); ar[0]=A; declare -p arr ar )\n"
        + "g() { local -n gr; gr=$1; gr+=(y); echo \"[${gr[@]}]\"; }; ga=(x); g ga; declare -p ga");

    /// <summary>REGRESSION: <c>declare -n r=$v</c> dropped the attribute, so r was a plain variable holding v's value.</summary>
    [SkippableFact]
    public Task DeclaredWithARunTimeTarget_IsAReference() => AssertOracle.EqualAsync(
        "v=tg; tg=hi; declare -n r=$v; echo \"[$r] [${r^^}] [${!r}]\"; declare -p r; r=new; echo \"tg=$tg\"\n"
        + "(w=tg; declare -n e4=$w e5=\"$w\"; echo \"[$e4] [$e5]\")\n"
        + "(w=arr; arr=(a b); declare -n e6=$w; e6+=(q); echo \"${arr[*]} [${e6[1]}]\")\n"
        + "(w=2x; declare -n e=$w; echo \"st=$?\"); (w='a b'; declare -n e2=$w; echo \"st=$?\")");

    /// <summary>REGRESSION: <c>local -n q=$1</c> called with a name the transpiler cannot see (<c>f "$nm"</c>) was a plain local.</summary>
    [SkippableFact]
    public Task LocalFromAParameter_ReadsAndWritesWhatItNames() => AssertOracle.EqualAsync(
        "f() { local -n q=$1; echo \"[${q[@]}] [${#q[@]}] [${!q}]\"; q+=(z); q[0]=A; }\n"
        + "arr=(a b); f arr; echo \"${arr[*]}\"; nm=arr; f \"$nm\"; echo \"${arr[*]}\"\n"
        + "h() { local -n p=\"$1\"; p=set_by_h; }; nm=tg; h \"$nm\"; echo \"tg=$tg\"\n"
        + "k() { local n=$1; local -n out=$n; out=$2; }; k tg kval; echo \"tg=$tg\"\n"
        + "m() { local -n mr=$1; mr=(x y); }; m \"$nm\"; declare -p tg");

    [SkippableFact]
    public Task Attributes_AreThoseOfTheTarget() => AssertOracle.EqualAsync(
        "declare -i ti=3; declare -n ri; w=ti; ri=$w; echo \"[${ri@a}]\"; tp=1; declare -n rp; w=tp; rp=$w; echo \"[${rp@a}]\"");

    /// <summary>Found here, true of every variable: with no word, <c>${x?}</c> says "parameter not set" (not the colon form's text).</summary>
    [SkippableFact]
    public Task ErrorForm_WithNoWord_IsParameterNotSet() => AssertOracle.EqualAsync(
        "(echo ${zz?}); (echo ${zz:?}); zz=; (echo ${zz:?}); (echo ${zz?}ok)");
}
