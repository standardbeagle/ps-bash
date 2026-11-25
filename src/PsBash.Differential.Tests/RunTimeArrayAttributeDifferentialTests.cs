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
        // Not m2: Windows reads $m2 as the environment's M2 (intentional-differences.md, names differing only in
        // case), and the GitHub Windows image sets M2 (Maven), so the after-call listing showed it.
        + "mm(){ declare -a \"$1\" \"$2\"; m1+=(z); declare -p m1 mb; }; mm 'm1=(a)' 'mb=(b c)'; declare -p m1 mb 2>&1\n"
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

    // A write after a run-time array declaration, in the same eval text: the declaration made the array the running
    // function's local, so `+=`, `[k]=`, `unset`, `read -a` and `mapfile` reach that local, and nothing is left after the
    // call. REGRESSION: the declaration was the local, the writes after it went to a global. Primitive: the eval text's
    // array write target is VarFrameStack.ArraySlot (resolved when it runs).
    [SkippableFact]
    public Task RunTimeArrayLocals_LaterWritesInTheSameEvalText_AreTheLocal() => Same(
        "f(){ eval \"$1\"; }\n"
        + "f 'local -a a=(1 2); a+=(3); declare -A m; m[k]=v; echo \"${a[*]} ${m[k]}\"'; echo \"[${a[*]}][${m[k]}]\"\n"
        + "f 'local -a u=(1 2 3); unset \"u[1]\"; echo \"u1:${u[*]}\"; unset u; echo \"u2:${u[*]-gone}\"'; echo \"[${u[*]-unset}]\"\n"
        + "f 'local -a r; read -a r <<< \"x y z\"; echo \"r:${r[*]}\"; declare -a q; mapfile -t q < <(printf \"1\\n2\\n\"); echo \"q:${q[*]}\"'; echo \"[${r[*]}][${q[*]}]\"\n"
        + "f 'declare -A h=([a]=1); h[b]=2; h+=([c]=3); echo \"h:${#h[@]}\"; local -a e; e[3]=x; e[0]=y; echo \"e:${!e[*]}\"'; echo \"[${#h[@]}][${e[*]}]\"\n"
        + "ca(){ local -a z=(caller); cb; echo \"ca:${z[*]}\"; }; cb(){ eval 'z+=(callee)'; }; ca; echo \"[${z[*]-unset}]\"\n"
        + "ga=(top); eval 'ga+=(more)'; echo \"ga:${ga[*]}\"\n"
        + "gb(){ eval 'gl+=(x)'; }; gb; gb; echo \"gl:${gl[*]}\"\n"
        + "c='declare -ga gx=(glob)'; gc(){ local -a gx=(loc); eval \"$c\"; echo \"in:${gx[*]}\"; }; gc; echo \"gx:${gx[*]}\"\n"
        + "( sz=(1); eval 'sz+=(2)'; echo \"sub:${sz[*]}\" ); echo \"[${sz[*]-none}]\"\n");

    // The same for a sourced file: what it declares in a function is the function's, and so are its later writes.
    [SkippableFact]
    public Task RunTimeArrayLocals_LaterWritesInTheSameSourcedFile_AreTheLocal() => Same(
        "printf 'declare -A M\\nM[k]=v\\ndeclare -a L=(a b)\\nL+=(c)\\n' > cfg.sh\n"
        + "g(){ source ./cfg.sh; echo \"${M[k]} ${L[*]}\"; }; g; echo \"[${L[*]}][${M[k]}]\"\n"
        + "printf 'local -a S=(1)\\nS+=(2)\\nS[5]=6\\nunset \"S[0]\"\\nread -a R <<< \"p q\"\\ndeclare -a R2\\nread -a R2 <<< \"s t\"\\n"
        + "mapfile -t MP <<< \"m1\"\\nlocal -a MQ; mapfile -t MQ <<< \"n1\"\\necho \"S:${S[*]} R2:${R2[*]} MQ:${MQ[*]}\"\\n' > src2.sh\n"
        + "k(){ source ./src2.sh; }; k; echo \"[${S[*]}][${R2[*]}][${MQ[*]}][${R[*]}][${MP[*]}]\"\n"
        + "source ./cfg.sh; echo \"top: ${M[k]} ${L[*]}\"\n");

    // Eval and sourced text writes through the run-time attribute table: an attribute its caller set, at run time or not,
    // holds there (`v=n; declare -i "$v"; source ./w.sh` with `n=2+3` in w.sh prints 5).
    [SkippableFact]
    public Task AttributesFromTheCaller_HoldInEvalAndSourcedText() => Same(
        "printf 'n=2+3\\necho \"w:$n\"\\nu=ab\\necho \"u:$u\"\\n' > w.sh\n"
        + "v=n; declare -i \"$v\"; source ./w.sh\n"
        + "unset n; declare -i n; declare -u u; source ./w.sh\n"
        + "c='n=4*4; u=cd'; eval \"$c\"; echo \"c:$n $u\"\n"
        + "f(){ local -i n; source ./w.sh; }; f; echo \"after:$n\"\n");

    // `read -a` / `mapfile` fill the array; for the shell's global its element 0 is `$name`, while a function's local
    // array leaves nothing behind. REGRESSION: the joined value went to the shell variable, a global: `$r` was `x y` and
    // `f(){ local -a r; read -a r <<< "x y"; }; f; echo ${r[*]}` printed x y.
    [SkippableFact]
    public Task WholeArrayBuiltins_FillTheArray_AndALocalLeavesNoGlobal() => Same(
        "s1(){ local -a r; read -a r <<< \"x y z\"; echo \"s1:${r[*]}\"; }; s1; echo \"[${r[*]}]\"; declare -p r 2>&1\n"
        + "ra=old; read -a ra <<< \"x y\"; echo \"ra:$ra ${ra[1]}\"; declare -p ra\n"
        + "read -a rb <<< \"p q\"; echo \"rb:$rb ${rb[*]}\"\n"
        + "mq=old; mapfile -t mq <<< \"m1\"; echo \"mq:$mq\"\n"
        + "h(){ read -a hr <<< \"3 4\"; }; h; echo \"hr:${hr[*]}\"\n"
        + "k(){ local -a ka=(0); kb; echo \"k:${ka[*]}\"; }; kb(){ read -a ka <<< \"5 6\"; }; k; echo \"[${ka[*]-unset}]\"\n");

    // `declare -g a=(...)` sets the shell's global even while a running function (this one or a caller) holds a local
    // array of the name, which keeps its value and stays the one the function reads; a later `a+=(x)` and `readonly`
    // write that local. REGRESSION: `f(){ local -a g=(l); declare -ga g=(x); }; f; echo ${g[*]}` printed nothing.
    [SkippableFact]
    public Task GlobalArrayDeclarations_WriteTheGlobal_OverAFunctionsLocal() => Same(
        "gd(){ local -a gy=(loc); declare -ga gy=(glob); echo \"in:${gy[*]}\"; gy+=(x); declare -ga gy+=(more); echo \"in2:${gy[*]}\"; }; gd; echo \"gy:${gy[*]}\"\n"
        + "ge(){ local -A gm=([k]=loc); declare -gA gm=([k]=glob); echo \"in:${gm[k]}\"; }; ge; echo \"gm:${gm[k]}\"\n"
        + "outer(){ local -a go=(outer); inner; echo \"outer:${go[*]}\"; }; inner(){ declare -ga go=(glob); }; outer; echo \"go:${go[*]}\"\n"
        + "gh(){ declare -ga gn=(1 2); echo \"in:${gn[*]}\"; }; gh; echo \"gn:${gn[*]}\"\n"
        + "gs(){ local -a gw=(loc); ( declare -ga gw=(sub); echo \"sub:${gw[*]}\" ); echo \"in:${gw[*]}\"; }; gw=(top); gs; echo \"gw:${gw[*]}\"\n"
        + "gr(){ local -a gq=(loc); readonly -a gq=(ro) 2>&1; echo \"in:${gq[*]}\"; }; gr; echo \"gq:${gq[*]-unset}\"\n"
        + "gt(){ local -a gv=(loc); eval 'declare -ga gv=(e1); gv+=(e2)'; echo \"in:${gv[*]}\"; }; gt; echo \"gv:${gv[*]}\"\n"
        + "printf 'declare -ga GS=(file)\\nGS+=(more)\\n' > gs.sh; gu(){ local -a GS=(loc); source ./gs.sh; echo \"in:${GS[*]}\"; }; gu; echo \"GS:${GS[*]}\"\n");

    [SkippableFact]
    public Task StaticEvalText_InAFunction_IsTheFunctionsOwnCode() => Same(
        "e1(){ eval 'declare -a ea=(7 8)'; eval 'declare -i ei=2*3'; ei+=1; ea+=(9); echo \"e1: ${ea[*]} $ei\"; }\n"
        + "e1; declare -p ea ei 2>&1\n"
        + "e2(){ eval 'declare -n er=ex'; er=set; }; ex=orig; e2; echo \"ex=$ex\"; declare -p er 2>&1\n"
        + "s2(){ declare -i si=2*3; si+=1; declare -l sl=AB; sl+=CD; echo \"s2: $si $sl\"; }; s2; echo \"after[${si-u}${sl-u}]\"\n"
        + "declare -l tl; tl=AB; tl+=CD; echo \"tl=$tl\"; declare -u tu=ab; tu+=cd; echo \"tu=$tu\"\n");
}
