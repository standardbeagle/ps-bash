using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>Expansion corners: <c>}</c> as an argument, slices of unset variables and of <c>$@</c>, <c>${arr[i]=d}</c>.</summary>
public class ExpansionCornerTests
{
    [SkippableFact]
    public Task RBrace_AfterCommandWord_IsAnArgument() => AssertOracle.EqualAsync(
        "rbrace() { echo }; }; rbrace; { echo a; }; echo } {");

    [SkippableFact]
    public Task Slice_OfUnsetVariable_IsEmpty() => AssertOracle.EqualAsync(
        "echo -${undef:1:2}- -${undef:3}-; x=abcdef; echo ${x:1:2} ${x:2}");

    [SkippableFact]
    public Task Slice_OfUnsetVariable_UnderNounset_IsUnbound() => AssertOracle.EqualAsync(
        "( set -o nounset; echo -${undef:1:2}-; echo not-reached ) 2>/dev/null; echo status=$?");

    [SkippableFact]
    public Task PositionalSlice_CountsFromDollarZero() => AssertOracle.EqualAsync(
        "set a b c; echo ${@:1:1}; echo ${@:2}; echo ${@: -1}; echo ${@:1}; echo \"${@:2:2}\"; echo ${@:5}; echo x ${@:9} y\n"
        + "r=$(echo ${@:0:1}); echo ${r//\"$0\"/SHELL}; f() { echo ${@:2}; echo ${*:1:2}; }; f x y z");

    /// <summary>The colon that ends a slice offset is not one that closes a `?` in it; an empty bound is 0.</summary>
    [SkippableFact]
    public Task Slice_TernaryOffsetAndEmptyBounds() => AssertOracle.EqualAsync(
        "s=abcd; echo ${s: 0 < 1 ? 2 : 0 : 1} ${s:1>0?1:3} ${s:(1?2:3):1}; a=(a b c d); echo [${a[@]: }] [${a[@]: : }] [${a[@]:: }]");

    /// <summary>An operator result glued to text is split like the bare word (`${v:-AxBxC}x` with IFS=x is A B Cx).</summary>
    [SkippableFact]
    public Task OperatorResultGluedToText_IsSplit() => AssertOracle.EqualAsync(
        "p() { printf '[%s]' \"$@\"; echo; }; IFS=x; v=; p ${v:-AxBxC}x; p ${v:-\"AxBxC\"}x; p \"${v:-AxBxC}\"x\n"
        + "IFS=' '; f='a b.c'; p ${f%.c}.o; p q${f/b/c d}; p ${f:+x y}z");

    /// <summary>A line continuation is removed after `$` (`$\&lt;nl&gt;?` is `$?`) and inside `$(( ))`.</summary>
    [SkippableFact]
    public Task LineContinuation_AfterDollarAndInArithmetic_IsRemoved() => AssertOracle.EqualAsync(
        "true; echo $\\\n?; x=5; echo $\\\nx; echo $((\n1 +\n2 + \\\n3\n))");

    /// <summary>`[[ $HOME =~ ~ ]]`: the tilde expands and is matched as text (HOME='^a$' matches itself).</summary>
    [SkippableFact]
    public Task RegexRhsTilde_ExpandsAndMatchesLiterally() => AssertOracle.EqualAsync(
        "HOME=foo; [[ ~ =~ $HOME ]]; echo $?; [[ $HOME =~ ~ ]]; echo $?\n"
        + "HOME='^a$'; [[ ~ =~ $HOME ]]; echo $?; [[ $HOME =~ ~ ]]; echo $?; [[ xx/^a$/y =~ ~/y ]]; echo $?");

    /// <summary>`(( x = A['spam'] ))`: a quoted associative key drops its quotes (it read key 'spam', so 0).</summary>
    [SkippableFact]
    public Task QuotedAssocKeyInArithmetic_IsTheKeyText() => AssertOracle.EqualAsync(
        "declare -A A=(['foo']=bar ['spam']=42 ['a b']=7); k=spam\n"
        + "(( x = A['spam'] )); (( y = A[\"$k\"] + A['a b'] )); (( A['n'] = 3 )); echo $x $y ${A[n]}");

    /// <summary>`a[]=42` is a bad array subscript: status 1, the array unchanged, the rest of the line dropped.</summary>
    [SkippableFact]
    public Task EmptySubscriptAssignment_IsAnError() => AssertOracle.EqualAsync(
        "exec 2>/dev/null\na=(1 2 3)\na[]=42; echo same-line\necho status=$?\necho ${a[@]}");

    /// <summary>A `for` list that fails under failglob ends the loop with status 1; a list that is merely empty leaves 0.</summary>
    [SkippableFact]
    public Task FailGlobInForList_KeepsStatusOne() => AssertOracle.EqualAsync(
        "exec 2>/dev/null\nfor x in *.ZZ; do echo $x; done\necho status=$?\nshopt -s failglob\n"
        + "for x in *.ZZ; do echo $x; done\necho status=$?\nfalse; for x in $(true); do :; done; echo status=$?\n"
        + "v='*.ZZ'; for x in $v; do :; done\necho status=$?\nfor x in *.ZZ; do :; done\ntrue; for x in; do :; done; echo status=$?");

    [SkippableFact]
    public Task AssignDefault_ToArrayElement_GrowsTheArray() => AssertOracle.EqualAsync(
        "arr=(); echo ${#arr[@]}; : ${arr[0]=x}; echo ${#arr[@]} ${arr[0]}; : ${arr[0]=y} ${arr[2]:=z}; echo \"${arr[@]}\"; : ${s=1} ${t:=2}; echo $s $t");
}
