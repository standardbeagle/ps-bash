using PsBash.Testing;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Read-only names, namerefs and arrays as a whole script sees them. Hand-written asserts, expected values taken from
/// Ubuntu bash 5.2 running the same scripts as files (stderr differs by the "script: line N:" prefix, so only stdout and
/// the status are asserted).
/// </summary>
public class VariableAttributeScriptTests
{
    private static async Task<SpawnResult> RunAsync(string script)
    {
        var runner = PsBashRunner.Create().WithTimeout(60).WithMode(PsBashMode.M3_FileArg);
        Skip.If(runner.TryResolveBinary() is null, "ps-bash binary not found -- build PsBash.Shell first");
        return await runner.RunScriptAsync(script);
    }

    private static string Out(SpawnResult r) => r.Stdout.Replace("\r\n", "\n");

    // bash: under set -e a refused write to a read-only variable (status 1) ends the script; ps-bash went on.
    [SkippableFact]
    public async Task ReadonlyWrite_UnderErrexit_EndsTheScriptWithStatus1()
    {
        var r = await RunAsync("set -o errexit\nreadonly foo=bar\nfoo=eggs\necho \"status=$?\"\n");
        Assert.Equal("", Out(r));
        Assert.Equal(1, r.ExitCode);
    }

    // Without set -e the refused write is status 1 and the next line runs.
    [SkippableFact]
    public async Task ReadonlyWrite_WithoutErrexit_Status1AndGoesOn()
    {
        var r = await RunAsync("readonly foo=bar\nfoo=eggs\necho \"status=$? foo=$foo\"\n");
        Assert.Equal("status=1 foo=bar\n", Out(r));
    }

    // bash: `local y` over a read-only y is refused (local: y: readonly variable, status 1); x is still declared.
    [SkippableFact]
    public async Task Local_OverReadonlyName_IsRefusedWithStatus1()
    {
        var r = await RunAsync("f() {\n  readonly y\n  local x=1 y=$(( x ))\n  echo \"y=$y st=$? x=$x\"\n}\nf\necho \"y=$y x=$x\"\n");
        Assert.Equal("y= st=1 x=1\ny= x=\n", Out(r));
    }

    // bash: a nameref target must be a variable name or an element: `@`, `1`, `a b` are refused (status 1), and the name
    // stays an ordinary unset variable.
    [SkippableFact]
    public async Task Nameref_ToSpecialParameter_IsRefused()
    {
        var r = await RunAsync("set -- A B\ntypeset -n ref='@'\necho status=$?\necho ref=$ref\ndeclare -n r2=1\necho status=$? r2=$r2\n");
        Assert.Equal("status=1\nref=\nstatus=1 r2=\n", Out(r));
    }

    // bash: through `typeset -n ref='array[0]'`, `ref[0]=foo` and `ref=(x y)` would make the element an array: refused
    // (`array[0]': not a valid identifier, status 1), the array unchanged. A plain `ref=v` still writes the element.
    [SkippableFact]
    public async Task Nameref_ToElement_ArrayWriteIsRefused_ScalarWriteGoesThrough()
    {
        var r = await RunAsync("array=(X Y Z)\ntypeset -n ref='array[0]'\nref[0]=foo\necho status=$?\nref=(p q)\necho status=$?\necho ${array[@]}\nref=W\necho ${array[@]}\n");
        Assert.Equal("status=1\nstatus=1\nX Y Z\nW Y Z\n", Out(r));
    }

    // bash: under set -u, `${undef@Q}` / `@P` / `@a` are unbound (the subshell ends, status 1); a set name transforms.
    [SkippableFact]
    public async Task TransformOps_OnUnsetNameUnderNounset_AreUnbound()
    {
        var r = await RunAsync("set -u\n(echo ${undef@Q}); echo \"stat: $?\"\n(echo ${undef@P}); echo \"stat: $?\"\n(echo ${undef@a}); echo \"stat: $?\"\ny=v; echo \"${y@Q}\"\n");
        Assert.Equal("stat: 1\nstat: 1\nstat: 1\n'v'\n", Out(r));
    }

    // bash: `a=([k]=-{a,b}-)` on an array that is not associative brace-expands the element first: two plain values.
    // With `declare -A a` the element is a key and its value keeps the braces.
    [SkippableFact]
    public async Task KeyedElementWithBraces_NotAssociative_IsBraceExpandedIntoPlainValues()
    {
        var r = await RunAsync("a=([k2]=-{a,b}-)\necho \"${#a[@]} ${a[0]} ${a[1]}\"\ndeclare -A m\nm=([k2]=-{a,b}-)\necho \"${m[k2]}\"\n");
        Assert.Equal("2 [k2]=-a- [k2]=-b-\n-{a,b}-\n", Out(r));
    }

    // bash: a `readonly x` later in a function is not in force at an earlier `local x=...` / `x=1`. The body is emitted
    // more than once (stdin-scope probe), and the second emission saw the first one's read-only names.
    [SkippableFact]
    public async Task ReadonlyLaterInFunction_DoesNotRefuseAnEarlierWrite()
    {
        var r = await RunAsync("f() {\n  local v=555\n  echo \"v=$v st=$?\"\n  readonly -p v >/dev/null\n  local -p v\n}\nf\ng() {\n  x=1\n  readonly x\n  echo \"x=$x\"\n  x=2\n  echo \"st=$? x=$x\"\n}\ng\n");
        Assert.Equal("v=555 st=0\ndeclare -r v=\"555\"\nx=1\nst=1 x=1\n", Out(r));
    }

    // bash: `export a[7]=8` is refused (`a[7]': not a valid identifier) with status 1; ps-bash printed the error and
    // then reset the status to 0.
    [SkippableFact]
    public async Task Export_WithSubscript_IsStatus1()
    {
        var r = await RunAsync("export a[7]=8\necho status=$?\n");
        Assert.Equal("status=1\n", Out(r));
    }

    // bash: a subscript on a nameref to an ELEMENT reads nothing (`${r[@]}`, `${r[0]}`); ps-bash emitted `$a[@]`, which
    // is no PowerShell (the whole script failed to parse).
    [SkippableFact]
    public async Task Nameref_ToElement_SubscriptedRead_IsEmpty()
    {
        var r = await RunAsync("a=(X Y Z)\ntypeset -n r1='a[@]' r2='a[1]'\necho \"[${r1[@]}]\" \"[${r1[0]}]\" \"[${r2[0]}]\" \"[$r2]\" \"[${#r1[@]}]\"\n");
        Assert.Equal("[] [] [] [Y] [0]\n", Out(r));
    }

    // bash: `${@@a}` is the (empty) attribute letters of each positional parameter; `${A[@]@P}` expands each VALUE of an
    // associative array (ps-bash printed the parameters themselves, and the BashAssoc type name).
    [SkippableFact]
    public async Task TransformOps_OnPositionalsAndAssocValues_ApplyPerElement()
    {
        var r = await RunAsync("set -- a b c\necho \"[${@@a}]\" ${*@a} end\ndeclare -A A=([\"x\"]=\"y\")\necho ${A@P} - ${A[@]@P} - ${A[*]@P}\necho - y\n");
        Assert.Equal("[  ] end\n- y - y\n- y\n", Out(r));
    }

    // bash: `declare -i i` gives the attribute only: i stays unset (it was set to 0), an existing value is kept, and
    // `i+=3` still adds.
    [SkippableFact]
    public async Task DeclareInteger_WithoutValue_LeavesTheVariableUnset()
    {
        var r = await RunAsync("declare -i i\necho \"[$i]\" \"${i-unset}\"\ndeclare -p i\nx=5; declare -i x; echo x=$x\ndeclare -i n; n+=3; echo n=$n\n");
        Assert.Equal("[] unset\ndeclare -i i\nx=5\nn=3\n", Out(r));
    }

    // bash: `declare -p` prints a value holding a control character as $'...' (one line that reads back).
    [SkippableFact]
    public async Task DeclarePrint_ControlCharacters_UseAnsiCQuoting()
    {
        var r = await RunAsync("zz=$'one\\ntwo'\ndeclare -p zz\narr=($'p\\tq' r)\ndeclare -p arr\n");
        Assert.Equal("declare -- zz=$'one\\ntwo'\ndeclare -a arr=([0]=$'p\\tq' [1]=\"r\")\n", Out(r));
    }

    // bash: a sparse COMPREPLY (`unset -v 'COMPREPLY[2]'`) gives its values in index order; ps-bash printed the type name.
    [SkippableFact]
    public async Task CompgenF_SparseCompReply_PrintsTheValues()
    {
        var r = await RunAsync("_c() {\n  COMPREPLY=({0..9})\n  unset -v 'COMPREPLY[2]' 'COMPREPLY[4]' 'COMPREPLY[6]'\n}\ncompgen -F _c 2>/dev/null | tr '\\n' ' '; echo\n");
        Assert.Equal("0 1 3 5 7 8 9 \n", Out(r));
    }

    // bash: `a['2']=3` / `b[0+]=y` on an indexed (or new) array: the subscript is an arithmetic syntax error, status 1,
    // nothing assigned, and the assignments after it on that statement are not made. An associative array takes the same
    // text as a key, including one declared -A in a function (known only when it runs).
    [SkippableFact]
    public async Task NonArithmeticSubscript_OnIndexedArray_IsRefused_OnAssocIsAKey()
    {
        var r = await RunAsync("a[\"1\"]=2\necho status=$? len=${#a[@]}\na['2']=3\necho status=$? len=${#a[@]}\na[3 + '4']=5\necho status=$? len=${#a[@]}\n"
            + "x=x b[0+]=y c=z\necho \"[$x] [$b] [$c]\"\ndeclare -A m\nm['k']=v\nm[0+]=w\necho \"st=$? ${m['k']} ${m[0+]} ${#m[@]}\"\n"
            + "f() { declare -gA g; }\nf\ng['q']=1\necho \"g=${g[q]} st=$?\"\n");
        Assert.Equal("status=0 len=1\nstatus=1 len=1\nstatus=1 len=1\n[x] [] []\nst=0 v w 2\ng=1 st=0\n", Out(r));
    }

    // bash: a function defined ABOVE `a=(...)` reads `${a-d}` as element 0: set to "" is not unset. ps-bash only knew a
    // was an array after the function body was emitted, and `${a-u}` read a scalar that does not exist.
    [SkippableFact]
    public async Task DefaultOp_InFunctionDefinedBeforeTheArray_ReadsElementZero()
    {
        var r = await RunAsync("f() { echo \"[${a-u}] [${a:-e}] [$a]\"; }\na=()\nf\na=(\"\")\nf\na=(x y)\nf\n");
        Assert.Equal("[u] [e] []\n[] [e] []\n[x] [x] [x]\n", Out(r));
    }
}
