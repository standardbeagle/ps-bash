using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Arrays, references and attributes declared by text built at run time, as bash 5.2 has them.
/// <para>An array or nameref declared that way inside a function is the function's local (<c>local -a "$1"</c>,
/// <c>declare -A "$2"</c>, <c>declare -n "$3"</c>, <c>eval 'declare -a a=(1)'</c>), while at the top level, and with
/// <c>-g</c> or <c>readonly</c>, it is global. REGRESSION: every one was global and outlived the call. Primitive:
/// <c>PsBuild.WhenInFunction</c> (the two forms, chosen when the text runs).</para>
/// <para>An attribute set that way holds for the writes after it (<c>eval 'declare -i n'; n=2*3</c> stores 6,
/// <c>-u</c>/<c>-l</c> convert). REGRESSION: the later write was the plain one. Primitive: <c>PsBuild.AssignAttributed</c>
/// over <c>BashShellState.AssignScalar</c>, emitted only in a unit that runs such text.</para>
/// </summary>
public class RunTimeArrayAttributeDifferentialTests
{
    private static Task Same(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task RunTimeArrayAndNamerefDeclarations_AreTheFunctionsLocals() => Same(
        "f(){ local -a \"$1\"; declare -A \"$2\"; declare -n \"$3\"; declare -p a m r; }\n"
        + "x=X; f 'a=(1 2)' m 'r=x'; declare -p a m r 2>&1; echo \"st=$?\"\n"
        + "g(){ local -a 'b=(3 4)'; declare -a c; c=(5 6); echo \"in: ${b[1]} ${c[0]}\"; }; g; declare -p b c 2>&1\n"
        + "h(){ eval 'local -A hm=([k]=v)'; eval 'declare -a ha=(7 8)'; hm[j]=w; ha+=(9); declare -p hm ha; }; h; declare -p hm ha 2>&1\n"
        + "ga=(top); k(){ declare -a \"ga=(inner)\"; echo \"k: ${ga[*]}\"; }; k; echo \"after: ${ga[*]}\"\n"
        + "n(){ declare -n \"$1\"; rr=changed; }; y=orig; n 'rr=y'; echo \"y=$y\"; declare -p rr 2>&1\n"
        + "q(){ declare -a \"$1\"; inner; declare -p qa; }; inner(){ qa[5]=five; }; qa=(outer); q 'qa=(x)'; declare -p qa\n"
        + "mm(){ declare -a \"$1\" \"$2\"; m1+=(z); declare -p m1 m2; }; mm 'm1=(a)' 'm2=(b c)'; declare -p m1 m2 2>&1\n"
        + "cv(){ local -A cm; c='local -a cm'; eval \"$c\"; echo \"cs=$?\"; declare -p cm; }; cv 2>/dev/null\n"
        + "rd(){ local -a 'ra=(1)'; local -a 'ra=(2 3)'; declare -p ra; }; rd; declare -p ra 2>&1\n");

    [SkippableFact]
    public Task RunTimeArrayDeclarations_GlobalByRequestOrAtTheTop_StayGlobal() => Same(
        "p(){ declare -ga \"gg=(1 2)\"; declare -Ag \"$1\"; }; p 'gh=([a]=b)'; declare -p gg gh\n"
        + "g(){ eval 'declare -ga gx=(1)'; eval 'readonly -a rx=(2)'; c='declare -gA gm=([k]=v)'; eval \"$c\"; }; g; declare -p gx rx gm 2>&1\n"
        + "eval 'declare -a ta=(1 2)'; c='declare -A tm=([x]=y)'; eval \"$c\"; declare -p ta tm\n");

    [SkippableFact]
    public Task QuotedArrayValues_WithAnArrayFlag_AreCompoundAssignments() => Same(
        "q1(){ declare 'qa=(1 2)'; local 'qb=(3 4)'; declare -a \"qc=(5 6)\"; declare -p qa qb qc; }; q1 2>&1\n"
        + "hv=H; q2(){ declare -a \"$1\"; declare -p qe; }; q2 'qe=($(echo hi) $hv x\\ y)' 2>&1\n"
        + "q3(){ declare -A \"$1\"; declare -p qm; }; q3 'qm=([a]=1 [b]=\"2 3\")' 2>&1\n"
        + "i2(){ local -a \"$1\"; declare -p ib 2>&1; }; i2 'ib=(`echo bq` \"$(echo cs)\" {p,q})' 2>&1\n"
        + "declare -a a='(1 2)'; declare -p a; declare e='(1 2)'; declare -p e; x=(9); declare -a x+='(10)'; declare -p x\n"
        + "f(){ local -a 'c=(3 4)'; typeset -A \"m=([k]=v)\"; local -a d=\"(5 6)\"; declare -p c m d; }; f\n");

    [SkippableFact]
    public Task AttributesSetAtRunTime_HoldForTheWritesAfterThem() => Same(
        "f(){ eval 'declare -i iv=2*3'; iv+=1; echo $iv; }; f\n"
        + "g(){ eval 'local -i n=1+1'; echo $n; n=3+4; echo $n; }; g\n"
        + "eval 'declare -u u'; u=ab; echo $u\n"
        + "eval 'declare -l lo'; lo=XY; echo $lo; lo+=ZZ; echo $lo\n"
        + "h(){ eval 'local -u hu'; hu=cd; hu+=ef; echo $hu; }; h; echo \"top[${hu-unset}]\"\n"
        + "w(){ eval 'declare -i wi=1'; (( wi += 2 )); echo \"wi=$wi\"; let wi=wi*3; echo \"wi=$wi\"; }; w\n"
        + "d='declare -i'; r1(){ $d ri=2*3; ri+=1; echo \"r1=$ri\"; }; r1\n"
        + "r2(){ local -i \"$1\"; rn=4*4; echo \"r2=$rn\"; }; r2 rn\n"
        + "r3(){ declare \"$1\" rc; rc=MiXed; rc+=Up; echo \"r3=$rc\"; }; r3 -u; r3 -l\n"
        + "r4(){ eval \"$1\"; rv=5+5; rv+=1; echo \"r4=$rv\"; }; r4 'declare -i rv'\n"
        + "x=5; r5(){ eval 'declare -i x'; x+=2; echo \"r5=$x\"; }; r5; echo \"x=$x\"\n"
        + "r6(){ declare -n \"$1\"; nr=via; echo \"r6=$nt\"; }; nt=orig; r6 nr=nt; echo \"nt=$nt\"\n"
        + "ni(){ eval 'declare -i nv'; declare -n nref=nv; nref=3*3; echo \"nv=$nv\"; }; ni\n"
        + "xu(){ eval 'declare -u xv'; xv=ab; export xv; xv+=cd; echo \"$xv\"; env | grep '^xv='; }; xu\n"
        + "dz=1; $d dq; x=$(false); echo \"s1=$?\"; y=ok; echo \"s2=$?\"; false; z=1; echo \"s3=$?\"\n");

    [SkippableFact]
    public Task StaticEvalText_InAFunction_IsTheFunctionsOwnCode() => Same(
        "e1(){ eval 'declare -a ea=(7 8)'; eval 'declare -i ei=2*3'; ei+=1; ea+=(9); echo \"e1: ${ea[*]} $ei\"; }\n"
        + "e1; declare -p ea ei 2>&1\n"
        + "e2(){ eval 'declare -n er=ex'; er=set; }; ex=orig; e2; echo \"ex=$ex\"; declare -p er 2>&1\n"
        + "s2(){ declare -i si=2*3; si+=1; declare -l sl=AB; sl+=CD; echo \"s2: $si $sl\"; }; s2; echo \"after[${si-u}${sl-u}]\"\n"
        + "declare -l tl; tl=AB; tl+=CD; echo \"tl=$tl\"; declare -u tu=ab; tu+=cd; echo \"tu=$tu\"\n");
}
