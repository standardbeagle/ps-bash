using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>declare</c>/<c>typeset</c> run from text built at run time (<c>declare "$2"</c>, <c>builtin declare v=$1</c>,
/// <c>eval "$c"</c>, a sourced file, <c>$dc v=1</c>) inside a function makes the function's local, as bash 5.2 does;
/// <c>-g</c> keeps it global, an inner function's declaration is local to the inner one (dynamic scope), and at the
/// top level it is global. REGRESSION: such text was emitted without its function, so every declaration was global
/// and outlived the call (<c>declare -p dd e t</c> found all three). Primitive: <c>PsBuild.LocalWhenInFunction</c> over
/// <c>PsBuild.RunTimeLocal</c>, the run-time <c>local</c>'s own frame. Arrays and namerefs:
/// <c>RunTimeArrayAttributeDifferentialTests</c>.
/// </summary>
public class RunTimeDeclareScopeDifferentialTests
{
    private static Task Same(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task Declare_InTextBuiltAtRunTime_IsTheFunctionsLocal() => Same(
        "f(){ builtin declare dd=$1; declare \"$2\"; typeset \"t=$3\"; echo \"in[$dd][$e][$t]\"; }; f a 'e=b' c\n"
        + "declare -p dd e t 2>/dev/null; echo \"p=$?\"\n"
        + "e1(){ c='declare d1=p'; eval \"$c\"; echo \"in1[$d1]\"; }; e1; echo \"out1[${d1-unset}]\"\n"
        + "e2(){ c='typeset -i d2=1+2'; eval \"$c\"; echo \"in2[$d2]\"; }; e2; echo \"out2[${d2-unset}]\"\n"
        + "printf 'declare sx=1\\n' > lib; s1(){ . ./lib; echo \"ins[$sx]\"; }; s1; echo \"outs[${sx-unset}]\"\n"
        + "w1(){ dc=declare; $dc wv=1; command declare cv=2; declare -- dv=3; echo \"inw[$wv][$cv][$dv]\"; }; w1\n"
        + "echo \"outw[${wv-unset}][${cv-unset}][${dv-unset}]\"\n"
        + "c='declare tl=9'; eval \"$c\"; echo \"toplevel[$tl]\"\n"
        + "n1(){ c='declare nn=1'; ( eval \"$c\"; echo \"sub[$nn]\" ); echo \"after[${nn-unset}]\"; }; n1\n");

    [SkippableFact]
    public Task Declare_GlobalExportedNestedAndAttributes_FollowBash() => Same(
        "g1(){ x='gg=1'; declare -g \"$x\"; c='declare -g gh=2'; eval \"$c\"; declare \"$1\"; }; g1 'lv=3'\n"
        + "echo \"g[${gg-unset}][${gh-unset}][${lv-unset}]\"\n"
        + "x1(){ v='xv=3'; declare -x \"$v\"; env | grep -c '^xv='; }; x1; echo \"x[${xv-unset}]\"; env | grep -c '^xv='\n"
        + "in1(){ declare \"$1\"; echo \"inner[$nv]\"; }\n"
        + "out1(){ nv=outer; in1 'nv=inner'; echo \"outerafter[$nv]\"; }; out1; echo \"top[${nv-unset}]\"\n"
        + "d3(){ declare \"$1\"; d4 \"$2\"; echo \"d3[$dn]\"; }; d4(){ declare \"$1\"; echo \"d4[$dn]\"; }; d3 dn=3 dn=4; echo \"dtop[${dn-unset}]\"\n"
        // The same park slot for a run-time `local` (REGRESSION: the inner call took the caller's slot as its own).
        + "l3(){ local \"$1\"; l4 \"$2\"; echo \"l3[$ln]\"; }; l4(){ local \"$1\"; echo \"l4[$ln]\"; }; l3 ln=3 ln=4; echo \"ltop[${ln-unset}]\"\n"
        + "p1(){ c='local pa+=x'; eval \"$c\"; eval \"$c\"; p2; echo \"p1[$pa]\"; }; p2(){ c='local pa+=y'; eval \"$c\"; echo \"p2[$pa]\"; }\n"
        + "pa=top; p1; echo \"ptop[$pa]\"\n"
        + "w2(){ local wl=a; w3; echo \"w2[$wl]\"; }; w3(){ dc=declare; $dc wl=b; echo \"w3[$wl]\"; }; w2; echo \"wtop[${wl-unset}]\"\n"
        + "r1(){ c='declare -r ro=1'; eval \"$c\"; echo \"inr[$ro]\"; }; r1; echo \"outr[${ro-unset}]\"; ro=2 2>/dev/null && echo \"rw[$ro]\"\n"
        + "a1(){ c='declare s+=x'; eval \"$c\"; eval \"$c\"; echo \"ina[$s]\"; }; s=top; a1; echo \"outa[$s]\"\n"
        + "k1(){ c='declare kv=$kv-in'; eval \"$c\"; echo \"ink[$kv]\"; }; kv=top; k1; echo \"outk[$kv]\"\n"
        + "u1(){ c='declare uv'; eval \"$c\"; uv=set; echo \"inu[$uv]\"; }; uv=top; u1; echo \"outu[$uv]\"\n"
        + "i1(){ c='declare -i iv=2*3'; eval \"$c\"; echo \"ini[$iv]\"; }; i1; echo \"outi[${iv-unset}]\"\n"
        // The written top-level `NAME+=` forms (REGRESSION: `$env:s+ = 'x'` failed the parse of the whole script).
        + "ap=a; declare ap+=x; an=3; declare -i an+=2; au=ab; declare -u au+=cd; typeset -x ax+=q; echo \"ap[$ap][$an][$au][$ax]\"\n");
}
