using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// An array assignment takes the kind bash gives it, even where the emitter cannot see a declaration:
/// <list type="bullet">
/// <item><c>s=world; s+=(x)</c> keeps the string as element 0, and <c>declare -A s+=([k]=v)</c> keeps it as key 0.
/// The append started from nothing, because the scalar lives in the environment.</item>
/// <item><c>a=([k1]=v1)</c> with no <c>declare -A</c> is an INDEXED array with arithmetic subscripts (k1 unset is 0).
/// It was an associative table. A variable that is associative at run time, declared in a sourced file, still takes
/// string keys, and a subscript's side effect (<c>[i++]</c>) runs once.</item>
/// <item>Mixed <c>b=([n]=x [0]=z)</c> stored the text <c>[n]=x</c>.</item>
/// <item><c>declare -A a; eval 'a=(1 2 3 4)'</c> takes the words as key/value pairs (bash 5.1+). eval'd text
/// cannot see the declare, and the words became an indexed array.</item>
/// </list>
/// </summary>
public class ArrayKindDifferentialTests
{
    [Theory]
    [InlineData("s1=hello; s2=world\ns1=(1 2 3 4); declare -p s1\ns2+=(1 2 3 4); declare -p s2\nt=world; declare -A t+=([a]=x [b]=y); declare -p t\nf() { local q=lv; q+=(z); declare -p q; }; f\nu=one; eval 'u+=(two)'; declare -p u\nw+=(fresh); declare -p w")]
    [InlineData("a=([k1]=v1 [k2]=v2)\necho ${a[\"k1\"]} ${a[\"k2\"]} ${#a[@]}\nn=3; b=([n]=x [n+1]=y [0]=z); declare -p b\nf=([2]=two [k]=kk); declare -p f\ni=0; c=([i++]=p [i++]=q); declare -p c; echo \"i=$i\"\ne=([x]=only); declare -p e")]
    [InlineData("cd \"$(mktemp -d)\"\nprintf 'declare -A M\\n' > decl.sh\nsource ./decl.sh\nM=([k]=v [j]=w); declare -p M\ndeclare -A N; N=([n]=x); declare -p N")]
    [InlineData("declare -A a=([a]=b)\neval \"a=(1 2 3 4)\"\ndeclare -p a\neval 'a=()'; declare -p a\nb=(x); eval 'b=(1 2)'; declare -p b\neval 'c=(p q)'; declare -p c\nf() { declare -A m; eval 'm=(k v j)'; declare -p m; }; f\neval 'd=(one)'; echo \"${#d[@]} ${d[0]}\"")]
    // Keys and values are data: `;` and `$( )` inside them never run.
    [InlineData("declare -A m\nm=(['a;echo pwned']='$(echo pwned2)')\ndeclare -p m\nk='x;echo pwned3'; eval 'm+=(\"$k\" v)' 2>/dev/null; echo \"s=$?\"")]
    public Task Array_TakesBashsKind(string script) => AssertOracle.EqualAsync(script);

    // `declare -A f; declare -p f` is `declare -A f`: declared, no value. It printed `=()`, which bash keeps for a table
    // that was assigned (`=()`) or written and emptied.
    [Fact]
    public Task DeclaredAssocWithNoValue_PrintsNoParens() => AssertOracle.EqualAsync(
        "declare -A f; declare -p f\ndeclare -A g; g=(); declare -p g\ndeclare -A d; d[k]=1; unset 'd[k]'; declare -p d\n"
        + "declare -A e=(); declare -p e\ndeclare -A h; declare -A h; declare -p h\nx=1; declare -A x; declare -p x\n"
        + "fn() { declare -A l; declare -p l; }; fn");
}
